using LllmFacade.Business.Autopilot;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class AutopilotRateLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 22, 0, 0, TimeSpan.Zero);
    private static readonly AutopilotSettings Settings = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

    [Fact]
    public void RateLimited_KeepsTheAutopilotRunningButWaitsFifteenMinutes()
    {
        var project = new TrackedProject { IsActive = true, AutopilotEnabled = true, Status = AutopilotStatus.Generating };

        AutopilotRules.RateLimited(project, "Too many requests", Now, Settings);

        Assert.True(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.WaitingRetry, project.Status);
        Assert.Equal(Now.AddMinutes(15), project.NextCheckAt);
        Assert.Equal(Now.AddMinutes(15), project.RateLimitUntil);
        Assert.Equal("Too many requests", project.LastError);
        Assert.Equal(1, project.ConsecutiveFailures);
    }

    [Fact]
    public void Observe_AfterCooldownClearsTheRateLimitMarker()
    {
        var project = new TrackedProject
        {
            IsActive = true,
            AutopilotEnabled = true,
            Status = AutopilotStatus.WaitingRetry,
            RateLimitUntil = Now
        };

        var action = AutopilotRules.Observe(project,
            new LllmFacade.Business.ChatGpt.ConversationState("c1", "n1", LllmFacade.Business.ChatGpt.ConversationPhase.Generating, null, Now),
            Now, Settings);

        Assert.Equal(AutopilotAction.Wait, action);
        Assert.Null(project.RateLimitUntil);
        Assert.Equal(AutopilotStatus.Generating, project.Status);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void ResumeFromBatch_PreservesAnUnexpiredRateLimitCooldown()
    {
        var project = new TrackedProject { IsActive = true, AutopilotEnabled = true };
        AutopilotRules.RateLimited(project, "429", Now, Settings);
        AutopilotRules.PauseForBatch(project);

        Assert.True(AutopilotRules.ResumeFromBatch(project, Now.AddMinutes(4)));
        Assert.True(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.WaitingRetry, project.Status);
        Assert.Equal(Now.AddMinutes(15), project.NextCheckAt);
        Assert.Equal("429", project.LastError);
    }

    [Fact]
    public void RateLimited_DoesNotOverwriteAManualVerificationPause()
    {
        var project = new TrackedProject { IsActive = true, AutopilotEnabled = true };
        AutopilotRules.Blocked(project, "Verifica richiesta", Now, TimeSpan.FromMinutes(30));
        var verificationUntil = project.NextCheckAt;

        var changed = AutopilotRules.RateLimited(project, "429", Now.AddMinutes(1), Settings, repeated: true);

        Assert.False(changed);
        Assert.Equal(AutopilotStatus.VerificationRequired, project.Status);
        Assert.Equal(verificationUntil, project.NextCheckAt);
        Assert.Null(project.RateLimitUntil);
    }

    [Fact]
    public void RateLimited_DoesNotExtendOrRecountAnActiveCooldown()
    {
        var project = new TrackedProject { IsActive = true, AutopilotEnabled = true };
        AutopilotRules.RateLimited(project, "429 iniziale", Now, Settings);
        var firstUntil = project.RateLimitUntil;
        var firstNextCheck = project.NextCheckAt;

        var changed = AutopilotRules.RateLimited(project, "429 duplicato", Now.AddSeconds(25), Settings);

        Assert.False(changed);
        Assert.Equal(firstUntil, project.RateLimitUntil);
        Assert.Equal(firstNextCheck, project.NextCheckAt);
        Assert.Equal(1, project.ConsecutiveFailures);
        Assert.Equal("429 iniziale", project.LastError);
    }

    [Fact]
    public void RateLimited_CloseRepeatUsesTheExtendedThirtyMinuteCooldown()
    {
        var settings = Settings with { RepeatedRateLimitCooldown = TimeSpan.FromMinutes(30) };
        var project = new TrackedProject
        {
            IsActive = true,
            AutopilotEnabled = true,
            LastRateLimitAt = Now.AddMinutes(-20)
        };

        AutopilotRules.RateLimited(project, "429 ripetuto", Now, settings, repeated: true);

        Assert.Equal(Now.AddMinutes(30), project.RateLimitUntil);
        Assert.Equal(Now.AddMinutes(30), project.NextCheckAt);
        Assert.Equal(Now, project.LastRateLimitAt);
    }

    [Fact]
    public void RateLimited_UsesTheConfiguredCooldown()
    {
        var settings = new AutopilotSettings(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7));
        var project = new TrackedProject { IsActive = true, AutopilotEnabled = true };

        AutopilotRules.RateLimited(project, "429", Now, settings);

        Assert.Equal(Now.AddMinutes(7), project.RateLimitUntil);
    }
}
