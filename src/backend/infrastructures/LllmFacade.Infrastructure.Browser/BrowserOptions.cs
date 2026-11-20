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
    public int MaxTabsPerEndpoint { get; set; } = 24;
    public int QueueTimeoutSeconds { get; set; } = 120;
    /// <summary>Deterministic settle time after the latest page load and before the composer receives a prompt.</summary>
    public int PageLoadToPromptDelayMilliseconds { get; set; } = 120_000;
    /// <summary>Deterministic pause after the exact prompt reaches the composer and before it is submitted.</summary>
    public int PromptToSendDelayMilliseconds { get; set; } = 8_000;
    /// <summary>Minimum deterministic spacing between prompt sends for the same endpoint.</summary>
    public int MinimumPromptSendIntervalMilliseconds { get; set; } = 60_000;
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
        "maximum length for this conversation|reached the maximum length|conversation is too long|" +
        "context length exceeded|conversation length exceeded|exceeded .{0,30}(length|limit)|" +
        "(length|limit) .{0,30}exceeded|start(ing)? a new chat|lunghezza massima|limite.{0,20}(contesto|conversazione)|conversazione .{0,20}troppo lunga";
    public string TemporaryChatQuery { get; set; } = "temporary-chat=true";
    public string ConversationStreamPattern { get; set; } = "^/backend-api/(f/)?conversation$";
    // The current web app has no #prompt-textarea: the composer is a contenteditable textbox inside form[data-chatgpt-composer].
    public string PromptSelector { get; set; } =
        "form[data-chatgpt-composer][data-composer-placement='home'] [data-composer-markdown][contenteditable='true'][role='textbox']:visible, " +
        "#prompt-textarea:visible, form[data-chatgpt-composer] [contenteditable='true'][role='textbox']:visible, " +
        "[data-composer-markdown][contenteditable='true']:visible, [contenteditable='true'][role='textbox'][aria-label='Ask ChatGPT']:visible, " +
        "[contenteditable='true'][role='textbox'][aria-label^='New chat in ']:visible";
    public string SendButtonSelector { get; set; } =
        "form[data-chatgpt-composer] button[type='submit']:visible, [data-testid='send-button']:visible, #composer-submit-button:visible, " +
        "button[data-composer-send]:visible, button[aria-label='Send']:visible, button[aria-label='Send prompt']:visible, " +
        "button[aria-label='Send message']:visible, button[aria-label='Invia prompt']:visible, button[aria-label='Invia messaggio']:visible";
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
