using LllmFacade.Business.ChatGpt;
using LllmFacade.Infrastructure.Browser;
using Xunit;

namespace LllmFacade.Browser.Tests;

public sealed class ChatGptStreamParserTests
{
    internal const string DeltaStream = """
        event: delta_encoding
        data: "v1"

        event: delta
        data: {"p": "", "o": "add", "v": {"message": {"id": "u1", "author": {"role": "user"}, "content": {"content_type": "text", "parts": ["Ciao"]}, "status": "finished_successfully", "recipient": "all"}, "conversation_id": "conv-1"}, "c": 0}

        event: delta
        data: {"p": "", "o": "add", "v": {"message": {"id": "t1", "author": {"role": "assistant"}, "content": {"content_type": "thoughts", "thoughts": []}, "status": "in_progress", "recipient": "all"}, "conversation_id": "conv-1"}, "c": 1}

        event: delta
        data: {"p": "/message/content/thoughts/0/content", "o": "append", "v": "ragionamento nascosto"}

        event: delta
        data: {"p": "", "o": "add", "v": {"message": {"id": "a1", "author": {"role": "assistant"}, "content": {"content_type": "text", "parts": [""]}, "status": "in_progress", "recipient": "all"}, "conversation_id": "conv-1"}, "c": 2}

        event: delta
        data: {"p": "/message/content/parts/0", "o": "append", "v": "Buon"}

        event: delta
        data: {"v": "giorno"}

        event: delta
        data: {"p": "", "o": "patch", "v": [{"p": "/message/content/parts/0", "o": "append", "v": "!"}, {"p": "/message/status", "o": "replace", "v": "finished_successfully"}]}

        data: {"type": "message_stream_complete", "conversation_id": "conv-1"}

        data: [DONE]


        """;

    [Fact]
    public void Feed_ReturnsOnlyVisibleAssistantTextFromDeltaEncoding()
    {
        var parser = new ChatGptStreamParser();

        var deltas = parser.Feed(DeltaStream);

        Assert.Equal(["Buon", "giorno", "!"], deltas);
        Assert.Equal("Buongiorno!", parser.Text);
        Assert.Equal("conv-1", parser.ConversationId);
        Assert.True(parser.Completed);
        Assert.Null(parser.Error);
    }

    [Fact]
    public void Feed_HandlesArbitraryChunkBoundariesAndCrLf()
    {
        var parser = new ChatGptStreamParser();
        var stream = DeltaStream.Replace("\r\n", "\n").Replace("\n", "\r\n");

        var text = string.Concat(stream.Select(c => string.Concat(parser.Feed(c.ToString()))));

        Assert.Equal("Buongiorno!", text);
        Assert.True(parser.Completed);
    }

    [Fact]
    public void Feed_ReadsLegacyFullMessageEventsAndIgnoresToolCalls()
    {
        const string stream = """
            data: {"message": {"id": "x1", "author": {"role": "assistant"}, "content": {"content_type": "code", "parts": ["search()"]}, "status": "finished_successfully", "recipient": "web.run"}, "conversation_id": "c9", "error": null}

            data: {"message": {"id": "a1", "author": {"role": "assistant"}, "content": {"content_type": "text", "parts": ["Ciao"]}, "status": "in_progress", "recipient": "all"}, "conversation_id": "c9", "error": null}

            data: {"message": {"id": "a1", "author": {"role": "assistant"}, "content": {"content_type": "text", "parts": ["Ciao mondo"]}, "status": "finished_successfully", "end_turn": true, "recipient": "all"}, "conversation_id": "c9", "error": null}

            data: [DONE]


            """;
        var parser = new ChatGptStreamParser();

        Assert.Equal(["Ciao", " mondo"], parser.Feed(stream));
        Assert.Equal("c9", parser.ConversationId);
    }

    [Fact]
    public void Feed_ReportsStreamErrors()
    {
        var parser = new ChatGptStreamParser();

        parser.Feed("data: {\"message\": null, \"conversation_id\": \"c1\", \"error\": \"Qualcosa è andato storto\"}\n\n");

        Assert.Equal("Qualcosa è andato storto", parser.Error);
    }
}

