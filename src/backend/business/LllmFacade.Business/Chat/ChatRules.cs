using System.Text;

namespace LllmFacade.Business.Chat;

public sealed record ChatMessageInput(string Role, string Content);

/// <summary>ChatGPT receives one user message: OpenAI chat history is flattened into a tagged transcript.</summary>
public static class ChatPromptComposer
{
    public const string TranscriptInstruction =
        "Continue the conversation below. Reply as the assistant to the last <user> message, following the <system> instructions if present.";

    public static string Compose(IReadOnlyList<ChatMessageInput> messages)
    {
        if (messages is null || messages.Count == 0)
            throw new ArgumentException("Serve almeno un messaggio.", nameof(messages));

        var system = new List<string>();
        var turns = new List<(string Role, string Text)>();
        foreach (var message in messages)
        {
            var role = message.Role?.Trim().ToLowerInvariant();
            var text = message.Content?.Trim() ?? string.Empty;
            switch (role)
            {
                case "system" or "developer":
                    if (text.Length > 0)
                        system.Add(text);
                    break;
                case "user" or "assistant" or "tool":
                    if (text.Length > 0)
                        turns.Add((role, text));
                    break;
                default:
                    throw new ArgumentException($"Ruolo non supportato: '{message.Role}'.", nameof(messages));
            }
        }

        if (turns.Count == 0)
            throw new ArgumentException("Serve almeno un messaggio con contenuto.", nameof(messages));
        if (system.Count == 0 && turns is [("user", var only)])
            return only;

        var prompt = new StringBuilder(TranscriptInstruction).Append("\n\n");
        if (system.Count > 0)
            prompt.Append("<system>\n").AppendJoin("\n\n", system).Append("\n</system>\n\n");
        foreach (var (role, text) in turns)
            prompt.Append('<').Append(role).Append(">\n").Append(text).Append("\n</").Append(role).Append(">\n\n");
        return prompt.ToString().TrimEnd();
    }
}

public static class ReasoningEfforts
{
    public static readonly IReadOnlyList<string> Known = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public static string? Normalize(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;
        var normalized = effort.Trim().ToLowerInvariant();
        if (!Known.Contains(normalized))
            throw new ArgumentException($"reasoning_effort non valido: valori ammessi {string.Join(", ", Known)}.", nameof(effort));
        return normalized;
    }

    /// <summary>ChatGPT thinking value for the request; null keeps the ChatGPT default.</summary>
    public static string? Resolve(string? requested, string? modelDefault, IReadOnlyDictionary<string, string> map)
    {
        var effort = Normalize(requested) ?? Normalize(modelDefault);
        return effort is not null && map.TryGetValue(effort, out var thinking) ? thinking : null;
    }
}
