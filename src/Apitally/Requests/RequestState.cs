using System.Collections.Concurrent;
using System.Diagnostics;
using Apitally.AspNetCore;
using Apitally.Export;
using Apitally.Logging;

namespace Apitally.Requests;

// Values observed when the response completed. Body sizes are null when unknown.
internal sealed record TransportCompletion(
    int StatusCode,
    string? Route,
    string Scheme,
    string? Host,
    int? Port,
    string? ClientAddress,
    long? RequestBodySize,
    long? ResponseBodySize
)
{
    public IReadOnlyList<KeyValuePair<string, string[]>>? RequestHeaders { get; init; }
    public IReadOnlyList<KeyValuePair<string, string[]>>? ResponseHeaders { get; init; }
    public CapturedBody? RequestBody { get; init; }
    public CapturedBody? ResponseBody { get; init; }

    // Complete response bytes retained for validation parsing, never exported.
    public byte[]? ValidationResponse { get; init; }
}

// The request detail released at finalization, in export order.
internal sealed record RequestDetail(
    SpanSnapshot? Server,
    IReadOnlyList<SpanSnapshot> Descendants,
    IReadOnlyList<LogSnapshot> Logs,
    TransportCompletion? Transport
);

// Everything Apitally tracks for one monitored request, shared by the middleware, the span
// processor, the logger provider and the request helpers. Mutations take a short lock;
// callbacks and exports run outside it.
internal sealed class RequestState
{
    public const int MaxBufferedSpans = 1_000;
    public const int MaxBufferedLogs = 1_000;

    private readonly object sync = new();
    private readonly List<(ActivityTraceId, ActivitySpanId)> associationKeys = [];
    private readonly List<SpanSnapshot> descendants = [];
    private readonly List<LogSnapshot> logs = [];
    private readonly Dictionary<string, object?> requestAttributes = [];
    private readonly List<ValidationDetail> validationDetails = [];
    private readonly CancellationToken requestAborted;
    private RequestConsumer? consumer;
    private (Exception Exception, DateTimeOffset Timestamp)? capturedException;
    private SpanSnapshot? server;
    private TransportCompletion? transport;
    private bool isServerAssociated;
    private bool isServerComplete;
    private bool isTransportComplete;
    private bool isFinalized;

    public RequestState(
        RequestEntry entry,
        Activity? serverActivity,
        bool isDetailKept,
        CancellationToken requestAborted
    )
    {
        Entry = entry;
        this.requestAborted = requestAborted;
        IsDetailKept = isDetailKept;
        if (serverActivity is not null)
        {
            ServerActivity = serverActivity;
            TraceId = serverActivity.TraceId;
            ServerSpanId = serverActivity.SpanId;
        }
    }

    public RequestEntry Entry { get; }
    public long StartTimestamp { get; } = Stopwatch.GetTimestamp();

    // Set by the middleware; null when the middleware did not observe the request.
    public BodyCapture? RequestBody { get; set; }
    public BodyCapture? ResponseBody { get; set; }

    // The SERVER activity handle, independent of Activity.Current becoming a child.
    public Activity? ServerActivity { get; }
    public ActivityTraceId TraceId { get; }
    public ActivitySpanId ServerSpanId { get; }

    // Whether trace and log detail is still wanted after exclusion and request sampling.
    public bool IsDetailKept { get; }

    public RequestConsumer? Consumer
    {
        get
        {
            lock (sync)
                return consumer;
        }
    }

    public Exception? Exception
    {
        get
        {
            lock (sync)
                return capturedException?.Exception;
        }
    }

    public bool IsAcceptingDetail
    {
        get
        {
            lock (sync)
                return IsDetailKept && !isFinalized;
        }
    }

    public void SetConsumer(
        string identifier,
        string? name,
        string? group,
        IReadOnlyDictionary<string, string?>? attributes
    )
    {
        lock (sync)
            consumer = ConsumerUpdates.Apply(consumer, identifier, name, group, attributes);
    }

    public void SetAttribute(string key, object? value)
    {
        lock (sync)
            requestAttributes[key] = value;
    }

    // Typed details from the first framework hook win; later hooks see the same errors.
    public void AddValidationDetails(IEnumerable<ValidationDetail> details)
    {
        lock (sync)
        {
            if (validationDetails.Count == 0)
                validationDetails.AddRange(details);
        }
    }

