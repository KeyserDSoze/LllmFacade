using LllmFacade.Domain;

namespace LllmFacade.Business.Autopilot;

public sealed record AutopilotSnapshot(
    AutopilotStatus Status, string? LastError, int ConsecutiveFailures, int ContinuationsSent, int? MaxContinuations, int ChatsCreated);

public sealed record LogDraft(ActivityLevel Level, string Message);

/// <summary>Decides what an autopilot step is worth in the activity log: transitions only, never every poll.</summary>
public static class AutopilotLogRules
{
    private const int RepeatEvery = 10;

    public static IReadOnlyList<LogDraft> Describe(AutopilotSnapshot before, AutopilotSnapshot after)
    {
        var entries = new List<LogDraft>();

        if (after.ChatsCreated > before.ChatsCreated)
        {
            entries.Add(new LogDraft(ActivityLevel.Info,
                $"Limite di lunghezza della chat raggiunto: creata una nuova chat nel project e inviato il prompt #{after.ContinuationsSent}. La nuova chat è ora quella da portare avanti."));
            return entries;
        }

        if (after.ContinuationsSent > before.ContinuationsSent)
        {
            entries.Add(after.Status == AutopilotStatus.Generating
                ? new LogDraft(ActivityLevel.Info,
                    $"Risposta terminata: prompt di continuazione #{after.ContinuationsSent} inviato, ChatGPT sta lavorando.")
                : new LogDraft(ActivityLevel.Warning,
                    $"Prompt di continuazione #{after.ContinuationsSent} inviato ma non confermato: controllo la chat prima di riprovare."
                    + (after.LastError is { Length: > 0 } reason ? " " + reason : string.Empty)));
            return entries;
        }

        if (after.Status == AutopilotStatus.VerificationRequired)
        {
            if (before.Status != AutopilotStatus.VerificationRequired || before.LastError != after.LastError)
                entries.Add(new LogDraft(ActivityLevel.Warning,
                    "ChatGPT richiede una verifica manuale nel browser collegato; nessun nuovo tentativo verrà inviato durante la pausa."));
            return entries;
        }

        if (after.Status == AutopilotStatus.WaitingRetry)
        {
            var changed = before.Status != AutopilotStatus.WaitingRetry || before.LastError != after.LastError;
            if (changed || after.ConsecutiveFailures % RepeatEvery == 0)
                entries.Add(new LogDraft(ActivityLevel.Warning,
                    $"Errore (tentativo {after.ConsecutiveFailures}): {after.LastError} Nuovo tentativo programmato."));
            return entries;
        }

        if (before.Status == AutopilotStatus.WaitingRetry)
            entries.Add(new LogDraft(ActivityLevel.Info,
                $"Connessione ripristinata dopo {before.ConsecutiveFailures} tentativi falliti."));

        if (after.Status == AutopilotStatus.CoolingDown && before.Status != AutopilotStatus.CoolingDown)
            entries.Add(new LogDraft(ActivityLevel.Info,
                "Risposta terminata: il thread resta in pausa prima del prossimo prompt."));
        else if (after.Status == AutopilotStatus.Completed && before.Status != AutopilotStatus.Completed)
            entries.Add(new LogDraft(ActivityLevel.Info,
                $"Completato: raggiunto il limite di {after.MaxContinuations ?? after.ContinuationsSent} invii."));
        else if (after.Status == AutopilotStatus.Generating && before.Status != AutopilotStatus.Generating)
            entries.Add(new LogDraft(ActivityLevel.Info, "ChatGPT sta lavorando nella chat."));

        return entries;
    }
}
