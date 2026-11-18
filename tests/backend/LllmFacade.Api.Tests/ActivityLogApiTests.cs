using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class ActivityLogApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<List<JsonElement>> LogsAsync(HttpClient admin, string query = "")
    {
        var page = await (await admin.GetAsync("/api/logs" + query, Token)).JsonAsync();
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    private static string Message(JsonElement entry) => entry.GetProperty("message").GetString()!;

    private async Task<(HttpClient Admin, Guid EndpointId, TrackedProjectView Project)> SetupAsync(string projectId = "g-p-alpha")
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        var project = await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId, name = "Alpha" }, Token))
            .ReadAsync<TrackedProjectView>();
        return (admin, endpointId, project);
    }

    [Fact]
    public async Task Logs_RequireLogin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/logs", Token)).StatusCode);
    }

    [Fact]
    public async Task Logs_TrackEndpointAndProjectActionsWithoutSecrets()
    {
        var (admin, endpointId, project) = await SetupAsync();
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Porta avanti", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/projects/{project.Id}/autopilot/stop", null, Token)).EnsureSuccessStatusCode();

        var forEndpoint = await LogsAsync(admin, $"?endpointId={endpointId}");
        var messages = forEndpoint.Select(Message).ToList();

        Assert.Contains(messages, m => m.StartsWith("Endpoint creato"));
        Assert.Contains(messages, m => m == "Project aggiunto e attivato.");
        Assert.Contains(messages, m => m.StartsWith("Autopilot avviato"));
        Assert.Contains(messages, m => m == "Autopilot fermato.");
        Assert.True(forEndpoint.First().GetProperty("id").GetInt64() > forEndpoint.Last().GetProperty("id").GetInt64());

        var raw = await admin.GetStringAsync("/api/logs?take=500", Token);
        Assert.DoesNotContain("segreto-sessione", raw);
        Assert.DoesNotContain("Porta avanti", raw);
    }

    [Fact]
    public async Task Logs_RecordRefusedCommandsAsWarnings()
    {
        var (admin, _, project) = await SetupAsync();

        var refused = await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = " ", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var warnings = await LogsAsync(admin, $"?level=warning&projectId={project.Id}");
        var entry = Assert.Single(warnings);
        Assert.Equal("Warning", entry.GetProperty("level").GetString());
        Assert.Equal("autopilot", entry.GetProperty("category").GetString());
        Assert.StartsWith("Avvio rifiutato", Message(entry));
        Assert.Contains("prompt", Message(entry));
    }

    [Fact]
    public async Task Logs_FilterByLevelCategoryAndProject()
    {
        var (admin, _, project) = await SetupAsync();
        (await admin.PostAsync($"/api/projects/{project.Id}/deactivate", null, Token)).EnsureSuccessStatusCode();

        var byProject = await LogsAsync(admin, $"?projectId={project.Id}");
        Assert.All(byProject, e => Assert.Equal(project.Id, e.GetProperty("projectId").GetGuid()));
        Assert.Contains(byProject, e => Message(e).StartsWith("Project disattivato"));
        Assert.Empty(await LogsAsync(admin, $"?projectId={project.Id}&level=error"));
        Assert.All(await LogsAsync(admin, "?category=endpoint"), e => Assert.Equal("endpoint", e.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task Logs_PaginateWithBeforeCursor()
    {
        var (admin, _, project) = await SetupAsync();
        for (var i = 0; i < 3; i++)
            (await admin.PostAsync($"/api/projects/{project.Id}/{(i % 2 == 0 ? "deactivate" : "activate")}", null, Token)).EnsureSuccessStatusCode();

        var first = await (await admin.GetAsync($"/api/logs?projectId={project.Id}&take=2", Token)).JsonAsync();
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("items")[1].GetProperty("id").GetInt64();

        var second = await (await admin.GetAsync($"/api/logs?projectId={project.Id}&take=50&before={cursor}", Token)).JsonAsync();
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.All(second.GetProperty("items").EnumerateArray(), e => Assert.True(e.GetProperty("id").GetInt64() < cursor));
    }

    [Fact]
    public async Task Autopilot_WritesTransitionsToTheLogOnly()
    {
        var (admin, _, project) = await SetupAsync();
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Finished, "Fatto", DateTimeOffset.UtcNow);
        await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(project.Id, Token));
        factory.ChatGpt.State = id => new ConversationState(id, "n2", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
        for (var i = 0; i < 3; i++)
            await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(project.Id, Token));
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.Upstream, "ChatGPT non raggiungibile.");
        try
        {
            for (var i = 0; i < 3; i++)
                await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(project.Id, Token));
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }
        await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(project.Id, Token));

        var messages = (await LogsAsync(admin, $"?projectId={project.Id}&category=autopilot")).Select(Message).Reverse().ToList();

        Assert.Equal(
        [
            "Autopilot avviato: primo controllo della chat in corso.",
            "Risposta terminata: prompt di continuazione #1 inviato, ChatGPT sta lavorando.",
            "Errore (tentativo 1): ChatGPT non raggiungibile. Nuovo tentativo programmato.",
            "Connessione ripristinata dopo 3 tentativi falliti.",
            "ChatGPT sta lavorando nella chat."
        ], messages);
    }

    [Fact]
    public async Task OpenAiRequests_AreLoggedWithoutContent()
    {
        var admin = await AdminAsync(factory);
        var (endpointId, apiKey) = await CreateEndpointAsync(admin);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        (await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "chatgpt-thinking", reasoning_effort = "high", messages = new[] { new { role = "user", content = "contenuto-riservato" } } }, Token))
            .EnsureSuccessStatusCode();
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.RateLimited, "Troppe richieste");
        try
        {
            await client.PostAsJsonAsync("/v1/chat/completions",
                new { model = "chatgpt-auto", messages = new[] { new { role = "user", content = "contenuto-riservato" } } }, Token);
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }

        var entries = await LogsAsync(admin, $"?endpointId={endpointId}&category=openai");
        Assert.Contains(entries, e => Message(e).Contains("chatgpt-thinking") && Message(e).Contains("thinking extended") && Message(e).Contains("completata"));
        Assert.Contains(entries, e => e.GetProperty("level").GetString() == "Warning" && Message(e).Contains("Troppe richieste"));
        Assert.DoesNotContain("contenuto-riservato", await admin.GetStringAsync("/api/logs?take=500", Token));
    }

    [Fact]
    public async Task FailedLogin_IsLoggedWithoutTheTypedName()
    {
        var admin = await AdminAsync(factory);

        await Anonymous(factory).PostAsJsonAsync("/api/auth/login", new { userName = "password-digitata-per-errore", password = "x" }, Token);

        var entries = await LogsAsync(admin, "?category=accesso&level=warning");
        Assert.Contains(entries, e => Message(e).StartsWith("Accesso non riuscito"));
        Assert.DoesNotContain("password-digitata-per-errore", await admin.GetStringAsync("/api/logs?take=500", Token));
    }

    [Fact]
    public async Task Prune_RemovesEntriesOlderThanRetention()
    {
        var admin = await AdminAsync(factory);
        await factory.WithDbAsync(async db =>
        {
            db.ActivityLog.Add(new ActivityLogEntry
            {
                At = DateTimeOffset.UtcNow.AddDays(-40), Level = ActivityLevel.Info, Category = "endpoint", Message = "vecchia-voce"
            });
            await db.SaveChangesAsync(Token);
        });
        Assert.Contains("vecchia-voce", await admin.GetStringAsync("/api/logs?take=500", Token));

        await factory.WithServiceAsync<ActivityLog>(log => log.PruneAsync(Token));

        Assert.DoesNotContain("vecchia-voce", await admin.GetStringAsync("/api/logs?take=500", Token));
    }

    [Fact]
    public async Task GlobalEvents_StreamTheStatusOfActiveProjectsAndLiveText()
    {
        var (admin, _, project) = await SetupAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await admin.GetAsync("/api/projects/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));

        async Task<(string Event, JsonElement Data)> NextAsync()
        {
            string? name = null;
            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    return (name ?? "message", JsonDocument.Parse(line[6..]).RootElement);
            }
            throw new InvalidOperationException("Stream chiuso.");
        }

        var seen = new List<Guid>();
        (string Event, JsonElement Data) status;
        do
        {
            status = await NextAsync();
            seen.Add(status.Data.GetProperty("id").GetGuid());
        }
        while (status.Data.GetProperty("id").GetGuid() != project.Id);
        Assert.Equal("status", status.Event);

        await factory.WithServiceAsync<ProjectEventHub>(hub =>
        {
            hub.AppendLiveText(project.Id, "Sto scrivendo");
            return Task.CompletedTask;
        });
        var live = await NextAsync();
        while (live.Event != "live" || live.Data.GetProperty("projectId").GetGuid() != project.Id)
            live = await NextAsync();
        Assert.Equal("Sto scrivendo", live.Data.GetProperty("text").GetString());
    }
}
