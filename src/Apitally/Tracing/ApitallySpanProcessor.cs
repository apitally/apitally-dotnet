using System.Diagnostics;
using System.Runtime.CompilerServices;
using Apitally.Export;
using Apitally.Logging;
using Apitally.Requests;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace Apitally.Tracing;

// Receives activity start and end from the attached provider and routes recorded activities
// into their request's state. Unassociated activities drop locally.
internal sealed class ApitallySpanProcessor(
    RequestRegistry registry,
    IHttpContextAccessor httpContextAccessor,
    string env,
    SdkDiagnostics diagnostics
)
{
    public const string HostingSourceName = "Microsoft.AspNetCore";
    public const string HostingOperationName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";

    private readonly ConditionalWeakTable<BaseProvider, Resource> exportResources = new();

    public void OnStart(Activity activity, BaseProvider? provider)
    {
        try
        {
            // Only the ASP.NET Core hosting activity starts a request, even with a remote parent.
            if (
                activity.OperationName == HostingOperationName
                && activity.Source.Name == HostingSourceName
            )
            {
                if (httpContextAccessor.HttpContext is { } context)
                    registry.GetOrCreate(context, activity, GetExportResource(provider));
            }
            else if (activity.Recorded)
            {
                registry.AssociateDescendant(activity);
            }
        }
        catch (Exception exception)
        {
            diagnostics.RequestProcessingFailed(exception);
        }
    }

    public void OnEnd(Activity activity, BaseProvider? provider)
    {
        try
        {
            if (
                !registry.TryGet(activity.TraceId, activity.SpanId, out var state)
                || !state.IsAcceptingDetail
            )
                return;
            // An earlier processor can clear Recorded in OnEnd; the request must still be released.
            var snapshot = activity.Recorded
                ? SpanSnapshots.Copy(activity, GetExportResource(provider))
                : null;
            if (activity.SpanId == state.ServerSpanId)
                registry.CompleteServer(state, snapshot);
            else if (snapshot is not null)
                state.AddDescendant(snapshot);
        }
        catch (Exception exception)
        {
            diagnostics.RequestProcessingFailed(exception);
        }
    }

    // Apitally copies carry Apitally's process identity and environment on the provider's resource.
    private Resource GetExportResource(BaseProvider? provider) =>
        provider is null
            ? Resource.Empty
            : exportResources.GetValue(
                provider,
                key => OtlpEncoder.CreateExportResource(key.GetResource(), env)
            );
}