    public List<ValidationDetail> GetValidationDetails()
    {
        lock (sync)
            return [.. validationDetails];
    }

    public Dictionary<string, object?> GetAttributes()
    {
        lock (sync)
            return new(requestAttributes);
    }

    // Keeps only the first exception; request cancellation is not a server error. The
    // timestamp uses the same clock as activities.
    public void CaptureException(Exception? exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];
        if (
            exception is null
            || (exception is OperationCanceledException && requestAborted.IsCancellationRequested)
        )
            return;
        lock (sync)
            capturedException ??= (exception, DateTimeOffset.UtcNow);
    }

    public (Exception Exception, DateTimeOffset Timestamp)? GetCapturedException()
    {
        lock (sync)
            return capturedException;
    }

    public bool TryAssociate(
        ActivityTraceId traceId,
        ActivitySpanId spanId,
        ConcurrentDictionary<(ActivityTraceId, ActivitySpanId), RequestState> associations
    )
    {
        lock (sync)
        {
            if (!IsDetailKept || isFinalized)
                return false;
            associationKeys.Add((traceId, spanId));
            associations[(traceId, spanId)] = this;
            isServerAssociated |= spanId == ServerSpanId;
            return true;
        }
    }

    public void AddDescendant(SpanSnapshot span)
    {
        lock (sync)
        {
            if (!isFinalized && descendants.Count < MaxBufferedSpans)
                descendants.Add(span);
        }
    }

    public void AddLog(LogSnapshot log)
    {
        lock (sync)
        {
            if (IsDetailKept && !isFinalized && logs.Count < MaxBufferedLogs)
                logs.Add(log);
        }
    }

    // Each returns true when this call made the request ready for its single finalization.
    public bool CompleteServer(SpanSnapshot snapshot)
    {
        lock (sync)
        {
            if (isServerComplete)
                return false;
            isServerComplete = true;
            server = snapshot;
            return TryClaimFinalization();
        }
    }

    public bool CompleteTransport(TransportCompletion completion)
    {
        lock (sync)
        {
            if (isTransportComplete)
                return false;
            isTransportComplete = true;
            transport = completion;
            return TryClaimFinalization();
        }
    }

    // Removes the request's associations so later spans and logs drop locally, and hands over
    // the buffered detail exactly once.
    public RequestDetail TakeDetail(
        ConcurrentDictionary<(ActivityTraceId, ActivitySpanId), RequestState> associations
    )
    {
        lock (sync)
        {
            RemoveAssociations(associations);
            var detail = new RequestDetail(
                IsDetailKept ? server : null,
                [.. descendants],
                [.. logs],
                transport
            );
            descendants.Clear();
            logs.Clear();
            server = null;
            return detail;
        }
    }

    // Discards detail for a request that has not been finalized by the shutdown cutoff.
    public void Cutoff(
        ConcurrentDictionary<(ActivityTraceId, ActivitySpanId), RequestState> associations
    )
    {
        lock (sync)
        {
            isFinalized = true;
            RemoveAssociations(associations);
            descendants.Clear();
            logs.Clear();
            server = null;
        }
    }

    // Detail waits for both transport completion and SERVER end; without kept detail, transport
    // completion alone finalizes. A SERVER activity dropped after start, for example by an
    // instrumentation filter, never reports its end.
    private bool TryClaimFinalization()
    {
        var isAwaitingServer =
            isServerAssociated
            && !isServerComplete
            && ServerActivity is { IsAllDataRequested: true, Recorded: true };
        if (isFinalized || !isTransportComplete || isAwaitingServer)
            return false;
        isFinalized = true;
        return true;
    }

    private void RemoveAssociations(
        ConcurrentDictionary<(ActivityTraceId, ActivitySpanId), RequestState> associations
    )
    {
        foreach (var key in associationKeys)
            associations.TryRemove(key, out _);
        associationKeys.Clear();
    }
}

// Request values read when the request entered the pipeline. Content-Encoding is read here
// because request decompression middleware removes it.
internal sealed record RequestEntry(
    string Method,
    string PathBase,
    string Path,
    string? Query,
    string? UserAgent,
    string? ContentEncoding,
    bool IsWebSocket
);
