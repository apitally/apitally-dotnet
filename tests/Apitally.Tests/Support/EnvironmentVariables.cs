// Tests share process-wide state: environment variables, activity listeners and meters.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Apitally.Tests.Support;

// Clears Apitally-related environment variables for a test and restores them afterwards.
public sealed class EnvironmentVariables : IDisposable
{
    private static readonly string[] Names =
    [
        "APITALLY_WRITE_TOKEN",
        "APITALLY_ENV",
        "APITALLY_DISABLED",
        "APITALLY_OTLP_ENDPOINT",
        "OTEL_SDK_DISABLED",
    ];

    private readonly Dictionary<string, string?> original = [];

    public EnvironmentVariables(params (string Name, string? Value)[] values)
    {
        foreach (var name in Names.Concat(values.Select(value => value.Name)).Distinct())
        {
            original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        foreach (var (name, value) in values)
            Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in original)
            Environment.SetEnvironmentVariable(name, value);
    }
}
