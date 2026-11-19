using LllmFacade.Business.Autopilot;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class AutopilotRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly AutopilotSettings Settings = new(
        PollInterval: TimeSpan.FromSeconds(30),
        RetryInterval: TimeSpan.FromMinutes(1),
        SubmissionGrace: TimeSpan.FromMinutes(5));

    private static TrackedProject Running(int sent = 0, int? max = null) => new()
    {
        IsActive = true,
        AutopilotEnabled = true,
        ContinuationPrompt = "Continua",
        ContinuationsSent = sent,
        MaxContinuations = max,
        Status = AutopilotStatus.Monitoring
    };

    private static ConversationState State(ConversationPhase phase, string node = "n1") =>
        new("c1", node, phase, "testo", Now);

    [Fact]
    public void Start_EnablesImmediateCheckAndResetsCounters()
    {
        var project = new TrackedProject { IsActive = true, ContinuationsSent = 4, ConsecutiveFailures = 2, LastError = "x", PendingAfterNodeId = "n" };

        AutopilotRules.Start(project, "  Continua il lavoro  ", 3, Now);

        Assert.True(project.AutopilotEnabled);
        Assert.Equal("Continua il lavoro", project.ContinuationPrompt);
        Assert.Equal(3, project.MaxContinuations);
        Assert.Equal(0, project.ContinuationsSent);
        Assert.Equal(0, project.ConsecutiveFailures);
        Assert.Null(project.LastError);
        Assert.Null(project.PendingAfterNodeId);
        Assert.Equal(AutopilotStatus.Monitoring, project.Status);
        Assert.Equal(Now, project.NextCheckAt);
    }

    [Fact]
    public void Start_RequiresActiveProjectPromptAndPositiveLimit()
    {
        Assert.Throws<InvalidOperationException>(() => AutopilotRules.Start(new TrackedProject(), "Continua", null, Now));
        Assert.Throws<ArgumentException>(() => AutopilotRules.Start(new TrackedProject { IsActive = true }, " ", null, Now));
        Assert.Throws<ArgumentException>(() => AutopilotRules.Start(new TrackedProject { IsActive = true }, "Continua", 0, Now));
    }

    [Fact]
    public void SelectConversation_UpdatesTheLiveSelectionAndClearsTheOldPendingNode()
    {
        var project = Running();
        project.CurrentConversationId = "old";
        project.PendingAfterNodeId = "n-old";
        project.Status = AutopilotStatus.Submitting;

        var changed = AutopilotRules.SelectConversation(project, "new", Now, Settings);

        Assert.True(changed);
        Assert.Equal("new", project.CurrentConversationId);
        Assert.Null(project.PendingAfterNodeId);
        Assert.Equal(AutopilotStatus.Monitoring, project.Status);
        Assert.Equal(Now, project.NextCheckAt);
        Assert.False(AutopilotRules.SelectConversation(project, "new", Now, Settings));
    }

    [Fact]
    public void Observe_WhileGeneratingWaitsForNextPoll()
    {
        var project = Running();
        project.ConsecutiveFailures = 3;
        project.LastError = "rete";

        var action = AutopilotRules.Observe(project, State(ConversationPhase.Generating), Now, Settings);

        Assert.Equal(AutopilotAction.Wait, action);
        Assert.Equal(AutopilotStatus.Generating, project.Status);
        Assert.Equal(Now.AddSeconds(30), project.NextCheckAt);
        Assert.Equal(Now, project.LastCheckAt);
        Assert.Equal("c1", project.CurrentConversationId);
        Assert.Equal(0, project.ConsecutiveFailures);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void Observe_FinishedChatAsksToSubmitContinuation()
    {
        var project = Running();

        Assert.Equal(AutopilotAction.Submit, AutopilotRules.Observe(project, State(ConversationPhase.Finished), Now, Settings));
        Assert.Equal(AutopilotStatus.Submitting, project.Status);
    }

    [Fact]
    public void Observe_DoesNotResubmitWhileSentPromptIsNotVisibleYet()
    {
        var project = Running(sent: 1);
        project.PendingAfterNodeId = "n1";
        project.LastContinuationAt = Now.AddMinutes(-2);

        Assert.Equal(AutopilotAction.Wait, AutopilotRules.Observe(project, State(ConversationPhase.Finished, "n1"), Now, Settings));
        Assert.Equal(AutopilotStatus.Submitting, project.Status);
        Assert.Equal(Now.AddSeconds(30), project.NextCheckAt);
    }

    [Fact]
    public void Observe_ResubmitsWhenSentPromptNeverAppearedAfterGrace()
    {
        var project = Running(sent: 1);
        project.PendingAfterNodeId = "n1";
        project.LastContinuationAt = Now.AddMinutes(-6);

        Assert.Equal(AutopilotAction.Submit, AutopilotRules.Observe(project, State(ConversationPhase.Finished, "n1"), Now, Settings));
        Assert.Null(project.PendingAfterNodeId);
    }

    [Fact]
    public void Observe_ClearsPendingOnceChatMovedOn()
    {
        var project = Running(sent: 1);
        project.PendingAfterNodeId = "n1";
        project.LastContinuationAt = Now.AddMinutes(-1);

        AutopilotRules.Observe(project, State(ConversationPhase.Generating, "n2"), Now, Settings);

        Assert.Null(project.PendingAfterNodeId);
    }

    [Fact]
    public void Observe_CompletesWhenLimitReached()
    {
        var project = Running(sent: 2, max: 2);

        Assert.Equal(AutopilotAction.Complete, AutopilotRules.Observe(project, State(ConversationPhase.Finished, "n9"), Now, Settings));
        Assert.False(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Completed, project.Status);
        Assert.Null(project.NextCheckAt);
    }

    [Fact]
    public void SubmissionSent_CountsAndWaitsForTheNewAnswer()
    {
        var project = Running(sent: 1);

        AutopilotRules.SubmissionSent(project, "n7", confirmed: true, Now, Settings);

        Assert.Equal(2, project.ContinuationsSent);
        Assert.Equal("n7", project.PendingAfterNodeId);
        Assert.Equal(Now, project.LastContinuationAt);
        Assert.Equal(AutopilotStatus.Generating, project.Status);
        Assert.Equal(Now.AddSeconds(30), project.NextCheckAt);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void SubmissionSent_UnconfirmedKeepsPendingAndWarns()
    {
        var project = Running();

        AutopilotRules.SubmissionSent(project, "n7", confirmed: false, Now, Settings);

        Assert.Equal(1, project.ContinuationsSent);
        Assert.Equal(AutopilotStatus.Submitting, project.Status);
        Assert.NotNull(project.LastError);
    }

    [Fact]
    public void Failed_RetriesAfterOneMinute()
    {
        var project = Running();

        AutopilotRules.Failed(project, "Connessione persa", Now, Settings);
        AutopilotRules.Failed(project, "Connessione persa", Now, Settings);

        Assert.Equal(2, project.ConsecutiveFailures);
        Assert.Equal(AutopilotStatus.WaitingRetry, project.Status);
        Assert.Equal(Now.AddMinutes(1), project.NextCheckAt);
        Assert.Equal("Connessione persa", project.LastError);
        Assert.True(project.AutopilotEnabled);
    }

    [Fact]
    public void Stop_DisablesAutopilot()
    {
        var project = Running();

        AutopilotRules.Stop(project);

        Assert.False(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Stopped, project.Status);
        Assert.Null(project.NextCheckAt);
    }

    [Fact]
    public void Deactivate_StopsRunningAutopilot()
    {
        var project = Running();

        AutopilotRules.Deactivate(project);

        Assert.False(project.IsActive);
        Assert.False(project.AutopilotEnabled);
        Assert.Equal(AutopilotStatus.Stopped, project.Status);
    }
}

public sealed class ChatGptIdsTests
{
    [Theory]
    [InlineData("g-p-6750a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("g-p-abc_DEF-123")]
    public void EnsureProjectId_AcceptsChatGptProjectIds(string id)
    {
        Assert.Equal(id, ChatGptIds.EnsureProjectId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("g-abc")]
    [InlineData("g-p-../../x")]
    [InlineData("g-p-a?b=c")]
    public void EnsureProjectId_RejectsOtherValues(string? id)
    {
        Assert.Throws<ArgumentException>(() => ChatGptIds.EnsureProjectId(id));
    }

    [Fact]
    public void EnsureConversationId_AcceptsUuidAndRejectsPaths()
    {
        Assert.Equal("6750a1b2-c3d4-e5f6-a7b8-c9d0e1f2a3b4", ChatGptIds.EnsureConversationId("6750a1b2-c3d4-e5f6-a7b8-c9d0e1f2a3b4"));
        Assert.Throws<ArgumentException>(() => ChatGptIds.EnsureConversationId("../admin"));
        Assert.Throws<ArgumentException>(() => ChatGptIds.EnsureConversationId(null));
    }
}
