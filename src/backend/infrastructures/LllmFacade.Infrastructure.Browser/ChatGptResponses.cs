using System.Globalization;
using System.Text.Json;
using LllmFacade.Business.ChatGpt;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>Defensive readers for the JSON returned by the ChatGPT web backend.</summary>
internal static class ChatGptResponses
{
    private const int MaxPreviewLength = 4000;

    public sealed record SessionInfo(string? AccessToken, DateTimeOffset? Expires, string? Email);

    public static SessionInfo ParseSession(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new SessionInfo(null, null, null);
        var token = ReadString(root, "accessToken");
        var expires = ParseTime(root, "expires");
        var email = Child(root, "user") is { } user ? ReadString(user, "email") : null;
        return new SessionInfo(string.IsNullOrWhiteSpace(token) ? null : token, expires, email);
    }

    public static (List<ChatGptProject> Items, string? Cursor) ParseProjects(string json)
    {
        using var document = JsonDocument.Parse(json);
        var projects = new List<ChatGptProject>();
        foreach (var item in Items(document.RootElement))
        {
            var gizmo = Child(item, "gizmo") is { } outer ? Child(outer, "gizmo") ?? outer : item;
            var id = ReadString(gizmo, "id");
            if (!ChatGptIds.IsProjectId(id))
                continue;
            var name = (Child(gizmo, "display") is { } display ? ReadString(display, "name") : null) ?? ReadString(gizmo, "name");
            projects.Add(new ChatGptProject(id!, string.IsNullOrWhiteSpace(name) ? id! : name));
        }
        return (projects, Cursor(document.RootElement));
    }

    public static (List<ChatGptConversation> Items, string? Cursor) ParseConversations(string json)
    {
        using var document = JsonDocument.Parse(json);
        var conversations = new List<ChatGptConversation>();
        foreach (var item in Items(document.RootElement))
        {
            var id = ReadString(item, "id");
            if (!ChatGptIds.IsConversationId(id))
                continue;
            var title = ReadString(item, "title");
            conversations.Add(new ChatGptConversation(
                id!, string.IsNullOrWhiteSpace(title) ? "(senza titolo)" : title, ParseTime(item, "create_time"), ParseTime(item, "update_time")));
        }
        return (conversations, Cursor(document.RootElement));
    }

    public static ConversationState ParseConversationState(string conversationId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var currentNode = ReadString(root, "current_node");
        var updatedAt = ParseTime(root, "update_time");
        if (currentNode is null || Child(root, "mapping") is not { } mapping)
            return new ConversationState(conversationId, currentNode, ConversationPhase.Finished, null, updatedAt);

        var phase = ConversationPhase.Finished;
        if (Child(mapping, currentNode) is { } node && Child(node, "message") is { } message)
            phase = IsFinalAnswer(message) ? ConversationPhase.Finished : ConversationPhase.Generating;
        return new ConversationState(conversationId, currentNode, phase, LastAssistantText(mapping, currentNode), updatedAt);
    }

    /// <summary>The turn is over when the current node is a completed assistant answer addressed to the user.</summary>
    private static bool IsFinalAnswer(JsonElement message)
    {
        var role = Child(message, "author") is { } author ? ReadString(author, "role") : null;
        var status = ReadString(message, "status");
        var recipient = ReadString(message, "recipient") ?? "all";
        var contentType = Child(message, "content") is { } content ? ReadString(content, "content_type") : null;
        var endTurn = message.TryGetProperty("end_turn", out var value) ? value.ValueKind : JsonValueKind.Null;
        return role == "assistant"
            && status is not null && status.StartsWith("finished", StringComparison.Ordinal)
            && endTurn != JsonValueKind.False
            && recipient == "all"
            && contentType is not ("thoughts" or "reasoning_recap");
    }

    private static string? LastAssistantText(JsonElement mapping, string currentNode)
    {
        var nodeId = currentNode;
        for (var step = 0; nodeId is not null && step < 10_000; step++)
        {
            if (Child(mapping, nodeId) is not { } node)
                return null;
            if (Child(node, "message") is { } message
                && Child(message, "author") is { } author && ReadString(author, "role") == "assistant"
                && (ReadString(message, "recipient") ?? "all") == "all"
                && Child(message, "content") is { } content && ReadString(content, "content_type") == "text"
                && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                var text = string.Concat(parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()));
                if (text.Length > 0)
                    return text.Length > MaxPreviewLength ? text[^MaxPreviewLength..] : text;
            }
            nodeId = ReadString(node, "parent");
        }
        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Cursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cursor", out var cursor))
            return null;
        return cursor.ValueKind switch
        {
            JsonValueKind.String when cursor.GetString() is { Length: > 0 } text => text,
            JsonValueKind.Number => cursor.GetRawText(),
            _ => null
        };
    }

    private static DateTimeOffset? ParseTime(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds))
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    private static JsonElement? Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
