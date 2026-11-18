using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LllmFacade.Business.Sessions;
using LllmFacade.Domain;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace LllmFacade.Api.Services;

public sealed record ApiError(string Error, IReadOnlyList<string>? Errors = null);

public sealed record SessionView(string UserName, bool MustChangePassword);

public sealed record EndpointView(
    Guid Id,
    string Name,
    IReadOnlyList<ModelMapping> Models,
    IReadOnlyDictionary<string, string> ThinkingEfforts,
    bool UseTemporaryChat,
    string UserAgent,
    string Locale,
    string TimeZone,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string ApiKeyPrefix,
    bool HasAccessToken,
    DateTimeOffset? AccessTokenExpiresAt,
    int CookieCount,
    string? AccountEmail,
    int RefreshIntervalMinutes,
    DateTimeOffset? LastRefreshAt,
    DateTimeOffset? LastRefreshAttemptAt,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? NextRefreshAt,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static EndpointView From(ChatGptEndpoint e) => new(
        e.Id, e.Name, e.Models, e.ThinkingEfforts, e.UseTemporaryChat, e.UserAgent, e.Locale, e.TimeZone, e.ExtraHeaders,
        e.ApiKeyPrefix, e.ProtectedAccessToken is not null, e.AccessTokenExpiresAt, e.CookieCount, e.AccountEmail,
        e.RefreshIntervalMinutes, e.LastRefreshAt, e.LastRefreshAttemptAt, e.LastError, e.LastErrorAt, NextRefresh(e), e.IsEnabled,
        e.CreatedAt, e.UpdatedAt);

    public static DateTimeOffset? NextRefresh(ChatGptEndpoint e) => e.IsEnabled
        ? SessionRefreshPolicy.NextDue(
            TimeSpan.FromMinutes(e.RefreshIntervalMinutes), e.LastRefreshAt, e.LastRefreshAttemptAt, e.AccessTokenExpiresAt, DateTimeOffset.UtcNow)
        : null;
}

public sealed record TrackedProjectView(
    Guid Id,
    Guid EndpointId,
    string EndpointName,
    string ProjectId,
    string Name,
    bool IsActive,
    string ContinuationPrompt,
    string? TargetConversationId,
    bool AutopilotEnabled,
    int? MaxContinuations,
    int ContinuationsSent,
    AutopilotStatus Status,
    string? CurrentConversationId,
    DateTimeOffset? LastCheckAt,
    DateTimeOffset? NextCheckAt,
    DateTimeOffset? LastContinuationAt,
    int ConsecutiveFailures,
    string? LastError,
    string NewChatPrompt,
    int ChatsCreated,
    DateTimeOffset UpdatedAt)
{
    public static TrackedProjectView From(TrackedProject p) => new(
        p.Id, p.EndpointId, p.Endpoint?.Name ?? string.Empty, p.ProjectId, p.Name, p.IsActive, p.ContinuationPrompt,
        p.TargetConversationId, p.AutopilotEnabled, p.MaxContinuations, p.ContinuationsSent, p.Status,
        p.CurrentConversationId, p.LastCheckAt, p.NextCheckAt, p.LastContinuationAt, p.ConsecutiveFailures,
        p.LastError, p.NewChatPrompt, p.ChatsCreated, p.UpdatedAt);
}

public sealed record ProjectEvent(string Type, string Json);

/// <summary>
/// In-memory fan-out of autopilot events to the admin UI. The live answer text is kept only in memory
/// (never stored or logged), so it is lost on restart by design.
/// </summary>
public sealed class ProjectEventHub(IOptions<JsonOptions> jsonOptions)
{
    private const int MaxLiveText = 4000;
    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ProjectEvent>>> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, Channel<ProjectEvent>> _everyone = new();
    private readonly ConcurrentDictionary<Guid, string> _liveText = new();

    /// <summary>One stream for all projects: browsers allow few concurrent SSE connections per origin.</summary>
    public ProjectSubscription SubscribeAll()
    {
        var channel = Channel.CreateBounded<ProjectEvent>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _everyone[id] = channel;
        return new ProjectSubscription(channel.Reader, () => _everyone.TryRemove(id, out _));
    }

    public ProjectSubscription Subscribe(Guid projectId)
    {
        var channel = Channel.CreateBounded<ProjectEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(projectId, _ => new ConcurrentDictionary<Guid, Channel<ProjectEvent>>())[id] = channel;
        return new ProjectSubscription(channel.Reader, () =>
        {
            if (_subscribers.TryGetValue(projectId, out var channels))
                channels.TryRemove(id, out _);
        });
    }

    public void PublishStatus(TrackedProject project) => Publish(project.Id, "status", TrackedProjectView.From(project));

    public void AppendLiveText(Guid projectId, string delta)
    {
        _liveText.AddOrUpdate(projectId, Tail(delta), (_, current) => Tail(current + delta));
        Publish(projectId, "live", new { projectId, text = delta, reset = false });
    }

    public void SetLiveText(Guid projectId, string text)
    {
        _liveText[projectId] = Tail(text);
        Publish(projectId, "live", new { projectId, text = Tail(text), reset = true });
    }

    public string? GetLiveText(Guid projectId) => _liveText.TryGetValue(projectId, out var text) ? text : null;

    public string Serialize(object payload) => JsonSerializer.Serialize(payload, _json);

    private void Publish(Guid projectId, string type, object payload)
    {
        var hasProjectListeners = _subscribers.TryGetValue(projectId, out var channels) && !channels.IsEmpty;
        if (!hasProjectListeners && _everyone.IsEmpty)
            return;
        var message = new ProjectEvent(type, Serialize(payload));
        if (hasProjectListeners)
            foreach (var channel in channels!.Values)
                channel.Writer.TryWrite(message);
        foreach (var channel in _everyone.Values)
            channel.Writer.TryWrite(message);
    }

    private static string Tail(string text) => text.Length > MaxLiveText ? text[^MaxLiveText..] : text;
}

public sealed class ProjectSubscription(ChannelReader<ProjectEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<ProjectEvent> Reader { get; } = reader;

    public void Dispose() => unsubscribe();
}

public sealed class LiveTextProgress(ProjectEventHub hub, Guid projectId) : IProgress<string>
{
    public void Report(string value) => hub.AppendLiveText(projectId, value);
}

internal static class ServerSentEvents
{
    public static void Prepare(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public static async Task WriteAsync(HttpResponse response, string? eventName, string data, CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        if (eventName is not null)
            message.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Split('\n'))
            message.Append("data: ").Append(line).Append('\n');
        message.Append('\n');
        await response.WriteAsync(message.ToString(), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public static async Task KeepAliveAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        await response.WriteAsync(": keep-alive\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
