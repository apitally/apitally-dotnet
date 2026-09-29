using System.Diagnostics;
using OpenTelemetry.Resources;

namespace Apitally;

/// <summary>
/// Apitally's own record of a span, passed to sampling and body masking callbacks. Values not
/// yet known at the callback's stage are unset.
/// </summary>
public sealed class SpanSnapshot
{
    internal SpanSnapshot() { }

    /// <summary>The span's trace ID.</summary>
    public ActivityTraceId TraceId { get; internal init; }

    /// <summary>The span's ID.</summary>
    public ActivitySpanId SpanId { get; internal init; }

    /// <summary>The parent span's ID, or the default value for a root span.</summary>
    public ActivitySpanId ParentSpanId { get; internal init; }

    /// <summary>The span's W3C trace flags.</summary>
    public ActivityTraceFlags TraceFlags { get; internal init; }

    /// <summary>The span's W3C trace state, if any.</summary>
    public string? TraceStateString { get; internal init; }

    /// <summary>The span's name, for example <c>GET /items/{id}</c>.</summary>
    public string DisplayName { get; internal init; } = "";

    /// <summary>The span's kind, for example <see cref="ActivityKind.Server"/>.</summary>
    public ActivityKind Kind { get; internal init; }

    /// <summary>The span's start time, in UTC.</summary>
    public DateTime StartTimeUtc { get; internal init; }

    /// <summary>The span's duration, or <c>null</c> if the span has not ended.</summary>
    public TimeSpan? Duration { get; internal init; }

    /// <summary>The span's status code.</summary>
    public ActivityStatusCode Status { get; internal init; }

    /// <summary>The span's status description, if any.</summary>
    public string? StatusDescription { get; internal init; }

    /// <summary>
    /// The span's attributes. Values are <c>string</c>, <c>bool</c>, <c>long</c>,
    /// <c>double</c>, arrays of these, or <c>null</c>. For example,
    /// <c>http.response.status_code</c> is a <c>long</c>, not an <c>int</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes => OwnedAttributes;

    /// <summary>The span's events.</summary>
    public IReadOnlyList<ActivityEvent> Events => OwnedEvents;

    /// <summary>The span's links.</summary>
    public IReadOnlyList<ActivityLink> Links { get; internal init; } = [];

    /// <summary>The span's OpenTelemetry resource.</summary>
    public Resource Resource { get; internal init; } = Resource.Empty;

    /// <summary>The name of the <see cref="ActivitySource"/> that created the span.</summary>
    public string ScopeName { get; internal init; } = "";

    /// <summary>The version of the <see cref="ActivitySource"/> that created the span.</summary>
    public string? ScopeVersion { get; internal init; }

    internal Dictionary<string, object?> OwnedAttributes { get; init; } = [];
    internal List<ActivityEvent> OwnedEvents { get; init; } = [];
}
