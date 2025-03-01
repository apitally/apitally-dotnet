namespace Apitally;

using System.Diagnostics;
using System.IO;
using Apitally.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

class ApitallyMiddleware(
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
            options.Value.RequestLogging.Enabled
            && options.Value.RequestLogging.IncludeRequestBody
            && RequestLogger.AllowedContentTypes.Any(ct =>
                context.Request.ContentType?.StartsWith(ct, StringComparison.OrdinalIgnoreCase)
                ?? false
            );
        var shouldCacheResponseBody =
            options.Value.RequestLogging.Enabled
            && options.Value.RequestLogging.IncludeResponseBody;
        var requestBody = Array.Empty<byte>();
        var responseBody = Array.Empty<byte>();
        var originalResponseBody = context.Response.Body;
        var stopwatch = Stopwatch.StartNew();
        long responseSize = -1;

        try
        {
            // Cache request body if needed
            if (shouldCacheRequestBody && context.Request.Body != null)
            {
                context.Request.EnableBuffering();
                using var memStream = new MemoryStream();
                await context.Request.Body.CopyToAsync(memStream);
                requestBody = memStream.ToArray();
                context.Request.Body.Position = 0;
            }

            // Cache response body if needed
            if (shouldCacheResponseBody)
            {
                using var memStream = new MemoryStream();
                context.Response.Body = memStream;
                await next(context);
                if (
                    RequestLogger.AllowedContentTypes.Any(ct =>
                        context.Response.ContentType?.StartsWith(
                            ct,
                            StringComparison.OrdinalIgnoreCase
                        ) ?? false
                    )
                )
                {
                    memStream.Position = 0;
                    responseBody = memStream.ToArray();
                }
                memStream.Position = 0;
                await memStream.CopyToAsync(originalResponseBody);
                responseSize = memStream.Length;
            }
            else
            {
                // Use a counting stream to measure response size without buffering
                var countingStream = new CountingStream(originalResponseBody);
                context.Response.Body = countingStream;
                await next(context);
                responseSize = countingStream.BytesWritten;
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
                        context.Response.ContentLength ?? responseSize
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
                        Size = context.Response.ContentLength ?? responseSize,
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
                context.Response.Body = originalResponseBody;
            }
        }
    }

    /// <summary>
    /// A stream that counts the number of bytes written to it while passing through all operations to an inner stream.
    /// </summary>
    private class CountingStream(Stream innerStream) : Stream
    {
        private readonly Stream _innerStream = innerStream;

        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            BytesWritten += count;
            return _innerStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            BytesWritten += buffer.Length;
            await _innerStream.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _innerStream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Reading is not supported by this stream");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("Seeking is not supported by this stream");

        public override void SetLength(long value) =>
            throw new NotSupportedException("Setting length is not supported by this stream");
    }
}
