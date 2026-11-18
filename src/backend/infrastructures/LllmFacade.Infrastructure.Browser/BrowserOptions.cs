using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using LllmFacade.Business.ChatGpt;

namespace LllmFacade.Infrastructure.Browser;

public sealed class BrowserOptions
{
    public bool Headless { get; set; } = true;
    /// <summary>Connects to a browser the user opened and approved instead of launching a separate headless browser.</summary>
    public string? ConnectOverCdpUrl { get; set; }
    /// <summary>Playwright channel, e.g. "chromium" for the new headless mode of the full browser.</summary>
    public string? Channel { get; set; }
    public string? ExecutablePath { get; set; }
    public List<string> Args { get; set; } = [];
    public int MaxPagesPerEndpoint { get; set; } = 3;
    public int MaxTabsPerEndpoint { get; set; } = 8;
    public int QueueTimeoutSeconds { get; set; } = 120;
    public int NavigationTimeoutSeconds { get; set; } = 60;
    public int ConfirmationTimeoutSeconds { get; set; } = 60;
    public int CompletionTimeoutMinutes { get; set; } = 30;
    public int ViewportWidth { get; set; } = 1366;
    public int ViewportHeight { get; set; } = 900;
}

/// <summary>ChatGPT web internals are not a public API: every path and selector can be changed in configuration.</summary>
public sealed class ChatGptWebOptions
{
    public string BaseUrl { get; set; } = "https://chatgpt.com";
    public string HomePath { get; set; } = "/";
    public string SessionPath { get; set; } = "/api/auth/session";
    public string ProjectsPath { get; set; } = "/backend-api/gizmos/snorlax/sidebar?owned_only=true&conversations_per_gizmo=0";
    public string ProjectConversationsPath { get; set; } = "/backend-api/gizmos/{projectId}/conversations?cursor={cursor}";
    public string ConversationApiPath { get; set; } = "/backend-api/conversation/{conversationId}";
    public string ConversationPagePath { get; set; } = "/c/{conversationId}";
    public string NewChatPath { get; set; } = "/?model={model}";
    public string ProjectPagePath { get; set; } = "/g/{projectId}/project";
    /// <summary>Text (regex, case-insensitive) ChatGPT shows or returns when a conversation reached its maximum length.</summary>
    public string ConversationFullPattern { get; set; } =
        "maximum length for this conversation|reached the maximum length|conversation is too long|lunghezza massima|conversazione .{0,20}troppo lunga";
    public string TemporaryChatQuery { get; set; } = "temporary-chat=true";
    public string ConversationStreamPattern { get; set; } = "^/backend-api/(f/)?conversation$";
    // The current web app has no #prompt-textarea: the composer is a contenteditable textbox inside form[data-chatgpt-composer].
    public string PromptSelector { get; set; } =
        "#prompt-textarea, form[data-chatgpt-composer] [contenteditable='true'][role='textbox'], [data-composer-markdown][contenteditable='true']";
    public string SendButtonSelector { get; set; } =
        "[data-testid='send-button'], #composer-submit-button, form[data-chatgpt-composer] button[type='submit'], " +
        "button[aria-label='Send prompt'], button[aria-label='Send message'], button[aria-label='Invia prompt'], button[aria-label='Invia messaggio']";
    public string StopButtonSelector { get; set; } = "[data-testid='stop-button']";
    public string AssistantMessageSelector { get; set; } = "[data-message-author-role='assistant']";
    public string LoginSelector { get; set; } = "[data-testid='login-button']";
    public string ThinkingEffortField { get; set; } = "thinking_effort";
    public int MaxListPages { get; set; } = 20;

    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/");
    public string Host => BaseUri.Host;
    public string Origin => BaseUri.GetLeftPart(UriPartial.Authority);

    public string Url(string pathAndQuery) => Origin + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
}

/// <summary>Extension point applied to every new browser context (used by tests to route a fake ChatGPT).</summary>
public interface IBrowserContextConfigurator
{
    Task ConfigureAsync(IBrowserContext context);
}

public static class BrowserServiceCollectionExtensions
{
    public static IServiceCollection AddChatGptBrowser(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BrowserOptions>(configuration.GetSection("Browser"));
        services.Configure<ChatGptWebOptions>(configuration.GetSection("ChatGpt"));
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton<IChatGptClient, PlaywrightChatGptClient>();
        return services;
    }
}
