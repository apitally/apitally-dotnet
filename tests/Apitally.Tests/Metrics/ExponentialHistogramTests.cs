using Apitally.Metrics;

namespace Apitally.Tests.Metrics;

public class ExponentialHistogramTests
{
    // Expected indexes are from OpenTelemetry .NET at scale 3. Exact powers of two are in the
    // lower bucket, and the last pair is just below and above a bucket boundary.
    [Theory]
    [InlineData(0.125, -25)]
    [InlineData(0.0078125, -57)]
    [InlineData(1.09050773266525, 0)]
    [InlineData(1.0905078, 1)]
    public void ValuesMapToOpenTelemetryBucketIndexes(double value, int index) =>
        Assert.Equal(index, ExponentialHistogram.MapToIndex(value));
}
