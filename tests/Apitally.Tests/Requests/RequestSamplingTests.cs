using System.Diagnostics;
using Apitally.Logging;
using Apitally.Requests;
using Apitally.Tests.Support;

namespace Apitally.Tests.Requests;

public class RequestSamplingTests
{
    [Theory]
    [InlineData("GET", "/healthz", null, true)]
    [InlineData("GET", "/api/health-check/", null, true)]
    [InlineData("GET", "/FAVICON.ico", null, true)]
    [InlineData("GET", "/.well-known/openid-configuration", null, true)]
    [InlineData("GET", "/items", "kube-probe/1.29", true)]
    [InlineData("GET", "/items", "GoogleHC/1.0", true)]
    [InlineData("OPTIONS", "/items", null, true)]
    [InlineData("GET", "/internal/metrics", null, true)]
    [InlineData("GET", "/items", "Mozilla/5.0", false)]
    [InlineData("GET", "/pingpong", null, false)]
    public void ExcludesBuiltInAndConfiguredPatterns(
        string method,
        string path,
        string? userAgent,
        bool isExcluded
    )
    {
        var sampling = new RequestSampling(
            TestConfiguration.Resolve(options => options.ExcludePaths = ["^/internal/"]),
            SdkDiagnostics.None
        );

        Assert.Equal(isExcluded, sampling.IsExcluded(method, path, userAgent, isWebSocket: false));
    }

    [Fact]
    public void SamplingComparesLowTraceIdBitsWithTheRoundedThreshold()
    {
        var low = ActivityTraceId.CreateFromString("ffffffffffffffff3fffffffffffffff");
        var high = ActivityTraceId.CreateFromString("00000000000000004000000000000001");

        Assert.True(RequestSampling.ShouldKeep(low, 0.25));
        Assert.False(RequestSampling.ShouldKeep(high, 0.25));
        Assert.True(RequestSampling.ShouldKeep(high, 1));
        Assert.False(RequestSampling.ShouldKeep(low, 0));
    }

    [Fact]
    public void ResponseStageAbstentionKeepsTheRequestDecision()
    {
        var sampling = new RequestSampling(
            TestConfiguration.Resolve(options => options.SampleOnResponse = _ => null),
            SdkDiagnostics.None
        );

        Assert.True(
            sampling.ShouldKeepAtResponseStage(ActivityTraceId.CreateRandom(), TestSpans.Create())
        );
    }

    [Fact]
    public void RequestStageAbstentionUsesTheStaticRate()
    {
        var sampling = new RequestSampling(
            TestConfiguration.Resolve(options =>
            {
                options.SampleRate = 0;
                options.SampleOnRequest = _ => null;
            }),
            SdkDiagnostics.None
        );

        Assert.False(
            sampling.ShouldKeepAtRequestStage(ActivityTraceId.CreateRandom(), TestSpans.Create())
        );
    }
}
