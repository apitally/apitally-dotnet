namespace Apitally.Tests;

using Xunit;

public class ResourceMonitorTests
{
    [Fact]
    public async Task GetCpuMemoryUsage_ShouldReturnNullFirstThenResourceUsage()
    {
        var resourceMonitor = new ResourceMonitor();

        var firstResult = resourceMonitor.GetCpuMemoryUsage();
        Assert.Null(firstResult);

        await Task.Delay(100);

        var secondResult = resourceMonitor.GetCpuMemoryUsage();
        Assert.NotNull(secondResult);
        Assert.True(secondResult.CpuPercent >= 0);
        Assert.True(secondResult.MemoryRss > 0);
    }
}
