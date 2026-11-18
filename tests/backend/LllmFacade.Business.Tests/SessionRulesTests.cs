using System.Text;
using LllmFacade.Business.Sessions;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class AccessTokensTests
{
    internal static string Jwt(string payloadJson)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode(payloadJson) + ".signature";
    }

    [Fact]
    public void ReadExpiry_ReturnsExpClaimAsUtc()
    {
        var expiry = AccessTokens.ReadExpiry(Jwt("{\"exp\":1893456000,\"sub\":\"user\"}"));

        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), expiry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.%%%.c")]
    public void ReadExpiry_ReturnsNullForUnreadableTokens(string? token)
    {
        Assert.Null(AccessTokens.ReadExpiry(token));
    }

    [Fact]
    public void ReadExpiry_ReturnsNullWithoutExp()
    {
        Assert.Null(AccessTokens.ReadExpiry(Jwt("{\"sub\":\"user\"}")));
    }

    [Fact]
    public void Normalize_StripsBearerPrefixAndKeepsEmptyAsNull()
    {
        Assert.Equal("eyJ.a.b", AccessTokens.Normalize("  Bearer eyJ.a.b "));
        Assert.Null(AccessTokens.Normalize("   "));
        Assert.Throws<ArgumentException>(() => AccessTokens.Normalize("eyJ.a\n.b"));
    }
}

public sealed class SessionRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public void IsDue_WhenNeverRefreshed()
    {
        Assert.True(SessionRefreshPolicy.IsDue(Now, Interval, null, null, null));
    }

    [Fact]
    public void IsDue_FalseWithinIntervalAndValidToken()
    {
        Assert.False(SessionRefreshPolicy.IsDue(Now, Interval, Now.AddMinutes(-10), Now.AddMinutes(-10), Now.AddDays(5)));
    }

    [Fact]
    public void IsDue_AfterInterval()
    {
        Assert.True(SessionRefreshPolicy.IsDue(Now, Interval, Now.AddMinutes(-31), Now.AddMinutes(-31), Now.AddDays(5)));
    }

    [Fact]
    public void IsDue_WhenTokenIsAboutToExpire()
    {
        Assert.True(SessionRefreshPolicy.IsDue(Now, Interval, Now.AddMinutes(-1), Now.AddMinutes(-1), Now.AddMinutes(10)));
    }

    [Fact]
    public void IsDue_BacksOffAfterRecentFailure()
    {
        Assert.False(SessionRefreshPolicy.IsDue(Now, Interval, Now.AddHours(-3), Now.AddMinutes(-1), Now.AddMinutes(-5)));
        Assert.True(SessionRefreshPolicy.IsDue(Now, Interval, Now.AddHours(-3), Now.AddMinutes(-6), Now.AddMinutes(-5)));
    }
}
