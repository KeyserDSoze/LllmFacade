using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class ActivitySummaryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElementWrapper> SummaryAsync(HttpClient admin) =>
        new(await (await admin.GetAsync("/api/activity/summary", Token)).JsonAsync());

    private sealed record JsonElementWrapper(System.Text.Json.JsonElement Root)
    {
        public IEnumerable<System.Text.Json.JsonElement> Working => Root.GetProperty("working").EnumerateArray();

        public System.Text.Json.JsonElement Endpoint(Guid id) =>
            Root.GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Summary_RequiresLogin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/activity/summary", Token)).StatusCode);
    }

    [Fact]
    public async Task Summary_ShowsWhenTheTokenWasRenewedAndWhenItWillBeAgain()
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        var before = await SummaryAsync(admin);
        Assert.Equal(JsonValueKindNull, before.Endpoint(endpointId).GetProperty("lastRefreshAt").ValueKind);
        Assert.True(before.Endpoint(endpointId).GetProperty("hasAccessToken").GetBoolean());

        (await admin.PostAsync($"/api/endpoints/{endpointId}/refresh", null, Token)).EnsureSuccessStatusCode();

        var endpoint = (await SummaryAsync(admin)).Endpoint(endpointId);
        var last = endpoint.GetProperty("lastRefreshAt").GetDateTimeOffset();
        Assert.Equal(last.AddMinutes(30), endpoint.GetProperty("nextRefreshAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero), endpoint.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());
        Assert.Equal("Account principale", endpoint.GetProperty("name").GetString());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Summary_ListsProjectsThatAreWorkingAndCountsTheOnesWatching()
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        var working = await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId = "g-p-alpha", name = "Alpha" }, Token)).ReadAsync<TrackedProjectView>();
        var idle = await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId = "g-p-beta", name = "Beta" }, Token)).ReadAsync<TrackedProjectView>();
        foreach (var project in new[] { working, idle })
            (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start",
                new { continuationPrompt = "Continua", targetConversationId = "conv-a", maxContinuations = (int?)null }, Token)).EnsureSuccessStatusCode();
        factory.ChatGpt.State = id => new ConversationState(id, "n1", ConversationPhase.Generating, null, DateTimeOffset.UtcNow);
        await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(working.Id, Token));

        var summary = await SummaryAsync(admin);

        var item = Assert.Single(summary.Working, w => w.GetProperty("name").GetString() == "Alpha");
        Assert.Equal("project", item.GetProperty("kind").GetString());
        Assert.Equal("Account principale", item.GetProperty("endpointName").GetString());
        Assert.DoesNotContain(summary.Working, w => w.GetProperty("name").GetString() == "Beta");
        Assert.True(summary.Root.GetProperty("watching").GetInt32() >= 1);
    }

    [Fact]
    public async Task Summary_IncludesRequestsOnTheOpenAiApiWhileTheyRun()
    {
        var admin = await AdminAsync(factory);
        var (endpointId, _) = await CreateEndpointAsync(admin);
        using var running = factory.Services.GetRequiredService<ActiveRequests>().Begin(endpointId, "Account principale", "chatgpt-thinking", stream: true);

        var during = await SummaryAsync(admin);
        var api = Assert.Single(during.Working, w => w.GetProperty("kind").GetString() == "api" && w.GetProperty("detail").GetString()!.Contains("chatgpt-thinking"));
        Assert.Equal("Account principale", api.GetProperty("name").GetString());

        running.Dispose();
        var after = await SummaryAsync(admin);
        Assert.DoesNotContain(after.Working, w => w.GetProperty("kind").GetString() == "api" && w.GetProperty("detail").GetString()!.Contains("chatgpt-thinking"));
    }
}
