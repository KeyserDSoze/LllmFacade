using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using LllmFacade.Infrastructure.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Xunit;

[assembly: AssemblyFixture(typeof(LllmFacade.Browser.Tests.PlaywrightInstallation))]

namespace LllmFacade.Browser.Tests;

/// <summary>Downloads the Chromium build that matches Microsoft.Playwright (no-op when already installed).</summary>
public sealed class PlaywrightInstallation
{
    public PlaywrightInstallation()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException($"Installazione di Chromium per Playwright non riuscita (exit {exitCode}).");
    }
}

public sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>Routes every request of the browser context to an in-process imitation of chatgpt.com.</summary>
public sealed class FakeChatGpt : IBrowserContextConfigurator
{
    public static readonly string AccessToken = Jwt(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public bool LoggedOut { get; set; }
    /// <summary>"classic" (#prompt-textarea), "modern-button" or "modern-enter" (composer of the current web app).</summary>
    public string Layout { get; set; } = "classic";
    public HashSet<string> FullConversations { get; } = [];
    public string? FullMessageAfterFailedSend { get; set; }
    public string? ProjectRouteAfterConversationLoad { get; set; }
    public int ConversationStatus { get; set; } = 200;
    public string ConversationBody { get; set; } = ChatGptStreamParserTests.DeltaStream;
    public string ConversationStateJson { get; set; } = ChatGptResponsesTests.Conversation("assistant", "finished_successfully");

    public Task ConfigureAsync(IBrowserContext context) => context.RouteAsync("**/*", HandleAsync);

    public IEnumerable<RecordedRequest> To(string path) =>
        Requests.Where(r => r.PathAndQuery.Split('?')[0] == path);

    internal Task HandleForTestAsync(IRoute route) => HandleAsync(route);

    private async Task HandleAsync(IRoute route)
    {

        var request = route.Request;
        var url = new Uri(request.Url);
        if (url.Host != "chatgpt.com")
        {
            await route.AbortAsync();
            return;
        }

        Requests.Enqueue(new RecordedRequest(request.Method, url.PathAndQuery, await request.AllHeadersAsync(), request.PostData));
        var path = url.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (request.Method == "GET" && path.StartsWith("/c/", StringComparison.Ordinal) && FullConversations.Contains(path[3..]))
        {
            await HtmlAsync(route, FullPage);
        }
        else if (request.Method == "GET" && (path == "/" || path.StartsWith("/c/", StringComparison.Ordinal) || path.EndsWith("/project", StringComparison.Ordinal)))
        {
            await HtmlAsync(route, LoggedOut ? LoginPage : Layout switch
            {
                "modern-button" => ModernComposerPage(withSendButton: true, fullAfterSend: FullMessageAfterFailedSend,
                    projectConversationRoute: ProjectRouteAfterConversationLoad),
                "modern-enter" => ModernComposerPage(withSendButton: false, fullAfterSend: FullMessageAfterFailedSend,
                    projectConversationRoute: ProjectRouteAfterConversationLoad),
                "modern-hidden-legacy" => ModernComposerPage(withSendButton: true, hiddenLegacy: true, fullAfterSend: FullMessageAfterFailedSend,
                    projectConversationRoute: ProjectRouteAfterConversationLoad),
                "modern-url-only" => ModernComposerPage(withSendButton: true, urlOnly: true),
                _ => ComposerPage
            });
        }
        else if (request.Method == "GET" && path == "/api/auth/session")
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Headers = new Dictionary<string, string>
                {
                    ["set-cookie"] = "__Secure-next-auth.session-token=rotated-session; Path=/; Secure; HttpOnly; SameSite=Lax"
                },
                Body = LoggedOut ? "{}" : JsonSerializer.Serialize(new
                {
                    user = new { email = "utente@example.com" },
                    expires = "2027-01-01T00:00:00.000Z",
                    accessToken = AccessToken
                })
            });
        }
        else if (request.Method == "GET" && path == "/backend-api/gizmos/snorlax/sidebar")
        {
            await JsonAsync(route, query["cursor"] is null
                ? """{"items":[{"gizmo":{"gizmo":{"id":"g-p-alpha","display":{"name":"Alpha"}}}}],"cursor":"c2"}"""
                : """{"items":[{"gizmo":{"gizmo":{"id":"g-p-beta","display":{"name":"Beta"}}}}],"cursor":null}""");
        }
        else if (request.Method == "GET" && path == "/backend-api/gizmos/g-p-alpha/conversations")
        {
            await JsonAsync(route, """
                {"items":[
                  {"id":"conv-2","title":"Seconda","create_time":1700000500,"update_time":1700000900},
                  {"id":"conv-1","title":"Prima","create_time":1700000000,"update_time":1700000100}
                ],"cursor":null}
                """);
        }
        else if (request.Method == "GET" && path.StartsWith("/backend-api/conversation/", StringComparison.Ordinal))
        {
            await JsonAsync(route, ConversationStateJson);
        }
        else if (request.Method == "POST" && path == "/backend-api/f/conversation")
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = ConversationStatus,
                ContentType = ConversationStatus == 200 ? "text/event-stream" : "application/json",
                Body = ConversationBody
            });
        }
        else
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Body = "not found" });
        }
    }

    private static Task HtmlAsync(IRoute route, string html) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html; charset=utf-8", Body = html });

    private static Task JsonAsync(IRoute route, string json) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = json });

    public static string Jwt(DateTimeOffset expiresAt)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}") + ".firma";
    }

    /// <summary>Structure of the current ChatGPT composer: no #prompt-textarea, a contenteditable textbox, dictation/voice buttons always present.</summary>
    private static string ModernComposerPage(
        bool withSendButton, bool hiddenLegacy = false, string? fullAfterSend = null, bool urlOnly = false,
        string? projectConversationRoute = null) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>ChatGPT</title></head>
        <body>
          {{(hiddenLegacy ? "<div id=\"prompt-textarea\" contenteditable=\"true\" style=\"display:none\"></div>" : string.Empty)}}
          <main id="thread"></main>
          <form data-composer-placement="thread" data-chatgpt-composer="">
            <fieldset>
              <div data-composer-layout="single-line">
                <button type="button" aria-label="Add files and more">+</button>
                <div contenteditable="true" aria-multiline="true" dir="auto" role="textbox" tabindex="0" data-composer-markdown="" aria-label="Ask ChatGPT" id="editor">
                  <p data-empty-paragraph="true" data-placeholder="Ask ChatGPT"><br></p>
                </div>
                <span id="trailing">
                  <button type="button" data-dictation-button="idle" aria-label="Dictate">mic</button>
                  <button type="button" data-composer-voice-start="true" aria-label="Start Voice">voice</button>
                </span>
              </div>
            </fieldset>
          </form>
          <script>
            document.cookie = 'page-visit=1; path=/; secure';
            const projectConversationRoute = {{JsonSerializer.Serialize(projectConversationRoute)}};
            if (projectConversationRoute && /^\/c\//.test(location.pathname)) {
              history.replaceState(null, '', projectConversationRoute);
            }
            const editor = document.getElementById('editor');
            async function send() {
              const prompt = editor.innerText.trim();
              if (!prompt) return;
              if ({{(urlOnly ? "true" : "false")}}) {
                history.pushState(null, '', '/g/g-p-alpha/c/conv-url-only');
                return;
              }
              const match = location.pathname.match(/\/c\/([^/]+)$/);
              const body = { action: 'next', model: 'auto', messages: [{ id: 'm1', author: { role: 'user' }, content: { content_type: 'text', parts: [prompt] } }] };
              if (match) body.conversation_id = match[1];
              const response = await fetch('/backend-api/f/conversation', {
                method: 'POST',
                headers: { 'content-type': 'application/json', authorization: 'Bearer token-della-pagina' },
                body: JSON.stringify(body)
              });
              const reader = response.body.getReader();
              while (!(await reader.read()).done) { }
              const fullMessage = {{JsonSerializer.Serialize(fullAfterSend)}};
              if (!response.ok && fullMessage) {
                const alert = document.createElement('div');
                alert.setAttribute('role', 'alert');
                alert.textContent = fullMessage;
                document.body.appendChild(alert);
              }
              if (!match && response.ok) history.pushState(null, '', '/g/g-p-alpha/c/conv-from-url');
              const answer = document.createElement('div');
              answer.setAttribute('data-message-author-role', 'assistant');
              answer.textContent = 'risposta renderizzata';
              document.getElementById('thread').appendChild(answer);
            }
            if ({{(withSendButton ? "true" : "false")}}) {
              // Like the real page, the send button only exists once there is text.
              editor.addEventListener('input', () => {
                const existing = document.getElementById('send-now');
                if (editor.innerText.trim() && !existing) {
                  const button = document.createElement('button');
                  button.type = 'submit';
                  button.id = 'send-now';
                  button.setAttribute('aria-label', 'Send prompt');
                  button.addEventListener('click', event => { event.preventDefault(); send(); });
                  document.getElementById('trailing').appendChild(button);
                } else if (!editor.innerText.trim() && existing) existing.remove();
              });
            } else {
              editor.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); send(); }
              });
            }
          </script>
        </body></html>
        """;

    private const string FullPage = """
        <!doctype html><html><head><title>ChatGPT</title></head>
        <body><div class="banner">You've reached the maximum length for this conversation, but you can keep talking by starting a new chat.</div></body></html>
        """;

    private const string LoginPage = """
        <!doctype html><html><head><title>ChatGPT</title></head>
        <body><button data-testid="login-button">Accedi</button></body></html>
        """;

    private const string ComposerPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>ChatGPT</title></head>
        <body>
          <div id="prompt-textarea" contenteditable="true"></div>
          <button data-testid="send-button" id="send">Invia</button>
          <main id="thread"></main>
          <script>
            document.cookie = 'page-visit=1; path=/; secure';
            document.getElementById('send').addEventListener('click', async () => {
              const prompt = document.getElementById('prompt-textarea').innerText;
              const match = location.pathname.match(/^\/c\/(.+)$/);
              const body = { action: 'next', model: 'auto', messages: [{ id: 'm1', author: { role: 'user' }, content: { content_type: 'text', parts: [prompt] } }] };
              if (match) body.conversation_id = match[1];
              const response = await fetch('/backend-api/f/conversation', {
                method: 'POST',
                headers: { 'content-type': 'application/json', authorization: 'Bearer token-della-pagina' },
                body: JSON.stringify(body)
              });
              const reader = response.body.getReader();
              while (!(await reader.read()).done) { }
              if (!match) history.pushState(null, '', '/g/g-p-alpha/c/conv-from-url');
              const answer = document.createElement('div');
              answer.setAttribute('data-message-author-role', 'assistant');
              answer.textContent = 'risposta renderizzata';
              document.getElementById('thread').appendChild(answer);
            });
          </script>
        </body></html>
        """;
}

