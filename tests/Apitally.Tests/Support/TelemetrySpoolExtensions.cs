using Apitally.Export;

namespace Apitally.Tests.Support;

internal static class TelemetrySpoolExtensions
{
    public static void Append(this TelemetrySpool spool, TelemetrySignal signal, byte[] payload) =>
        spool.Append(signal, payload.Length, stream => stream.Write(payload));
}
