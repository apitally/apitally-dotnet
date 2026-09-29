using System.Collections.Concurrent;
using System.Diagnostics;
using Apitally.AspNetCore;
using Apitally.Export;
using Apitally.Hosting;
using Apitally.Logging;
using Apitally.Metrics;
using Apitally.Tracing;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OpenTelemetry.Resources;

namespace Apitally.Requests;

// Connects HttpContext, activities and log records to their RequestState, and releases each
// request's detail once both transport completion and SERVER end have been observed.
internal sealed class RequestRegistry(
    RuntimeConfiguration configuration,
    RequestSampling sampling,
    ConsumerUpdates consumerUpdates,
    ApitallyMetrics metrics,
    ErrorAggregates errorAggregates,
    ApitallyBatchProcessor<SpanExportEntry> spanProcessor,
    ApitallyBatchProcessor<LogSnapshot> logProcessor,
    SdkDiagnostics diagnostics
)
{
    // Keyed by trace and span ID; trace IDs alone are shared across requests.
    private readonly ConcurrentDictionary<
        (ActivityTraceId, ActivitySpanId),
        RequestState
    > associations = new();
    private readonly ConcurrentDictionary<RequestState, byte> inFlight = new();
    private volatile bool isCutOff;

    public bool IsEmpty => inFlight.IsEmpty && associations.IsEmpty;

    public static RequestState? Get(HttpContext? context) => context?.Features.Get<RequestState>();

    // Called at SERVER activity start with the provider's resource, or at middleware entry
    // without one. Only requests observed by the span processor keep trace detail.
    public RequestState GetOrCreate(
        HttpContext context,
        Activity? serverActivity,
        Resource? resource
    )
    {
        if (Get(context) is { } existing)
            return existing;
        var request = context.Request;
        var entry = new RequestEntry(
            request.Method.ToUpperInvariant(),
            request.PathBase.Value ?? "",
            request.Path.Value ?? "",
            request.QueryString.HasValue ? request.QueryString.Value![1..] : null,
            request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null,
            request.Headers.ContentEncoding,
            IsWebSocketRequest(context)
        );
        var isObserved = serverActivity?.Recorded == true && resource is not null && !isCutOff;
        var isDetailKept =
            isObserved && ShouldKeepDetail(entry, context, serverActivity!, resource!);
        var state = new RequestState(entry, serverActivity, isDetailKept, context.RequestAborted);
        context.Features.Set(state);
        inFlight.TryAdd(state, 0);
        if (isDetailKept)
            state.TryAssociate(serverActivity!.TraceId, serverActivity.SpanId, associations);
        context.Response.OnCompleted(
            static value =>
            {
                var (registry, state, context) = ((
                    RequestRegistry,
                    RequestState,
                    HttpContext
                ))value;
                registry.CompleteTransport(state, context);
                return Task.CompletedTask;
            },
            (this, state, context)
        );
        return state;
    }

    // Children inherit their parent's request, including parents supplied as explicit contexts.
    public void AssociateDescendant(Activity activity)
    {
        if (associations.TryGetValue((activity.TraceId, activity.ParentSpanId), out var state))
            state.TryAssociate(activity.TraceId, activity.SpanId, associations);
    }

    public bool IsRequestBodyCaptured(HttpContext context, RequestState state) =>
        configuration.CaptureRequestBody
        && state.IsDetailKept
        && BodyCapture.IsAllowedContentType(context.Request.ContentType)
        && BodyCapture.IsSupportedContentEncoding(state.Entry.ContentEncoding);

    public bool IsResponseBodyCaptured(HttpContext context, RequestState state) =>
        configuration.CaptureResponseBody
        && state.IsDetailKept
        && BodyCapture.IsAllowedContentType(context.Response.ContentType)
        && BodyCapture.IsSupportedContentEncoding(context.Response.Headers.ContentEncoding);

    public bool TryGet(ActivityTraceId traceId, ActivitySpanId spanId, out RequestState state) =>
        associations.TryGetValue((traceId, spanId), out state!);

    public void CompleteServer(RequestState state, SpanSnapshot snapshot)
    {
        if (state.CompleteServer(snapshot))
            Release(state);
    }

    // OnCompleted marks the end of transport observation, not proof of client receipt.
    public void CompleteTransport(RequestState state, HttpContext context)
    {
        try
        {
            var completion = CreateCompletion(state, context);
            CommitRequestData(state, completion, context);
            if (state.CompleteTransport(completion))
                Release(state);
        }
        catch (Exception exception)
        {
            diagnostics.RequestProcessingFailed(exception);
        }
    }

    // Discards detail still awaiting completion; already released requests are unaffected.
    public void Cutoff()
    {
        isCutOff = true;
        foreach (var state in inFlight.Keys)
            state.Cutoff(associations);
        inFlight.Clear();
        associations.Clear();
    }

    private bool ShouldKeepDetail(
        RequestEntry entry,
        HttpContext context,
        Activity serverActivity,
        Resource resource
    )
    {
        try
        {
            if (sampling.IsExcluded(entry.Method, entry.Path, entry.UserAgent, entry.IsWebSocket))
                return false;
            var snapshot = sampling.HasRequestCallback
                ? SpanSnapshots.CopyAtRequestStart(serverActivity, context, resource)
                : null;
            return sampling.ShouldKeepAtRequestStage(serverActivity.TraceId, snapshot);
        }
        catch (Exception exception)
        {
            diagnostics.RequestProcessingFailed(exception);
            return false;
        }
    }

    // Scheme, host and client address are read here, after the application's forwarded-headers
    // handling. The path was read at entry, before any path rewriting.
    private TransportCompletion CreateCompletion(RequestState state, HttpContext context)
    {
        state.CaptureException(context.Features.Get<IExceptionHandlerFeature>()?.Error);
        var request = context.Request;
        var response = context.Response;
        var isCanceled = context.RequestAborted.IsCancellationRequested;
        var isBodyless = HttpMethods.IsHead(request.Method) || response.StatusCode is 204 or 304;
        var requestCapture = state.RequestBody;
        var responseCapture = state.ResponseBody;
        long? requestSize = null;
        var isRequestComplete = false;
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false)
        {
            requestSize = 0;
            isRequestComplete = true;
        }
        else if (requestCapture is not null && !requestCapture.IsIncomplete && !isCanceled)
        {
            var declared =
                request.Headers.TransferEncoding.Count == 0 ? request.ContentLength : null;
            isRequestComplete =
                requestCapture.IsEndOfStream
                || (declared is not null && declared == requestCapture.Count);
            requestSize = declared ?? (requestCapture.IsEndOfStream ? requestCapture.Count : null);
        }
        long? responseSize = null;
        var isResponseComplete = false;
        if (responseCapture is not null && !responseCapture.IsIncomplete && !isCanceled)
        {
            var declared =
                response.Headers.TransferEncoding.Count == 0 ? response.ContentLength : null;
            isResponseComplete =
                !isBodyless && (declared is null || declared == responseCapture.Count);
            responseSize = isBodyless
                ? 0
                : declared ?? (responseCapture.HasUnknownLength ? null : responseCapture.Count);
        }
        var captureDetail = state.IsDetailKept;
        return new TransportCompletion(
            response.StatusCode,
            EndpointMetadata.ResolveRoute(context, state.Entry.PathBase),
            request.Scheme,
            request.Host.HasValue ? request.Host.Host : null,
            request.Host.Port,
            context.Connection.RemoteIpAddress?.ToString(),
            requestSize,
            responseSize
        )
        {
            RequestHeaders =
                captureDetail && configuration.CaptureRequestHeaders
                    ? CopyHeaders(request.Headers)
                    : null,
            ResponseHeaders =
                captureDetail && configuration.CaptureResponseHeaders
                    ? CopyHeaders(response.Headers)
                    : null,
            RequestBody = requestCapture?.GetBody(isRequestComplete, state.Entry.ContentEncoding),
            ResponseBody = IsResponseBodyCaptured(context, state)
                ? responseCapture?.GetBody(isResponseComplete, response.Headers.ContentEncoding)
                : null,
            ValidationResponse = isResponseComplete
                ? responseCapture?.GetRetainedBytes(isComplete: true)
                : null,
        };
    }

    // Metrics, error aggregates and consumer updates are independent of exclusion and trace
    // sampling. Only routed requests contribute metrics and errors.
    private void CommitRequestData(
        RequestState state,
        TransportCompletion completion,
        HttpContext context
    )
    {
        var entry = state.Entry;
        if (entry.IsWebSocket)
            return;
        var consumer = state.Consumer;
        if (consumer is not null)
            consumerUpdates.EmitIfChanged(consumer);
        if (entry.Method == "OPTIONS" || completion.Route is not { } route)
            return;
        var validationDetails = state.GetValidationDetails();
        if (validationDetails.Count == 0 && completion.ValidationResponse is { } bytes)
            validationDetails = ValidationCapture.ParseResponse(
                completion.StatusCode,
                context.Response.ContentType,
                context.Response.Headers.ContentEncoding,
                bytes
            );
        errorAggregates.AddValidationErrors(
            consumer?.Identifier,
            entry.Method,
            route,
            validationDetails
        );
        if (completion.StatusCode == 500 && state.Exception is { } exception)
            errorAggregates.AddServerError(consumer?.Identifier, entry.Method, route, exception);
        metrics.RecordRequest(
            entry.Method,
            route,
            completion.StatusCode,
            consumer?.Identifier,
            completion.Scheme,
            Stopwatch.GetElapsedTime(state.StartTimestamp),
            completion.RequestBodySize,
            completion.ResponseBodySize
        );
    }

    // Callbacks and queue submission run outside the request lock, after the single claim.
    private void Release(RequestState state)
    {
        inFlight.TryRemove(state, out _);
        var detail = state.TakeDetail(associations);
        if (detail.Server is not { } server || detail.Transport is not { } transport)
            return;
        try
        {
            SpanSnapshots.EnrichServer(server, state, transport);
            if (!sampling.ShouldKeepAtResponseStage(state.TraceId, server))
                return;
            foreach (var descendant in detail.Descendants)
                spanProcessor.OnEnd(new SpanExportEntry(descendant));
            spanProcessor.OnEnd(
                new SpanExportEntry(server)
                {
                    RequestHeaders = transport.RequestHeaders,
                    ResponseHeaders = transport.ResponseHeaders,
                    RequestBody = transport.RequestBody,
                    ResponseBody = transport.ResponseBody,
                }
            );
            foreach (var log in detail.Logs)
                logProcessor.OnEnd(log);
        }
        catch (Exception exception)
        {
            diagnostics.RequestProcessingFailed(exception);
        }
    }

    // The WebSockets middleware runs inside Apitally's middleware, so the request headers are
    // checked directly: an HTTP/1.1 upgrade or an HTTP/2 extended CONNECT.
    private static bool IsWebSocketRequest(HttpContext context) =>
        context.Request.Headers.Upgrade.Any(value =>
            value?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true
        )
        || context.Features.Get<IHttpExtendedConnectFeature>()
            is { IsExtendedConnect: true, Protocol: var protocol }
            && string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase);

    private static List<KeyValuePair<string, string[]>> CopyHeaders(IHeaderDictionary headers) =>
        [
            .. headers.Select(header => new KeyValuePair<string, string[]>(
                header.Key,
                header.Value.ToArray()!
            )),
        ];
}
