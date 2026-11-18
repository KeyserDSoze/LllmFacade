using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace LllmFacade.Api.Tests;

/// <summary>Real API and PostgreSQL (Testcontainers); only the ChatGPT browser port is replaced.</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public FakeChatGptClient ChatGpt { get; } = new();

    protected Dictionary<string, string> Settings { get; } = new()
    {
        ["Workers:Enabled"] = "false",
        ["Admin:LoginAttemptsPerMinute"] = "1000"
    };

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatGptClient>();
            services.AddSingleton<IChatGptClient>(ChatGpt);
        });
    }

    public async Task WithDbAsync(Func<LllmFacadeDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<LllmFacadeDbContext>());
    }

    public async Task WithServiceAsync<T>(Func<T, Task> action) where T : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class RateLimitedApiFactory : ApiFactory
{
    public RateLimitedApiFactory() => Settings["Admin:LoginAttemptsPerMinute"] = "2";
}

public static class Api
{
    public const string AdminPassword = "Password-Sicura-2026";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static HttpClient Anonymous(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "LllmFacade");
        return client;
    }

    /// <summary>Logged-in admin; changes the initial admin/admin password the first time.</summary>
    public static async Task<HttpClient> AdminAsync(ApiFactory factory)
    {
        var client = Anonymous(factory);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AdminPassword }, Token);
        if (login.IsSuccessStatusCode)
            return client;

        (await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin" }, Token)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "admin", newPassword = AdminPassword }, Token)).EnsureSuccessStatusCode();
        return client;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;

    public static object EndpointRequest(string cookies = "__Secure-next-auth.session-token=segreto-sessione; oai-did=device-1", string? accessToken = null) => new
    {
        name = "Account principale",
        models = new[]
        {
            new { name = "chatgpt-thinking", upstreamModel = "gpt-5-thinking", defaultReasoningEffort = "medium" },
            new { name = "chatgpt-auto", upstreamModel = "auto", defaultReasoningEffort = (string?)null }
        },
        thinkingEfforts = new Dictionary<string, string> { ["medium"] = "standard", ["high"] = "extended" },
        useTemporaryChat = true,
        userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Test",
        locale = "it-IT",
        timeZone = "Europe/Rome",
        extraHeaders = new Dictionary<string, string> { ["Accept-Language"] = "it-IT,it;q=0.9" },
        cookies,
        accessToken = accessToken ?? Jwt(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        refreshIntervalMinutes = 30,
        isEnabled = true
    };

    public static async Task<(Guid Id, string ApiKey)> CreateEndpointAsync(HttpClient admin)
    {
        var created = await (await admin.PostAsJsonAsync("/api/endpoints", EndpointRequest(), Token)).JsonAsync();
        return (created.GetProperty("endpoint").GetProperty("id").GetGuid(), created.GetProperty("apiKey").GetString()!);
    }

    public static string Jwt(DateTimeOffset expiresAt)
    {
        static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}") + ".firma";
    }
}

/// <summary>Scriptable stand-in for the browser adapter that records what the API asked for.</summary>
public sealed class FakeChatGptClient : IChatGptClient
{
    public Func<ChatGptIdentity, SessionSnapshot> Refresh { get; set; } = _ => new SessionSnapshot(
        Api.Jwt(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        null,
        "utente@example.com",
        StorageStates.FromCookies(CookieImport.Parse("a=1; b=2; c=3", "chatgpt.com")));

    public List<ChatGptProject> Projects { get; set; } = [new("g-p-alpha", "Alpha"), new("g-p-beta", "Beta")];

    public List<ChatGptConversation> Conversations { get; set; } =
    [
        new("conv-new", "Più recente", new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.Zero)),
        new("conv-old", "Più vecchia", new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero))
    ];

    public Func<string, ConversationState> State { get; set; } =
        id => new ConversationState(id, "n1", ConversationPhase.Finished, "Risposta finita", DateTimeOffset.UtcNow);

    public Func<CompletionCommand, IReadOnlyList<CompletionChunk>> Completion { get; set; } =
        _ => [new("Ciao "), new("mondo"), new(string.Empty, IsFinal: true, ConversationId: "conv-tmp")];

    public Exception? Failure { get; set; }
    public SubmissionOutcome Outcome { get; set; } = SubmissionOutcome.Confirmed;
    public string? LiveStorageState { get; set; }
    public ConcurrentQueue<ChatGptIdentity> Identities { get; } = new();
    public ConcurrentQueue<CompletionCommand> Commands { get; } = new();
    public ConcurrentQueue<(PromptTarget Target, string Prompt)> Submissions { get; } = new();
    public ConcurrentQueue<Guid> Released { get; } = new();
    public ConcurrentQueue<string> ClosedTabs { get; } = new();

    public Task<SessionSnapshot> RefreshSessionAsync(ChatGptIdentity identity, CancellationToken cancellationToken) =>
        Run(identity, () => Refresh(identity));

    public Task<IReadOnlyList<ChatGptProject>> ListProjectsAsync(ChatGptIdentity identity, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ChatGptProject>>(identity, () => Projects);

    public Task<IReadOnlyList<ChatGptConversation>> ListProjectConversationsAsync(
        ChatGptIdentity identity, string projectId, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ChatGptConversation>>(identity, () => Conversations);

    public Task<ConversationState> GetConversationStateAsync(ChatGptIdentity identity, string conversationId, CancellationToken cancellationToken) =>
        Run(identity, () => State(conversationId));

    public Task<SubmissionOutcome> SubmitPromptAsync(
        ChatGptIdentity identity, PromptTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken) =>
        Run(identity, () =>
        {
            Submissions.Enqueue((target, prompt));
            liveText?.Report("Sto lavorando");
            return Outcome;
        });

    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(
        ChatGptIdentity identity, CompletionCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Identities.Enqueue(identity);
        Commands.Enqueue(command);
        if (Failure is not null)
            throw Failure;
        foreach (var chunk in Completion(command))
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    public Task<string?> CaptureStorageStateAsync(Guid endpointId, CancellationToken cancellationToken) => Task.FromResult(LiveStorageState);

    public Task CloseTabAsync(Guid endpointId, string tabKey)
    {
        ClosedTabs.Enqueue(tabKey);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid endpointId)
    {
        Released.Enqueue(endpointId);
        return Task.CompletedTask;
    }

    private Task<T> Run<T>(ChatGptIdentity identity, Func<T> action)
    {
        Identities.Enqueue(identity);
        return Failure is null ? Task.FromResult(action()) : Task.FromException<T>(Failure);
    }
}
