using System.Diagnostics;
using Apitally.Export;
using Apitally.Tracing;
using Microsoft.AspNetCore.Http;

namespace Apitally.Requests;

// Resolves the current request on each call and never stores an HttpContext. Updates target
// the SERVER span handle rather than Activity.Current, which may be a child.
internal sealed class RequestHelpers(IHttpContextAccessor httpContextAccessor) : IApitally
{
    public void SetConsumer(
        string identifier,
        string? name = null,
        string? group = null,
        IReadOnlyDictionary<string, string?>? attributes = null
    )
    {
        if (RequestRegistry.Get(httpContextAccessor.HttpContext) is not { } state)
            return;
        state.SetConsumer(identifier, name, group, attributes);
        if (state.Consumer is { } consumer)
            SetServerTag(state, SpanSnapshots.ConsumerIdentifierAttribute, consumer.Identifier);
    }

    public void SetRequestAttribute(string key, object? value)
    {
        if (
            RequestRegistry.Get(httpContextAccessor.HttpContext) is not { } state
            || !AttributeValues.TryNormalize(value, out var normalized)
        )
            return;
        state.SetAttribute(key, normalized);
        SetServerTag(state, key, normalized);
    }

    public void CaptureException(Exception exception) =>
        RequestRegistry.Get(httpContextAccessor.HttpContext)?.CaptureException(exception);

    public Activity? StartActivity(string name) =>
        TracingIntegration.ManualSource.StartActivity(name, ActivityKind.Internal);

    // Writes to an ended activity are skipped; Apitally's export copy still receives the value.
    private static void SetServerTag(RequestState state, string key, object? value)
    {
        if (state.ServerActivity is { IsAllDataRequested: true, IsStopped: false } activity)
            activity.SetTag(key, value);
    }
}
