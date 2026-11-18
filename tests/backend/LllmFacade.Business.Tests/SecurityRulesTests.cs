using LllmFacade.Business.Security;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class PasswordRulesTests
{
    [Fact]
    public void Validate_AcceptsLongPasswordDifferentFromCurrent()
    {
        Assert.Empty(PasswordRules.Validate("admin", "admin", "Lunga-Password-2026"));
    }

    [Fact]
    public void Validate_RejectsShortPassword()
    {
        Assert.Contains(PasswordRules.Validate("admin", "admin", "Corta-1"), e => e.Contains("12"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_RejectsMissingPassword(string? password)
    {
        Assert.NotEmpty(PasswordRules.Validate("admin", "admin", password));
    }

    [Fact]
    public void Validate_RejectsUserNameAsPassword()
    {
        Assert.NotEmpty(PasswordRules.Validate("amministratore", "admin", "AMMINISTRATORE"));
    }

    [Fact]
    public void Validate_RejectsUnchangedPassword()
    {
        Assert.NotEmpty(PasswordRules.Validate("admin", "Lunga-Password-2026", "Lunga-Password-2026"));
    }
}

public sealed class ApiKeysTests
{
    [Fact]
    public void Generate_ReturnsVerifiableKeyWithLookupPrefix()
    {
        var generated = ApiKeys.Generate();

        Assert.StartsWith(ApiKeys.Prefix, generated.Key);
        Assert.True(generated.Key.Length >= 35);
        Assert.Equal(generated.Key[..ApiKeys.LookupLength], generated.LookupPrefix);
        Assert.True(ApiKeys.Verify(generated.Key, generated.Hash));
        Assert.DoesNotContain(generated.Key, generated.Hash);
    }

    [Fact]
    public void Generate_ProducesDifferentKeys()
    {
        Assert.NotEqual(ApiKeys.Generate().Key, ApiKeys.Generate().Key);
    }

    [Fact]
    public void Verify_RejectsOtherKey()
    {
        var first = ApiKeys.Generate();
        var second = ApiKeys.Generate();

        Assert.False(ApiKeys.Verify(second.Key, first.Hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-abc")]
    [InlineData("lf_short")]
    public void TryGetLookupPrefix_RejectsMalformedKeys(string? key)
    {
        Assert.False(ApiKeys.TryGetLookupPrefix(key, out _));
    }

    [Fact]
    public void TryGetLookupPrefix_ReturnsPrefixOfValidKey()
    {
        var generated = ApiKeys.Generate();

        Assert.True(ApiKeys.TryGetLookupPrefix(generated.Key, out var prefix));
        Assert.Equal(generated.LookupPrefix, prefix);
    }
}