public sealed class BrowserHarness : IAsyncDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LllmFacadeTest/1.0";

    public BrowserHarness(Action<BrowserOptions>? configure = null)
    {
        var browserOptions = new BrowserOptions { NavigationTimeoutSeconds = 10, ConfirmationTimeoutSeconds = 10, CompletionTimeoutMinutes = 1 };
        configure?.Invoke(browserOptions);
        var web = Options.Create(new ChatGptWebOptions());
        Manager = new BrowserSessionManager(Options.Create(browserOptions), web, [Fake], NullLogger<BrowserSessionManager>.Instance);
        Client = new PlaywrightChatGptClient(Manager, web, Options.Create(browserOptions));
    }

    public FakeChatGpt Fake { get; } = new();
    public BrowserSessionManager Manager { get; }
    public PlaywrightChatGptClient Client { get; }

    public static ChatGptIdentity Identity(string? accessToken = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        StorageStates.FromCookies(CookieImport.Parse("__Secure-next-auth.session-token=original-session; oai-did=device-1", "chatgpt.com")),
        accessToken,
        UserAgent,
        "it-IT",
        "Europe/Rome",
        new Dictionary<string, string> { ["X-Test-Header"] = "valore-di-test" });

    public ValueTask DisposeAsync() => Manager.DisposeAsync();
}

