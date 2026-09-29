using Apitally.Hosting;
using Apitally.Logging;

namespace Apitally.Tests.Support;

internal static class TestConfiguration
{
    public const string WriteToken = "apt_3kPmN9xQv2bR7tH4wZ8yL5cE";

    public static RuntimeConfiguration Resolve(Action<ApitallyOptions>? configure = null)
    {
        using var environment = new EnvironmentVariables();
        var options = new ApitallyOptions { WriteToken = WriteToken, Env = "test" };
        configure?.Invoke(options);
        return RuntimeConfiguration.Resolve(options, SdkDiagnostics.None)!;
    }
}
