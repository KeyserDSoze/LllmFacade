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
    public int PollIntervalSeconds { get; set; } = 30;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int SubmissionGraceSeconds { get; set; } = 300;
    public int ScanIntervalSeconds { get; set; } = 5;
    public int MaxParallelProjects { get; set; } = 4;

    public AutopilotSettings ToSettings() => new(
        TimeSpan.FromSeconds(PollIntervalSeconds),
        TimeSpan.FromSeconds(RetryIntervalSeconds),
        TimeSpan.FromSeconds(SubmissionGraceSeconds));
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
public sealed class AutopilotProcessor(
    LllmFacadeDbContext db,
    EndpointIdentityService identities,
    IChatGptClient client,
    ProjectEventHub hub,
    TimeProvider time,
    ActivityLog activity,
    IOptions<AutopilotOptions> options,
    ILogger<AutopilotProcessor> logger)
{
    public static string TabKey(TrackedProject project) => project.Id.ToString("N");

    public async Task<IReadOnlyList<Guid>> FindDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await db.Projects
            .Where(p => p.AutopilotEnabled && p.IsActive && p.NextCheckAt != null && p.NextCheckAt <= now && p.Endpoint!.IsEnabled)
            .OrderBy(p => p.NextCheckAt)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task ProcessAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.Include(p => p.Endpoint).SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is not { AutopilotEnabled: true, IsActive: true, Endpoint: { } endpoint })
            return;

        var settings = options.Value.ToSettings();
        var before = Snapshot(project);
        try
        {
            var identity = identities.CreateIdentity(endpoint);
            var conversationId = project.TargetConversationId ?? await LatestConversationAsync(identity, project, cancellationToken);
            var state = await client.GetConversationStateAsync(identity, conversationId, cancellationToken);
            if (state.Phase == ConversationPhase.Finished && state.LastAssistantText is { } answer)
                hub.SetLiveText(project.Id, answer);

            var action = AutopilotRules.Observe(project, state, time.GetUtcNow(), settings);
            if (action == AutopilotAction.Submit)
                await SubmitAsync(project, identity, conversationId, state, settings, cancellationToken);
            else if (action == AutopilotAction.Complete)
                await client.CloseTabAsync(endpoint.Id, TabKey(project));
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

    private async Task<string> LatestConversationAsync(ChatGptIdentity identity, TrackedProject project, CancellationToken cancellationToken)
    {
        var conversations = await client.ListProjectConversationsAsync(identity, project.ProjectId, cancellationToken);
        return conversations.OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt ?? DateTimeOffset.MinValue).FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("Il project non contiene chat: aprirne una su ChatGPT.");
    }
}

public sealed class AutopilotWorker(
    IServiceScopeFactory scopes, AutopilotSignal signal, IOptions<AutopilotOptions> options, ILogger<AutopilotWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
