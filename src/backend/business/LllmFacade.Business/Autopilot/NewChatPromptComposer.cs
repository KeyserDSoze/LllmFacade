namespace LllmFacade.Business.Autopilot;

/// <summary>First message of the chat opened when the previous one reached ChatGPT's length limit.</summary>
public static class NewChatPromptComposer
{
    private const int MaxLastAnswer = 2_500;

    public static string Compose(string? newChatPrompt, string continuationPrompt, string? lastAnswer)
    {
        if (!string.IsNullOrWhiteSpace(newChatPrompt))
            return newChatPrompt.Trim();

        var intro = "La chat precedente di questo project ha raggiunto il limite di lunghezza, quindi proseguiamo in questa nuova chat. " +
            "Riparti dal contesto del project";
        var answer = lastAnswer?.Trim();
        if (string.IsNullOrEmpty(answer))
            return $"{intro}.\n\n{continuationPrompt.Trim()}";

        if (answer.Length > MaxLastAnswer)
            answer = "…" + answer[^MaxLastAnswer..];
        return $"{intro} e da questo ultimo messaggio della chat precedente:\n\n{answer}\n\n{continuationPrompt.Trim()}";
    }
}
