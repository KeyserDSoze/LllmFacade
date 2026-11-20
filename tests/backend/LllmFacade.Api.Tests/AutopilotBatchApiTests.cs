using System.Net;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.Autopilot;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

[CollectionDefinition(nameof(AutopilotBatchCollection), DisableParallelization = true)]
public sealed class AutopilotBatchCollection;

[Collection(nameof(AutopilotBatchCollection))]
public sealed class AutopilotBatchApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Admin, TrackedProjectView Project)> RunningProjectAsync(string projectId, string name)
    {
        var admin = await AdminAsync(factory);
        var endpoints = await admin.GetFromJsonAsync<List<EndpointView>>("/api/endpoints", Json, Token);
        var endpointId = endpoints!.FirstOrDefault()?.Id;
        if (endpointId is null)
            (endpointId, _) = await CreateEndpointAsync(admin);
        var project = await (await admin.PostAsJsonAsync("/api/projects", new { endpointId, projectId, name }, Token))
            .ReadAsync<TrackedProjectView>();
        (await admin.PostAsJsonAsync($"/api/projects/{project.Id}/autopilot/start", new
        {
            continuationPrompt = "Continua",
            targetConversationId = "conv-old",
            maxContinuations = 9,
            newChatPrompt = "Riparti"
        }, Token)).EnsureSuccessStatusCode();
        return (admin, project);
    }

    private static async Task<List<TrackedProjectView>> ActiveAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<List<TrackedProjectView>>("/api/projects?state=active", Json, Token))!;

    private static async Task ResetActiveAsync(HttpClient admin)
    {
        foreach (var project in await ActiveAsync(admin))
            (await admin.PostAsync($"/api/projects/{project.Id}/deactivate", null, Token)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StopAll_PausesOnlyCurrentlyRunningActiveProjectsAndKeepsTheirTabs()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-alpha-batch", "Alpha batch");
        var (_, beta) = await RunningProjectAsync("g-p-beta-batch", "Beta batch");
        var (_, gamma) = await RunningProjectAsync("g-p-gamma-batch", "Gamma batch");
        (await admin.PostAsync($"/api/projects/{gamma.Id}/autopilot/stop", null, Token)).EnsureSuccessStatusCode();

        var result = await (await admin.PostAsync("/api/projects/autopilot/stop-all", null, Token)).JsonAsync();

        Assert.Equal(2, result.GetProperty("changed").GetInt32());
        var paused = result.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(alpha.Id, paused);
        Assert.Contains(beta.Id, paused);
        Assert.DoesNotContain(gamma.Id, paused);
        var active = await ActiveAsync(admin);
        Assert.All(active.Where(p => paused.Contains(p.Id)), p =>
        {
            Assert.True(p.BatchPaused);
            Assert.False(p.AutopilotEnabled);
            Assert.Equal(AutopilotStatus.Stopped, p.Status);
        });
        Assert.False(active.Single(p => p.Id == gamma.Id).BatchPaused);
        Assert.DoesNotContain(alpha.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
        Assert.DoesNotContain(beta.Id.ToString("N"), factory.ChatGpt.ClosedTabs);

        var again = await (await admin.PostAsync("/api/projects/autopilot/stop-all", null, Token)).JsonAsync();
        Assert.Equal(0, again.GetProperty("changed").GetInt32());
    }

    [Fact]
    public async Task ResumeAll_RestartsEveryEligibleStoppedActiveProjectAndPreservesTheirState()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-alpha-resume", "Alpha resume");
        var (_, beta) = await RunningProjectAsync("g-p-beta-resume", "Beta resume");
        var (_, manual) = await RunningProjectAsync("g-p-manual-resume", "Manual resume");
        (await admin.PostAsync($"/api/projects/{manual.Id}/autopilot/stop", null, Token)).EnsureSuccessStatusCode();
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Projects.SingleAsync(p => p.Id == alpha.Id, Token);
            stored.ContinuationsSent = 4;
            stored.ChatsCreated = 2;
            stored.PendingAfterNodeId = "node-sent";
            await db.SaveChangesAsync(Token);
        });
        (await admin.PostAsync("/api/projects/autopilot/stop-all", null, Token)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/projects/{beta.Id}/deactivate", null, Token)).EnsureSuccessStatusCode();

        var result = await (await admin.PostAsync("/api/projects/autopilot/resume-all", null, Token)).JsonAsync();

        Assert.Equal(2, result.GetProperty("changed").GetInt32());
        var active = await ActiveAsync(admin);
        var resumed = active.Single(p => p.Id == alpha.Id);
        Assert.True(resumed.AutopilotEnabled);
        Assert.False(resumed.BatchPaused);
        Assert.Equal(AutopilotStatus.Monitoring, resumed.Status);
        Assert.Equal(4, resumed.ContinuationsSent);
        Assert.Equal(2, resumed.ChatsCreated);
        Assert.Equal("conv-old", resumed.TargetConversationId);
        var manuallyStopped = active.Single(p => p.Id == manual.Id);
        Assert.True(manuallyStopped.AutopilotEnabled);
        Assert.False(manuallyStopped.BatchPaused);
        Assert.Equal(AutopilotStatus.Monitoring, manuallyStopped.Status);
        Assert.DoesNotContain(active, p => p.Id == beta.Id);
        var releaseTimes = new[] { resumed.NextCheckAt!.Value, manuallyStopped.NextCheckAt!.Value }
            .Order().ToList();
        Assert.Equal(2, releaseTimes.Distinct().Count());
        Assert.Equal(30, (releaseTimes[1] - releaseTimes[0]).TotalSeconds);
    }

    [Fact]
    public async Task RateLimit429_PausesEveryActiveAutopilotForFifteenMinutesAndSurvivesRestart()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-rate-alpha", "Rate alpha");
        var (_, beta) = await RunningProjectAsync("g-p-rate-beta", "Rate beta");
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.RateLimited, "Too many requests");
        var before = DateTimeOffset.UtcNow;
        try
        {
            await factory.WithServiceAsync<AutopilotProcessor>(p => p.ProcessAsync(alpha.Id, Token));
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }

        var active = await ActiveAsync(admin);
        foreach (var project in active.Where(p => p.Id == alpha.Id || p.Id == beta.Id))
        {
            Assert.True(project.AutopilotEnabled);
            Assert.Equal(AutopilotStatus.WaitingRetry, project.Status);
            Assert.NotNull(project.RateLimitUntil);
            Assert.InRange(project.RateLimitUntil!.Value, before.AddMinutes(14.5), before.AddMinutes(15.5));
            Assert.True(project.NextCheckAt >= project.RateLimitUntil);
            Assert.Contains("request", project.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
        }

        var scheduled = active.Where(p => p.Id == alpha.Id || p.Id == beta.Id)
            .ToDictionary(p => p.Id, p => p.NextCheckAt);
        await factory.WithServiceAsync<AutopilotProcessor>(p => p.ResumeAfterRestartAsync(Token));

        var afterRestart = await ActiveAsync(admin);
        foreach (var project in afterRestart.Where(p => p.Id == alpha.Id || p.Id == beta.Id))
        {
            Assert.True(project.AutopilotEnabled);
            Assert.Equal(scheduled[project.Id], project.NextCheckAt);
            Assert.True(project.NextCheckAt >= project.RateLimitUntil);
            Assert.True(project.NextCheckAt > DateTimeOffset.UtcNow.AddMinutes(10));
        }
    }

    [Fact]
    public async Task ResumeAfterRestart_StaggersNormalActiveProjects()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-restart-stagger-alpha", "Restart stagger alpha");
        var (_, beta) = await RunningProjectAsync("g-p-restart-stagger-beta", "Restart stagger beta");
        var (_, gamma) = await RunningProjectAsync("g-p-restart-stagger-gamma", "Restart stagger gamma");
        var ids = new[] { alpha.Id, beta.Id, gamma.Id };

        var before = DateTimeOffset.UtcNow;
        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.ResumeAfterRestartAsync(Token));
        var resumed = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id))
            .OrderBy(project => project.NextCheckAt).ToList();

        Assert.Equal(3, resumed.Select(project => project.NextCheckAt).Distinct().Count());
        Assert.Equal([0, 30, 60], resumed.Select(project =>
            (project.NextCheckAt!.Value - resumed[0].NextCheckAt!.Value).TotalSeconds).Select(Convert.ToInt32));
        Assert.True(resumed[0].NextCheckAt >= before);
    }

    [Fact]
    public async Task ResumeAfterRestart_PreservesAPerThreadCompletionPause()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, project) = await RunningProjectAsync("g-p-completion-pause", "Completion pause");
        var scheduled = DateTimeOffset.UtcNow.AddMinutes(9.5);
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Projects.SingleAsync(item => item.Id == project.Id, Token);
            stored.Status = AutopilotStatus.CoolingDown;
            stored.NextCheckAt = scheduled;
            await db.SaveChangesAsync(Token);
        });

        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.ResumeAfterRestartAsync(Token));

        var resumed = (await ActiveAsync(admin)).Single(item => item.Id == project.Id);
        Assert.Equal(AutopilotStatus.CoolingDown, resumed.Status);
        Assert.InRange(resumed.NextCheckAt!.Value, scheduled.AddMilliseconds(-1), scheduled.AddMilliseconds(1));
    }

    [Fact]
    public async Task ResumeAfterRestart_StaggersLegacyCooldownsWithOneReleaseTime()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-rate-legacy-alpha", "Rate legacy alpha");
        var (_, beta) = await RunningProjectAsync("g-p-rate-legacy-beta", "Rate legacy beta");
        var (_, gamma) = await RunningProjectAsync("g-p-rate-legacy-gamma", "Rate legacy gamma");
        var ids = new[] { alpha.Id, beta.Id, gamma.Id };

        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.CooldownAllAfterRateLimitAsync(alpha.Id, "Too many requests", Token));
        await factory.WithDbAsync(async db =>
        {
            var projects = await db.Projects.Where(project => ids.Contains(project.Id)).ToListAsync(Token);
            foreach (var project in projects)
                project.NextCheckAt = project.RateLimitUntil;
            await db.SaveChangesAsync(Token);
        });

        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.ResumeAfterRestartAsync(Token));
        var resumed = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id))
            .OrderBy(project => project.Name).ToList();

        Assert.Equal(3, resumed.Select(project => project.NextCheckAt).Distinct().Count());
        Assert.Equal([0, 30, 60], resumed.Select(project =>
            (project.NextCheckAt!.Value - project.RateLimitUntil!.Value).TotalSeconds).Select(Convert.ToInt32));
    }

    [Fact]
    public async Task ResumeAll_CanRestartProjectsWaitingForManualVerification()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, project) = await RunningProjectAsync("g-p-resume-verification", "Resume verification");
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Projects.SingleAsync(item => item.Id == project.Id, Token);
            AutopilotRules.Blocked(stored, "Verifica richiesta", DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(30));
            await db.SaveChangesAsync(Token);
        });

        var result = await (await admin.PostAsync("/api/projects/autopilot/resume-all", null, Token)).JsonAsync();
        var resumed = (await ActiveAsync(admin)).Single(item => item.Id == project.Id);

        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.True(resumed.AutopilotEnabled);
        Assert.False(resumed.BatchPaused);
        Assert.Equal(AutopilotStatus.Monitoring, resumed.Status);
        Assert.Null(resumed.LastError);
        Assert.Equal(0, resumed.ConsecutiveFailures);
    }

    [Fact]
    public async Task Blocked403_PausesEveryActiveProjectOfTheEndpointForThirtyMinutes()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-blocked-alpha", "Blocked alpha");
        var (_, beta) = await RunningProjectAsync("g-p-blocked-beta", "Blocked beta");
        factory.ChatGpt.Failure = new ChatGptException(
            ChatGptFailure.Blocked, "ChatGPT richiede una verifica di sicurezza nella pagina.");
        var before = DateTimeOffset.UtcNow;
        try
        {
            await factory.WithServiceAsync<AutopilotProcessor>(processor => processor.ProcessAsync(alpha.Id, Token));
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }

        var active = (await ActiveAsync(admin)).Where(project =>
            project.Id == alpha.Id || project.Id == beta.Id).ToList();
        Assert.All(active, project =>
        {
            Assert.True(project.AutopilotEnabled);
            Assert.Equal(AutopilotStatus.VerificationRequired, project.Status);
            Assert.InRange(project.NextCheckAt!.Value, before.AddMinutes(29.5), before.AddMinutes(30.5));
            Assert.Null(project.RateLimitUntil);
            Assert.Equal(1, project.ConsecutiveFailures);
            Assert.Contains("verifica", project.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(project.Id.ToString("N"), factory.ChatGpt.ClosedTabs);
        });
    }

    [Fact]
    public async Task RateLimit429_WithinThirtyMinutesUsesTheExtendedCooldown()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-rate-repeat-alpha", "Rate repeat alpha");
        var (_, beta) = await RunningProjectAsync("g-p-rate-repeat-beta", "Rate repeat beta");
        var ids = new[] { alpha.Id, beta.Id };
        var previousAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        await factory.WithDbAsync(async db =>
        {
            var projects = await db.Projects.Where(project => ids.Contains(project.Id)).ToListAsync(Token);
            foreach (var project in projects)
                project.LastRateLimitAt = previousAt;
            await db.SaveChangesAsync(Token);
        });
        var before = DateTimeOffset.UtcNow;

        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.CooldownAllAfterRateLimitAsync(alpha.Id, "Too many requests again", Token));

        var projects = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id)).ToList();
        Assert.All(projects, project => Assert.InRange(project.RateLimitUntil!.Value,
            before.AddMinutes(29.5), before.AddMinutes(30.5)));
        Assert.All(projects, project => Assert.Contains("30 minuti", project.LastError));
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Projects.Where(project => ids.Contains(project.Id)).ToListAsync(Token);
            Assert.All(stored, project => Assert.True(project.LastRateLimitAt > previousAt));
        });
    }

    [Fact]
    public async Task RateLimit429_DoublesTheReleaseStaggerAfterRepeatedFailures()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-rate-backoff-alpha", "Rate backoff alpha");
        var (_, beta) = await RunningProjectAsync("g-p-rate-backoff-beta", "Rate backoff beta");
        var (_, gamma) = await RunningProjectAsync("g-p-rate-backoff-gamma", "Rate backoff gamma");
        var ids = new[] { alpha.Id, beta.Id, gamma.Id };

        await factory.WithDbAsync(async db =>
        {
            var projects = await db.Projects.Where(project => ids.Contains(project.Id)).ToListAsync(Token);
            foreach (var project in projects)
            {
                project.ConsecutiveFailures = 1;
                project.RateLimitUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
                project.NextCheckAt = project.RateLimitUntil;
            }
            await db.SaveChangesAsync(Token);
        });
        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.CooldownAllAfterRateLimitAsync(alpha.Id, "Too many requests again", Token));
        var projects = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id))
            .OrderBy(project => project.Name).ToList();

        Assert.All(projects, project => Assert.Equal(2, project.ConsecutiveFailures));
        Assert.Equal([0, 60, 120], projects.Select(project =>
            (project.NextCheckAt!.Value - project.RateLimitUntil!.Value).TotalSeconds).Select(Convert.ToInt32));
    }

    [Fact]
    public async Task RateLimit429_StaggersTheReleaseAndCoalescesRapidDuplicates()
    {
        var admin = await AdminAsync(factory);
        await ResetActiveAsync(admin);
        var (_, alpha) = await RunningProjectAsync("g-p-rate-stagger-alpha", "Rate stagger alpha");
        var (_, beta) = await RunningProjectAsync("g-p-rate-stagger-beta", "Rate stagger beta");
        var (_, gamma) = await RunningProjectAsync("g-p-rate-stagger-gamma", "Rate stagger gamma");
        var ids = new[] { alpha.Id, beta.Id, gamma.Id };

        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.CooldownAllAfterRateLimitAsync(alpha.Id, "Too many requests", Token));
        var first = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id))
            .OrderBy(project => project.Name).ToList();

        await Task.Delay(25, Token);
        await factory.WithServiceAsync<AutopilotProcessor>(processor =>
            processor.CooldownAllAfterRateLimitAsync(alpha.Id, "Too many requests again", Token));
        var second = (await ActiveAsync(admin)).Where(project => ids.Contains(project.Id))
            .OrderBy(project => project.Name).ToList();

        Assert.Equal(first.Select(project => project.RateLimitUntil),
            second.Select(project => project.RateLimitUntil));
        Assert.Equal(first.Select(project => project.NextCheckAt),
            second.Select(project => project.NextCheckAt));
        Assert.All(second, project => Assert.Equal(1, project.ConsecutiveFailures));
        Assert.Equal(3, second.Select(project => project.NextCheckAt).Distinct().Count());
        Assert.All(second, project => Assert.True(project.NextCheckAt >= project.RateLimitUntil));
        Assert.Equal([0, 30, 60], second.Select(project =>
            (project.NextCheckAt!.Value - project.RateLimitUntil!.Value).TotalSeconds).Select(Convert.ToInt32));
    }

    [Fact]
    public async Task BatchEndpoints_RequireLogin()
    {
        var client = Anonymous(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/projects/autopilot/stop-all", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/projects/autopilot/resume-all", null, Token)).StatusCode);
    }
}
