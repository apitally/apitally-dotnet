using Apitally.Hosting;

namespace Apitally;

/// <summary>
/// Options for the Apitally SDK. Values are read from the <c>Apitally</c> configuration
/// section and can be overridden in code through <c>AddApitally(options => ...)</c>.
/// </summary>
public sealed class ApitallyOptions
{
    /// <summary>
    /// Write token for the Apitally app. <c>APITALLY_WRITE_TOKEN</c> overrides the configuration
    /// section.
    /// </summary>
    public string? WriteToken { get; set; }

    /// <summary>
    /// Environment name. <c>APITALLY_ENV</c> overrides the configuration section. When blank,
    /// defaults to the lowercased host environment name, with <c>Production</c> and
    /// <c>Development</c> shortened to <c>prod</c> and <c>dev</c>.
    /// </summary>
    public string? Env { get; set; }

    /// <summary>Version of the application, included in the startup event.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Disables all Apitally telemetry.</summary>
    public bool Disabled { get; set; }

    /// <summary>Captures application logs emitted during requests.</summary>
    public bool CaptureLogs { get; set; } = true;

    /// <summary>Captures request headers. Sensitive headers are redacted.</summary>
    public bool CaptureRequestHeaders { get; set; }

    /// <summary>Captures request bodies. Sensitive JSON fields are redacted.</summary>
    public bool CaptureRequestBody { get; set; }

    /// <summary>Captures response headers. Sensitive headers are redacted.</summary>
    public bool CaptureResponseHeaders { get; set; } = true;

    /// <summary>Captures response bodies. Sensitive JSON fields are redacted.</summary>
    public bool CaptureResponseBody { get; set; }

    /// <summary>Probability in [0, 1] that a request's trace and logs are captured.</summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Returns a keep probability in [0, 1] when a request starts, or <c>null</c> to use
    /// <see cref="SampleRate"/>.
    /// </summary>
    public Func<SpanSnapshot, double?>? SampleOnRequest { get; set; }

    /// <summary>
    /// Returns a keep probability in [0, 1] after the response completes, or <c>null</c> to
    /// keep the request-stage decision.
    /// </summary>
    public Func<SpanSnapshot, double?>? SampleOnResponse { get; set; }

    /// <summary>
    /// Returns replacement bytes for a captured request body, or <c>null</c> to redact it.
    /// May run later on another thread.
    /// </summary>
    public Func<SpanSnapshot, byte[], byte[]?>? MaskRequestBody { get; set; }

    /// <summary>
    /// Returns replacement bytes for a captured response body, or <c>null</c> to redact it.
    /// May run later on another thread.
    /// </summary>
    public Func<SpanSnapshot, byte[], byte[]?>? MaskResponseBody { get; set; }

    /// <summary>Returns the supplied log record to keep it, or <c>null</c> to drop it.</summary>
    public Func<LogRecordSnapshot, LogRecordSnapshot?>? MaskLogRecord { get; set; }

    /// <summary>Additional query parameter name patterns to redact.</summary>
    public List<string> MaskQueryParams { get; set; } = [];

    /// <summary>Additional header name patterns to redact.</summary>
    public List<string> MaskHeaders { get; set; } = [];

    /// <summary>Additional JSON body field name patterns to redact.</summary>
    public List<string> MaskBodyFields { get; set; } = [];

    /// <summary>Additional path patterns of requests whose traces and logs are not captured.</summary>
    public List<string> ExcludePaths { get; set; } = [];
}
