using LllmFacade.Business.Autopilot;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Sessions;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class SessionNextDueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public void NextDue_IsLastRefreshPlusInterval()
    {
        var last = Now.AddMinutes(-10);

        Assert.Equal(Now.AddMinutes(20), SessionRefreshPolicy.NextDue(Interval, last, last, Now.AddDays(5), Now));
    }

    [Fact]
    public void NextDue_NeverRefreshed_IsNow()
    {
        Assert.Equal(Now, SessionRefreshPolicy.NextDue(Interval, null, null, null, Now));
    }

    [Fact]
    public void NextDue_AnticipatesTheTokenExpiryMargin()
    {
        var last = Now.AddMinutes(-1);

        Assert.Equal(Now.AddMinutes(5), SessionRefreshPolicy.NextDue(Interval, last, last, Now.AddMinutes(20), Now));
    }

    [Fact]
    public void NextDue_WaitsForTheBackoffAfterAFailedAttempt()
    {
        Assert.Equal(Now.AddMinutes(4),
            SessionRefreshPolicy.NextDue(Interval, Now.AddHours(-3), Now.AddMinutes(-1), null, Now));
    }

    [Fact]
    public void IsDue_AgreesWithNextDue()
    {
        var last = Now.AddMinutes(-30);

        Assert.True(SessionRefreshPolicy.IsDue(Now, Interval, last, last, Now.AddDays(1)));
        Assert.False(SessionRefreshPolicy.IsDue(Now.AddMinutes(-1), Interval, last, last, Now.AddDays(1)));
    }
}

public sealed class ChatRolloverRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly AutopilotSettings Settings = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

    private static TrackedProject Running() => new()
    {
        IsActive = true,
        AutopilotEnabled = true,
        ContinuationPrompt = "Continua",
        ContinuationsSent = 1,
        TargetConversationId = "old-chat",
        CurrentConversationId = "old-chat",
        Status = AutopilotStatus.Submitting
    };

    [Fact]
    public void ChatRolledOver_PinsTheNewChatAndCountsTheHandoffPrompt()
    {
        var project = Running();
        project.ConsecutiveFailures = 2;
        project.LastError = "x";

        AutopilotRules.ChatRolledOver(project, "new-chat", confirmed: true, Now, Settings);

        Assert.Equal("new-chat", project.TargetConversationId);
        Assert.Equal("new-chat", project.CurrentConversationId);
        Assert.Equal(1, project.ChatsCreated);
        Assert.Equal(2, project.ContinuationsSent);
        Assert.Equal(AutopilotRules.NewChatPending, project.PendingAfterNodeId);
        Assert.Equal(Now, project.LastContinuationAt);
        Assert.Equal(AutopilotStatus.Generating, project.Status);
        Assert.Equal(Now.AddSeconds(30), project.NextCheckAt);
        Assert.Equal(0, project.ConsecutiveFailures);
        Assert.Null(project.LastError);
    }

    [Fact]
    public void ChatRolledOver_Unconfirmed_KeepsWatchingTheNewChat()
    {
        var project = Running();

        AutopilotRules.ChatRolledOver(project, "new-chat", confirmed: false, Now, Settings);

        Assert.Equal("new-chat", project.TargetConversationId);
        Assert.Equal(AutopilotStatus.Submitting, project.Status);
        Assert.NotNull(project.LastError);
    }

    [Fact]
    public void Observe_DoesNotResendWhileTheNewChatHasNoMessagesYet()
    {
        var project = Running();
        project.PendingAfterNodeId = AutopilotRules.NewChatPending;
        project.LastContinuationAt = Now.AddMinutes(-1);
        var empty = new ConversationState("new-chat", null, ConversationPhase.Finished, null, null);

        Assert.Equal(AutopilotAction.Wait, AutopilotRules.Observe(project, empty, Now, Settings));
        Assert.Equal(AutopilotRules.NewChatPending, project.PendingAfterNodeId);
    }

    [Fact]
    public void Observe_SendsAgainWhenTheNewChatStaysEmptyPastTheGrace()
    {
        var project = Running();
        project.PendingAfterNodeId = AutopilotRules.NewChatPending;
        project.LastContinuationAt = Now.AddMinutes(-6);
        var empty = new ConversationState("new-chat", null, ConversationPhase.Finished, null, null);

        Assert.Equal(AutopilotAction.Submit, AutopilotRules.Observe(project, empty, Now, Settings));
    }

    [Fact]
    public void Observe_ClearsTheNewChatMarkerOnceMessagesAppear()
    {
        var project = Running();
        project.PendingAfterNodeId = AutopilotRules.NewChatPending;
        project.LastContinuationAt = Now.AddMinutes(-1);

        AutopilotRules.Observe(project, new ConversationState("new-chat", "n1", ConversationPhase.Generating, null, null), Now, Settings);

        Assert.Null(project.PendingAfterNodeId);
    }
}

public sealed class NewChatPromptComposerTests
{
    [Fact]
    public void Compose_UsesTheConfiguredHandoffPromptAsIs()
    {
        Assert.Equal("Riparti da qui.", NewChatPromptComposer.Compose("  Riparti da qui. ", "Continua", "Ultima risposta"));
    }

    [Fact]
    public void Compose_WithoutHandoffPromptExplainsTheSwitchAndKeepsTheLastAnswer()
    {
        var prompt = NewChatPromptComposer.Compose(null, "Porta avanti il lavoro", "Capitolo 3 completato.");

        Assert.Contains("limite di lunghezza", prompt);
        Assert.Contains("Capitolo 3 completato.", prompt);
        Assert.EndsWith("Porta avanti il lavoro", prompt);
    }

    [Fact]
    public void Compose_WithoutLastAnswerStillExplainsTheSwitch()
    {
        var prompt = NewChatPromptComposer.Compose(" ", "Porta avanti il lavoro", null);

        Assert.Contains("limite di lunghezza", prompt);
        Assert.DoesNotContain("ultimo messaggio", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Porta avanti il lavoro", prompt);
    }

    [Fact]
    public void Compose_TrimsVeryLongLastAnswers()
    {
        var prompt = NewChatPromptComposer.Compose(null, "Continua", new string('x', 10_000));

        Assert.True(prompt.Length < 4_000);
    }
}
