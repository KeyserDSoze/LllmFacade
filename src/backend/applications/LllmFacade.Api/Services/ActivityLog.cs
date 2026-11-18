using LllmFacade.Api.Auth;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LllmFacade.Api.Services;

public sealed class LogOptions
{
    public int RetentionDays { get; set; } = 14;
}

public sealed record ActivityLogView(
    long Id, DateTimeOffset At, ActivityLevel Level, string Category, string Message, Guid? EndpointId, Guid? ProjectId, string? Subject)
{
    public static ActivityLogView From(ActivityLogEntry e) =>
        new(e.Id, e.At, e.Level, e.Category, e.Message, e.EndpointId, e.ProjectId, e.Subject);
}

public sealed record ActivityLogPage(IReadOnlyList<ActivityLogView> Items, bool HasMore);

/// <summary>
/// Writes the audit trail with its own scope and context, so a log write never saves unrelated pending changes.
/// Failures are swallowed: logging must not break the operation being logged. Callers must never pass secrets.
/// </summary>
public sealed class ActivityLog(IServiceScopeFactory scopes, TimeProvider time, IOptions<LogOptions> options, ILogger<ActivityLog> logger)
{
    private const int MaxMessage = 1000;
    private const int MaxSubject = 200;

    public async Task WriteAsync(
        ActivityLevel level, string category, string message, Guid? endpointId = null, Guid? projectId = null, string? subject = null)
    {
        var text = message.Length > MaxMessage ? message[..MaxMessage] : message;
        logger.Log(level switch { ActivityLevel.Error => LogLevel.Error, ActivityLevel.Warning => LogLevel.Warning, _ => LogLevel.Information },
            "[{Category}] {Subject}: {Message}", category, subject ?? "-", text);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LllmFacadeDbContext>();
            db.ActivityLog.Add(new ActivityLogEntry
            {
                At = time.GetUtcNow(),
                Level = level,
                Category = category,
                Message = text,
                EndpointId = endpointId,
                ProjectId = projectId,
                Subject = subject is { Length: > MaxSubject } ? subject[..MaxSubject] : subject
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Registro attività non scrivibile.");
        }
    }

    public Task InfoAsync(string category, string message, Guid? endpointId = null, Guid? projectId = null, string? subject = null) =>
        WriteAsync(ActivityLevel.Info, category, message, endpointId, projectId, subject);

    public Task WarnAsync(string category, string message, Guid? endpointId = null, Guid? projectId = null, string? subject = null) =>
        WriteAsync(ActivityLevel.Warning, category, message, endpointId, projectId, subject);

    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var limit = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.RetentionDays));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LllmFacadeDbContext>();
        return await db.ActivityLog.Where(e => e.At < limit).ExecuteDeleteAsync(cancellationToken);
    }
}

public static class ActivityLogApi
{
    public static void MapActivityLogApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs", async (
            string? level, string? category, Guid? projectId, Guid? endpointId, long? before, int? take,
            LllmFacadeDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.ActivityLog.AsNoTracking().AsQueryable();
            if (Enum.TryParse<ActivityLevel>(level, ignoreCase: true, out var minimum) && minimum > ActivityLevel.Info)
                query = minimum == ActivityLevel.Error
                    ? query.Where(e => e.Level == ActivityLevel.Error)
                    : query.Where(e => e.Level != ActivityLevel.Info);
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(e => e.Category == category);
            if (projectId is { } project)
                query = query.Where(e => e.ProjectId == project);
            if (endpointId is { } endpoint)
                query = query.Where(e => e.EndpointId == endpoint);
            if (before is { } cursor)
                query = query.Where(e => e.Id < cursor);

            var size = Math.Clamp(take ?? 100, 1, 500);
            var rows = await query.OrderByDescending(e => e.Id).Take(size + 1).ToListAsync(cancellationToken);
            return new ActivityLogPage(rows.Take(size).Select(ActivityLogView.From).ToList(), rows.Count > size);
        }).RequireAuthorization(AdminAuth.AdminPolicy);
    }
}
