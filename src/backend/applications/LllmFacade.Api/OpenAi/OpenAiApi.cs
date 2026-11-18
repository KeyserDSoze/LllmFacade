using System.Text;
using System.Text.Json;
using LllmFacade.Api.Services;
using LllmFacade.Business.Chat;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Security;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.OpenAi;

/// <summary>OpenAI-compatible surface (/v1/models, /v1/chat/completions) backed by the endpoint's ChatGPT browser.</summary>
public static class OpenAiApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);
    private static readonly TimeSpan HeaderGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public static void MapOpenAiApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");
        v1.MapGet("/models", ListModelsAsync);
        v1.MapPost("/chat/completions", ChatCompletionsAsync);
    }

    private static async Task ListModelsAsync(HttpContext http, LllmFacadeDbContext db)
    {
        if (await AuthenticateAsync(http, db) is not { } endpoint)
            return;
        var created = endpoint.CreatedAt.ToUnixTimeSeconds();
        await WriteJsonAsync(http, StatusCodes.Status200OK, new ModelList("list",
            endpoint.Models.Select(model => new ModelInfo(model.Name, "model", created, "chatgpt")).ToList()));
    }

    private static async Task ChatCompletionsAsync(
        HttpContext http, LllmFacadeDbContext db, EndpointIdentityService identities, IChatGptClient client, TimeProvider time,
        ActivityLog log, ActiveRequests active)
    {
        if (await AuthenticateAsync(http, db) is not { } endpoint)
            return;

        CompletionCommand command;
        string model;
        ChatCompletionRequest request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ChatCompletionRequest>(http.Request.Body, Json, http.RequestAborted)
                ?? throw new OpenAiRequestException("Corpo della richiesta mancante.", null);
            (command, model) = BuildCommand(endpoint, request);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "JSON non valido.", "invalid_request_error", null, "invalid_json");
            return;
        }
        catch (OpenAiRequestException error)
        {
            var status = error.Code == "model_not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
            await WriteErrorAsync(http, status, error.Message, "invalid_request_error", error.Param, error.Code);
            return;
        }

        var identity = identities.CreateIdentity(endpoint);
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var trace = new RequestTrace();
        var started = time.GetTimestamp();
        using var running = active.Begin(endpoint.Id, endpoint.Name, model, request.Stream == true);
        try
        {
            if (request.Stream == true)
                await StreamAsync(http, client, identity, command, id, created, model, request.StreamOptions?.IncludeUsage == true, trace);
            else
                await CompleteAsync(http, client, identity, command, id, created, model, trace);
        }
        finally
        {
            await identities.PersistBrowserStateAsync(endpoint, CancellationToken.None);
            var mode = request.Stream == true ? "streaming" : "risposta unica";
            var seconds = time.GetElapsedTime(started).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            if (trace.Error is { } error)
                await log.WarnAsync(ActivityCategories.OpenAi, $"Richiesta {model} ({mode}) fallita dopo {seconds} s: {error.Message}",
                    endpoint.Id, subject: endpoint.Name);
            else if (!http.RequestAborted.IsCancellationRequested)
                await log.InfoAsync(ActivityCategories.OpenAi,
                    $"Richiesta {model} ({mode}{(command.ThinkingEffort is null ? string.Empty : ", thinking " + command.ThinkingEffort)}) completata in {seconds} s, {trace.Chars} caratteri.",
                    endpoint.Id, subject: endpoint.Name);
            else
                await log.WarnAsync(ActivityCategories.OpenAi, $"Richiesta {model} interrotta dal client dopo {seconds} s.",
                    endpoint.Id, subject: endpoint.Name);
        }
    }

    private sealed class RequestTrace
    {
        public int Chars { get; set; }
        public ChatGptException? Error { get; set; }
    }

    private static (CompletionCommand Command, string Model) BuildCommand(ChatGptEndpoint endpoint, ChatCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new OpenAiRequestException("Il campo model è obbligatorio.", "model", "missing_model");
        var mapping = endpoint.Models.FirstOrDefault(m => m.Name.Equals(request.Model.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new OpenAiRequestException($"Il modello '{request.Model}' non esiste per questo endpoint.", "model", "model_not_found");
        if (request.N is > 1)
            throw new OpenAiRequestException("È supportata una sola risposta (n = 1).", "n", "unsupported_parameter");
        if (request.Messages is not { Count: > 0 })
            throw new OpenAiRequestException("Il campo messages è obbligatorio.", "messages", "missing_messages");

        string prompt;
        try
        {
            prompt = ChatPromptComposer.Compose(request.Messages
                .Select(message => new ChatMessageInput(message.Role ?? string.Empty, ReadContent(message.Content)))
                .ToList());
        }
        catch (ArgumentException error)
        {
            throw new OpenAiRequestException(error.Message, "messages");
        }

        string? thinking;
        try
        {
            thinking = ReasoningEfforts.Resolve(
                request.ReasoningEffort ?? request.Reasoning?.Effort, mapping.DefaultReasoningEffort, endpoint.ThinkingEfforts);
        }
        catch (ArgumentException error)
        {
            throw new OpenAiRequestException(error.Message, "reasoning_effort", "invalid_reasoning_effort");
        }
        return (new CompletionCommand(mapping.UpstreamModel, prompt, thinking, endpoint.UseTemporaryChat), mapping.Name);
    }

    private static string ReadContent(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(ReadTextPart)),
        _ => throw new OpenAiRequestException("Formato di content non valido.", "messages")
    };

    private static string ReadTextPart(JsonElement part) =>
        part.ValueKind == JsonValueKind.Object
        && part.TryGetProperty("type", out var type) && type.GetString() is "text" or "input_text"
        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty
            : throw new OpenAiRequestException("Sono supportati solo contenuti testuali.", "messages", "unsupported_content");

    private static async Task CompleteAsync(
        HttpContext http, IChatGptClient client, ChatGptIdentity identity, CompletionCommand command, string id, long created, string model,
        RequestTrace trace)
    {
        var answer = new StringBuilder();
        try
        {
            await foreach (var chunk in client.CompleteAsync(identity, command, http.RequestAborted))
                answer.Append(chunk.Text);
        }
        catch (ChatGptException error)
        {
            trace.Error = error;
            await WriteUpstreamErrorAsync(http, error);
            return;
        }

        var text = answer.ToString();
        trace.Chars = text.Length;
        await WriteJsonAsync(http, StatusCodes.Status200OK, new ChatCompletion(
            id, "chat.completion", created, model,
            [new CompletionChoice(0, new AssistantMessage("assistant", text), "stop")],
            CompletionUsage.Estimate(command.Prompt, text)));
    }

    private static async Task StreamAsync(
        HttpContext http, IChatGptClient client, ChatGptIdentity identity, CompletionCommand command,
        string id, long created, string model, bool includeUsage, RequestTrace trace)
    {
        var cancellationToken = http.RequestAborted;
        await using var chunks = client.CompleteAsync(identity, command, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = chunks.MoveNextAsync().AsTask();

        // Failures that happen before any output (expired session, anti-bot block) keep a real HTTP status.
        if (await Task.WhenAny(next, Task.Delay(HeaderGrace, cancellationToken)) == next
            && next.Exception?.InnerException is ChatGptException early)
        {
            trace.Error = early;
            await WriteUpstreamErrorAsync(http, early);
            return;
        }

        ServerSentEvents.Prepare(http.Response);
        var answer = new StringBuilder();
        await WriteChunkAsync(new ChunkDelta(Role: "assistant", Content: string.Empty), null);
        try
        {
            while (true)
            {
                while (await Task.WhenAny(next, Task.Delay(KeepAliveInterval, cancellationToken)) != next)
                    await ServerSentEvents.KeepAliveAsync(http.Response, cancellationToken);
                if (!await next)
                    break;
                var chunk = chunks.Current;
                if (chunk.Text.Length > 0)
                {
                    answer.Append(chunk.Text);
                    await WriteChunkAsync(new ChunkDelta(Content: chunk.Text), null);
                }
                if (chunk.IsFinal)
                    break;
                next = chunks.MoveNextAsync().AsTask();
            }

            await WriteChunkAsync(new ChunkDelta(), "stop");
            trace.Chars = answer.Length;
            if (includeUsage)
                await WriteDataAsync(new ChatCompletionChunk(id, "chat.completion.chunk", created, model, [],
                    CompletionUsage.Estimate(command.Prompt, answer.ToString())));
        }
        catch (ChatGptException error)
        {
            trace.Error = error;
            await WriteDataAsync(UpstreamError(error).Body);
        }
        await ServerSentEvents.WriteAsync(http.Response, null, "[DONE]", cancellationToken);

        Task WriteChunkAsync(ChunkDelta delta, string? finishReason) => WriteDataAsync(
            new ChatCompletionChunk(id, "chat.completion.chunk", created, model, [new ChunkChoice(0, delta, finishReason)]));

        Task WriteDataAsync<T>(T payload) =>
            ServerSentEvents.WriteAsync(http.Response, null, JsonSerializer.Serialize(payload, Json), cancellationToken);
    }

    private static async Task<ChatGptEndpoint?> AuthenticateAsync(HttpContext http, LllmFacadeDbContext db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        ChatGptEndpoint? endpoint = null;
        if (ApiKeys.TryGetLookupPrefix(key, out var prefix))
        {
            endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.ApiKeyPrefix == prefix, http.RequestAborted);
            if (endpoint is not null && !ApiKeys.Verify(key!, endpoint.ApiKeyHash))
                endpoint = null;
        }

        if (endpoint is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status401Unauthorized, "Chiave API mancante o non valida.",
                "invalid_request_error", null, "invalid_api_key");
            return null;
        }
        if (!endpoint.IsEnabled)
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, "Endpoint disabilitato dall'amministratore.",
                "invalid_request_error", null, "endpoint_disabled");
            return null;
        }
        return endpoint;
    }

    private static Task WriteUpstreamErrorAsync(HttpContext http, ChatGptException error)
    {
        var (status, body) = UpstreamError(error);
        return WriteJsonAsync(http, status, body);
    }

    private static (int Status, OpenAiError Body) UpstreamError(ChatGptException error)
    {
        var (status, type, code) = error.Failure switch
        {
            ChatGptFailure.RateLimited => (StatusCodes.Status429TooManyRequests, "rate_limit_error", "rate_limit_exceeded"),
            ChatGptFailure.Busy => (StatusCodes.Status429TooManyRequests, "rate_limit_error", "endpoint_busy"),
            ChatGptFailure.Timeout => (StatusCodes.Status504GatewayTimeout, "api_error", "upstream_timeout"),
            ChatGptFailure.SessionExpired => (StatusCodes.Status503ServiceUnavailable, "api_error", "chatgpt_session_expired"),
            ChatGptFailure.Blocked => (StatusCodes.Status503ServiceUnavailable, "api_error", "chatgpt_blocked"),
            _ => (StatusCodes.Status502BadGateway, "api_error", "upstream_error")
        };
        return (status, new OpenAiError(new OpenAiErrorBody(error.Message, type, null, code)));
    }

    private static Task WriteErrorAsync(HttpContext http, int status, string message, string type, string? param, string? code) =>
        WriteJsonAsync(http, status, new OpenAiError(new OpenAiErrorBody(message, type, param, code)));

    private static async Task WriteJsonAsync<T>(HttpContext http, int status, T payload)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, payload, Json, http.RequestAborted);
    }
}
