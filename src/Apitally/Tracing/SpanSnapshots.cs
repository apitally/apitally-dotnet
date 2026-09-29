using System.Diagnostics;
using Apitally.Export;
using Apitally.Requests;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Resources;

namespace Apitally.Tracing;

// Apitally-owned span records copied from activities. Late enrichment applies to these copies,
// never to the application's activities.
internal static class SpanSnapshots
{
    public const string ConsumerIdentifierAttribute = "apitally.consumer.identifier";

    public static SpanSnapshot Copy(Activity activity, Resource resource) =>
        new(
            activity.TraceId,
            activity.SpanId,
            activity.ParentSpanId,
            activity.ActivityTraceFlags,
            activity.TraceStateString,
            activity.DisplayName,
            activity.Kind,
            activity.StartTimeUtc,
            activity.IsStopped ? activity.Duration : null,
            activity.Status,
            activity.StatusDescription,
            AttributeValues.Normalize(activity.TagObjects),
            [.. activity.Events.Select(CopyEvent)],
            [.. activity.Links.Select(CopyLink)],
            resource,
            activity.Source.Name,
            string.IsNullOrEmpty(activity.Source.Version) ? null : activity.Source.Version
        );

    // HTTP tags can be absent at SERVER start, so request values come from the HttpContext.
    public static SpanSnapshot CopyAtRequestStart(
        Activity activity,
        HttpContext context,
        Resource resource
    )
    {
        var snapshot = Copy(activity, resource);
        var request = context.Request;
        var attributes = snapshot.OwnedAttributes;
        attributes["http.request.method"] = request.Method.ToUpperInvariant();
        attributes["url.scheme"] = request.Scheme;
        attributes["url.path"] = (request.PathBase + request.Path).Value;
        if (request.QueryString.HasValue)
            attributes["url.query"] = request.QueryString.Value![1..];
        if (request.Host.HasValue)
            attributes["server.address"] = request.Host.Host;
        if (request.Headers.UserAgent.Count > 0)
            attributes["user_agent.original"] = request.Headers.UserAgent.ToString();
        return snapshot;
    }

    // Final values observed at transport completion win over those set by instrumentation.
    public static void EnrichServer(
        SpanSnapshot server,
        RequestState state,
        TransportCompletion transport
    )
    {
        var attributes = server.OwnedAttributes;
        foreach (var (key, value) in state.GetAttributes())
            attributes[key] = value;
        var entry = state.Entry;
        attributes["http.request.method"] = entry.Method;
        attributes["url.scheme"] = transport.Scheme;
        attributes["url.path"] = entry.PathBase + entry.Path;
        SetOrRemove(attributes, "url.query", entry.Query);
        SetOrRemove(attributes, "server.address", transport.Host);
        SetOrRemove(attributes, "server.port", (long?)transport.Port);
        SetOrRemove(attributes, "user_agent.original", entry.UserAgent);
        SetOrRemove(attributes, "client.address", transport.ClientAddress);
        SetOrRemove(attributes, "http.route", transport.Route);
        attributes["http.response.status_code"] = (long)transport.StatusCode;
        SetOrRemove(attributes, "http.request.body.size", transport.RequestBodySize);
        SetOrRemove(attributes, "http.response.body.size", transport.ResponseBodySize);
        SetOrRemove(attributes, ConsumerIdentifierAttribute, state.Consumer?.Identifier);
        AddExceptionEvent(server, state);
    }

    // At most one SDK exception event, skipped when instrumentation already recorded one.
    private static void AddExceptionEvent(SpanSnapshot server, RequestState state)
    {
        if (
            state.GetCapturedException() is not { } captured
            || server.OwnedEvents.Any(activityEvent => activityEvent.Name == "exception")
        )
            return;
        var (exception, timestamp) = captured;
        server.OwnedEvents.Add(
            new ActivityEvent(
                "exception",
                timestamp,
                new ActivityTagsCollection
                {
                    ["exception.type"] = exception.GetType().FullName,
                    ["exception.message"] = exception.Message,
                    ["exception.stacktrace"] = ExceptionStacktrace.Get(exception),
                }
            )
        );
    }

    private static void SetOrRemove(
        Dictionary<string, object?> attributes,
        string key,
        object? value
    )
    {
        if (value is null)
            attributes.Remove(key);
        else
            attributes[key] = value;
    }

    private static ActivityEvent CopyEvent(ActivityEvent activityEvent) =>
        new(
            activityEvent.Name,
            activityEvent.Timestamp,
            new ActivityTagsCollection(AttributeValues.Normalize(activityEvent.Tags))
        );

    private static ActivityLink CopyLink(ActivityLink link) =>
        new(
            link.Context,
            link.Tags is null
                ? null
                : new ActivityTagsCollection(AttributeValues.Normalize(link.Tags))
        );
}
