using Apitally.Hosting;
using Apitally.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Apitally.Tests.Hosting;

public class RuntimeConfigurationTests
{
    private const string WriteToken = "apt_3kPmN9xQv2bR7tH4wZ8yL5cE";

    [Fact]
    public void ResolvesOptionsIntoImmutableSettings()
    {
        using var environment = new EnvironmentVariables();
        using var diagnostics = new DiagnosticsCollector();
        var options = new ApitallyOptions
        {
            WriteToken = WriteToken,
            Env = "prod",
            AppVersion = "1.2.3",
            MaskHeaders = ["x-internal"],
        };

        var configuration = RuntimeConfiguration.Resolve(options, diagnostics.Diagnostics);
        options.MaskHeaders.Add("x-other");

        Assert.NotNull(configuration);
        Assert.Equal(WriteToken, configuration.WriteToken);
        Assert.Equal("prod", configuration.Env);
        Assert.Equal("1.2.3", configuration.AppVersion);
        Assert.True(configuration.CaptureLogs);
        Assert.True(configuration.CaptureResponseHeaders);
        Assert.False(configuration.CaptureRequestHeaders);
        Assert.Equal(1.0, configuration.SampleRate);
        Assert.Single(configuration.MaskHeaders);
        Assert.Equal(new Uri("https://otlp.apitally.io"), configuration.OtlpEndpoint);
        Assert.Empty(diagnostics.Collector.GetSnapshot());
    }

    [Fact]
    public void MissingWriteTokenDisablesTelemetryWithError()
    {
        using var environment = new EnvironmentVariables();
        using var diagnostics = new DiagnosticsCollector();

        var configuration = RuntimeConfiguration.Resolve(new(), diagnostics.Diagnostics);

        Assert.Null(configuration);
        Assert.Single(diagnostics.Records(LogLevel.Error));
    }

    [Fact]
    public void InvalidWriteTokenDisablesTelemetryWithMaskedError()
    {
        using var environment = new EnvironmentVariables();
        using var diagnostics = new DiagnosticsCollector();

        var configuration = RuntimeConfiguration.Resolve(
            new() { WriteToken = "apt_secretvalue-invalid" },
            diagnostics.Diagnostics
        );

        Assert.Null(configuration);
        var error = Assert.Single(diagnostics.Records(LogLevel.Error));
        Assert.Contains("apt_secr...", error.Message);
        Assert.DoesNotContain("secretvalue", error.Message);
    }

    [Theory]
    [InlineData("APITALLY_DISABLED", "true")]
    [InlineData("OTEL_SDK_DISABLED", " YES ")]
    [InlineData("APITALLY_DISABLED", "1")]
    public void DisableEnvironmentVariablesOverrideOptions(string name, string value)
    {
        using var environment = new EnvironmentVariables((name, value));
        using var diagnostics = new DiagnosticsCollector();

        var configuration = RuntimeConfiguration.Resolve(
            new() { WriteToken = WriteToken, Disabled = false },
            diagnostics.Diagnostics
        );

        Assert.Null(configuration);
        Assert.Empty(diagnostics.Records(LogLevel.Error));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void InvalidSampleRateResolvesToFullCapture(double sampleRate)
    {
        using var environment = new EnvironmentVariables();

        var configuration = RuntimeConfiguration.Resolve(
            new() { WriteToken = WriteToken, SampleRate = sampleRate },
            new DiagnosticsCollector().Diagnostics
        )!;

        Assert.Equal(1.0, configuration.SampleRate);
    }

    [Fact]
    public void InvalidPatternIsDroppedWithErrorWhileOthersRemain()
    {
        using var environment = new EnvironmentVariables();
        using var diagnostics = new DiagnosticsCollector();

        var configuration = RuntimeConfiguration.Resolve(
            new() { WriteToken = WriteToken, MaskBodyFields = ["(unclosed", "iban"] },
            diagnostics.Diagnostics
        );

        Assert.NotNull(configuration);
        Assert.Equal("iban", Assert.Single(configuration.MaskBodyFields).ToString());
        Assert.Single(diagnostics.Records(LogLevel.Error));
    }

    [Fact]
    public void CustomPatternsAreCaseInsensitiveUnlessInlineOptionsSayOtherwise()
    {
        using var environment = new EnvironmentVariables();

        var configuration = RuntimeConfiguration.Resolve(
            new() { WriteToken = WriteToken, MaskQueryParams = ["session", "(?-i:Signature)"] },
            new DiagnosticsCollector().Diagnostics
        )!;

        Assert.True(RuntimeConfiguration.MatchesAny(configuration.MaskQueryParams, "SESSION_ID"));
        Assert.True(RuntimeConfiguration.MatchesAny(configuration.MaskQueryParams, "Signature"));
        Assert.False(RuntimeConfiguration.MatchesAny(configuration.MaskQueryParams, "signature"));
    }
}
