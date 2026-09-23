using System.Diagnostics;
using OpenTelemetry.Resources;

// Experimental owned export data, not a proposed public SDK span or callback API.
internal sealed record SpanSnapshot(
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    string Name,
    ActivityKind Kind,
    string SourceName,
    string? SourceVersion,
    DateTime StartTimeUtc,
    TimeSpan Duration,
    string? TraceState,
    ActivityTraceFlags Flags,
    ActivityStatusCode Status,
    string? StatusDescription,
    Dictionary<string, object?> Tags,
    Dictionary<string, object?> Resource,
    SnapshotEvent[] Events,
    SnapshotLink[] Links
)
{
    internal byte[]? RawBody { get; set; }

    internal static SpanSnapshot Copy(Activity activity, Resource resource) =>
        new(
            activity.TraceId,
            activity.SpanId,
            activity.ParentSpanId,
            activity.DisplayName,
            activity.Kind,
            activity.Source.Name,
            activity.Source.Version,
            activity.StartTimeUtc,
            activity.Duration,
            activity.TraceStateString,
            activity.ActivityTraceFlags,
            activity.Status,
            activity.StatusDescription,
            CopyTags(activity.TagObjects),
            CopyTags(
                resource.Attributes.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value))
            ),
            activity
                .Events.Select(e => new SnapshotEvent(e.Name, e.Timestamp, CopyTags(e.Tags)))
                .ToArray(),
            activity
                .Links.Select(l => new SnapshotLink(l.Context, CopyTags(l.Tags ?? [])))
                .ToArray()
        );

    internal static Dictionary<string, object?> CopyTags(
        IEnumerable<KeyValuePair<string, object?>> tags
    )
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in tags)
        {
            if (value is null || IsScalar(value.GetType()))
            {
                result[key] = value;
            }
            else if (
                value is Array array
                && array.Rank == 1
                && IsScalar(array.GetType().GetElementType()!)
            )
            {
                result[key] = array.Clone();
            }
            // Arbitrary objects are omitted, not retained or stringified on the request thread.
        }
        return result;
    }

    private static bool IsScalar(Type type) =>
        type == typeof(string)
        || type == typeof(bool)
        || type == typeof(byte)
        || type == typeof(short)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(float)
        || type == typeof(double);
}

internal sealed record SnapshotEvent(
    string Name,
    DateTimeOffset Timestamp,
    Dictionary<string, object?> Tags
);

internal sealed record SnapshotLink(ActivityContext Context, Dictionary<string, object?> Tags);
