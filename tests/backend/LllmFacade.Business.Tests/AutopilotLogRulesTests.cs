using LllmFacade.Business.Autopilot;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class AutopilotLogRulesTests
{
    private static AutopilotSnapshot Snap(
        AutopilotStatus status, string? error = null, int failures = 0, int sent = 0, int? max = null, int chats = 0) =>
        new(status, error, failures, sent, max, chats);

    [Fact]
    public void Describe_ChatRollover_ExplainsTheNewChat()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Submitting, sent: 4, chats: 0), Snap(AutopilotStatus.Generating, sent: 5, chats: 1));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("Limite di lunghezza", entry.Message);
        Assert.Contains("nuova chat", entry.Message);
        Assert.Contains("#5", entry.Message);
    }

    [Fact]
    public void Describe_NothingChanged_LogsNothing()
    {
        var state = Snap(AutopilotStatus.Generating);

        Assert.Empty(AutopilotLogRules.Describe(state, state));
    }

    [Fact]
    public void Describe_StartedWorking_IsInfo()
    {
        var entries = AutopilotLogRules.Describe(Snap(AutopilotStatus.Monitoring), Snap(AutopilotStatus.Generating));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("sta lavorando", entry.Message);
    }

    [Fact]
    public void Describe_ConfirmedPrompt_LogsFinishedAnswerAndSubmission()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Generating, sent: 1), Snap(AutopilotStatus.Generating, sent: 2));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("Risposta terminata", entry.Message);
        Assert.Contains("#2", entry.Message);
        Assert.Contains("inviato", entry.Message);
    }

    [Fact]
    public void Describe_UnconfirmedSubmission_IsWarningWithTheReason()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Submitting),
            Snap(AutopilotStatus.Submitting, error: "Invio non confermato", sent: 1));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Contains("non confermato", entry.Message);
    }

    [Fact]
    public void Describe_FirstFailure_IsWarningWithTheError()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Monitoring),
            Snap(AutopilotStatus.WaitingRetry, "ChatGPT non raggiungibile.", failures: 1));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Contains("ChatGPT non raggiungibile.", entry.Message);
        Assert.Contains("tentativo 1", entry.Message);
    }

    [Fact]
    public void Describe_RepeatedSameFailure_IsNotLoggedEveryMinute()
    {
        var before = Snap(AutopilotStatus.WaitingRetry, "Errore", failures: 3);

        Assert.Empty(AutopilotLogRules.Describe(before, Snap(AutopilotStatus.WaitingRetry, "Errore", failures: 4)));
        Assert.Single(AutopilotLogRules.Describe(before, Snap(AutopilotStatus.WaitingRetry, "Errore", failures: 10)));
        Assert.Single(AutopilotLogRules.Describe(before, Snap(AutopilotStatus.WaitingRetry, "Altro errore", failures: 4)));
    }

    [Fact]
    public void Describe_Recovery_IsInfoWithAttemptCount()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.WaitingRetry, "Errore", failures: 5), Snap(AutopilotStatus.Generating));

        Assert.Contains(entries, e => e.Level == ActivityLevel.Info && e.Message.Contains("ripristinata") && e.Message.Contains('5'));
    }

    [Fact]
    public void Describe_VerificationRequired_ExplainsThatAutomationIsPaused()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Generating),
            Snap(AutopilotStatus.VerificationRequired, "ChatGPT richiede verifica"));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Contains("verifica manuale", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pausa", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_CoolingDown_ExplainsThePerThreadPause()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Generating, sent: 1), Snap(AutopilotStatus.CoolingDown, sent: 1));

        var entry = Assert.Single(entries);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("thread", entry.Message);
        Assert.Contains("pausa", entry.Message);
    }

    [Fact]
    public void Describe_Completed_MentionsTheLimit()
    {
        var entries = AutopilotLogRules.Describe(
            Snap(AutopilotStatus.Submitting, sent: 3, max: 3), Snap(AutopilotStatus.Completed, sent: 3, max: 3));

        Assert.Contains(entries, e => e.Message.Contains("Completato") && e.Message.Contains('3'));
    }
}
