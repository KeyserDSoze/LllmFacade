using System.Net;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
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
