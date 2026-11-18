using LllmFacade.Business.Endpoints;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class CookieImportTests
{
    private const string Host = "chatgpt.com";

    [Fact]
    public void Parse_ReadsCookieHeaderLikeTheBrowserSendsIt()
    {
        var cookies = CookieImport.Parse(
            "Cookie: __Secure-next-auth.session-token=abc.def; oai-did=123; __Host-csrf=a=b=c", Host);

        Assert.Equal(3, cookies.Count);
        var session = Assert.Single(cookies, c => c.Name == "__Secure-next-auth.session-token");
        Assert.Equal("abc.def", session.Value);
        Assert.Equal(".chatgpt.com", session.Domain);
        Assert.Equal("/", session.Path);
        Assert.True(session.Secure);
        Assert.Equal(-1, session.Expires);
        var hostOnly = Assert.Single(cookies, c => c.Name == "__Host-csrf");
        Assert.Equal("chatgpt.com", hostOnly.Domain);
        Assert.Equal("a=b=c", hostOnly.Value);
    }

    [Fact]
    public void Parse_ReadsCookieEditorExportAndKeepsOnlyTargetDomain()
    {
        const string json = """
            [
              {"name":"a","value":"1","domain":".chatgpt.com","path":"/","expirationDate":1893456000.5,
               "httpOnly":true,"secure":true,"sameSite":"no_restriction"},
              {"name":"b","value":"2","domain":"sub.chatgpt.com","path":"/x","session":true,"sameSite":"lax"},
              {"name":"x","value":"y","domain":".google.com","path":"/"}
            ]
            """;

        var cookies = CookieImport.Parse(json, Host);

        Assert.Equal(2, cookies.Count);
        var first = cookies[0];
        Assert.Equal(new BrowserCookie("a", "1", ".chatgpt.com", "/", 1893456000.5, true, true, "None"), first);
        Assert.Equal(-1, cookies[1].Expires);
        Assert.Equal("Lax", cookies[1].SameSite);
    }

    [Fact]
    public void Parse_ReadsPlaywrightStorageState()
    {
        const string json = """
            {"cookies":[{"name":"b","value":"2","domain":"chatgpt.com","path":"/","expires":-1,
              "httpOnly":false,"secure":true,"sameSite":"Strict"}],"origins":[]}
            """;

        var cookie = Assert.Single(CookieImport.Parse(json, Host));

        Assert.Equal("Strict", cookie.SameSite);
        Assert.Equal("chatgpt.com", cookie.Domain);
    }

    [Fact]
    public void Parse_KeepsLastDuplicate()
    {
        var cookie = Assert.Single(CookieImport.Parse("a=1; a=2", Host));

        Assert.Equal("2", cookie.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("noequals")]
    [InlineData("[{\"name\":\"x\",\"value\":\"y\",\"domain\":\".google.com\"}]")]
    [InlineData("{not json")]
    [InlineData("a=1\r\nInjected: yes")]
    [InlineData("=value")]
    public void Parse_RejectsInvalidInput(string? input)
    {
        Assert.Throws<ArgumentException>(() => CookieImport.Parse(input, Host));
    }

    [Fact]
    public void StorageState_RoundTripsCookies()
    {
        var cookies = CookieImport.Parse("a=1; __Secure-b=2", Host);

        var json = StorageStates.FromCookies(cookies);

        Assert.Equal(cookies, CookieImport.Parse(json, Host));
        Assert.Equal(2, StorageStates.CountCookies(json));
    }

    [Fact]
    public void StorageState_HashIgnoresCookieOrder()
    {
        var one = StorageStates.FromCookies(CookieImport.Parse("a=1; b=2", Host));
        var two = StorageStates.FromCookies(CookieImport.Parse("b=2; a=1", Host));

        Assert.Equal(StorageStates.Hash(one), StorageStates.Hash(two));
        Assert.NotEqual(StorageStates.Hash(one), StorageStates.Hash(StorageStates.FromCookies(CookieImport.Parse("a=1; b=3", Host))));
    }
}
