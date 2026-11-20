using LllmFacade.Business.Autopilot;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class AutopilotBatchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static TrackedProject Project(bool running = true) => new()
    {
        IsActive = true,
        AutopilotEnabled = running,
        BatchPaused = false,
        ContinuationPrompt = "Continua",
        NewChatPrompt = "Nuova chat",
        TargetConversationId = "chat-pinned",
        CurrentConversationId = "chat-current",
        PendingAfterNodeId = "node-sent",
        MaxContinuations = 20,
        ContinuationsSent = 7,
        ChatsCreated = 2,
        Status = running ? AutopilotStatus.Generating : AutopilotStatus.Stopped,
        ConsecutiveFailures = 3,
        LastError = "temporary"
    };

    [Fact]
    public void PauseForBatch_RemembersOnlyARunningProject()
    {
        var running = Project();

        Assert.True(AutopilotRules.PauseForBatch(running));
        Assert.True(running.BatchPaused);
        Assert.False(running.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Stopped, running.Status);
        Assert.Null(running.NextCheckAt);

        var alreadyStopped = Project(running: false);
        Assert.False(AutopilotRules.PauseForBatch(alreadyStopped));
        Assert.False(alreadyStopped.BatchPaused);
    }

    [Fact]
    public void ResumeFromBatch_ContinuesFromPersistedStateWithoutResettingCounters()
    {
        var project = Project();
        AutopilotRules.PauseForBatch(project);

        Assert.True(AutopilotRules.ResumeFromBatch(project, Now));
        Assert.False(project.BatchPaused);
        Assert.True(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Monitoring, project.Status);
        Assert.Equal(Now, project.NextCheckAt);
        Assert.Equal(7, project.ContinuationsSent);
        Assert.Equal(2, project.ChatsCreated);
        Assert.Equal("Continua", project.ContinuationPrompt);
        Assert.Equal("Nuova chat", project.NewChatPrompt);
        Assert.Equal("chat-pinned", project.TargetConversationId);
        Assert.Equal("chat-current", project.CurrentConversationId);
        Assert.Equal("node-sent", project.PendingAfterNodeId);
        Assert.Equal(0, project.ConsecutiveFailures);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void Resume_RestartsAStoppedActiveProjectWithItsSavedState()
    {
        var project = Project(running: false);

        Assert.True(AutopilotRules.Resume(project, Now));
        Assert.True(project.AutopilotEnabled);
        Assert.False(project.BatchPaused);
        Assert.Equal(AutopilotStatus.Monitoring, project.Status);
        Assert.Equal(Now, project.NextCheckAt);
        Assert.Equal(7, project.ContinuationsSent);
        Assert.Equal("node-sent", project.PendingAfterNodeId);
        Assert.Equal(0, project.ConsecutiveFailures);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void ResumeFromBatch_RequiresActivePreviouslyPausedProject()
    {
        var notPaused = Project(running: false);
        Assert.False(AutopilotRules.ResumeFromBatch(notPaused, Now));

        var inactive = Project();
        AutopilotRules.PauseForBatch(inactive);
        inactive.IsActive = false;
        Assert.False(AutopilotRules.ResumeFromBatch(inactive, Now));
        Assert.True(inactive.BatchPaused);
    }

    [Fact]
    public void ManualStopClearsBatchPauseAndPreventsAnUnexpectedRestart()
    {
        var project = Project();
        AutopilotRules.PauseForBatch(project);

        AutopilotRules.Stop(project);

        Assert.False(project.BatchPaused);
        Assert.False(AutopilotRules.ResumeFromBatch(project, Now));
    }

    [Fact]
    public void DeactivateClearsBatchPauseAndCannotBeRevivedByResumeAll()
    {
        var project = Project();
        AutopilotRules.PauseForBatch(project);

        AutopilotRules.Deactivate(project);
        project.IsActive = true;

        Assert.False(project.BatchPaused);
        Assert.False(project.AutopilotEnabled);
        Assert.False(AutopilotRules.ResumeFromBatch(project, Now));
    }

    [Fact]
    public void StartClearsBatchPauseWithoutCarryingAStaleMarker()
    {
        var project = Project(running: false);
        project.BatchPaused = true;

        AutopilotRules.Start(project, "Continua", null, Now);

        Assert.False(project.BatchPaused);
        Assert.True(project.AutopilotEnabled);
    }
}
