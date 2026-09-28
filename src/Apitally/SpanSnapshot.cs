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

    public ActivityTraceId TraceId { get; }
    public ActivitySpanId SpanId { get; }
    public ActivitySpanId ParentSpanId { get; }
    public ActivityTraceFlags TraceFlags { get; }
    public string? TraceStateString { get; }
    public string DisplayName { get; }
    public ActivityKind Kind { get; }
    public DateTime StartTimeUtc { get; }

    /// <summary>The span's duration, or <c>null</c> if the span has not ended.</summary>
    public TimeSpan? Duration { get; }
    public ActivityStatusCode Status { get; }
    public string? StatusDescription { get; }
    public IReadOnlyDictionary<string, object?> Attributes => OwnedAttributes;
    public IReadOnlyList<ActivityEvent> Events => OwnedEvents;
    public IReadOnlyList<ActivityLink> Links { get; }
    public Resource Resource { get; }
    public string ScopeName { get; }
    public string? ScopeVersion { get; }

    internal Dictionary<string, object?> OwnedAttributes { get; }
    internal List<ActivityEvent> OwnedEvents { get; }
}
