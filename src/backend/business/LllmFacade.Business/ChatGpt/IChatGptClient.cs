using System.Text.RegularExpressions;

namespace LllmFacade.Business.ChatGpt;

/// <summary>Browser identity of one endpoint, with secrets already decrypted for the adapter.</summary>
public sealed record ChatGptIdentity(
    Guid EndpointId,
    Guid CredentialsStamp,
    string StorageStateJson,
    string? AccessToken,
    string UserAgent,
    string Locale,
    string TimeZone,
    IReadOnlyDictionary<string, string> ExtraHeaders);

public sealed record SessionSnapshot(
    string? AccessToken, DateTimeOffset? AccessTokenExpiresAt, string? AccountEmail, string StorageStateJson);

public sealed record ChatGptProject(string Id, string Name);

public sealed record ChatGptConversation(string Id, string Title, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

public enum ConversationPhase
{
    Generating,
    Finished
}

public sealed record ConversationState(
    string ConversationId, string? CurrentNodeId, ConversationPhase Phase, string? LastAssistantText, DateTimeOffset? UpdatedAt);

public sealed record CompletionCommand(string UpstreamModel, string Prompt, string? ThinkingEffort, bool TemporaryChat);

public sealed record CompletionChunk(string Text, bool IsFinal = false, string? ConversationId = null);

/// <summary>A browser tab kept open for one tracked project.</summary>
public sealed record PromptTarget(string TabKey, string ConversationId, string? ProjectId);

/// <summary>Where to open a brand new chat inside a project.</summary>
public sealed record NewChatTarget(string TabKey, string ProjectId);

public sealed record StartedChat(string ConversationId, SubmissionOutcome Outcome);

public enum SubmissionOutcome
{
    Confirmed,
    Unconfirmed
}

/// <summary>Port towards the ChatGPT web application, driven like a real browser.</summary>
public interface IChatGptClient
{
    Task<SessionSnapshot> RefreshSessionAsync(ChatGptIdentity identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatGptProject>> ListProjectsAsync(ChatGptIdentity identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatGptConversation>> ListProjectConversationsAsync(
        ChatGptIdentity identity, string projectId, CancellationToken cancellationToken);

    Task<ConversationState> GetConversationStateAsync(
        ChatGptIdentity identity, string conversationId, CancellationToken cancellationToken);

    /// <summary>Opens or reuses the tracked conversation tab and leaves it open for future polling.</summary>
    Task EnsureConversationTabAsync(
        ChatGptIdentity identity, PromptTarget target, CancellationToken cancellationToken);

    /// <summary>Types the prompt in the conversation tab; the tab keeps streaming into <paramref name="liveText"/>.</summary>
    Task<SubmissionOutcome> SubmitPromptAsync(
        ChatGptIdentity identity, PromptTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken);

    /// <summary>Opens a new chat in the project, sends the prompt and returns the id of the created conversation.</summary>
    Task<StartedChat> StartProjectChatAsync(
        ChatGptIdentity identity, NewChatTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken);

    IAsyncEnumerable<CompletionChunk> CompleteAsync(
        ChatGptIdentity identity, CompletionCommand command, CancellationToken cancellationToken);

    /// <summary>Current cookies of the live browser context, null when no context is open.</summary>
    Task<string?> CaptureStorageStateAsync(Guid endpointId, CancellationToken cancellationToken);

    Task CloseTabAsync(Guid endpointId, string tabKey);

    Task ReleaseAsync(Guid endpointId);
}

public enum ChatGptFailure
{
    SessionExpired,
    Blocked,
    RateLimited,
    Busy,
    Upstream,
    Interface,
    Timeout,
    /// <summary>The conversation reached ChatGPT's maximum length: only a new chat can continue the work.</summary>
    ConversationFull
}

public sealed class ChatGptException(
    ChatGptFailure failure, string message, bool promptMayHaveBeenSent = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ChatGptFailure Failure { get; } = failure;
    public bool PromptMayHaveBeenSent { get; } = promptMayHaveBeenSent;
}

/// <summary>Identifiers are interpolated into ChatGPT URLs: accept only their known alphabet.</summary>
public static partial class ChatGptIds
{
    public static bool IsProjectId(string? id) => id is not null && ProjectIdPattern().IsMatch(id);

    public static bool IsConversationId(string? id) => id is not null && ConversationIdPattern().IsMatch(id);

    public static string EnsureProjectId(string? id) =>
        IsProjectId(id) ? id! : throw new ArgumentException("ID project ChatGPT non valido.", nameof(id));

    public static string EnsureConversationId(string? id) =>
        IsConversationId(id) ? id! : throw new ArgumentException("ID chat ChatGPT non valido.", nameof(id));

    [GeneratedRegex("^g-p-[A-Za-z0-9_-]{1,124}$")]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex ConversationIdPattern();
}
