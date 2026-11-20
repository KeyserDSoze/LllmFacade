using LllmFacade.Business.ChatGpt;
using LllmFacade.Domain;

namespace LllmFacade.Business.Autopilot;

public sealed record AutopilotSettings(
    TimeSpan PollInterval,
    TimeSpan RetryInterval,
    TimeSpan SubmissionGrace,
    TimeSpan? RateLimitCooldown = null,
    TimeSpan? CompletionPause = null,
    TimeSpan? RepeatedRateLimitCooldown = null,
    TimeSpan? RateLimitRepeatWindow = null);

public enum AutopilotAction
{
    Wait,
    Submit,
    Complete
}

/// <summary>"Carry on the work": send the saved prompt each time the chat finishes, never twice for the same answer.</summary>
public static class AutopilotRules
{
    public const int MaxPromptLength = 20_000;
    public const int MaxErrorLength = 500;
    /// <summary>Pending marker for a freshly created chat, whose first message may not be listed yet.</summary>
    public const string NewChatPending = "new-chat";

    public static string NormalizePrompt(string? prompt)
    {
        var normalized = prompt?.Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Il prompt di continuazione è obbligatorio.", nameof(prompt));
        if (normalized.Length > MaxPromptLength)
            throw new ArgumentException($"Il prompt di continuazione supera {MaxPromptLength} caratteri.", nameof(prompt));
        return normalized;
    }

    public static bool SelectConversation(
        TrackedProject project, string conversationId, DateTimeOffset now, AutopilotSettings settings)
    {
        if (project.CurrentConversationId == conversationId)
            return false;

        project.CurrentConversationId = conversationId;
        project.PendingAfterNodeId = null;
        project.LastError = null;
        project.Status = AutopilotStatus.Monitoring;
        project.NextCheckAt = now;
        return true;
    }

    public static void Start(TrackedProject project, string? prompt, int? maxContinuations, DateTimeOffset now)
    {
        if (!project.IsActive)
            throw new InvalidOperationException("Attivare il project prima di portare avanti il lavoro.");
        var normalized = NormalizePrompt(prompt);
        if (maxContinuations is < 1 or > 10_000)
            throw new ArgumentException("Il numero massimo di continuazioni deve essere tra 1 e 10000.", nameof(maxContinuations));

        project.ContinuationPrompt = normalized;
        project.MaxContinuations = maxContinuations;
        project.ContinuationsSent = 0;
        project.AutopilotEnabled = true;
        project.BatchPaused = false;
        project.Status = project.RateLimitUntil is { } cooldown && cooldown > now
            ? AutopilotStatus.WaitingRetry
            : AutopilotStatus.Monitoring;
        project.NextCheckAt = project.Status == AutopilotStatus.WaitingRetry ? project.RateLimitUntil : now;
        if (project.Status != AutopilotStatus.WaitingRetry)
        {
            project.ConsecutiveFailures = 0;
            project.LastError = null;
            project.PendingAfterNodeId = null;
        }
    }

    public static AutopilotAction Observe(
        TrackedProject project, ConversationState state, DateTimeOffset now, AutopilotSettings settings)
    {
        var wasGenerating = project.Status is AutopilotStatus.Generating;
        var pendingAdvanced = project.PendingAfterNodeId is not null
            && state.CurrentNodeId is not null
            && project.PendingAfterNodeId != state.CurrentNodeId;
        project.LastCheckAt = now;
        project.CurrentConversationId = state.ConversationId;
        project.ConsecutiveFailures = 0;
        project.LastError = null;
        project.RateLimitUntil = null;
        if (pendingAdvanced)
            project.PendingAfterNodeId = null;

        if (state.Phase == ConversationPhase.Generating)
        {
            project.Status = AutopilotStatus.Generating;
            project.NextCheckAt = now + settings.PollInterval;
            return AutopilotAction.Wait;
        }

        if (project.PendingAfterNodeId is not null)
        {
            if (project.LastContinuationAt is { } sentAt && now - sentAt < settings.SubmissionGrace)
            {
                project.Status = AutopilotStatus.Submitting;
                project.NextCheckAt = now + settings.PollInterval;
                return AutopilotAction.Wait;
            }
            project.PendingAfterNodeId = null;
        }

        if (project.MaxContinuations is { } max && project.ContinuationsSent >= max)
        {
            project.AutopilotEnabled = false;
            project.Status = AutopilotStatus.Completed;
            project.NextCheckAt = null;
            return AutopilotAction.Complete;
        }

        if ((wasGenerating || pendingAdvanced)
            && settings.CompletionPause is { } completionPause
            && completionPause > TimeSpan.Zero)
        {
            project.Status = AutopilotStatus.CoolingDown;
            project.NextCheckAt = now + completionPause;
            return AutopilotAction.Wait;
        }

        project.Status = AutopilotStatus.Submitting;
        return AutopilotAction.Submit;
    }

