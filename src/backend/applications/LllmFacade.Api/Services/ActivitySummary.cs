using System.Collections.Concurrent;
using LllmFacade.Api.Auth;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.Services;

/// <summary>In-flight calls on the OpenAI-compatible API, for the "who is working now" panel.</summary>
public sealed class ActiveRequests(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, ActiveRequest> _running = new();

    public IDisposable Begin(Guid endpointId, string endpointName, string model, bool stream)
    {
        var id = Guid.NewGuid();
        _running[id] = new ActiveRequest(endpointId, endpointName, model, stream, time.GetUtcNow());
        return new Registration(() => _running.TryRemove(id, out _));
    }

    public IReadOnlyList<ActiveRequest> Snapshot() => _running.Values.OrderBy(r => r.StartedAt).ToList();

    private sealed class Registration(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
        }
    }
}

public sealed record ActiveRequest(Guid EndpointId, string EndpointName, string Model, bool Stream, DateTimeOffset StartedAt);

public sealed record WorkingItem(string Kind, Guid? Id, string Name, string EndpointName, string Detail, DateTimeOffset? Since);

public sealed record EndpointTokenView(
    Guid Id,
    string Name,
    bool IsEnabled,
    bool HasAccessToken,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? LastRefreshAt,
    DateTimeOffset? NextRefreshAt,
    string? LastError);

public sealed record ActivitySummary(
    IReadOnlyList<WorkingItem> Working, int Watching, IReadOnlyList<EndpointTokenView> Endpoints, DateTimeOffset ServerTime);

public static class ActivitySummaryApi
{
    public static void MapActivitySummaryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/activity/summary", async (
            LllmFacadeDbContext db, ActiveRequests requests, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var projects = await db.Projects.AsNoTracking().Include(p => p.Endpoint)
                .Where(p => p.IsActive && p.AutopilotEnabled).OrderBy(p => p.Name).ToListAsync(cancellationToken);
            var working = projects
                .Where(p => p.Status is AutopilotStatus.Generating or AutopilotStatus.Submitting)
                .Select(p => new WorkingItem(
                    "project", p.Id, p.Name, p.Endpoint?.Name ?? string.Empty,
                    p.Status == AutopilotStatus.Generating ? "ChatGPT sta scrivendo" : "Invio del prompt in corso",
                    p.LastContinuationAt ?? p.LastCheckAt))
                .Concat(requests.Snapshot().Select(r => new WorkingItem(
                    "api", null, r.EndpointName, r.EndpointName,
                    $"Richiesta API {r.Model} ({(r.Stream ? "streaming" : "risposta unica")})", r.StartedAt)))
                .ToList();

            var endpoints = (await db.Endpoints.AsNoTracking().OrderBy(e => e.Name).ToListAsync(cancellationToken))
                .Select(e => new EndpointTokenView(
                    e.Id, e.Name, e.IsEnabled, e.ProtectedAccessToken is not null, e.AccessTokenExpiresAt, e.LastRefreshAt,
                    EndpointView.NextRefresh(e), e.LastError))
                .ToList();

            return new ActivitySummary(working, projects.Count(p => p.Status is AutopilotStatus.Monitoring or AutopilotStatus.WaitingRetry),
                endpoints, time.GetUtcNow());
        }).RequireAuthorization(AdminAuth.AdminPolicy);
    }
}