public sealed class ChatGptResponsesTests
{
    [Fact]
    public void ParseSession_ReadsTokenEmailAndExpiry()
    {
        var session = ChatGptResponses.ParseSession(
            """{"user":{"email":"utente@example.com"},"expires":"2026-12-01T00:00:00.000Z","accessToken":"tok"}""");

        Assert.Equal("tok", session.AccessToken);
        Assert.Equal("utente@example.com", session.Email);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), session.Expires);
        Assert.Null(ChatGptResponses.ParseSession("{}").AccessToken);
    }

    [Fact]
    public void ParseProjects_KeepsOnlyProjectGizmosAndCursor()
    {
        var (items, cursor) = ChatGptResponses.ParseProjects("""
            {"items":[
              {"gizmo":{"gizmo":{"id":"g-p-alpha","display":{"name":"Alpha"}}},"conversations":{"items":[]}},
              {"gizmo":{"gizmo":{"id":"g-123","display":{"name":"GPT personalizzato"}}}}
            ],"cursor":"next-1"}
            """);

        Assert.Equal(new ChatGptProject("g-p-alpha", "Alpha"), Assert.Single(items));
        Assert.Equal("next-1", cursor);
    }

    [Fact]
    public void ParseConversations_ReadsUnixAndIsoTimes()
    {
        var (items, cursor) = ChatGptResponses.ParseConversations("""
            {"items":[
              {"id":"c-1","title":"Prima","create_time":1700000000.5,"update_time":"2026-11-18T10:00:00Z"},
              {"id":"../bad","title":"Scartata"},
              {"id":"c-2","title":null}
            ],"cursor":20}
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500), items[0].CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), items[0].UpdatedAt);
        Assert.Equal("(senza titolo)", items[1].Title);
        Assert.Equal("20", cursor);
    }

    internal static string Conversation(string role, string status, string contentType = "text", string recipient = "all", string endTurn = "true") => $$$"""
        {"current_node":"n2","update_time":1700000000,
         "mapping":{
           "n1":{"id":"n1","parent":null,"message":{"author":{"role":"assistant"},"content":{"content_type":"text","parts":["Risposta precedente"]},"status":"finished_successfully","end_turn":true,"recipient":"all"}},
           "n2":{"id":"n2","parent":"n1","message":{"author":{"role":"{{{role}}}"},"content":{"content_type":"{{{contentType}}}","parts":["Ultimo testo"]},"status":"{{{status}}}","end_turn":{{{endTurn}}},"recipient":"{{{recipient}}}"}}
         }}
        """;

    [Fact]
    public void ParseConversationState_FinishedAnswer()
    {
        var state = ChatGptResponses.ParseConversationState("c-1", Conversation("assistant", "finished_successfully"));

        Assert.Equal(ConversationPhase.Finished, state.Phase);
        Assert.Equal("n2", state.CurrentNodeId);
        Assert.Equal("Ultimo testo", state.LastAssistantText);
    }

    [Theory]
    [InlineData("assistant", "in_progress", "text", "all", "null")]
    [InlineData("user", "finished_successfully", "text", "all", "null")]
    [InlineData("assistant", "finished_successfully", "code", "python", "false")]
    [InlineData("tool", "finished_successfully", "text", "all", "null")]
    [InlineData("assistant", "finished_successfully", "text", "all", "false")]
    public void ParseConversationState_StillWorking(string role, string status, string contentType, string recipient, string endTurn)
    {
        var state = ChatGptResponses.ParseConversationState("c-1", Conversation(role, status, contentType, recipient, endTurn));

        Assert.Equal(ConversationPhase.Generating, state.Phase);
    }

    [Fact]
    public void ParseConversationState_PreviewSkipsUserMessages()
    {
        var state = ChatGptResponses.ParseConversationState("c-1", Conversation("user", "finished_successfully"));

        Assert.Equal("Risposta precedente", state.LastAssistantText);
    }
}
