using System.Runtime.InteropServices;

namespace EncodingMetrics;

internal static class Program
{
    internal static readonly string Artifacts = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "artifacts",
            $"net{Environment.Version.Major}.0"
        )
    );

    private static async Task Main()
    {
        Directory.CreateDirectory(Artifacts);
        Console.WriteLine(
            $"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSArchitecture}"
        );
        var metrics = MetricsProbe.Run();
        var replay = EncodingProbe.Run(metrics);
        await HttpProbe.Run(replay);
        Console.WriteLine("PASS: all encoding/metrics probes completed");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
