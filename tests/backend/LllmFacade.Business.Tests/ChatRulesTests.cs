using LllmFacade.Business.Chat;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class ChatPromptComposerTests
{
    [Fact]
    public void Compose_SingleUserMessageIsSentAsIs()
    {
        Assert.Equal("Ciao\nmondo", ChatPromptComposer.Compose([new ChatMessageInput("user", "Ciao\nmondo")]));
    }

    [Fact]
    public void Compose_KeepsSystemInstructionsAndTurnOrder()
    {
        var prompt = ChatPromptComposer.Compose(
        [
            new ChatMessageInput("system", "Rispondi in italiano."),
            new ChatMessageInput("developer", "Sii breve."),
            new ChatMessageInput("user", "Domanda 1"),
            new ChatMessageInput("assistant", "Risposta 1"),
            new ChatMessageInput("user", "Domanda 2")
        ]);

        var system = prompt.IndexOf("<system>\nRispondi in italiano.\n\nSii breve.\n</system>", StringComparison.Ordinal);
        var firstUser = prompt.IndexOf("<user>\nDomanda 1\n</user>", StringComparison.Ordinal);
        var assistant = prompt.IndexOf("<assistant>\nRisposta 1\n</assistant>", StringComparison.Ordinal);
        var lastUser = prompt.IndexOf("<user>\nDomanda 2\n</user>", StringComparison.Ordinal);
        Assert.True(system >= 0 && system < firstUser && firstUser < assistant && assistant < lastUser, prompt);
        Assert.Contains(ChatPromptComposer.TranscriptInstruction, prompt);
    }

    [Fact]
    public void Compose_RejectsEmptyOrUnknownMessages()
    {
        Assert.Throws<ArgumentException>(() => ChatPromptComposer.Compose([]));
        Assert.Throws<ArgumentException>(() => ChatPromptComposer.Compose([new ChatMessageInput("user", "  ")]));
        Assert.Throws<ArgumentException>(() => ChatPromptComposer.Compose([new ChatMessageInput("robot", "x")]));
    }
}

public sealed class ReasoningEffortsTests
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["medium"] = "standard",
        ["high"] = "extended"
    };

    [Theory]
    [InlineData("high", null, "extended")]
    [InlineData("HIGH", null, "extended")]
    [InlineData(null, "medium", "standard")]
    [InlineData("medium", "high", "standard")]
    [InlineData(null, null, null)]
    [InlineData("low", null, null)]
    public void Resolve_MapsRequestedOrDefaultEffort(string? requested, string? modelDefault, string? expected)
    {
        Assert.Equal(expected, ReasoningEfforts.Resolve(requested, modelDefault, Map));
    }

    [Fact]
    public void Resolve_RejectsUnknownEffort()
    {
        Assert.Throws<ArgumentException>(() => ReasoningEfforts.Resolve("ultra", null, Map));
    }
}
