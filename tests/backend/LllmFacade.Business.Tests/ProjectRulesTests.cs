using LllmFacade.Business;
using Xunit;

namespace LllmFacade.Business.Tests;

public sealed class ProjectRulesTests
{
    [Fact]
    public void NormalizeName_RemovesSurroundingSpaces()
    {
        Assert.Equal("Cliente", ProjectRules.NormalizeName("  Cliente  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeName_RejectsEmptyNames(string? name)
    {
        Assert.Throws<ArgumentException>(() => ProjectRules.NormalizeName(name));
    }

    [Fact]
    public void NormalizeName_RejectsLongNames()
    {
        Assert.Throws<ArgumentException>(() => ProjectRules.NormalizeName(new string('a', 61)));
    }
}

