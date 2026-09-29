using Apitally.Hosting;
using Apitally.Requests;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Apitally.AspNetCore;

// The outermost middleware, registered by the startup filter so it also observes responses
// written by exception handlers and compression middleware.
internal sealed class ApitallyMiddleware(RequestDelegate next, TelemetryRuntime runtime)
{
    // Constructed after activation, so an inactive runtime never becomes active later.
    private readonly RequestRegistry? registry = runtime.Registry;

    public async Task InvokeAsync(HttpContext context)
    {
        var state = TryGetOrCreateState(context);
        if (state is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var requestBody = context.Request.Body;
        var responseBodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
        var lifetimeFeature = context.Features.Get<IHttpRequestLifetimeFeature>();
        TryObserveBodies(context, state, responseBodyFeature, lifetimeFeature);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            state.CaptureException(exception);
            // The server completes a response that has not started with an empty 500.
            if (context.Response.HasStarted)
                state.ResponseBody?.MarkIncomplete();
            throw;
        }
        finally
        {
            context.Request.Body = requestBody;
            context.Features.Set(responseBodyFeature);
            context.Features.Set(lifetimeFeature);
        }
    }

    private RequestState? TryGetOrCreateState(HttpContext context)
    {
        if (registry is null)
            return null;
        try
        {
            var serverActivity = context.Features.Get<IHttpActivityFeature>()?.Activity;
            return registry.GetOrCreate(context, serverActivity, resource: null);
        }
        catch (Exception exception)
        {
            runtime.Diagnostics.RequestProcessingFailed(exception);
            return null;
        }
    }

    // Capture decisions use headers only; ineligible bodies are counted but never retained.
    private void TryObserveBodies(
        HttpContext context,
        RequestState state,
        IHttpResponseBodyFeature? responseBodyFeature,
        IHttpRequestLifetimeFeature? lifetimeFeature
    )
    {
        if (registry is null || responseBodyFeature is null || lifetimeFeature is null)
            return;
        try
        {
            var request = context.Request;
            var response = context.Response;
            state.RequestBody = new BodyCapture(
                () => registry.IsRequestBodyCaptured(context, state),
                () => request.ContentLength
            );
            // Validation responses are retained for parsing even when body capture is off.
            state.ResponseBody = new BodyCapture(
                () =>
                    registry.IsResponseBodyCaptured(context, state)
                    || ValidationCapture.IsValidationResponse(
                        response.StatusCode,
                        response.ContentType
                    ),
                () => response.ContentLength
            );
            request.Body = new ObservedStream(request.Body, state.RequestBody, isRequest: true);
            context.Features.Set<IHttpResponseBodyFeature>(
                new ObservedResponseBodyFeature(responseBodyFeature, state.ResponseBody)
            );
            context.Features.Set<IHttpRequestLifetimeFeature>(
                new ObservedRequestLifetimeFeature(
                    lifetimeFeature,
                    state.RequestBody,
                    state.ResponseBody
                )
            );
        }
        catch (Exception exception)
        {
            runtime.Diagnostics.RequestProcessingFailed(exception);
            state.RequestBody = null;
            state.ResponseBody = null;
        }
    }
}

// The developer exception page handles exceptions without exposing an exception feature.
internal sealed class DeveloperPageExceptionCapture : IDeveloperPageExceptionFilter
{
    public Task HandleExceptionAsync(ErrorContext errorContext, Func<ErrorContext, Task> next)
    {
        RequestRegistry.Get(errorContext.HttpContext)?.CaptureException(errorContext.Exception);
        return next(errorContext);
    }
}
