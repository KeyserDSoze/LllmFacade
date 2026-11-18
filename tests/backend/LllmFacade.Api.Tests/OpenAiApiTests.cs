using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LllmFacade.Business.ChatGpt;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class OpenAiApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(Guid Id, HttpClient Client)> ClientAsync()
    {
        var (id, apiKey) = await CreateEndpointAsync(await AdminAsync(factory));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return (id, client);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lf_chiave-inventata-ma-abbastanza-lunga-12345")]
    public async Task Models_RequireAValidKey(string? key)
    {
        var client = factory.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var response = await client.GetAsync("/v1/models", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_api_key", (await response.JsonAsync()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Models_ListTheEndpointModels()
    {
        var (_, client) = await ClientAsync();

        var models = await (await client.GetAsync("/v1/models", Token)).JsonAsync();

        Assert.Equal("list", models.GetProperty("object").GetString());
        Assert.Equal(["chatgpt-thinking", "chatgpt-auto"], models.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task ChatCompletions_ReturnsOpenAiResponseAndMapsThinking()
    {
        var (id, client) = await ClientAsync();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "chatgpt-thinking",
            reasoning_effort = "high",
            messages = new object[]
            {
                new { role = "system", content = "Rispondi in italiano." },
                new { role = "user", content = new object[] { new { type = "text", text = "Ciao?" } } }
            }
        }, Token);

        var body = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chat.completion", body.GetProperty("object").GetString());
        Assert.Equal("chatgpt-thinking", body.GetProperty("model").GetString());
        var choice = body.GetProperty("choices")[0];
        Assert.Equal("Ciao mondo", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.True(body.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);

        var command = factory.ChatGpt.Commands.Last();
        Assert.Equal("gpt-5-thinking", command.UpstreamModel);
        Assert.Equal("extended", command.ThinkingEffort);
        Assert.True(command.TemporaryChat);
        Assert.Contains("<system>\nRispondi in italiano.\n</system>", command.Prompt);
        Assert.Equal(id, factory.ChatGpt.Identities.Last().EndpointId);
    }

    [Fact]
    public async Task ChatCompletions_UsesTheModelDefaultEffort()
    {
        var (_, client) = await ClientAsync();

        (await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "chatgpt-thinking", messages = new[] { new { role = "user", content = "Ciao" } } }, Token)).EnsureSuccessStatusCode();

        var command = factory.ChatGpt.Commands.Last();
        Assert.Equal("standard", command.ThinkingEffort);
        Assert.Equal("Ciao", command.Prompt);
    }

    [Fact]
    public async Task ChatCompletions_StreamsServerSentEvents()
    {
        var (_, client) = await ClientAsync();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "chatgpt-auto",
            stream = true,
            stream_options = new { include_usage = true },
            messages = new[] { new { role = "user", content = "Ciao" } }
        }, Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = (await response.Content.ReadAsStringAsync(Token))
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e["data: ".Length..])
            .ToList();
        Assert.Equal("[DONE]", events[^1]);
        var chunks = events[..^1].Select(e => JsonDocument.Parse(e).RootElement).ToList();
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString()));
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        var text = string.Concat(chunks
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString()));
        Assert.Equal("Ciao mondo", text);
        Assert.Equal("stop", chunks[^2].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.True(chunks[^1].GetProperty("usage").GetProperty("completion_tokens").GetInt32() > 0);
    }

    [Theory]
    [InlineData("{\"model\":\"inesistente\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", HttpStatusCode.NotFound, "model_not_found")]
    [InlineData("{\"model\":\"chatgpt-auto\",\"reasoning_effort\":\"ultra\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", HttpStatusCode.BadRequest, "invalid_reasoning_effort")]
    [InlineData("{\"model\":\"chatgpt-auto\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"x\"}}]}]}", HttpStatusCode.BadRequest, "unsupported_content")]
    [InlineData("{\"model\":\"chatgpt-auto\",\"messages\":[]}", HttpStatusCode.BadRequest, "missing_messages")]
    [InlineData("{non json", HttpStatusCode.BadRequest, "invalid_json")]
    public async Task ChatCompletions_RejectsInvalidRequestsInOpenAiFormat(string json, HttpStatusCode status, string code)
    {
        var (_, client) = await ClientAsync();

        var response = await client.PostAsync("/v1/chat/completions", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Token);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.JsonAsync()).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCompletions_MapsExpiredChatGptSessionTo503(bool stream)
    {
        var (_, client) = await ClientAsync();
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.SessionExpired, "Sessione ChatGPT scaduta.");
        try
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions",
                new { model = "chatgpt-auto", stream, messages = new[] { new { role = "user", content = "Ciao" } } }, Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = (await response.JsonAsync()).GetProperty("error");
            Assert.Equal("chatgpt_session_expired", error.GetProperty("code").GetString());
            Assert.Equal("Sessione ChatGPT scaduta.", error.GetProperty("message").GetString());
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }
    }

    [Fact]
    public async Task ChatCompletions_RefusesDisabledEndpoints()
    {
        var admin = await AdminAsync(factory);
        var (id, apiKey) = await CreateEndpointAsync(admin);
        var request = JsonSerializer.SerializeToNode(EndpointRequest(), Json)!.AsObject();
        request["cookies"] = null;
        request["isEnabled"] = false;
        (await admin.PutAsJsonAsync($"/api/endpoints/{id}", request, Token)).EnsureSuccessStatusCode();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.GetAsync("/v1/models", Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
