using System.Diagnostics;
using OpenTelemetry.Resources;

namespace Apitally;

/// <summary>
/// Apitally's own record of a span, passed to sampling and body masking callbacks. Values not
/// yet known at the callback's stage are unset.
/// </summary>
public sealed class SpanSnapshot
{
    internal SpanSnapshot(
        ActivityTraceId traceId,
        ActivitySpanId spanId,
        ActivitySpanId parentSpanId,
        ActivityTraceFlags traceFlags,
        string? traceStateString,
        string displayName,
        ActivityKind kind,
        DateTime startTimeUtc,
        TimeSpan? duration,
        ActivityStatusCode status,
        string? statusDescription,
        Dictionary<string, object?> attributes,
        List<ActivityEvent> events,
        IReadOnlyList<ActivityLink> links,
        Resource resource,
        string scopeName,
        string? scopeVersion
    )
    {
        TraceId = traceId;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceFlags = traceFlags;
        TraceStateString = traceStateString;
        DisplayName = displayName;
        Kind = kind;
        StartTimeUtc = startTimeUtc;
        Duration = duration;
        Status = status;
        StatusDescription = statusDescription;
        OwnedAttributes = attributes;
        OwnedEvents = events;
        Links = links;
        Resource = resource;
        ScopeName = scopeName;
        ScopeVersion = scopeVersion;
    }

    /// <summary>The span's trace ID.</summary>
    public ActivityTraceId TraceId { get; }

    /// <summary>The span's ID.</summary>
    public ActivitySpanId SpanId { get; }

    /// <summary>The parent span's ID, or the default value for a root span.</summary>
    public ActivitySpanId ParentSpanId { get; }

    /// <summary>The span's W3C trace flags.</summary>
    public ActivityTraceFlags TraceFlags { get; }

    /// <summary>The span's W3C trace state, if any.</summary>
    public string? TraceStateString { get; }

    /// <summary>The span's name, for example <c>GET /items/{id}</c>.</summary>
    public string DisplayName { get; }

    /// <summary>The span's kind, for example <see cref="ActivityKind.Server"/>.</summary>
    public ActivityKind Kind { get; }

    /// <summary>The span's start time, in UTC.</summary>
    public DateTime StartTimeUtc { get; }

    /// <summary>The span's duration, or <c>null</c> if the span has not ended.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>The span's status code.</summary>
    public ActivityStatusCode Status { get; }

    /// <summary>The span's status description, if any.</summary>
    public string? StatusDescription { get; }

    /// <summary>
    /// The span's attributes. Values are <c>string</c>, <c>bool</c>, <c>long</c>,
    /// <c>double</c>, arrays of these, or <c>null</c>. For example,
    /// <c>http.response.status_code</c> is a <c>long</c>, not an <c>int</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes => OwnedAttributes;

    /// <summary>The span's events.</summary>
    public IReadOnlyList<ActivityEvent> Events => OwnedEvents;

    /// <summary>The span's links.</summary>
    public IReadOnlyList<ActivityLink> Links { get; }

    /// <summary>The span's OpenTelemetry resource.</summary>
    public Resource Resource { get; }

    /// <summary>The name of the <see cref="ActivitySource"/> that created the span.</summary>
    public string ScopeName { get; }

    /// <summary>The version of the <see cref="ActivitySource"/> that created the span.</summary>
    public string? ScopeVersion { get; }

    internal Dictionary<string, object?> OwnedAttributes { get; }
    internal List<ActivityEvent> OwnedEvents { get; }
}
