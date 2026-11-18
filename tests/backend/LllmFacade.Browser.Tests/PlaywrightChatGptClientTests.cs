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
    public int ConversationStatus { get; set; } = 200;
    public string ConversationBody { get; set; } = ChatGptStreamParserTests.DeltaStream;
    public string ConversationStateJson { get; set; } = ChatGptResponsesTests.Conversation("assistant", "finished_successfully");

    public Task ConfigureAsync(IBrowserContext context) => context.RouteAsync("**/*", HandleAsync);

    public IEnumerable<RecordedRequest> To(string path) =>
        Requests.Where(r => r.PathAndQuery.Split('?')[0] == path);

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
        if (request.Method == "GET" && (path == "/" || path.StartsWith("/c/", StringComparison.Ordinal)))
        {
            await HtmlAsync(route, LoggedOut ? LoginPage : ComposerPage);
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

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
