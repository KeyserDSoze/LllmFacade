using LllmFacade.Infrastructure.Browser;
using Xunit;

namespace LllmFacade.Browser.Tests;

public sealed class BrowserConnectionTests
{
    [Fact]
    public void ParseEndpoint_AcceptsOnlyLocalHosts()
    {
        Assert.Equal("127.0.0.1", BrowserCdp.ParseEndpoint("http://127.0.0.1:63401").Host);
        Assert.Equal("localhost", BrowserCdp.ParseEndpoint("http://localhost:63401/").Host);
        Assert.Equal("host.docker.internal", BrowserCdp.ParseEndpoint("http://host.docker.internal:63401").Host);
        Assert.Equal("[::1]", BrowserCdp.ParseEndpoint("http://[::1]:63401").Host);
    }

    [Theory]
    [InlineData("https://example.com:63401")]
    [InlineData("http://user:pass@localhost:63401")]
    [InlineData("http://127.0.0.1:63401/path")]
    [InlineData("ftp://localhost:21")]
    [InlineData("not a uri")]
    public void ParseEndpoint_RejectsUntrustedAddresses(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => BrowserCdp.ParseEndpoint(endpoint));
    }

    [Fact]
    public void HostHeader_PresentsTheRemoteDebuggingEndpointAsLocal()
    {
        Assert.Equal("127.0.0.1:63401", BrowserCdp.HostHeader(
            BrowserCdp.ParseEndpoint("http://host.docker.internal:63401")));
    }

    [Fact]
    public void RewriteWebSocket_ReplacesLoopbackWithTheHostReachableFromTheContainer()
    {
        var rewritten = BrowserCdp.RewriteWebSocket(
            "ws://127.0.0.1:63401/devtools/browser/abc", BrowserCdp.ParseEndpoint("http://host.docker.internal:63401"));

        Assert.Equal("ws://host.docker.internal:63401/devtools/browser/abc", rewritten);
    }

    [Fact]
    public void RewriteWebSocket_RejectsNonWebSocketUrlsAndUserInfo()
    {
        Assert.Throws<ArgumentException>(() => BrowserCdp.RewriteWebSocket(
            "https://localhost:63401/devtools/browser/abc", BrowserCdp.ParseEndpoint("http://localhost:63401")));
        Assert.Throws<ArgumentException>(() => BrowserCdp.RewriteWebSocket(
            "ws://user:pass@localhost:63401/devtools/browser/abc", BrowserCdp.ParseEndpoint("http://localhost:63401")));
    }
}
