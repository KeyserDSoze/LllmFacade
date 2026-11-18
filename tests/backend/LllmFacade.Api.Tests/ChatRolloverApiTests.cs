using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class ChatRolloverApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Admin, TrackedProjectView Project)> StartedProjectAsync(
        string target, string? newChatPrompt = null, string projectId = "g-p-alpha")
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        var project = await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId, name = "Alpha" }, Token))
            .ReadAsync<TrackedProjectView>();
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start", new
        {
            continuationPrompt = "Porta avanti il lavoro",
            targetConversationId = target,
            maxContinuations = (int?)null,
            newChatPrompt
        }, Token)).EnsureSuccessStatusCode();
        factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Finished, "Capitolo 3 completato.", DateTimeOffset.UtcNow);
        return (admin, project);
    }

    private Task ProcessAsync(Guid projectId) =>
        factory.WithServiceAsync<AutopilotProcessor>(processor => processor.ProcessAsync(projectId, Token));

    private static async Task<TrackedProjectView> GetAsync(HttpClient admin, Guid projectId) =>
        (await admin.GetFromJsonAsync<List<TrackedProjectView>>("/api/projects", Json, Token))!.Single(p => p.Id == projectId);

    [Fact]
    public async Task FullConversation_OpensANewChatWithAHandoffAndPinsIt()
    {
        var (admin, project) = await StartedProjectAsync("conv-full-1");
        factory.ChatGpt.FullConversations.Add("conv-full-1");
        factory.ChatGpt.NewChatId = "conv-created-1";
        var chatsBefore = factory.ChatGpt.NewChats.Count;

        await ProcessAsync(project.Id);

        Assert.Equal(chatsBefore + 1, factory.ChatGpt.NewChats.Count);
        var (target, prompt) = factory.ChatGpt.NewChats.Last();
        Assert.Equal("g-p-alpha", target.ProjectId);
        Assert.Equal(project.Id.ToString("N"), target.TabKey);
        Assert.Contains("limite di lunghezza", prompt);
        Assert.Contains("Capitolo 3 completato.", prompt);
        Assert.EndsWith("Porta avanti il lavoro", prompt);
        Assert.DoesNotContain(factory.ChatGpt.Submissions, s => s.Target.ConversationId == "conv-full-1" && s.Prompt == prompt);

        var after = await GetAsync(admin, project.Id);
        Assert.Equal("conv-created-1", after.TargetConversationId);
        Assert.Equal("conv-created-1", after.CurrentConversationId);
        Assert.Equal(1, after.ChatsCreated);
        Assert.Equal(1, after.ContinuationsSent);
        Assert.Equal(AutopilotStatus.Generating, after.Status);
        Assert.Null(after.LastError);

        var logs = await (await admin.GetAsync($"/api/logs?projectId={project.Id}&category=autopilot", Token)).JsonAsync();
        Assert.Contains(logs.GetProperty("items").EnumerateArray(), e => e.GetProperty("message").GetString()!.StartsWith("Limite di lunghezza"));
    }

    [Fact]
    public async Task FullConversation_UsesTheConfiguredNewChatPrompt()
    {
        var (_, project) = await StartedProjectAsync("conv-full-2", newChatPrompt: "Riparti dal capitolo 4.");
        factory.ChatGpt.FullConversations.Add("conv-full-2");
        factory.ChatGpt.NewChatId = "conv-created-2";

        await ProcessAsync(project.Id);

        Assert.Equal("Riparti dal capitolo 4.", factory.ChatGpt.NewChats.Last().Prompt);
    }

    [Fact]
    public async Task AfterTheRollover_TheNextChecksWatchTheNewChatAndDoNotOpenAnotherOne()
    {
        var (admin, project) = await StartedProjectAsync("conv-full-3");
        factory.ChatGpt.FullConversations.Add("conv-full-3");
        factory.ChatGpt.NewChatId = "conv-created-3";
        await ProcessAsync(project.Id);
        var created = factory.ChatGpt.NewChats.Count;

        factory.ChatGpt.State = id => id == "conv-created-3"
            ? new ConversationState(id, null, ConversationPhase.Finished, null, null)
            : throw new InvalidOperationException("la chat piena non va più controllata");
        await ProcessAsync(project.Id);
        factory.ChatGpt.State = id => new ConversationState(id, "n9", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
        await ProcessAsync(project.Id);

        Assert.Equal(created, factory.ChatGpt.NewChats.Count);
        var after = await GetAsync(admin, project.Id);
        Assert.Equal("conv-created-3", after.CurrentConversationId);
        Assert.Equal(AutopilotStatus.Generating, after.Status);
        Assert.Equal(1, after.ChatsCreated);
    }

    [Fact]
    public async Task FullConversation_WhenTheNewChatFails_RetriesWithoutPinningAnything()
    {
        var (admin, project) = await StartedProjectAsync("conv-full-4");
        factory.ChatGpt.FullConversations.Add("conv-full-4");
        factory.ChatGpt.NewChatFailure = new ChatGptException(ChatGptFailure.Upstream, "Pagina del project non disponibile.");
        try
        {
            await ProcessAsync(project.Id);
        }
        finally
        {
            factory.ChatGpt.NewChatFailure = null;
        }

        var after = await GetAsync(admin, project.Id);
        Assert.Equal(AutopilotStatus.WaitingRetry, after.Status);
        Assert.Equal("conv-full-4", after.TargetConversationId);
        Assert.Equal(0, after.ChatsCreated);
        Assert.Contains("Pagina del project non disponibile.", after.LastError);
    }

    [Fact]
    public async Task Settings_PersistTheNewChatPrompt()
    {
        var (admin, project) = await StartedProjectAsync("conv-full-5");

        var saved = await (await admin.PutAsJsonAsync($"/api/projects/{project.Id}/settings", new
        {
            continuationPrompt = "Porta avanti il lavoro",
            targetConversationId = "conv-full-5",
            maxContinuations = (int?)null,
            newChatPrompt = "  Primo messaggio della nuova chat  "
        }, Token)).ReadAsync<TrackedProjectView>();

        Assert.Equal("Primo messaggio della nuova chat", saved.NewChatPrompt);
    }
}
