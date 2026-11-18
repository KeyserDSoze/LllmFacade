using LllmFacade.Business.Endpoints;
using LllmFacade.Domain;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class EndpointRulesTests
{
    [Fact]
    public void NormalizeName_TrimsAndLimits()
    {
        Assert.Equal("Account principale", EndpointRules.NormalizeName("  Account principale "));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeName(" "));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeName(new string('a', 81)));
    }

    [Fact]
    public void NormalizeModels_TrimsAndValidates()
    {
        var models = EndpointRules.NormalizeModels([new ModelMapping(" gpt-5-thinking ", " gpt-5-thinking ", "HIGH")]);

        Assert.Equal(new ModelMapping("gpt-5-thinking", "gpt-5-thinking", "high"), Assert.Single(models));
    }

    [Fact]
    public void NormalizeModels_RejectsMissingDuplicateOrInvalidModels()
    {
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels([]));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels(null));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels(
            [new ModelMapping("gpt", "auto", null), new ModelMapping("GPT", "gpt-5", null)]));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels([new ModelMapping("con spazi", "auto", null)]));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels([new ModelMapping("gpt", " ", null)]));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeModels([new ModelMapping("gpt", "auto", "ultra")]));
    }

    [Fact]
    public void NormalizeThinking_AcceptsKnownEffortsOnly()
    {
        var map = EndpointRules.NormalizeThinking(new Dictionary<string, string> { ["HIGH"] = " extended " });

        Assert.Equal("extended", map["high"]);
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeThinking(new Dictionary<string, string> { ["ultra"] = "max" }));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeThinking(new Dictionary<string, string> { ["low"] = " " }));
        Assert.Empty(EndpointRules.NormalizeThinking(null));
    }

    [Fact]
    public void NormalizeHeaders_AcceptsBrowserHeadersAndRejectsReservedOnes()
    {
        var headers = EndpointRules.NormalizeHeaders(new Dictionary<string, string>
        {
            [" Accept-Language "] = "it-IT,it;q=0.9",
            ["sec-ch-ua-platform"] = "\"Windows\""
        });

        Assert.Equal("it-IT,it;q=0.9", headers["Accept-Language"]);
        foreach (var reserved in new[] { "Cookie", "authorization", "Host", "Content-Length", "Proxy-Authorization" })
            Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeHeaders(new Dictionary<string, string> { [reserved] = "x" }));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeHeaders(new Dictionary<string, string> { ["X-Test"] = "a\r\nb" }));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeHeaders(new Dictionary<string, string> { ["bad header"] = "a" }));
    }

    [Fact]
    public void NormalizeUserAgent_IsRequiredSingleLine()
    {
        Assert.Equal("Mozilla/5.0 Test", EndpointRules.NormalizeUserAgent(" Mozilla/5.0 Test "));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeUserAgent(""));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeUserAgent("Mozilla\n/5.0"));
    }

    [Fact]
    public void NormalizeLocaleAndTimeZone_ValidateFormat()
    {
        Assert.Equal("it-IT", EndpointRules.NormalizeLocale(null));
        Assert.Equal("en-US", EndpointRules.NormalizeLocale("en-US"));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeLocale("it IT"));
        Assert.Equal("Europe/Rome", EndpointRules.NormalizeTimeZone(null));
        Assert.Equal("America/Argentina/Buenos_Aires", EndpointRules.NormalizeTimeZone("America/Argentina/Buenos_Aires"));
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeTimeZone("../etc"));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(5, 5)]
    [InlineData(1440, 1440)]
    public void NormalizeRefreshInterval_UsesDefaultAndRange(int? input, int expected)
    {
        Assert.Equal(expected, EndpointRules.NormalizeRefreshInterval(input));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    public void NormalizeRefreshInterval_RejectsOutOfRange(int input)
    {
        Assert.Throws<ArgumentException>(() => EndpointRules.NormalizeRefreshInterval(input));
    }
}
