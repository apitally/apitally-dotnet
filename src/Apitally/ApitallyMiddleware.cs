namespace Apitally;

using System.Diagnostics;
using System.IO;
using Apitally.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class ApitallyMiddleware(
    RequestDelegate next,
    ApitallyClient client,
    ILogger<ApitallyMiddleware> logger,
    IOptions<ApitallyOptions> options
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!client.Enabled)
        {
            logger.LogInformation("Apitally is disabled");
            await next(context);
            return;
        }

        Exception? exception = null;
        var shouldCacheRequestBody =
            options.Value.RequestLogging.Enabled && options.Value.RequestLogging.IncludeRequestBody;
        var shouldCacheResponseBody =
            options.Value.RequestLogging.Enabled
            && options.Value.RequestLogging.IncludeResponseBody;
        var requestBody = Array.Empty<byte>();
        var responseBody = Array.Empty<byte>();
        var originalResponseBody = context.Response.Body;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Cache request body if needed
            if (shouldCacheRequestBody && context.Request.Body != null)
            {
                using var requestMs = new MemoryStream();
                await context.Request.Body.CopyToAsync(requestMs);
                requestBody = requestMs.ToArray();
                context.Request.Body = new MemoryStream(requestBody);
            }

            // Cache response body if needed
            if (shouldCacheResponseBody)
            {
                using var responseMs = new MemoryStream();
                context.Response.Body = responseMs;
                await next(context);
                responseBody = responseMs.ToArray();
                await responseMs.CopyToAsync(originalResponseBody);
            }
            else
            {
                await next(context);
            }
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
                var consumer = context.Items.TryGetValue("ApitallyConsumer", out var consumerObj)
                    ? ConsumerRegistry.ConsumerFromObject(consumerObj)
                    : null;
                client.ConsumerRegistry.AddOrUpdateConsumer(consumer);
                var consumerIdentifier = consumer?.Identifier ?? "";

                if (routePattern != null)
                {
                    // Add request to counter
                    client.RequestCounter.AddRequest(
                        consumerIdentifier,
                        context.Request.Method,
                        routePattern,
                        statusCode,
                        responseTimeMs,
                        context.Request.ContentLength
                            ?? (shouldCacheRequestBody ? requestBody.Length : -1),
                        context.Response.ContentLength
                            ?? (shouldCacheResponseBody ? responseBody.Length : -1)
                    );

                    // Add server error to counter
                    if (exception != null)
                    {
                        client.ServerErrorCounter.AddServerError(
                            consumerIdentifier,
                            context.Request.Method,
                            routePattern,
                            exception
                        );
                    }
                }

                // Log request if enabled
                if (options.Value.RequestLogging.Enabled)
                {
                    var request = new Request
                    {
                        Consumer = consumerIdentifier,
                        Method = context.Request.Method,
                        Path = routePattern,
                        Url = context.Request.GetDisplayUrl(),
                        Headers =
                        [
                            .. context.Request.Headers.Select(h => new Header(
                                h.Key,
                                h.Value.ToString()
                            )),
                        ],
                        Size = context.Request.ContentLength ?? requestBody.Length,
                        Body = requestBody,
                    };
                    var response = new Response
                    {
                        StatusCode = statusCode,
                        ResponseTime = responseTimeMs / 1000.0,
                        Headers =
                        [
                            .. context.Response.Headers.Select(h => new Header(
                                h.Key,
                                h.Value.ToString()
                            )),
                        ],
                        Size = context.Response.ContentLength ?? responseBody.Length,
                        Body = responseBody,
                    };
                    client.RequestLogger.LogRequest(request, response);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Apitally middleware");
            }
            finally
            {
                if (shouldCacheResponseBody)
                {
                    context.Response.Body = originalResponseBody;
                }
            }
        }
    }
}
