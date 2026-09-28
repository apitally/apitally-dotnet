using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Apitally.Hosting;
using Apitally.Logging;

namespace Apitally.Requests;

// Exclusions and the two sampling stages. Exclusion answers "never wanted" and runs first;
// excluded requests never invoke a sampling callback.
internal sealed partial class RequestSampling(
    RuntimeConfiguration configuration,
    SdkDiagnostics diagnostics
)
{
    private const int PatternTimeoutMilliseconds = 100;

    public bool IsExcluded(string method, string path, string? userAgent, bool isWebSocket) =>
        isWebSocket
        || method == "OPTIONS"
        || DefaultExcludedPathPattern().IsMatch(path)
        || RuntimeConfiguration.MatchesAny(configuration.ExcludePaths, path)
        || (userAgent is not null && ExcludedUserAgentPattern().IsMatch(userAgent));

    public bool HasRequestCallback => configuration.SampleOnRequest is not null;

    // A null callback result falls back to the static rate.
    public bool ShouldKeepAtRequestStage(ActivityTraceId traceId, SpanSnapshot? snapshot)
    {
        var rate = configuration.SampleRate;
        if (configuration.SampleOnRequest is { } callback && snapshot is not null)
            rate =
                InvokeCallback(callback, snapshot, nameof(ApitallyOptions.SampleOnRequest)) ?? rate;
        return ShouldKeep(traceId, rate);
    }

    // A null callback result keeps the request-stage decision.
    public bool ShouldKeepAtResponseStage(ActivityTraceId traceId, SpanSnapshot snapshot)
    {
        if (configuration.SampleOnResponse is not { } callback)
            return true;
        var rate = InvokeCallback(callback, snapshot, nameof(ApitallyOptions.SampleOnResponse));
        return rate is null || ShouldKeep(traceId, rate.Value);
    }

    // Both stages test the same low 64 trace-ID bits, so the combined rate is the minimum of
    // the two rates, and services sampling at the same rate keep the same traces.
    public static bool ShouldKeep(ActivityTraceId traceId, double rate)
    {
        if (rate >= 1)
            return true;
        if (rate <= 0)
            return false;
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        var value = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        var threshold = Math.Round(rate * 18446744073709551616.0);
        return threshold >= 18446744073709551615.0 || value < (ulong)threshold;
    }

    // Throwing or invalid callbacks fail open for their stage.
    private double? InvokeCallback(
        Func<SpanSnapshot, double?> callback,
        SpanSnapshot snapshot,
        string optionName
    )
    {
        double? rate;
        try
        {
            rate = callback(snapshot);
        }
        catch (Exception exception)
        {
            diagnostics.SamplingCallbackFailed(optionName, exception);
            return 1.0;
        }
        if (rate is null or (>= 0 and <= 1))
            return rate;
        diagnostics.SamplingCallbackInvalid(optionName);
        return 1.0;
    }

    [GeneratedRegex(
        """/_?healthz?/?$|/_?health[-_]?checks?/?$|/_?heart[-_]?beats?/?$|/ping/?$|/ready/?$|/live/?$|/favicon(?:-[\w-]+)?\.(ico|png|svg)$|/apple-touch-icon(?:-[\w-]+)?\.png$|/robots\.txt$|/sitemap\.xml$|/manifest\.json$|/site\.webmanifest$|/service-worker\.js$|/sw\.js$|/\.well-known/""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex DefaultExcludedPathPattern();

    [GeneratedRegex(
        "health[-_ ]?check|microsoft-azure-application-lb|googlehc|kube-probe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex ExcludedUserAgentPattern();
}
