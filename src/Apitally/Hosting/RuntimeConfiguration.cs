using System.Net;
using System.Text.RegularExpressions;
using Apitally.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Apitally.Hosting;

// Applies the Apitally configuration section, the APITALLY_* environment variables, then the
// host environment for a blank Env, before any AddApitally callbacks run as post-configuration.
internal sealed class BaseOptionsConfiguration(IServiceProvider services)
    : IConfigureOptions<ApitallyOptions>
{
    public const string SectionName = "Apitally";

    public void Configure(ApitallyOptions options)
    {
        services.GetService<IConfiguration>()?.GetSection(SectionName).Bind(options);
        if (RuntimeConfiguration.ReadEnvironmentVariable("APITALLY_WRITE_TOKEN") is { } token)
            options.WriteToken = token;
        if (RuntimeConfiguration.ReadEnvironmentVariable("APITALLY_ENV") is { } env)
            options.Env = env;
        if (
            string.IsNullOrWhiteSpace(options.Env)
            && services.GetService<IHostEnvironment>() is { } host
        )
            options.Env =
                host.IsProduction() ? "prod"
                : host.IsDevelopment() ? "dev"
                : host.EnvironmentName.ToLowerInvariant();
    }
}

// Immutable settings resolved once from ApitallyOptions at startup.
internal sealed partial class RuntimeConfiguration
{
    public const string DefaultEnv = "dev";
    public const string DefaultOtlpEndpoint = "https://otlp.apitally.io";
    public static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(100);

    public required string WriteToken { get; init; }
    public required string Env { get; init; }
    public string? AppVersion { get; init; }
    public bool CaptureLogs { get; init; }
    public bool CaptureRequestHeaders { get; init; }
    public bool CaptureRequestBody { get; init; }
    public bool CaptureResponseHeaders { get; init; }
    public bool CaptureResponseBody { get; init; }
    public double SampleRate { get; init; }
    public Func<SpanSnapshot, double?>? SampleOnRequest { get; init; }
    public Func<SpanSnapshot, double?>? SampleOnResponse { get; init; }
    public Func<SpanSnapshot, byte[], byte[]?>? MaskRequestBody { get; init; }
    public Func<SpanSnapshot, byte[], byte[]?>? MaskResponseBody { get; init; }
    public Func<LogRecordSnapshot, LogRecordSnapshot?>? MaskLogRecord { get; init; }

    // User patterns only; the modules using them add the built-in defaults.
    public IReadOnlyList<Regex> MaskQueryParams { get; init; } = [];
    public IReadOnlyList<Regex> MaskHeaders { get; init; } = [];
    public IReadOnlyList<Regex> MaskBodyFields { get; init; } = [];
    public IReadOnlyList<Regex> ExcludePaths { get; init; } = [];

    public Uri OtlpEndpoint { get; init; } = new(DefaultOtlpEndpoint);
    public IWebProxy? Proxy { get; init; }

    // Returns null when telemetry is disabled.
    public static RuntimeConfiguration? Resolve(ApitallyOptions options, SdkDiagnostics diagnostics)
    {
        if (
            options.Disabled
            || IsTruthy(ReadEnvironmentVariable("APITALLY_DISABLED"))
            || IsTruthy(ReadEnvironmentVariable("OTEL_SDK_DISABLED"))
        )
        {
            diagnostics.Disabled();
            return null;
        }
        var writeToken = options.WriteToken?.Trim() ?? "";
        if (!IsValidWriteToken(writeToken, diagnostics))
            return null;
        var endpoint = ReadEnvironmentVariable("APITALLY_OTLP_ENDPOINT") ?? DefaultOtlpEndpoint;

        return new RuntimeConfiguration
        {
            WriteToken = writeToken,
            Env = string.IsNullOrWhiteSpace(options.Env) ? DefaultEnv : options.Env.Trim(),
            AppVersion = string.IsNullOrWhiteSpace(options.AppVersion)
                ? null
                : options.AppVersion.Trim(),
            CaptureLogs = options.CaptureLogs,
            CaptureRequestHeaders = options.CaptureRequestHeaders,
            CaptureRequestBody = options.CaptureRequestBody,
            CaptureResponseHeaders = options.CaptureResponseHeaders,
            CaptureResponseBody = options.CaptureResponseBody,
            // An invalid rate resolves to capturing everything, so no data is lost.
            SampleRate = options.SampleRate is >= 0 and <= 1 ? options.SampleRate : 1.0,
            SampleOnRequest = options.SampleOnRequest,
            SampleOnResponse = options.SampleOnResponse,
            MaskRequestBody = options.MaskRequestBody,
            MaskResponseBody = options.MaskResponseBody,
            MaskLogRecord = options.MaskLogRecord,
            MaskQueryParams = CompilePatterns(
                nameof(options.MaskQueryParams),
                options.MaskQueryParams,
                diagnostics
            ),
            MaskHeaders = CompilePatterns(
                nameof(options.MaskHeaders),
                options.MaskHeaders,
                diagnostics
            ),
            MaskBodyFields = CompilePatterns(
                nameof(options.MaskBodyFields),
                options.MaskBodyFields,
                diagnostics
            ),
            ExcludePaths = CompilePatterns(
                nameof(options.ExcludePaths),
                options.ExcludePaths,
                diagnostics
            ),
            OtlpEndpoint = new Uri(endpoint.TrimEnd('/')),
            Proxy = HttpClient.DefaultProxy,
        };
    }

    public static string? ReadEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value ? value : null;

    public static bool MatchesAny(IReadOnlyList<Regex> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(value))
                return true;
        }
        return false;
    }

    private static bool IsValidWriteToken(string writeToken, SdkDiagnostics diagnostics)
    {
        if (writeToken.Length == 0)
        {
            diagnostics.WriteTokenMissing();
            return false;
        }
        if (!WriteTokenFormat().IsMatch(writeToken))
        {
            // The write token is a credential and must never appear unmasked in logs.
            diagnostics.WriteTokenInvalid(writeToken[..Math.Min(writeToken.Length, 8)] + "...");
            return false;
        }
        return true;
    }

    private static bool IsTruthy(string? value) =>
        value?.ToLowerInvariant() is "1" or "true" or "yes";

    // Custom patterns are case-insensitive by default; inline options such as (?-i:...) override that.
    private static List<Regex> CompilePatterns(
        string optionName,
        IEnumerable<string>? patterns,
        SdkDiagnostics diagnostics
    )
    {
        var compiled = new List<Regex>();
        foreach (var pattern in patterns ?? [])
        {
            try
            {
                compiled.Add(
                    new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        PatternMatchTimeout
                    )
                );
            }
            catch (ArgumentException exception)
            {
                diagnostics.PatternInvalid(optionName, pattern, exception);
            }
        }
        return compiled;
    }

    [GeneratedRegex("^apt_[a-zA-Z0-9]{24}$")]
    private static partial Regex WriteTokenFormat();
}
