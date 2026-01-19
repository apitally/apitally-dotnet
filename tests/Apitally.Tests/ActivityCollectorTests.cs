namespace Apitally.Tests;

using System.Diagnostics;
using Xunit;

public class ActivityCollectorTests
{
    [Fact]
    public void StartCollection_WhenDisabled_ShouldReturnHandleWithNullTraceId()
    {
        var collector = new ActivityCollector(enabled: false);

        var handle = collector.StartCollection();

        Assert.Null(handle.TraceId);
        Assert.Null(handle.EndCollection());

        collector.Dispose();
    }

    [Fact]
    public void StartCollection_WhenEnabled_ShouldReturnHandleWithTraceId()
    {
        var collector = new ActivityCollector(enabled: true);

        var handle = collector.StartCollection();

        Assert.NotNull(handle.TraceId);
        Assert.Matches("^[a-f0-9]{32}$", handle.TraceId);

        handle.EndCollection();
        collector.Dispose();
    }

    [Fact]
    public void EndCollection_ShouldCollectActivities()
    {
        var collector = new ActivityCollector(enabled: true);
        var source = new ActivitySource("TestSource");

        var handle = collector.StartCollection();
        handle.SetName("TestEndpoint");

        using (var childActivity = source.StartActivity("ChildOperation"))
        {
            childActivity?.SetStatus(ActivityStatusCode.Ok);
            childActivity?.SetTag("test.key", "test.value");
        }

        var activities = handle.EndCollection();

        Assert.NotNull(activities);
        Assert.Equal(2, activities.Count);

        var rootActivity = activities.FirstOrDefault(a => a.ParentSpanId == null);
        Assert.NotNull(rootActivity);
        Assert.Equal("TestEndpoint", rootActivity.Name);
        Assert.Equal("INTERNAL", rootActivity.Kind);

        var childActivityData = activities.FirstOrDefault(a => a.Name == "ChildOperation");
        Assert.NotNull(childActivityData);
        Assert.NotNull(childActivityData.ParentSpanId);
        Assert.Equal("OK", childActivityData.Status);
        Assert.NotNull(childActivityData.Attributes);
        Assert.Equal("test.value", childActivityData.Attributes["test.key"]);

        collector.Dispose();
        source.Dispose();
    }

    [Fact]
    public void EndCollection_ShouldNotCollectUnrelatedActivities()
    {
        var collector = new ActivityCollector(enabled: true);
        var source = new ActivitySource("TestSource");

        var handle = collector.StartCollection();
        var traceId = handle.TraceId;

        handle.EndCollection();

        var handle2 = collector.StartCollection();

        using (var unrelatedActivity = source.StartActivity("Unrelated"))
        {
            // This activity is started after the first collection ended
        }

        var activities = handle2.EndCollection();

        Assert.NotNull(activities);
        Assert.All(activities, a => Assert.NotEqual(traceId, handle2.TraceId));

        collector.Dispose();
        source.Dispose();
    }

    [Fact]
    public void ActivityData_ShouldHaveCorrectTimestampFormat()
    {
        var collector = new ActivityCollector(enabled: true);

        var beforeStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        var handle = collector.StartCollection();
        var activities = handle.EndCollection();
        var afterEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

        Assert.NotNull(activities);
        Assert.Single(activities);

        var activity = activities[0];
        Assert.True(activity.StartTime >= beforeStart);
        Assert.True(activity.EndTime <= afterEnd);
        Assert.True(activity.EndTime >= activity.StartTime);

        collector.Dispose();
    }

    [Fact]
    public void Dispose_ShouldClearCollections()
    {
        var collector = new ActivityCollector(enabled: true);

        var handle = collector.StartCollection();

        collector.Dispose();

        var activities = handle.EndCollection();
        Assert.Null(activities);
    }
}
