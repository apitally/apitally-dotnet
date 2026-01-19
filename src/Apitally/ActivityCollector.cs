namespace Apitally;

using System.Collections.Concurrent;
using System.Diagnostics;
using Apitally.Models;

class ActivityCollector : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("Apitally");
    private readonly ActivityListener? _listener;

    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<string, byte>
    > _includedActivityIds = new();
    private readonly ConcurrentDictionary<
        string,
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
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
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

        var traceId = activity.TraceId.ToString();
        _includedActivityIds[traceId] = new ConcurrentDictionary<string, byte>();
        _includedActivityIds[traceId].TryAdd(activity.SpanId.ToString(), 0);
        _collectedActivities[traceId] = [];

        return new Handle(traceId, activity, this);
    }

    internal void OnActivityStarted(Activity activity)
    {
        var traceId = activity.TraceId.ToString();
        if (!_includedActivityIds.TryGetValue(traceId, out var included))
            return;

        if (
            activity.ParentSpanId != default
            && included.ContainsKey(activity.ParentSpanId.ToString())
        )
        {
            included.TryAdd(activity.SpanId.ToString(), 0);
        }
    }

    internal void OnActivityStopped(Activity activity)
    {
        var traceId = activity.TraceId.ToString();
        var spanId = activity.SpanId.ToString();

        if (!_includedActivityIds.TryGetValue(traceId, out var included))
            return;
        if (!included.ContainsKey(spanId))
            return;

        if (_collectedActivities.TryGetValue(traceId, out var activities))
        {
            activities.Add(SerializeActivity(activity));
        }
    }

    public List<ActivityData>? GetAndClearActivities(string? traceId)
    {
        if (traceId == null)
            return null;

        _includedActivityIds.TryRemove(traceId, out _);
        if (_collectedActivities.TryRemove(traceId, out var activities))
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

        var tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
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

    public class Handle(string? traceId, Activity? rootActivity, ActivityCollector collector)
    {
        public string? TraceId { get; } = traceId;

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
            return collector.GetAndClearActivities(TraceId);
        }
    }
}
