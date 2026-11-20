using LllmFacade.Api.Services;
using LllmFacade.Business.Autopilot;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using LllmFacade.Business.Sessions;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LllmFacade.Api.Workers;

public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int RefreshScanSeconds { get; set; } = 60;
}

public sealed class AutopilotOptions
{
    public int PollIntervalSeconds { get; set; } = 300;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int SubmissionGraceSeconds { get; set; } = 300;
    public int CompletionPauseMinutes { get; set; } = 10;
    public int ChallengeCooldownMinutes { get; set; } = 30;
    public int RateLimitCooldownMinutes { get; set; } = 15;
    public int RepeatedRateLimitCooldownMinutes { get; set; } = 30;
    public int RateLimitRepeatWindowMinutes { get; set; } = 30;
    public int RateLimitReleaseStaggerSeconds { get; set; } = 30;
    public int RateLimitReleaseStaggerMaxSeconds { get; set; } = 300;
    public int ScanIntervalSeconds { get; set; } = 5;
    public int MaxParallelProjects { get; set; } = 4;

    public AutopilotSettings ToSettings() => new(
        TimeSpan.FromSeconds(PollIntervalSeconds),
        TimeSpan.FromSeconds(RetryIntervalSeconds),
        TimeSpan.FromSeconds(SubmissionGraceSeconds),
        TimeSpan.FromMinutes(RateLimitCooldownMinutes),
        TimeSpan.FromMinutes(CompletionPauseMinutes),
        TimeSpan.FromMinutes(RepeatedRateLimitCooldownMinutes),
        TimeSpan.FromMinutes(RateLimitRepeatWindowMinutes));

    public TimeSpan ReleaseStagger(IReadOnlyCollection<TrackedProject> projects)
    {
        var baseSeconds = Math.Max(0, RateLimitReleaseStaggerSeconds);
        var maximumSeconds = Math.Max(baseSeconds, RateLimitReleaseStaggerMaxSeconds);
        var failureDepth = projects.Count == 0 ? 1 : Math.Max(1, projects.Max(project => project.ConsecutiveFailures));
        var multiplier = Math.Pow(2, Math.Min(failureDepth - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(maximumSeconds, baseSeconds * multiplier));
    }
}

/// <summary>Renews the ChatGPT access token through the web app session endpoint, like the browser does.</summary>
public sealed class SessionRefreshProcessor(
    LllmFacadeDbContext db,
    EndpointIdentityService identities,
    CredentialProtector protector,
    IChatGptClient client,
    TimeProvider time,
    ActivityLog activity,
    ILogger<SessionRefreshProcessor> logger)
{
    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => e.IsEnabled).ToListAsync(cancellationToken);
        var due = endpoints.Where(e => SessionRefreshPolicy.IsDue(
            now, TimeSpan.FromMinutes(e.RefreshIntervalMinutes), e.LastRefreshAt, e.LastRefreshAttemptAt, e.AccessTokenExpiresAt)).ToList();
        foreach (var endpoint in due)
            await RefreshAsync(endpoint, cancellationToken);
        return due.Count;
    }

    public async Task RefreshAsync(ChatGptEndpoint endpoint, CancellationToken cancellationToken, bool manual = false)
    {
        var now = time.GetUtcNow();
        try
        {
            var snapshot = await client.RefreshSessionAsync(identities.CreateIdentity(endpoint), cancellationToken);
            var token = snapshot.AccessToken
                ?? throw new ChatGptException(ChatGptFailure.SessionExpired, "ChatGPT non ha restituito un token di accesso.");
            var protectedToken = protector.Protect(token);
            var expiresAt = snapshot.AccessTokenExpiresAt ?? AccessTokens.ReadExpiry(token);
            var protectedState = protector.Protect(snapshot.StorageStateJson);
            var hash = StorageStates.Hash(snapshot.StorageStateJson);
            var cookieCount = StorageStates.CountCookies(snapshot.StorageStateJson);
            await db.Endpoints
                .Where(e => e.Id == endpoint.Id && e.CredentialsStamp == endpoint.CredentialsStamp)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.ProtectedAccessToken, protectedToken)
                    .SetProperty(e => e.AccessTokenExpiresAt, expiresAt)
                    .SetProperty(e => e.AccountEmail, snapshot.AccountEmail)
                    .SetProperty(e => e.ProtectedStorageState, protectedState)
                    .SetProperty(e => e.StorageStateHash, hash)
                    .SetProperty(e => e.CookieCount, cookieCount)
                    .SetProperty(e => e.LastRefreshAt, now)
                    .SetProperty(e => e.LastRefreshAttemptAt, now)
                    .SetProperty(e => e.LastError, (string?)null)
                    .SetProperty(e => e.LastErrorAt, (DateTimeOffset?)null), cancellationToken);
            await activity.InfoAsync(ActivityCategories.Session,
                $"Sessione rinnovata{(manual ? " su richiesta" : string.Empty)}: {cookieCount} cookie, token {(expiresAt is { } valid ? "valido fino al " + valid.ToString("dd/MM/yyyy HH:mm 'UTC'") : "senza scadenza nota")}.",
                endpoint.Id, subject: endpoint.Name);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Rinnovo sessione non riuscito per l'endpoint {EndpointId}: {Reason}", endpoint.Id, Describe(error));
            var message = Describe(error);
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.LastRefreshAttemptAt, now)
                .SetProperty(e => e.LastError, message)
                .SetProperty(e => e.LastErrorAt, now), CancellationToken.None);
            if (manual || endpoint.LastError != message)
                await activity.WarnAsync(ActivityCategories.Session, "Rinnovo sessione non riuscito: " + message,
                    endpoint.Id, subject: endpoint.Name);
        }
    }

    internal static string Describe(Exception error) => error switch
    {
        ChatGptException chatGpt => chatGpt.Message,
        _ => "Errore inatteso: " + error.Message
    };
}

