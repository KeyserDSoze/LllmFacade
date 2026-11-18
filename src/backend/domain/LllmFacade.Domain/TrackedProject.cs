namespace LllmFacade.Domain;

/// <summary>ChatGPT project marked by the admin, with its "carry on the work" automation.</summary>
public sealed class TrackedProject
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public ChatGptEndpoint? Endpoint { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string ContinuationPrompt { get; set; } = string.Empty;
    /// <summary>Null means the most recently updated chat of the project.</summary>
    public string? TargetConversationId { get; set; }
    public bool AutopilotEnabled { get; set; }
    public int? MaxContinuations { get; set; }
    public int ContinuationsSent { get; set; }
    public AutopilotStatus Status { get; set; } = AutopilotStatus.Idle;
    public string? CurrentConversationId { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public DateTimeOffset? LastContinuationAt { get; set; }
    /// <summary>Conversation node after which the last continuation was sent, until the chat moves past it.</summary>
    public string? PendingAfterNodeId { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public enum AutopilotStatus
{
    Idle,
    Monitoring,
    Generating,
    Submitting,
    WaitingRetry,
    Completed,
    Stopped
}
