namespace LllmFacade.Domain;

public enum ActivityLevel
{
    Info,
    Warning,
    Error
}

public static class ActivityCategories
{
    public const string Auth = "accesso";
    public const string Endpoint = "endpoint";
    public const string Session = "sessione";
    public const string Project = "project";
    public const string Autopilot = "autopilot";
    public const string OpenAi = "openai";

    public static readonly IReadOnlyList<string> All = [Auth, Endpoint, Session, Project, Autopilot, OpenAi];
}

/// <summary>Audit trail shown in the admin UI. Never contains cookies, tokens, keys or answer text.</summary>
public sealed class ActivityLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public ActivityLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Guid? EndpointId { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>Name of the endpoint or project at the time, kept after deletion.</summary>
    public string? Subject { get; set; }
}
