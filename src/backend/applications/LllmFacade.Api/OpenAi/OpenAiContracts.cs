using System.Text.Json;
using System.Text.Json.Serialization;

namespace LllmFacade.Api.OpenAi;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessageDto>? Messages { get; set; }
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    [JsonPropertyName("stream_options")] public StreamOptionsDto? StreamOptions { get; set; }
    [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
    [JsonPropertyName("reasoning")] public ReasoningDto? Reasoning { get; set; }
    [JsonPropertyName("n")] public int? N { get; set; }
}

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
}

public sealed class StreamOptionsDto
{
    [JsonPropertyName("include_usage")] public bool IncludeUsage { get; set; }
}

public sealed class ReasoningDto
{
    [JsonPropertyName("effort")] public string? Effort { get; set; }
}

public sealed record ChatCompletion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<CompletionChoice> Choices,
    [property: JsonPropertyName("usage")] CompletionUsage Usage);

public sealed record CompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] AssistantMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record AssistantMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>ChatGPT does not report token usage: values are estimates (about 4 characters per token).</summary>
public sealed record CompletionUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens)
{
    public static CompletionUsage Estimate(string prompt, string completion)
    {
        var promptTokens = (prompt.Length + 3) / 4;
        var completionTokens = (completion.Length + 3) / 4;
        return new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens);
    }
}

public sealed record ChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChunkChoice> Choices,
    [property: JsonPropertyName("usage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompletionUsage? Usage = null);

public sealed record ChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ChunkDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record ChunkDelta(
    [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null);

public sealed record ModelList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<ModelInfo> Data);

public sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

public sealed record OpenAiError([property: JsonPropertyName("error")] OpenAiErrorBody Error);

public sealed record OpenAiErrorBody(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("code")] string? Code);

public sealed class OpenAiRequestException(string message, string? param, string code = "invalid_request") : Exception(message)
{
    public string? Param { get; } = param;
    public string Code { get; } = code;
}