public sealed class PlaywrightChatGptClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RefreshSession_BehavesLikeTheBrowserAndKeepsRotatedCookies()
    {
        await using var harness = new BrowserHarness();

        var snapshot = await harness.Client.RefreshSessionAsync(BrowserHarness.Identity(), Token);

        Assert.Equal(FakeChatGpt.AccessToken, snapshot.AccessToken);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), snapshot.AccessTokenExpiresAt);
        Assert.Equal("utente@example.com", snapshot.AccountEmail);

        var request = Assert.Single(harness.Fake.To("/api/auth/session"));
        Assert.Contains("__Secure-next-auth.session-token=original-session", request.Headers["cookie"]);
        Assert.Contains("oai-did=device-1", request.Headers["cookie"]);
        Assert.Equal(BrowserHarness.UserAgent, request.Headers["user-agent"]);
        Assert.Equal("valore-di-test", request.Headers["x-test-header"]);
        Assert.StartsWith("it-IT", request.Headers["accept-language"]);

        Assert.Contains("rotated-session", snapshot.StorageStateJson);
        Assert.DoesNotContain("original-session", snapshot.StorageStateJson);
        Assert.Contains("page-visit", snapshot.StorageStateJson);
    }

    [Fact]
    public async Task RefreshSession_ReportsExpiredSession()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.LoggedOut = true;

        var error = await Assert.ThrowsAsync<ChatGptException>(() => harness.Client.RefreshSessionAsync(BrowserHarness.Identity(), Token));

        Assert.Equal(ChatGptFailure.SessionExpired, error.Failure);
    }

    [Fact]
    public async Task ListProjects_FollowsCursorWithBearerTokenAndDeviceId()
    {
        await using var harness = new BrowserHarness();

        var projects = await harness.Client.ListProjectsAsync(BrowserHarness.Identity("token-admin"), Token);

        Assert.Equal([new ChatGptProject("g-p-alpha", "Alpha"), new ChatGptProject("g-p-beta", "Beta")], projects);
        var requests = harness.Fake.To("/backend-api/gizmos/snorlax/sidebar").ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("Bearer token-admin", r.Headers["authorization"]));
        Assert.All(requests, r => Assert.Equal("device-1", r.Headers["oai-device-id"]));
        Assert.Contains("cursor=c2", requests[1].PathAndQuery);
    }

    [Fact]
    public async Task ListProjects_RequiresAccessToken()
    {
        await using var harness = new BrowserHarness();

        var error = await Assert.ThrowsAsync<ChatGptException>(() => harness.Client.ListProjectsAsync(BrowserHarness.Identity(), Token));

        Assert.Equal(ChatGptFailure.SessionExpired, error.Failure);
    }

    [Fact]
    public async Task ConversationsAndState_AreReadThroughTheWebBackend()
    {
        await using var harness = new BrowserHarness();
        var identity = BrowserHarness.Identity("token-admin");

        var conversations = await harness.Client.ListProjectConversationsAsync(identity, "g-p-alpha", Token);
        var state = await harness.Client.GetConversationStateAsync(identity, "conv-2", Token);

        Assert.Equal(["conv-2", "conv-1"], conversations.Select(c => c.Id));
        Assert.Equal(ConversationPhase.Finished, state.Phase);
        Assert.Equal("conv-2", state.ConversationId);
        Assert.Single(harness.Fake.To("/backend-api/conversation/conv-2"));
    }

    [Fact]
    public async Task Complete_TypesPromptAppliesModelThinkingAndStreamsTheAnswer()
    {
        await using var harness = new BrowserHarness();
        const string prompt = "Prima riga\nseconda riga";

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in harness.Client.CompleteAsync(
            BrowserHarness.Identity("token-admin"), new CompletionCommand("gpt-5-thinking", prompt, "extended", TemporaryChat: true), Token))
            chunks.Add(chunk);

        Assert.Equal("Buongiorno!", string.Concat(chunks.Select(c => c.Text)));
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("conv-1", final.ConversationId);

        Assert.Contains(harness.Fake.Requests, r => r.Method == "GET" && r.PathAndQuery == "/?model=gpt-5-thinking&temporary-chat=true");
        var send = Assert.Single(harness.Fake.To("/backend-api/f/conversation"));
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal("gpt-5-thinking", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("extended", body.RootElement.GetProperty("thinking_effort").GetString());
        Assert.True(body.RootElement.GetProperty("history_and_training_disabled").GetBoolean());
        Assert.Equal(prompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("parts")[0].GetString());
        Assert.Equal("Bearer token-della-pagina", send.Headers["authorization"]);
        Assert.Equal(BrowserHarness.UserAgent, send.Headers["user-agent"]);
    }

    [Fact]
    public async Task Complete_MapsRateLimitToChatGptException()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.ConversationStatus = 429;
        harness.Fake.ConversationBody = """{"detail":"Troppe richieste"}""";

        var error = await Assert.ThrowsAsync<ChatGptException>(async () =>
        {
            await foreach (var _ in harness.Client.CompleteAsync(
                BrowserHarness.Identity("t"), new CompletionCommand("auto", "Ciao", null, TemporaryChat: false), Token))
            {
            }
        });

        Assert.Equal(ChatGptFailure.RateLimited, error.Failure);
        Assert.Contains("Troppe richieste", error.Message);
    }

    [Fact]
    public async Task Complete_ReportsLoginPageAsExpiredSession()
    {
        await using var harness = new BrowserHarness(options => options.NavigationTimeoutSeconds = 2);
        harness.Fake.LoggedOut = true;

        var error = await Assert.ThrowsAsync<ChatGptException>(async () =>
        {
            await foreach (var _ in harness.Client.CompleteAsync(
                BrowserHarness.Identity("t"), new CompletionCommand("auto", "Ciao", null, TemporaryChat: false), Token))
            {
            }
        });

        Assert.Equal(ChatGptFailure.SessionExpired, error.Failure);
    }

    [Fact]
    public async Task SubmitPrompt_TypesInTheProjectChatTabAndStreamsLiveText()
    {
        await using var harness = new BrowserHarness();
        var identity = BrowserHarness.Identity("token-admin");
        var live = new ConcurrentQueue<string>();

        var outcome = await harness.Client.SubmitPromptAsync(
            identity, new PromptTarget("project-1", "conv-1", "g-p-alpha"), "Continua il lavoro", new SyncProgress(live.Enqueue), Token);

        Assert.Equal(SubmissionOutcome.Confirmed, outcome);
        Assert.Contains(harness.Fake.Requests, r => r.Method == "GET" && r.PathAndQuery == "/c/conv-1");
        var send = Assert.Single(harness.Fake.To("/backend-api/f/conversation"));
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal("conv-1", body.RootElement.GetProperty("conversation_id").GetString());
        Assert.Equal("Continua il lavoro", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("parts")[0].GetString());
        Assert.Equal("auto", body.RootElement.GetProperty("model").GetString());

        for (var attempt = 0; attempt < 50 && string.Concat(live) != "Buongiorno!"; attempt++)
            await Task.Delay(100, Token);
        Assert.Equal("Buongiorno!", string.Concat(live));

        await harness.Client.CloseTabAsync(identity.EndpointId, "project-1");
    }

    [Fact]
    public async Task ChangedSettings_RecreateContextKeepingLiveCookies()
    {
        await using var harness = new BrowserHarness();
        var identity = BrowserHarness.Identity();
        await harness.Client.RefreshSessionAsync(identity, Token);

        await harness.Client.RefreshSessionAsync(identity with { UserAgent = "Mozilla/5.0 Altro" }, Token);

        var requests = harness.Fake.To("/api/auth/session").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("Mozilla/5.0 Altro", requests[1].Headers["user-agent"]);
        Assert.Contains("rotated-session", requests[1].Headers["cookie"]);
    }

    [Fact]
    public async Task AttachedBrowser_ReleasesOnlyLllmFacadePagesAndKeepsTheUserBrowserOpen()
    {
        var port = GetAvailableTcpPort();
        using var host = await Playwright.CreateAsync();
        await using var userBrowser = await host.Chromium.LaunchPersistentContextAsync(Path.GetTempPath() + "/lf-cdp-" + Guid.NewGuid(),
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = true,
                Args = [$"--remote-debugging-port={port}"]
            });
        await userBrowser.NewPageAsync();
        var userPage = userBrowser.Pages[0];
        await userPage.GotoAsync("data:text/html,<title>Utente</title><h1>Pagina utente</h1>");
        var fake = new FakeChatGpt();
        await userBrowser.RouteAsync("**/*", fake.HandleForTestAsync);

        var browserOptions = new BrowserOptions
        {
            ConnectOverCdpUrl = $"http://127.0.0.1:{port}",
            ConfirmationTimeoutSeconds = 5,
            NavigationTimeoutSeconds = 5
        };
        var webOptions = Options.Create(new ChatGptWebOptions());
        await using var manager = new BrowserSessionManager(
            Options.Create(browserOptions), webOptions, [], NullLogger<BrowserSessionManager>.Instance);
        var client = new PlaywrightChatGptClient(manager, webOptions, Options.Create(browserOptions));
        var identity = BrowserHarness.Identity("token-reale");

        await client.RefreshSessionAsync(identity, Token);
        var created = userBrowser.Pages.Count(page => page.Url.StartsWith("https://chatgpt.com", StringComparison.Ordinal));
        Assert.Equal(1, created);

        await client.ReleaseAsync(identity.EndpointId);

        Assert.False(userPage.IsClosed);
        Assert.Equal("Utente", await userPage.TitleAsync());
        Assert.Contains(userPage, userBrowser.Pages);
        Assert.All(userBrowser.Pages, page => Assert.False(page.Url.StartsWith("https://chatgpt.com", StringComparison.Ordinal)));
        Assert.True(userBrowser.Browser?.IsConnected ?? false);
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task SubmitPrompt_ReportsAFullConversationFromTheBannerWithoutSending()
    {
        await using var harness = new BrowserHarness(options => options.NavigationTimeoutSeconds = 5);
        harness.Fake.FullConversations.Add("conv-1");

        var error = await Assert.ThrowsAsync<ChatGptException>(() => harness.Client.SubmitPromptAsync(
            BrowserHarness.Identity("t"), new PromptTarget("project-1", "conv-1", "g-p-alpha"), "Continua", null, Token));

        Assert.Equal(ChatGptFailure.ConversationFull, error.Failure);
        Assert.False(error.PromptMayHaveBeenSent);
        Assert.Empty(harness.Fake.To("/backend-api/f/conversation"));
    }

    [Fact]
    public async Task SubmitPrompt_ReportsAFullConversationWhenTheSendIsRejected()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.ConversationStatus = 400;
        harness.Fake.ConversationBody = """{"detail":"You've reached the maximum length for this conversation"}""";

        var error = await Assert.ThrowsAsync<ChatGptException>(() => harness.Client.SubmitPromptAsync(
            BrowserHarness.Identity("t"), new PromptTarget("project-1", "conv-1", "g-p-alpha"), "Continua", null, Token));

        Assert.Equal(ChatGptFailure.ConversationFull, error.Failure);
        Assert.False(error.PromptMayHaveBeenSent);
    }

    [Fact]
    public async Task StartProjectChat_OpensANewChatInTheProjectAndReturnsItsId()
    {
        await using var harness = new BrowserHarness();
        var identity = BrowserHarness.Identity("t");

        var started = await harness.Client.StartProjectChatAsync(
            identity, new NewChatTarget("project-1", "g-p-alpha"), "Riparti da qui", null, Token);

        Assert.Equal(new StartedChat("conv-1", SubmissionOutcome.Confirmed), started);
        Assert.Contains(harness.Fake.Requests, r => r.Method == "GET" && r.PathAndQuery == "/g/g-p-alpha/project");
        var send = Assert.Single(harness.Fake.To("/backend-api/f/conversation"));
        using var body = JsonDocument.Parse(send.Body!);
        Assert.False(body.RootElement.TryGetProperty("conversation_id", out _));
        Assert.Equal("Riparti da qui", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("parts")[0].GetString());

        await harness.Client.CloseTabAsync(identity.EndpointId, "project-1");
    }

    [Fact]
    public async Task StartProjectChat_ReusesTheCanonicalProjectRouteWithSlug()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.Layout = "modern-button";
        harness.Fake.ProjectRouteAfterConversationLoad = "/g/g-p-alpha-project-name/c/conv-1";
        var identity = BrowserHarness.Identity("t");

        var existing = await harness.Client.SubmitPromptAsync(
            identity, new PromptTarget("project-route", "conv-1", "g-p-alpha"), "Continua", null, Token);
        Assert.Equal(SubmissionOutcome.Confirmed, existing);

        await harness.Client.StartProjectChatAsync(
            identity, new NewChatTarget("project-route", "g-p-alpha"), "Riparti da qui", null, Token);

        Assert.Contains(harness.Fake.Requests,
            request => request.Method == "GET" && request.PathAndQuery == "/g/g-p-alpha-project-name/project");
    }

    [Fact]
    public async Task StartProjectChat_FallsBackToTheAddressBarWhenTheStreamHasNoId()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.ConversationBody = "data: [DONE]\n\n";

        var started = await harness.Client.StartProjectChatAsync(
            BrowserHarness.Identity("t"), new NewChatTarget("project-2", "g-p-alpha"), "Riparti da qui", null, Token);

        Assert.Equal("conv-from-url", started.ConversationId);
    }

    [Fact]
    public async Task StartProjectChat_ConfirmsWhenTheConversationUrlAppearsEvenIfTheStreamStartWasMissed()
    {
        await using var harness = new BrowserHarness(options => options.ConfirmationTimeoutSeconds = 1);
        harness.Fake.Layout = "modern-url-only";

        var started = await harness.Client.StartProjectChatAsync(
            BrowserHarness.Identity("t"), new NewChatTarget("project-url-only", "g-p-alpha"), "Riparti da qui", null, Token);

        Assert.Equal(new StartedChat("conv-url-only", SubmissionOutcome.Confirmed), started);
    }

    [Fact]
    public async Task StartProjectChat_RejectsProjectIdsThatAreNotChatGptProjects()
    {
        await using var harness = new BrowserHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.StartProjectChatAsync(
            BrowserHarness.Identity("t"), new NewChatTarget("project-3", "../admin"), "x", null, Token));
    }

    [Theory]
    [InlineData("modern-button")]
    [InlineData("modern-enter")]
    [InlineData("modern-hidden-legacy")]
    public async Task SubmitPrompt_WorksWithTheCurrentComposerLayout(string layout)
    {
        await using var harness = new BrowserHarness(options => options.NavigationTimeoutSeconds = 10);
        harness.Fake.Layout = layout;
        const string prompt = "andiamo avanti con gli sviluppi\ncontrolla le issue";

        var outcome = await harness.Client.SubmitPromptAsync(
            BrowserHarness.Identity("t"), new PromptTarget("project-modern", "conv-1", "g-p-alpha"), prompt, null, Token);

        Assert.Equal(SubmissionOutcome.Confirmed, outcome);
        var send = Assert.Single(harness.Fake.To("/backend-api/f/conversation"));
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal(prompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("parts")[0].GetString());
        Assert.Equal("conv-1", body.RootElement.GetProperty("conversation_id").GetString());
    }

    [Theory]
    [InlineData("modern-button")]
    [InlineData("modern-enter")]
    [InlineData("modern-hidden-legacy")]
    public async Task StartProjectChat_WorksWithTheCurrentComposerLayout(string layout)
    {
        await using var harness = new BrowserHarness();
        harness.Fake.Layout = layout;

        var started = await harness.Client.StartProjectChatAsync(
            BrowserHarness.Identity("t"), new NewChatTarget("project-modern-new", "g-p-alpha"), "Riparti da qui", null, Token);

        Assert.Equal("conv-1", started.ConversationId);
    }

    [Fact]
    public async Task SubmitPrompt_ClassifiesAVisibleLengthLimitAfterGenericHttp500AsConversationFull()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.Layout = "modern-button";
        harness.Fake.ConversationStatus = 500;
        harness.Fake.ConversationBody = """{"detail":"Request timeout"}""";
        harness.Fake.FullMessageAfterFailedSend = "Context length exceeded. Start a new chat to continue.";

        var error = await Assert.ThrowsAsync<ChatGptException>(() => harness.Client.SubmitPromptAsync(
            BrowserHarness.Identity("t"), new PromptTarget("project-modern-full", "conv-1", "g-p-alpha"), "Continua", null, Token));

        Assert.Equal(ChatGptFailure.ConversationFull, error.Failure);
        Assert.False(error.PromptMayHaveBeenSent);
    }

    [Fact]
    public async Task Complete_WorksWithTheCurrentComposerLayout()
    {
        await using var harness = new BrowserHarness();
        harness.Fake.Layout = "modern-button";

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in harness.Client.CompleteAsync(
            BrowserHarness.Identity("t"), new CompletionCommand("auto", "Ciao", null, TemporaryChat: false), Token))
            chunks.Add(chunk);

        Assert.Equal("Buongiorno!", string.Concat(chunks.Select(c => c.Text)));
    }
}
