using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Apitally.Tests.Support;

internal sealed record ReceivedExport(
    string Path,
    string? ContentType,
    string? ContentEncoding,
    string? Authorization,
    string? Env,
    byte[] Body
);

// A physical loopback OTLP/HTTP endpoint recording every export it receives.
internal sealed class OtlpReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly SemaphoreSlim received = new(0);

    private OtlpReceiver(WebApplication app) => this.app = app;

    public ConcurrentQueue<ReceivedExport> Exports { get; } = new();

    // Returns the status and optional export interval header for each request.
    public Func<ReceivedExport, (int Status, string? ExportInterval)> Respond { get; set; } =
        _ => (200, null);

    public Uri Endpoint { get; private set; } = null!;

    public static async Task<OtlpReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var receiver = new OtlpReceiver(app);
        app.MapPost(
            "/v1/{signal}",
            async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body);
                var export = new ReceivedExport(
                    context.Request.Path,
                    context.Request.ContentType,
                    context.Request.Headers.ContentEncoding,
                    context.Request.Headers.Authorization,
                    context.Request.Headers["Apitally-Env"],
                    body.ToArray()
                );
                var (status, interval) = receiver.Respond(export);
                receiver.Exports.Enqueue(export);
                receiver.received.Release();
                context.Response.StatusCode = status;
                if (interval is not null)
                    context.Response.Headers["Apitally-Export-Interval"] = interval;
            }
        );
        await app.StartAsync();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        receiver.Endpoint = new Uri(address);
        return receiver;
    }

    public IEnumerable<ReceivedExport> For(string signal) =>
        Exports.Where(export => export.Path == "/v1/" + signal);

    public List<OpenTelemetry.Proto.Trace.V1.ResourceSpans> ResourceSpans() =>
        For("traces").SelectMany(export => OtlpDecoding.Traces(export.Body).ResourceSpans).ToList();

    public List<OpenTelemetry.Proto.Trace.V1.Span> Spans() =>
        ResourceSpans()
            .SelectMany(resource => resource.ScopeSpans)
            .SelectMany(scope => scope.Spans)
            .ToList();

    public List<OpenTelemetry.Proto.Logs.V1.ScopeLogs> ScopeLogs() =>
        For("logs")
            .SelectMany(export => OtlpDecoding.Logs(export.Body).ResourceLogs)
            .SelectMany(resource => resource.ScopeLogs)
            .ToList();

    // Application log records, excluding SDK internal events.
    public List<OpenTelemetry.Proto.Logs.V1.LogRecord> ApplicationLogs() =>
        ScopeLogs()
            .Where(scope => scope.Scope.Name != "apitally")
            .SelectMany(scope => scope.LogRecords)
            .ToList();

    public List<OpenTelemetry.Proto.Logs.V1.LogRecord> Events(string eventName) =>
        ScopeLogs()
            .Where(scope => scope.Scope.Name == "apitally")
            .SelectMany(scope => scope.LogRecords)
            .Where(log => log.EventName == eventName)
            .ToList();

    public List<OpenTelemetry.Proto.Metrics.V1.ResourceMetrics> ResourceMetrics() =>
        For("metrics")
            .SelectMany(export => OtlpDecoding.Metrics(export.Body).ResourceMetrics)
            .ToList();

    public List<OpenTelemetry.Proto.Metrics.V1.Metric> Metrics(string name) =>
        ResourceMetrics()
            .SelectMany(resource => resource.ScopeMetrics)
            .SelectMany(scope => scope.Metrics)
            .Where(metric => metric.Name == name)
            .ToList();

    public async Task WaitForExportsAsync(int count, TimeSpan? timeout = null)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await received.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)))
                throw new TimeoutException($"Received {i} of {count} expected exports");
        }
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
