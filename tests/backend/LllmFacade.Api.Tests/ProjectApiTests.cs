using System.Net;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class ProjectApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Admin, Guid EndpointId)> SetupAsync()
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        return (admin, endpointId);
    }

    private static async Task<TrackedProjectView> TrackAsync(HttpClient admin, Guid endpointId, string projectId, string name) =>
        await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId, name }, Token)).ReadAsync<TrackedProjectView>();

    private Task ProcessAsync(Guid projectId) =>
        factory.WithServiceAsync<AutopilotProcessor>(processor => processor.ProcessAsync(projectId, Token));

    private static async Task<TrackedProjectView> GetAsync(HttpClient admin, Guid projectId) =>
        (await admin.GetFromJsonAsync<List<TrackedProjectView>>("/api/projects", Json, Token))!.Single(p => p.Id == projectId);

    [Fact]
    public async Task ChatGptProjects_ShowTrackingState()
    {
        var (admin, endpointId) = await SetupAsync();
        var tracked = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");

        var projects = await (await admin.GetAsync($"/api/endpoints/{endpointId}/chatgpt-projects", Token)).JsonAsync();

        var alpha = projects.EnumerateArray().Single(p => p.GetProperty("projectId").GetString() == "g-p-alpha");
        Assert.Equal(tracked.Id, alpha.GetProperty("trackedId").GetGuid());
        Assert.True(alpha.GetProperty("isActive").GetBoolean());
        var beta = projects.EnumerateArray().Single(p => p.GetProperty("projectId").GetString() == "g-p-beta");
        Assert.False(beta.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ActiveAndInactiveViews_AllowReactivation()
    {
        var (admin, endpointId) = await SetupAsync();
        var alpha = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        var beta = await TrackAsync(admin, endpointId, "g-p-beta", "Beta");

        (await admin.PostAsync($"/api/projects/{beta.Id}/deactivate", null, Token)).EnsureSuccessStatusCode();

        var active = await admin.GetFromJsonAsync<List<TrackedProjectView>>("/api/projects?state=active", Json, Token);
        var inactive = await admin.GetFromJsonAsync<List<TrackedProjectView>>("/api/projects?state=inactive", Json, Token);
        Assert.Contains(active!, p => p.Id == alpha.Id);
        Assert.DoesNotContain(active!, p => p.Id == beta.Id);
        Assert.Contains(inactive!, p => p.Id == beta.Id);

        (await admin.PostAsync($"/api/projects/{beta.Id}/activate", null, Token)).EnsureSuccessStatusCode();
        Assert.True((await GetAsync(admin, beta.Id)).IsActive);
    }

    [Fact]
    public async Task Track_RejectsInvalidProjectIds()
    {
        var (admin, endpointId) = await SetupAsync();

        var response = await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId = "../g-p-x", name = "X" }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Conversations_AreListedChronologically()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");

        var conversations = await (await admin.GetAsync($"/api/projects/{project.Id}/conversations", Token)).JsonAsync();

        Assert.Equal(["conv-old", "conv-new"], conversations.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Autopilot_RequiresContinuationPrompt()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");

        var response = await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = " ", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Autopilot_SendsPromptEachTimeTheChatFinishesUntilTheLimit()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        var started = await (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Porta avanti il lavoro", targetConversationId = (string?)null, maxContinuations = 2 }, Token))
            .ReadAsync<TrackedProjectView>();
        Assert.True(started.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Monitoring, started.Status);
        var submissions = factory.ChatGpt.Submissions.Count;

        factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Finished, "Fatto", DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);
        var afterFirst = await GetAsync(admin, project.Id);
        Assert.Contains(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
        Assert.Equal(submissions + 1, factory.ChatGpt.Submissions.Count);
        var (target, prompt) = factory.ChatGpt.Submissions.Last();
        Assert.Equal("conv-new", target.ConversationId);
        Assert.Equal("Porta avanti il lavoro", prompt);
        Assert.Equal(1, afterFirst.ContinuationsSent);
        Assert.Equal(AutopilotStatus.Generating, afterFirst.Status);
        Assert.Equal("conv-new", afterFirst.CurrentConversationId);

        await ProcessAsync(project.Id);
        Assert.Equal(submissions + 1, factory.ChatGpt.Submissions.Count);

        factory.ChatGpt.State = id => new ConversationState(id, "n2", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);
        Assert.Equal(AutopilotStatus.Generating, (await GetAsync(admin, project.Id)).Status);

        factory.ChatGpt.State = id => new ConversationState(id, "n3", ConversationPhase.Finished, "Fatto ancora", DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);
        Assert.Equal(submissions + 2, factory.ChatGpt.Submissions.Count);

        factory.ChatGpt.State = id => new ConversationState(id, "n5", ConversationPhase.Finished, "Ultimo", DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);
        var completed = await GetAsync(admin, project.Id);
        Assert.Equal(submissions + 2, factory.ChatGpt.Submissions.Count);
        Assert.Equal(AutopilotStatus.Completed, completed.Status);
        Assert.False(completed.AutopilotEnabled);
        Assert.Contains(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
    }

    [Fact]
    public async Task Autopilot_UpdatesTheLatestConversationEvenWhenItsDetailFetchFails()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        var originalConversations = factory.ChatGpt.Conversations;
        var originalState = factory.ChatGpt.State;
        try
        {
            factory.ChatGpt.Conversations =
            [
                new("conv-older", "Vecchia", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1)),
                new("conv-newest", "Nuovissima", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1))
            ];
            factory.ChatGpt.State = _ =>
                throw new ChatGptException(ChatGptFailure.Timeout, "Timeout nel dettaglio della chat");

            await ProcessAsync(project.Id);

            var after = await GetAsync(admin, project.Id);
            Assert.Equal("conv-newest", after.CurrentConversationId);
            Assert.Null(after.TargetConversationId);
            Assert.Equal(AutopilotStatus.WaitingRetry, after.Status);
            Assert.Contains("Timeout", after.LastError);
        }
        finally
        {
            factory.ChatGpt.Conversations = originalConversations;
            factory.ChatGpt.State = originalState;
        }
    }

    [Fact]
    public async Task Autopilot_SwitchesToANewerConversationAfterItAppearsInTheProjectList()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        var originalConversations = factory.ChatGpt.Conversations;
        var originalState = factory.ChatGpt.State;
        try
        {
            factory.ChatGpt.Conversations = [new("conv-old", "Vecchia", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1))];
            factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
            await ProcessAsync(project.Id);
            Assert.Equal("conv-old", (await GetAsync(admin, project.Id)).CurrentConversationId);

            factory.ChatGpt.Conversations.Add(new("conv-new", "Nuova", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1)));
            await ProcessAsync(project.Id);

            var after = await GetAsync(admin, project.Id);
            Assert.Equal("conv-new", after.CurrentConversationId);
            Assert.Null(after.TargetConversationId);
        }
        finally
        {
            factory.ChatGpt.Conversations = originalConversations;
            factory.ChatGpt.State = originalState;
        }
    }

    [Fact]
    public async Task Autopilot_UsesTheNextRecentConversationWhenTheNewestDetailFails()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        var originalConversations = factory.ChatGpt.Conversations;
        var originalState = factory.ChatGpt.State;
        try
        {
            factory.ChatGpt.Conversations =
            [
                new("conv-newest", "Nuovissima", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1)),
                new("conv-previous", "Precedente", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddMinutes(-30))
            ];
            factory.ChatGpt.State = id => id == "conv-newest"
                ? throw new ChatGptException(ChatGptFailure.Timeout, "Timeout nella chat più recente")
                : new ConversationState(id, "n-prev", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);

            await ProcessAsync(project.Id);

            var after = await GetAsync(admin, project.Id);
            Assert.Equal("conv-previous", after.CurrentConversationId);
            Assert.Equal(AutopilotStatus.Generating, after.Status);
            Assert.Equal(0, after.ConsecutiveFailures);
            Assert.Null(after.LastError);
            var logs = await (await admin.GetAsync($"/api/logs?projectId={project.Id}&category=project", Token)).JsonAsync();
            Assert.Contains(logs.GetProperty("items").EnumerateArray(),
                entry => entry.GetProperty("message").GetString()!.Contains("successiva in ordine cronologico"));
        }
        finally
        {
            factory.ChatGpt.Conversations = originalConversations;
            factory.ChatGpt.State = originalState;
        }
    }

    [Fact]
    public async Task ResumeAfterRestart_ImmediatelySchedulesActiveAutopilotsWithoutResettingTheirState()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = "conv-old", maxContinuations = 8 }, Token)).EnsureSuccessStatusCode();
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Projects.SingleAsync(p => p.Id == project.Id, Token);
            stored.Status = AutopilotStatus.Generating;
            stored.NextCheckAt = DateTimeOffset.UtcNow.AddHours(1);
            stored.CurrentConversationId = "conv-old";
            stored.PendingAfterNodeId = "node-sent";
            stored.ContinuationsSent = 3;
            stored.LastError = "ultimo errore mantenuto finché il controllo successivo non recupera";
            await db.SaveChangesAsync(Token);
        });
        var before = DateTimeOffset.UtcNow;

        await factory.WithServiceAsync<AutopilotProcessor>(p => p.ResumeAfterRestartAsync(Token));

        var after = await GetAsync(admin, project.Id);
        Assert.InRange(after.NextCheckAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(AutopilotStatus.Generating, after.Status);
        Assert.Equal("conv-old", after.CurrentConversationId);
        Assert.Equal(3, after.ContinuationsSent);
        Assert.Equal("ultimo errore mantenuto finché il controllo successivo non recupera", after.LastError);
        Assert.True(after.AutopilotEnabled);
        IReadOnlyList<Guid> due = [];
        await factory.WithServiceAsync<AutopilotProcessor>(async p => due = await p.FindDueAsync(Token));
        Assert.Contains(project.Id, due);
    }

    [Fact]
    public async Task Autopilot_RetriesEveryMinuteWhenTheConnectionDrops()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-beta", "Beta");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = "conv-old", maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.Upstream, "ChatGPT non raggiungibile.");
        try
        {
            var before = DateTimeOffset.UtcNow;
            await ProcessAsync(project.Id);

            var failed = await GetAsync(admin, project.Id);
            Assert.Contains(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
            Assert.Equal(AutopilotStatus.WaitingRetry, failed.Status);
            Assert.Equal("ChatGPT non raggiungibile.", failed.LastError);
            Assert.Equal(1, failed.ConsecutiveFailures);
            Assert.True(failed.AutopilotEnabled);
            Assert.InRange(failed.NextCheckAt!.Value, before.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(65));
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }

        factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);
        var recovered = await GetAsync(admin, project.Id);
        Assert.Equal(AutopilotStatus.Generating, recovered.Status);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task Autopilot_StopAndDueSelection()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();

        IReadOnlyList<Guid> due = [];
        await factory.WithServiceAsync<AutopilotProcessor>(async processor => due = await processor.FindDueAsync(Token));
        Assert.Contains(project.Id, due);

        var stopped = await (await admin.PostAsync($"/api/projects/{project.Id}/autopilot/stop", null, Token)).ReadAsync<TrackedProjectView>();
        Assert.Equal(AutopilotStatus.Stopped, stopped.Status);
        Assert.Contains(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
        await factory.WithServiceAsync<AutopilotProcessor>(async processor => due = await processor.FindDueAsync(Token));
        Assert.DoesNotContain(project.Id, due);
    }

    [Fact]
    public async Task Events_StreamStatusChangesToTheAdminUi()
    {
        var (admin, endpointId) = await SetupAsync();
        var project = await TrackAsync(admin, endpointId, "g-p-alpha", "Alpha");
        using var events = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await admin.GetAsync($"/api/projects/{project.Id}/events", HttpCompletionOption.ResponseHeadersRead, events.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(events.Token));

        Assert.Equal("event: status", await reader.ReadLineAsync(events.Token));
        Assert.Contains("\"autopilotEnabled\":false", await reader.ReadLineAsync(events.Token));

        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
            new { continuationPrompt = "Continua", targetConversationId = (string?)null, maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        string? line;
        do
        {
            line = await reader.ReadLineAsync(events.Token);
        }
        while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));
        Assert.Contains("\"autopilotEnabled\":true", line);
        Assert.Contains("\"status\":\"Monitoring\"", line);
    }
}