    public static void SubmissionSent(
        TrackedProject project, string? afterNodeId, bool confirmed, DateTimeOffset now, AutopilotSettings settings)
    {
        project.ContinuationsSent++;
        project.LastContinuationAt = now;
        project.PendingAfterNodeId = afterNodeId;
        project.ConsecutiveFailures = 0;
        project.Status = confirmed ? AutopilotStatus.Generating : AutopilotStatus.Submitting;
        project.LastError = confirmed ? null : "Invio non confermato: controllo la chat prima di riprovare.";
        project.NextCheckAt = now + settings.PollInterval;
    }

    /// <summary>The chat hit ChatGPT's length limit: continue in a new chat, which becomes the one to carry on.</summary>
    public static void ChatRolledOver(
        TrackedProject project, string newConversationId, bool confirmed, DateTimeOffset now, AutopilotSettings settings)
    {
        project.TargetConversationId = newConversationId;
        project.CurrentConversationId = newConversationId;
        project.ChatsCreated++;
        project.ContinuationsSent++;
        project.LastContinuationAt = now;
        project.PendingAfterNodeId = NewChatPending;
        project.ConsecutiveFailures = 0;
        project.Status = confirmed ? AutopilotStatus.Generating : AutopilotStatus.Submitting;
        project.LastError = confirmed ? null : "Invio nella nuova chat non confermato: controllo la chat prima di riprovare.";
        project.NextCheckAt = now + settings.PollInterval;
    }

    public static void Failed(TrackedProject project, string error, DateTimeOffset now, AutopilotSettings settings)
    {
        project.LastCheckAt = now;
        project.ConsecutiveFailures++;
        project.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        project.Status = AutopilotStatus.WaitingRetry;
        project.NextCheckAt = now + settings.RetryInterval;
    }

    public static bool Blocked(
        TrackedProject project, string error, DateTimeOffset now, TimeSpan challengeCooldown)
    {
        if (project.Status == AutopilotStatus.VerificationRequired
            && project.NextCheckAt is { } current && current > now)
            return false;

        project.LastCheckAt = now;
        project.ConsecutiveFailures++;
        project.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        project.Status = AutopilotStatus.VerificationRequired;
        project.RateLimitUntil = null;
        project.NextCheckAt = now + challengeCooldown;
        return true;
    }

    public static bool RateLimited(
        TrackedProject project, string error, DateTimeOffset now, AutopilotSettings settings, bool repeated = false)
    {
        if (project.Status == AutopilotStatus.VerificationRequired
            && project.NextCheckAt is { } verificationUntil && verificationUntil > now)
            return false;
        if (project.RateLimitUntil is { } current && current > now)
            return false;

        var cooldown = repeated
            ? settings.RepeatedRateLimitCooldown ?? TimeSpan.FromMinutes(30)
            : settings.RateLimitCooldown ?? TimeSpan.FromMinutes(15);
        var until = now + cooldown;
        project.LastCheckAt = now;
        project.LastRateLimitAt = now;
        project.ConsecutiveFailures++;
        project.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        project.Status = AutopilotStatus.WaitingRetry;
        project.RateLimitUntil = until;
        project.NextCheckAt = until;
        return true;
    }

    public static bool PauseForBatch(TrackedProject project)
    {
        if (!project.AutopilotEnabled)
            return false;
        project.BatchPaused = true;
        StopInternal(project);
        return true;
    }

    public static bool Resume(TrackedProject project, DateTimeOffset now)
    {
        if (!project.IsActive || string.IsNullOrWhiteSpace(project.ContinuationPrompt))
            return false;
        if (project.AutopilotEnabled && project.Status != AutopilotStatus.VerificationRequired)
            return false;
        ResumeInternal(project, now);
        return true;
    }

    public static bool ResumeFromBatch(TrackedProject project, DateTimeOffset now)
    {
        if (!project.IsActive || !project.BatchPaused || project.AutopilotEnabled)
            return false;
        ResumeInternal(project, now);
        return true;
    }

    private static void ResumeInternal(TrackedProject project, DateTimeOffset now)
    {
        project.BatchPaused = false;
        project.AutopilotEnabled = true;
        var coolingDown = project.RateLimitUntil is { } cooldown && cooldown > now;
        project.Status = coolingDown ? AutopilotStatus.WaitingRetry : AutopilotStatus.Monitoring;
        project.NextCheckAt = coolingDown ? project.RateLimitUntil : now;
        if (!coolingDown)
        {
            project.ConsecutiveFailures = 0;
            project.LastError = null;
        }
    }

    public static void Stop(TrackedProject project)
    {
        project.BatchPaused = false;
        StopInternal(project);
    }

    private static void StopInternal(TrackedProject project)
    {
        project.AutopilotEnabled = false;
        project.Status = AutopilotStatus.Stopped;
        project.NextCheckAt = null;
    }

    public static void Deactivate(TrackedProject project)
    {
        Stop(project);
        project.IsActive = false;
    }
}
