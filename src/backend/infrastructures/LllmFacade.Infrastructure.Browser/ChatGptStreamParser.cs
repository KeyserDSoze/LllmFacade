using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>
/// Incremental parser of the ChatGPT conversation SSE stream. Supports the "v1" delta encoding
/// (add/append/replace/patch operations) and the older full-message events. Only the visible
/// assistant text is returned: thoughts, tool calls and metadata are ignored.
/// </summary>
internal sealed class ChatGptStreamParser
{
    private const string TextPath = "/message/content/parts/0";
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _text = new();
    private MessageState? _current;
    private string? _lastPath;
    private string? _lastOperation;

    public string? ConversationId { get; private set; }
    public bool Completed { get; private set; }
    public string? Error { get; private set; }
    public string Text => _text.ToString();

    public IReadOnlyList<string> Feed(string chunk)
    {
        _pending.Append(chunk);
        var buffer = _pending.ToString();
        var trailingCarriageReturn = buffer.EndsWith('\r');
        if (trailingCarriageReturn)
            buffer = buffer[..^1];
        buffer = buffer.Replace("\r\n", "\n").Replace('\r', '\n');

        var deltas = new List<string>();
        int separator;
        while ((separator = buffer.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
        {
            ProcessEvent(buffer[..separator], deltas);
            buffer = buffer[(separator + 2)..];
        }

        _pending.Clear().Append(buffer);
        if (trailingCarriageReturn)
            _pending.Append('\r');
        return deltas;
    }

    private void ProcessEvent(string block, List<string> deltas)
    {
        string? eventName = null;
        var data = new List<string>();
        foreach (var line in block.Split('\n'))
        {
            if (line.Length == 0 || line[0] == ':')
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            if (field == "event")
                eventName = value;
            else if (field == "data")
                data.Add(value);
        }

        if (data.Count == 0 || eventName == "delta_encoding")
            return;
        var payload = string.Join('\n', data);
        if (payload == "[DONE]")
        {
            Completed = true;
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject json)
            return;
        if (AsString(json["conversation_id"]) is { } conversationId)
            ConversationId = conversationId;
        if (AsString(json["error"]) is { Length: > 0 } error)
        {
            Error = error;
            return;
        }
        if (AsString(json["type"]) is { } type)
        {
            if (type == "message_stream_complete")
                Completed = true;
            return;
        }
        if (json.ContainsKey("v") || json.ContainsKey("p") || json.ContainsKey("o"))
            ApplyOperation(json, deltas);
        else if (json["message"] is JsonObject message)
            ApplyFullMessage(message, deltas);
    }

    private void ApplyOperation(JsonObject json, List<string> deltas)
    {
        var path = json.ContainsKey("p") ? AsString(json["p"]) ?? string.Empty : _lastPath ?? string.Empty;
        var operation = json.ContainsKey("o") ? AsString(json["o"]) : _lastOperation;
        var value = json["v"];
        var isPatch = operation == "patch"
            || (operation is null && value is JsonArray items && items.All(item => item is JsonObject o && o.ContainsKey("p")));
        if (isPatch && value is JsonArray patch)
        {
            foreach (var item in patch.OfType<JsonObject>())
                Apply(AsString(item["p"]) ?? string.Empty, AsString(item["o"]) ?? "append", item["v"], deltas);
            return;
        }
        Apply(path, operation ?? "append", value, deltas);
    }

    private void Apply(string path, string operation, JsonNode? value, List<string> deltas)
    {
        _lastPath = path;
        _lastOperation = operation;
        if (path.Length == 0 && operation == "add" && value is JsonObject added)
        {
            if (AsString(added["conversation_id"]) is { } conversationId)
                ConversationId = conversationId;
            if (added["message"] is JsonObject message)
                StartMessage(message, deltas);
            return;
        }

        if (_current is null || path != TextPath || AsString(value) is not { } text)
            return;
        if (operation == "append")
            Emit(text, deltas);
        else if (operation == "replace" && text.StartsWith(_current.Emitted.ToString(), StringComparison.Ordinal))
            Emit(text[_current.Emitted.Length..], deltas);
    }

    private void StartMessage(JsonObject message, List<string> deltas)
    {
        _current = new MessageState(AsString(message["id"]), IsVisible(message));
        Emit(FirstPart(message), deltas);
    }

    private void ApplyFullMessage(JsonObject message, List<string> deltas)
    {
        var id = AsString(message["id"]);
        if (_current is null || _current.Id != id)
            _current = new MessageState(id, IsVisible(message));
        var full = FirstPart(message);
        var emitted = _current.Emitted.ToString();
        if (full.Length > emitted.Length && full.StartsWith(emitted, StringComparison.Ordinal))
            Emit(full[emitted.Length..], deltas);
    }

    private void Emit(string text, List<string> deltas)
    {
        if (_current is not { Visible: true } || text.Length == 0)
            return;
        var delta = _current.Emitted.Length == 0 && _text.Length > 0 ? "\n\n" + text : text;
        _current.Emitted.Append(text);
        _text.Append(delta);
        deltas.Add(delta);
    }

    private static bool IsVisible(JsonObject message) =>
        AsString(message["author"]?["role"]) == "assistant"
        && AsString(message["content"]?["content_type"]) == "text"
        && (AsString(message["recipient"]) ?? "all") == "all";

    private static string FirstPart(JsonObject message) =>
        message["content"]?["parts"] is JsonArray parts && parts.Count > 0 ? AsString(parts[0]) ?? string.Empty : string.Empty;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed class MessageState(string? id, bool visible)
    {
        public string? Id { get; } = id;
        public bool Visible { get; } = visible;
        public StringBuilder Emitted { get; } = new();
    }
}
