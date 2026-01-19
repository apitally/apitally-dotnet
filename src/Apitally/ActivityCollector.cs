namespace Apitally;

using System.Collections.Concurrent;
using System.Diagnostics;
using Apitally.Models;

class ActivityCollector : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("Apitally");
    private readonly ActivityListener? _listener;

    private readonly ConcurrentDictionary<
        ActivityTraceId,
        ConcurrentDictionary<ActivitySpanId, byte>
    > _includedActivityIds = new();
    private readonly ConcurrentDictionary<
        ActivityTraceId,
        ConcurrentBag<ActivityData>
    > _collectedActivities = new();

    public bool Enabled { get; }

    public ActivityCollector(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
            return;

        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                // Sample our root activity and descendants of traces we're collecting
                if (
                    options.Source.Name == "Apitally"
                    || (
                        options.Parent.TraceId != default
                        && _includedActivityIds.ContainsKey(options.Parent.TraceId)
                    )
                )
                {
                    return ActivitySamplingResult.AllDataAndRecorded;
                }
                return ActivitySamplingResult.None;
            },
            ActivityStarted = OnActivityStarted,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Handle StartCollection()
    {
        if (!Enabled)
            return new Handle(null, null, this);

        var activity = ActivitySource.StartActivity("Root", ActivityKind.Internal);
        if (activity == null)
            return new Handle(null, null, this);

        _includedActivityIds[activity.TraceId] = new ConcurrentDictionary<ActivitySpanId, byte>();
        _includedActivityIds[activity.TraceId].TryAdd(activity.SpanId, 0);
        _collectedActivities[activity.TraceId] = [];

        return new Handle(activity.TraceId, activity, this);
    }

    internal void OnActivityStarted(Activity activity)
    {
        if (!_includedActivityIds.TryGetValue(activity.TraceId, out var included))
            return;

        if (activity.ParentSpanId != default && included.ContainsKey(activity.ParentSpanId))
        {
            included.TryAdd(activity.SpanId, 0);
        }
    }

    internal void OnActivityStopped(Activity activity)
    {
        if (!_includedActivityIds.TryGetValue(activity.TraceId, out var included))
            return;
        if (!included.ContainsKey(activity.SpanId))
            return;

        if (_collectedActivities.TryGetValue(activity.TraceId, out var activities))
        {
            activities.Add(SerializeActivity(activity));
        }
    }

    internal List<ActivityData>? GetAndClearActivities(ActivityTraceId? traceId)
    {
        if (traceId == null)
            return null;

        _includedActivityIds.TryRemove(traceId.Value, out _);
        if (_collectedActivities.TryRemove(traceId.Value, out var activities))
        {
            return [.. activities];
        }

        return null;
    }

    private static ActivityData SerializeActivity(Activity activity)
    {
        var data = new ActivityData
        {
            SpanId = activity.SpanId.ToString(),
            ParentSpanId =
                activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            Name = activity.DisplayName,
            Kind = activity.Kind.ToString().ToUpperInvariant(),
            StartTime = ToUnixTimeNanoseconds(activity.StartTimeUtc),
            EndTime = ToUnixTimeNanoseconds(activity.StartTimeUtc + activity.Duration),
        };

        if (activity.Status != ActivityStatusCode.Unset)
        {
            data.Status = activity.Status.ToString().ToUpperInvariant();
        }

        var tags = new Dictionary<string, object?>();
        foreach (var tag in activity.TagObjects)
        {
            tags[tag.Key] = tag.Value;
        }
        if (tags.Count > 0)
        {
            data.Attributes = tags;
        }

        return data;
    }

    private static long ToUnixTimeNanoseconds(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds() * 1_000_000;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _includedActivityIds.Clear();
        _collectedActivities.Clear();
    }

    public class Handle(
        ActivityTraceId? traceId,
        Activity? rootActivity,
        ActivityCollector collector
    )
    {
        private readonly ActivityTraceId? _traceId = traceId;

        public string? TraceId => _traceId?.ToString();

        public void SetName(string name)
        {
            if (rootActivity != null)
            {
                rootActivity.DisplayName = name;
            }
        }

        public List<ActivityData>? EndCollection()
        {
            rootActivity?.Stop();
            return collector.GetAndClearActivities(_traceId);
        }
    }
}