public sealed class SessionRefreshWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger<SessionRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.RefreshScanSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SessionRefreshProcessor>().RefreshDueAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Ciclo di rinnovo delle sessioni non riuscito.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Deletes activity log entries older than the configured retention, once an hour.</summary>
public sealed class LogRetentionWorker(ActivityLog activity, ILogger<LogRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await activity.PruneAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Pulizia del registro attività non riuscita.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Wakes the autopilot loop immediately after an admin command.</summary>
public sealed class AutopilotSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>One autopilot step for a project: look at the chat, send the saved prompt when the answer is finished.</summary>
public sealed class AutopilotCooldownCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);
    public void Release() => _gate.Release();
}

public sealed class AutopilotProcessor(
    LllmFacadeDbContext db,
    EndpointIdentityService identities,
    IChatGptClient client,
    ProjectEventHub hub,
    TimeProvider time,
    ActivityLog activity,
    IOptions<AutopilotOptions> options,
    AutopilotCooldownCoordinator cooldownCoordinator,
    ILogger<AutopilotProcessor> logger)
{
    public static string TabKey(TrackedProject project) => project.Id.ToString("N");

    public async Task<IReadOnlyList<Guid>> FindDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await db.Projects
            .Where(p => p.AutopilotEnabled && p.IsActive && (p.NextCheckAt == null || p.NextCheckAt <= now) && p.Endpoint!.IsEnabled)
            .OrderBy(p => p.NextCheckAt ?? DateTimeOffset.MinValue)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Reconciles every persisted active autopilot immediately after process restart.</summary>
    public async Task<int> ResumeAfterRestartAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var projects = await db.Projects.Include(p => p.Endpoint)
            .Where(p => p.AutopilotEnabled && p.IsActive && p.Endpoint!.IsEnabled)
            .ToListAsync(cancellationToken);
        var cooldownProjects = projects.Where(project => project.RateLimitUntil is not null)
            .OrderBy(project => project.Name).ToList();
        var stagger = options.Value.ReleaseStagger(cooldownProjects);
        var scheduledRelease = cooldownProjects.Select(project => project.NextCheckAt).ToList();
        var scheduleIsCurrent = cooldownProjects.Count > 1
            && scheduledRelease[0] is { } firstRelease
            && scheduledRelease.Select((release, index) => (Release: release, Index: index))
                .All(item => item.Release is { } release && release > now && release == firstRelease + stagger * item.Index);
        var releasePositions = cooldownProjects.Count > 1 && !scheduleIsCurrent
            ? cooldownProjects.Select((project, index) => (project.Id, Index: index))
                .ToDictionary(item => item.Id, item => item.Index)
            : [];
        var releaseBase = releasePositions.Count > 0
            ? cooldownProjects.Max(project => project.RateLimitUntil!.Value) > now
                ? cooldownProjects.Max(project => project.RateLimitUntil!.Value)
                : now
            : (DateTimeOffset?)null;
        var restartProjects = projects.Where(project => project.RateLimitUntil is null
                && !((project.Status is AutopilotStatus.CoolingDown or AutopilotStatus.VerificationRequired)
                    && project.NextCheckAt > now))
            .OrderBy(project => project.Name)
            .ToList();
        var restartStagger = options.Value.ReleaseStagger(restartProjects);
        var restartPositions = restartProjects.Select((project, index) => (project.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index);
        foreach (var project in projects)
        {
            if (releaseBase is { } baseTime && releasePositions.TryGetValue(project.Id, out var position))
                project.NextCheckAt = baseTime + stagger * position;
            else if (restartPositions.TryGetValue(project.Id, out var restartPosition))
                project.NextCheckAt = now + restartStagger * restartPosition;
            else
                project.NextCheckAt = project.NextCheckAt is { } scheduled && scheduled > now
                    && (project.Status is AutopilotStatus.CoolingDown or AutopilotStatus.VerificationRequired
                        || project.RateLimitUntil is not null)
                        ? scheduled
                        : project.RateLimitUntil is { } cooldown && cooldown > now ? cooldown : now;
            project.UpdatedAt = now;
        }
        if (projects.Count == 0)
            return 0;

        await db.SaveChangesAsync(cancellationToken);
        foreach (var project in projects)
        {
            hub.PublishStatus(project);
            var delayedUntil = project.NextCheckAt;
            var message = delayedUntil is { } scheduled && scheduled > now
                ? $"Autopilot ripristinato dopo il riavvio: ripresa del controllo rinviata al {scheduled.ToLocalTime():dd/MM HH:mm}."
                : "Autopilot ripreso dopo il riavvio del servizio: controllo immediato dello stato della chat.";
            await activity.InfoAsync(ActivityCategories.Autopilot, message,
                project.EndpointId, project.Id, project.Name);
        }
        return projects.Count;
    }

    public async Task ProcessAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.Include(p => p.Endpoint).SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is not { AutopilotEnabled: true, IsActive: true, Endpoint: { } endpoint })
            return;

        var settings = options.Value.ToSettings();
        var before = Snapshot(project);
        var selectionChanged = false;
        var usedFallback = false;
        try
        {
            var identity = identities.CreateIdentity(endpoint);
            var candidates = await ConversationCandidatesAsync(identity, project, cancellationToken);
            Exception? detailError = null;
            ConversationState? state = null;
            string? conversationId = null;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                try
                {
                    state = await client.GetConversationStateAsync(identity, candidate, cancellationToken);
                    conversationId = candidate;
                    usedFallback = index > 0;
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    detailError = error;
                    if (project.TargetConversationId is not null || !CanFallbackToOlderConversation(error) || index == candidates.Count - 1)
                        break;
                }
            }

            if (state is null || conversationId is null)
            {
                AutopilotRules.SelectConversation(project, candidates[0], time.GetUtcNow(), settings);
                throw detailError ?? new InvalidOperationException("Nessuna chat del project è leggibile.");
            }

            selectionChanged = AutopilotRules.SelectConversation(project, conversationId, time.GetUtcNow(), settings);
            if (selectionChanged)
                hub.PublishStatus(project);
            if (state.Phase == ConversationPhase.Finished && state.LastAssistantText is { } answer)
                hub.SetLiveText(project.Id, answer);

            await client.EnsureConversationTabAsync(
                identity, new PromptTarget(TabKey(project), conversationId, project.ProjectId), cancellationToken);
            var action = AutopilotRules.Observe(project, state, time.GetUtcNow(), settings);
            if (action == AutopilotAction.Submit)
                await SubmitAsync(project, identity, conversationId, state, settings, cancellationToken);
        }
        catch (ChatGptException error) when (error.Failure == ChatGptFailure.RateLimited)
        {
            var affected = await CooldownAllAfterRateLimitAsync(project.Id, error.Message, cancellationToken);
            logger.LogWarning("ChatGPT ha limitato le richieste: {Count} project in pausa.", affected.Count);
            await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
            return;
        }
        catch (ChatGptException error) when (error.Failure == ChatGptFailure.Blocked)
        {
            var affected = await PauseEndpointAfterBlockedAsync(endpoint.Id, error.Message, cancellationToken);
            logger.LogWarning("ChatGPT richiede verifica manuale: {Count} project dell'endpoint in pausa.", affected.Count);
            await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
            return;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AutopilotRules.Failed(project, SessionRefreshProcessor.Describe(error), time.GetUtcNow(), settings);
            logger.LogWarning("Autopilot del project {ProjectId}: {Reason}", project.Id, project.LastError);
        }

        project.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The admin changed the project meanwhile (e.g. stopped it): their change wins.
            return;
        }
        hub.PublishStatus(project);
        if (selectionChanged)
            await activity.InfoAsync(ActivityCategories.Project,
                "Chat più recente selezionata o aggiornata per il monitoraggio del project.", project.EndpointId, project.Id, project.Name);
        if (usedFallback)
            await activity.WarnAsync(ActivityCategories.Project,
                "La chat più recente non era leggibile: usata la successiva in ordine cronologico.", project.EndpointId, project.Id, project.Name);
        foreach (var entry in AutopilotLogRules.Describe(before, Snapshot(project)))
            await activity.WriteAsync(entry.Level, ActivityCategories.Autopilot, entry.Message, project.EndpointId, project.Id, project.Name);
        await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
    }

    private static AutopilotSnapshot Snapshot(TrackedProject project) =>
        new(project.Status, project.LastError, project.ConsecutiveFailures, project.ContinuationsSent, project.MaxContinuations,
            project.ChatsCreated);

    private async Task SubmitAsync(
        TrackedProject project, ChatGptIdentity identity, string conversationId, ConversationState state, AutopilotSettings settings,
        CancellationToken cancellationToken)
    {
        hub.SetLiveText(project.Id, string.Empty);
        hub.PublishStatus(project);
        try
        {
            var outcome = await client.SubmitPromptAsync(
                identity,
                new PromptTarget(TabKey(project), conversationId, project.ProjectId),
                project.ContinuationPrompt,
                new LiveTextProgress(hub, project.Id),
                cancellationToken);
            AutopilotRules.SubmissionSent(project, state.CurrentNodeId, outcome == SubmissionOutcome.Confirmed, time.GetUtcNow(), settings);
        }
        catch (ChatGptException error) when (error.Failure == ChatGptFailure.ConversationFull)
        {
            await RollOverAsync(project, identity, state, settings, cancellationToken);
        }
        catch (ChatGptException error) when (error.PromptMayHaveBeenSent)
        {
            AutopilotRules.SubmissionSent(project, state.CurrentNodeId, confirmed: false, time.GetUtcNow(), settings);
            project.LastError = error.Message;
        }
    }

    /// <summary>The chat reached ChatGPT's length limit: continue in a new chat of the same project and pin it.</summary>
    private async Task RollOverAsync(
        TrackedProject project, ChatGptIdentity identity, ConversationState state, AutopilotSettings settings,
        CancellationToken cancellationToken)
    {
        var prompt = NewChatPromptComposer.Compose(project.NewChatPrompt, project.ContinuationPrompt, state.LastAssistantText);
        var requestedAt = time.GetUtcNow();
        StartedChat started;
        try
        {
            started = await client.StartProjectChatAsync(
                identity, new NewChatTarget(TabKey(project), project.ProjectId), prompt, new LiveTextProgress(hub, project.Id), cancellationToken);
        }
        catch (ChatGptException error) when (error.PromptMayHaveBeenSent)
        {
            // The chat may exist even though its id was not read: adopt the newest one instead of creating a duplicate.
            var created = (await client.ListProjectConversationsAsync(identity, project.ProjectId, cancellationToken))
                .Where(c => c.CreatedAt >= requestedAt.AddMinutes(-1))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault() ?? throw error;
            started = new StartedChat(created.Id, SubmissionOutcome.Unconfirmed);
        }
        AutopilotRules.ChatRolledOver(project, started.ConversationId, started.Outcome == SubmissionOutcome.Confirmed, time.GetUtcNow(), settings);
    }

    private async Task<IReadOnlyList<string>> ConversationCandidatesAsync(
        ChatGptIdentity identity, TrackedProject project, CancellationToken cancellationToken)
    {
        if (project.TargetConversationId is { Length: > 0 } target)
            return [target];

        var conversations = await client.ListProjectConversationsAsync(identity, project.ProjectId, cancellationToken);
        var ids = conversations
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(c => c.Id)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("Il project non contiene chat: aprirne una su ChatGPT.");
        return ids;
    }

    private static bool CanFallbackToOlderConversation(Exception error) =>
        error is ChatGptException { Failure: ChatGptFailure.Timeout or ChatGptFailure.Upstream or ChatGptFailure.Interface };

    public async Task<IReadOnlyList<TrackedProject>> PauseEndpointAfterBlockedAsync(
        Guid endpointId, string error, CancellationToken cancellationToken)
    {
        await cooldownCoordinator.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var projects = await db.Projects.Include(p => p.Endpoint)
                .Where(p => p.EndpointId == endpointId && p.IsActive && p.AutopilotEnabled)
                .OrderBy(p => p.Name)
                .ToListAsync(cancellationToken);
            var cooldown = TimeSpan.FromMinutes(Math.Max(1, options.Value.ChallengeCooldownMinutes));
            var changed = projects.Where(project => AutopilotRules.Blocked(project, error, now, cooldown)).ToList();
            if (changed.Count == 0)
                return projects;

            foreach (var project in changed)
                project.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            foreach (var project in changed)
                hub.PublishStatus(project);
            await activity.WarnAsync(ActivityCategories.Autopilot,
                $"ChatGPT richiede verifica manuale nel browser: {changed.Count} project dell'endpoint restano " +
                $"in pausa per {cooldown.TotalMinutes:0} minuti. Nessun bypass o nuova richiesta verrà tentata.",
                endpointId);
            return projects;
        }
        finally
        {
            cooldownCoordinator.Release();
        }
    }

    public async Task<IReadOnlyList<TrackedProject>> CooldownAllAfterRateLimitAsync(
        Guid? sourceProjectId, string error, CancellationToken cancellationToken)
    {
        await cooldownCoordinator.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var now = time.GetUtcNow();
                var projects = await db.Projects.Include(p => p.Endpoint)
                    .Where(p => p.IsActive && p.AutopilotEnabled)
                    .OrderBy(p => p.Name)
                    .ToListAsync(cancellationToken);
                var settings = options.Value.ToSettings();
                var repeatWindow = settings.RateLimitRepeatWindow ?? TimeSpan.FromMinutes(30);
                var repeated = repeatWindow > TimeSpan.Zero && projects.Any(project =>
                    project.LastRateLimitAt is { } last && last >= now - repeatWindow);
                var effectiveCooldown = repeated
                    ? settings.RepeatedRateLimitCooldown ?? TimeSpan.FromMinutes(30)
                    : settings.RateLimitCooldown ?? TimeSpan.FromMinutes(15);
                var changed = new List<TrackedProject>();
                foreach (var tracked in projects)
                {
                    var message = tracked.Id == sourceProjectId
                        ? $"{error} Pausa globale degli autopilot per {effectiveCooldown.TotalMinutes:0} minuti."
                        : $"ChatGPT ha ricevuto un 429 (too many requests): pausa globale per {effectiveCooldown.TotalMinutes:0} minuti.";
                    if (AutopilotRules.RateLimited(tracked, message, now, settings, repeated))
                        changed.Add(tracked);
                }
                if (changed.Count == 0)
                    return projects;

                var stagger = options.Value.ReleaseStagger(changed);
                for (var index = 0; index < changed.Count; index++)
                {
                    var tracked = changed[index];
                    tracked.NextCheckAt = tracked.RateLimitUntil + stagger * index;
                    tracked.UpdatedAt = now;
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException) when (attempt < 2)
                {
                    db.ChangeTracker.Clear();
                    continue;
                }
                foreach (var tracked in changed)
                    hub.PublishStatus(tracked);
                await activity.WarnAsync(ActivityCategories.Autopilot,
                    $"Too many requests: {changed.Count} project attivi sono stati messi in pausa per " +
                    $"{effectiveCooldown.TotalMinutes:0} minuti; la ripresa sarà distribuita ogni " +
                    $"{stagger.TotalSeconds:0} secondi.");
                return projects;
            }
            throw new InvalidOperationException("Il cooldown globale degli autopilot è rimasto in conflitto concorrente.");
        }
        finally
        {
            cooldownCoordinator.Release();
        }
    }
}

public sealed class AutopilotWorker(
    IServiceScopeFactory scopes, AutopilotSignal signal, IOptions<AutopilotOptions> options, ILogger<AutopilotWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var resumed = await scope.ServiceProvider.GetRequiredService<AutopilotProcessor>()
                .ResumeAfterRestartAsync(stoppingToken);
            if (resumed > 0)
                logger.LogInformation("Ripresi {Count} autopilot persistenti dopo l'avvio del servizio.", resumed);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(error, "Ripresa iniziale degli autopilot non riuscita.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.Value.ScanIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Ciclo autopilot non riuscito.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
            due = await scope.ServiceProvider.GetRequiredService<AutopilotProcessor>().FindDueAsync(stoppingToken);

        await Parallel.ForEachAsync(due,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxParallelProjects), CancellationToken = stoppingToken },
            async (projectId, cancellationToken) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AutopilotProcessor>().ProcessAsync(projectId, cancellationToken);
            });
    }
}
