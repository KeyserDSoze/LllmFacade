using LllmFacade.Infrastructure.Browser;
using Xunit;

namespace LllmFacade.Browser.Tests;

public sealed class BrowserOptionsTests
{
    [Fact]
    public void Defaults_AllowMoreThanEightTrackedProjectsToKeepTheirTabs()
    {
        Assert.True(new BrowserOptions().MaxTabsPerEndpoint >= 24);
    }
}
