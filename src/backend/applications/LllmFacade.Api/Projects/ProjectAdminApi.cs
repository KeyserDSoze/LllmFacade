using LllmFacade.Api.Auth;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.Autopilot;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.Projects;

public sealed record ChatGptProjectView(string ProjectId, string Name, Guid? TrackedId, bool IsActive);

public sealed record TrackProjectRequest(Guid EndpointId, string? ProjectId, string? Name);

public sealed record ProjectSettingsRequest(
    string? ContinuationPrompt, string? TargetConversationId, int? MaxContinuations, string? NewChatPrompt = null);

public sealed record ConversationView(string Id, string Title, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Project views (all / active / inactive), chats and the "carry on the work" controls.</summary>
public static class ProjectAdminApi
{
    public static void MapProjectAdminApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/endpoints/{endpointId:guid}/chatgpt-projects", ListChatGptProjectsAsync).RequireAuthorization(AdminAuth.AdminPolicy);

        var group = app.MapGroup("/api/projects").RequireAuthorization(AdminAuth.AdminPolicy);
        group.MapGet("/", ListTrackedAsync);
        group.MapPost("/", TrackAsync);
        group.MapGet("/events", StreamAllEventsAsync);
        group.MapPost("/{id:guid}/activate", (Guid id, ProjectCommands commands, CancellationToken cancellationToken) =>
            commands.UpdateAsync(id, project => project.IsActive = true, cancellationToken,
                log: new LogSpec(ActivityCategories.Project, "Project attivato.", "Attivazione rifiutata")));
        group.MapPost("/{id:guid}/deactivate", (Guid id, ProjectCommands commands, CancellationToken cancellationToken) =>
            commands.UpdateAsync(id, AutopilotRules.Deactivate, cancellationToken, closeTab: true,
                log: new LogSpec(ActivityCategories.Project, "Project disattivato (autopilot fermato).", "Disattivazione rifiutata")));
        group.MapPut("/{id:guid}/settings", (Guid id, ProjectSettingsRequest request, ProjectCommands commands, CancellationToken cancellationToken) =>
            commands.UpdateAsync(id, project => ApplySettings(project, request), cancellationToken,
                log: new LogSpec(ActivityCategories.Project, "Impostazioni salvate.", "Salvataggio impostazioni rifiutato")));
        group.MapPost("/{id:guid}/autopilot/start", async (Guid id, ProjectSettingsRequest request, ProjectCommands commands, TimeProvider time, AutopilotSignal signal, CancellationToken cancellationToken) =>
        {
            var result = await commands.UpdateAsync(id, project =>
            {
                ApplySettings(project, request);
                AutopilotRules.Start(project, project.ContinuationPrompt, project.MaxContinuations, time.GetUtcNow());
            }, cancellationToken, log: new LogSpec(ActivityCategories.Autopilot, "Autopilot avviato: primo controllo della chat in corso.", "Avvio rifiutato"));
            signal.Wake();
            return result;
        });
        group.MapPost("/{id:guid}/autopilot/stop", (Guid id, ProjectCommands commands, CancellationToken cancellationToken) =>
            commands.UpdateAsync(id, AutopilotRules.Stop, cancellationToken, closeTab: true,
                log: new LogSpec(ActivityCategories.Autopilot, "Autopilot fermato.", "Arresto rifiutato")));
        group.MapGet("/{id:guid}/conversations", ListConversationsAsync);
        group.MapGet("/{id:guid}/events", StreamEventsAsync);
    }

    private static async Task<IResult> ListChatGptProjectsAsync(
        Guid endpointId, LllmFacadeDbContext db, EndpointIdentityService identities, IChatGptClient client, ActivityLog log,
        CancellationToken cancellationToken)
    {
        if (await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken) is not { } endpoint)
            return Results.NotFound(new ApiError("Endpoint non trovato."));

        IReadOnlyList<ChatGptProject> projects;
        try
        {
            projects = await client.ListProjectsAsync(identities.CreateIdentity(endpoint), cancellationToken);
        }
        catch (ChatGptException error)
        {
            await log.WarnAsync(ActivityCategories.Project, "Elenco dei project da ChatGPT non riuscito: " + error.Message,
                endpoint.Id, subject: endpoint.Name);
            return Results.Json(new ApiError(error.Message), statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
        }

        var tracked = await db.Projects.AsNoTracking().Where(p => p.EndpointId == endpointId).ToListAsync(cancellationToken);
        return Results.Ok(projects.Select(project =>
        {
            var match = tracked.FirstOrDefault(t => t.ProjectId == project.Id);
            return new ChatGptProjectView(project.Id, project.Name, match?.Id, match?.IsActive ?? false);
        }));
    }

    private static async Task<IReadOnlyList<TrackedProjectView>> ListTrackedAsync(
        string? state, LllmFacadeDbContext db, CancellationToken cancellationToken)
    {
        var query = db.Projects.AsNoTracking().Include(p => p.Endpoint).AsQueryable();
        query = state switch
        {
            "active" => query.Where(p => p.IsActive),
            "inactive" => query.Where(p => !p.IsActive),
            _ => query
        };
        return (await query.OrderBy(p => p.Name).ToListAsync(cancellationToken)).Select(TrackedProjectView.From).ToList();
    }

    private static async Task<IResult> TrackAsync(
        TrackProjectRequest request, LllmFacadeDbContext db, TimeProvider time, ActivityLog log, CancellationToken cancellationToken)
    {
        if (!ChatGptIds.IsProjectId(request.ProjectId))
            return Results.BadRequest(new ApiError("ID project ChatGPT non valido."));
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            return Results.BadRequest(new ApiError("Il nome del project deve contenere da 1 a 200 caratteri."));
        if (!await db.Endpoints.AnyAsync(e => e.Id == request.EndpointId, cancellationToken))
            return Results.NotFound(new ApiError("Endpoint non trovato."));

        var now = time.GetUtcNow();
        var project = await db.Projects.Include(p => p.Endpoint)
            .SingleOrDefaultAsync(p => p.EndpointId == request.EndpointId && p.ProjectId == request.ProjectId, cancellationToken);
        var created = project is null;
        project ??= db.Projects.Add(new TrackedProject
        {
            Id = Guid.NewGuid(),
            EndpointId = request.EndpointId,
            ProjectId = request.ProjectId!,
            CreatedAt = now
        }).Entity;
        project.Name = name;
        project.IsActive = true;
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(project).Reference(p => p.Endpoint).LoadAsync(cancellationToken);
        await log.InfoAsync(ActivityCategories.Project, created ? "Project aggiunto e attivato." : "Project riattivato.",
            project.EndpointId, project.Id, project.Name);
        var view = TrackedProjectView.From(project);
        return created ? Results.Created($"/api/projects/{project.Id}", view) : Results.Ok(view);
    }

    private static async Task<IResult> ListConversationsAsync(
        Guid id, LllmFacadeDbContext db, EndpointIdentityService identities, IChatGptClient client, ActivityLog log,
        CancellationToken cancellationToken)
    {
        if (await db.Projects.AsNoTracking().Include(p => p.Endpoint).SingleOrDefaultAsync(p => p.Id == id, cancellationToken) is not { Endpoint: { } endpoint } project)
            return Results.NotFound(new ApiError("Project non trovato."));
        try
        {
            var conversations = await client.ListProjectConversationsAsync(identities.CreateIdentity(endpoint), project.ProjectId, cancellationToken);
            return Results.Ok(conversations
                .OrderBy(c => c.CreatedAt ?? c.UpdatedAt ?? DateTimeOffset.MaxValue)
                .Select(c => new ConversationView(c.Id, c.Title, c.CreatedAt, c.UpdatedAt)));
        }
        catch (ChatGptException error)
        {
            await log.WarnAsync(ActivityCategories.Project, "Elenco delle chat non riuscito: " + error.Message,
                project.EndpointId, project.Id, project.Name);
            return Results.Json(new ApiError(error.Message), statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
        }
    }

    private static async Task StreamEventsAsync(Guid id, HttpContext http, LllmFacadeDbContext db, ProjectEventHub hub)
    {
        var cancellationToken = http.RequestAborted;
        var project = await db.Projects.AsNoTracking().Include(p => p.Endpoint).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var subscription = hub.Subscribe(id);
        await PumpAsync(http, hub, subscription, [project]);
    }

    private static async Task StreamAllEventsAsync(HttpContext http, LllmFacadeDbContext db, ProjectEventHub hub)
    {
        using var subscription = hub.SubscribeAll();
        var active = await db.Projects.AsNoTracking().Include(p => p.Endpoint)
            .Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync(http.RequestAborted);
        await PumpAsync(http, hub, subscription, active);
    }

    private static async Task PumpAsync(
        HttpContext http, ProjectEventHub hub, ProjectSubscription subscription, IReadOnlyList<TrackedProject> initial)
    {
        var cancellationToken = http.RequestAborted;
        ServerSentEvents.Prepare(http.Response);
        try
        {
            foreach (var project in initial)
            {
                await ServerSentEvents.WriteAsync(http.Response, "status", hub.Serialize(TrackedProjectView.From(project)), cancellationToken);
                if (hub.GetLiveText(project.Id) is { } liveText)
                    await ServerSentEvents.WriteAsync(http.Response, "live",
                        hub.Serialize(new { projectId = project.Id, text = liveText, reset = true }), cancellationToken);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                keepAlive.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var message = await subscription.Reader.ReadAsync(keepAlive.Token);
                    await ServerSentEvents.WriteAsync(http.Response, message.Type, message.Json, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await ServerSentEvents.KeepAliveAsync(http.Response, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void ApplySettings(TrackedProject project, ProjectSettingsRequest request)
    {
        project.ContinuationPrompt = string.IsNullOrWhiteSpace(request.ContinuationPrompt)
            ? string.Empty
            : AutopilotRules.NormalizePrompt(request.ContinuationPrompt);
        project.TargetConversationId = string.IsNullOrWhiteSpace(request.TargetConversationId)
            ? null
            : ChatGptIds.EnsureConversationId(request.TargetConversationId.Trim());
        if (request.MaxContinuations is < 1 or > 10_000)
            throw new ArgumentException("Il numero massimo di continuazioni deve essere tra 1 e 10000.");
        project.MaxContinuations = request.MaxContinuations;
        var newChatPrompt = request.NewChatPrompt?.Trim() ?? string.Empty;
        if (newChatPrompt.Length > AutopilotRules.MaxPromptLength)
            throw new ArgumentException($"Il prompt della nuova chat supera {AutopilotRules.MaxPromptLength} caratteri.");
        project.NewChatPrompt = newChatPrompt;
    }
}

/// <summary>What a project command writes to the activity log when it succeeds or is refused.</summary>
public sealed record LogSpec(string Category, string Done, string Refused);

/// <summary>Shared load-modify-save flow for project commands, with status broadcast and activity log.</summary>
public sealed class ProjectCommands(
    LllmFacadeDbContext db, IChatGptClient client, ProjectEventHub hub, TimeProvider time, ActivityLog activity)
{
    public async Task<IResult> UpdateAsync(
        Guid id, Action<TrackedProject> change, CancellationToken cancellationToken, bool closeTab = false, LogSpec? log = null)
    {
        if (await db.Projects.Include(p => p.Endpoint).SingleOrDefaultAsync(p => p.Id == id, cancellationToken) is not { } project)
            return Results.NotFound(new ApiError("Project non trovato."));
        try
        {
            change(project);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            if (log is not null)
                await activity.WarnAsync(log.Category, $"{log.Refused}: {error.Message}", project.EndpointId, project.Id, project.Name);
            return Results.BadRequest(new ApiError(error.Message));
        }

        project.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("Il project è stato aggiornato nel frattempo: riprovare."));
        }
        if (closeTab)
            await client.CloseTabAsync(project.EndpointId, AutopilotProcessor.TabKey(project));
        hub.PublishStatus(project);
        if (log is not null)
            await activity.InfoAsync(log.Category, log.Done, project.EndpointId, project.Id, project.Name);
        return Results.Ok(TrackedProjectView.From(project));
    }
}
