namespace Apitally;

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;

public class ApitallyMiddleware(RequestDelegate next, ApitallyClient client, ILogger<ApitallyMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ApitallyClient _client = client;
    private readonly ILogger<ApitallyMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? exception = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            try
            {
                stopwatch.Stop();
                var responseTimeMs = stopwatch.ElapsedMilliseconds;
                var endpoint = context.GetEndpoint();
                var routePattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
                var statusCode = exception != null ? 500 : context.Response.StatusCode;

                // Handle consumer registration
                var consumer = context.Items.TryGetValue("ApitallyConsumer", out var consumerObj) ?
                    ConsumerRegistry.ConsumerFromObject(consumerObj) : null;
                _client.ConsumerRegistry.AddOrUpdateConsumer(consumer);
                var consumerIdentifier = consumer?.Identifier ?? "";

                if (routePattern != null)
                {
                    // Add request to counter
                    _client.RequestCounter.AddRequest(
                        consumerIdentifier,
                        context.Request.Method,
                        routePattern,
                        statusCode,
                        responseTimeMs,
                        context.Request.ContentLength ?? -1,
                        context.Response.ContentLength ?? -1
                    );

                    // Add server error to counter
                    if (exception != null)
                    {
                        _client.ServerErrorCounter.AddServerError(
                            consumerIdentifier,
                            context.Request.Method,
                            routePattern,
                            exception
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Apitally middleware");
            }
        }
    }
}
