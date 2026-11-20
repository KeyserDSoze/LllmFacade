using LllmFacade.Infrastructure.Browser;
using Xunit;

namespace LllmFacade.Browser.Tests;

public sealed class BrowserOptionsTests
{
    [Fact]
    public void Defaults_AllowMoreThanEightTrackedProjectsToKeepTheirTabs()
    {
        var options = new BrowserOptions();

        Assert.True(options.MaxTabsPerEndpoint >= 24);
        Assert.True(options.PageLoadToPromptDelayMilliseconds >= 120_000);
        Assert.True(options.PromptToSendDelayMilliseconds >= 3_000);
        Assert.True(options.MinimumPromptSendIntervalMilliseconds >= 30_000);
    }
}
