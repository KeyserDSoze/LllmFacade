namespace LllmFacade.Domain;

/// <summary>OpenAI-compatible entry point bound to one ChatGPT browser identity.</summary>
public sealed class ChatGptEndpoint
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ModelMapping> Models { get; set; } = [];
    /// <summary>OpenAI reasoning_effort value mapped to the ChatGPT thinking value.</summary>
    public Dictionary<string, string> ThinkingEfforts { get; set; } = [];
    public bool UseTemporaryChat { get; set; } = true;
    public string UserAgent { get; set; } = string.Empty;
    public string Locale { get; set; } = "it-IT";
    public string TimeZone { get; set; } = "Europe/Rome";
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];
    public string ApiKeyHash { get; set; } = string.Empty;
    public string ApiKeyPrefix { get; set; } = string.Empty;
    public string? ProtectedAccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    /// <summary>Encrypted Playwright storage state: cookies and localStorage of the browser profile.</summary>
    public string ProtectedStorageState { get; set; } = string.Empty;
    public string StorageStateHash { get; set; } = string.Empty;
    public int CookieCount { get; set; }
    public string? AccountEmail { get; set; }
    /// <summary>Changes whenever the admin replaces browser identity data, forcing a new browser context.</summary>
    public Guid CredentialsStamp { get; set; }
    public int RefreshIntervalMinutes { get; set; } = 30;
    public DateTimeOffset? LastRefreshAt { get; set; }
    public DateTimeOffset? LastRefreshAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed record ModelMapping(string Name, string UpstreamModel, string? DefaultReasoningEffort);
