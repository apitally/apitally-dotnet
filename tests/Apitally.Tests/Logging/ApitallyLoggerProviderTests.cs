using System.Collections.Concurrent;
using System.Diagnostics;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Logs.V1;

namespace Apitally.Tests.Logging;

public class ApitallyLoggerProviderTests
{
    private static readonly ActivitySource ApplicationSource = new("TestApp.Logging");

    [Fact]
    public async Task RequestLogsAreLinkedToTheServerSpanAndEmittingSpan()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(receiver);

        await host.Client.GetAsync("/log");
        await host.StopAsync();

        var spans = receiver.Spans();
        var server = spans.Server();
        var child = spans.Single(span => span.Name == "child");
        var logs = receiver.ApplicationLogs();
        Assert.Equal(
            ["Handling order 42", "Inside child"],
            logs.Select(log => log.Body.StringValue)
        );
        Assert.All(
            logs,
            log =>
            {
                Assert.Equal(server.TraceId, log.TraceId);
                Assert.Equal(
                    server.SpanId.Hex(),
                    OtlpDecoding.Attributes(log.Attributes)["apitally.request.server_span_id"]
                );
            }
        );
        Assert.Equal(server.SpanId, logs[0].SpanId);
        Assert.Equal(child.SpanId, logs[1].SpanId);
        Assert.Equal(SeverityNumber.Info, logs[0].SeverityNumber);
        Assert.Equal(
            "TestApp.Orders",
            receiver.ScopeLogs().Single(scope => scope.Scope.Name != "apitally").Scope.Name
        );
        Assert.Equal(
            ["apitally.request.server_span_id"],
            OtlpDecoding.Attributes(logs[0].Attributes).Keys
        );
    }

    [Fact]
    public async Task MaskCallbackEditsApplyAndDropsAreHonored()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(
            receiver,
            options =>
                options.MaskLogRecord = record =>
                {
                    if (record.Body!.Contains("drop"))
                        return null;
                    if (record.Body.Contains("throw"))
                        throw new InvalidOperationException();
                    if (record.Body.Contains("empty"))
                        record.Body = "";
                    record.Body = record.Body.Replace("42", "**");
                    return record;
                }
        );

        await host.Client.GetAsync("/log-variants");
        await host.StopAsync();

        var log = Assert.Single(receiver.ApplicationLogs());
        Assert.Equal("Handling order **", log.Body.StringValue);
        Assert.Contains(host.Logs.GetSnapshot(), record => record.Message == "Handling order 42");
    }

    [Fact]
    public async Task MasksSeeFullMessagesAndExportedMessagesAreTruncated()
    {
        var maskedLengths = new ConcurrentQueue<int>();
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(
            receiver,
            options =>
                options.MaskLogRecord = record =>
                {
                    maskedLengths.Enqueue(record.Body!.Length);
                    return record;
                }
        );

        await host.Client.GetAsync("/long-log");
        await host.StopAsync();

        Assert.Equal([3_000], maskedLengths);
        Assert.Equal(2_048, Assert.Single(receiver.ApplicationLogs()).Body.StringValue.Length);
    }

    [Fact]
    public async Task LogsOutsideRequestsAndFromFrameworkCategoriesAreNotCaptured()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(receiver);

        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("TestApp.Background")
            .LogInformation("Background");
        await host.Client.GetAsync("/missing");
        await host.StopAsync();

        Assert.Empty(receiver.ApplicationLogs());
    }

    [Fact]
    public async Task LogsOfSampledOutRequestsAndDisabledCaptureAreNotExported()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var sampledOut = await StartAsync(receiver, options => options.SampleRate = 0);
        await sampledOut.Client.GetAsync("/log");
        await sampledOut.StopAsync();
        await using var disabled = await StartAsync(
            receiver,
            options => options.CaptureLogs = false
        );
        await disabled.Client.GetAsync("/log");
        await disabled.StopAsync();

        Assert.Empty(receiver.ApplicationLogs());
        Assert.Equal(2, receiver.Events("apitally.app.startup").Count);
    }

    [Fact]
    public async Task ProviderAliasFiltersNarrowCapture()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(
            receiver,
            arguments: "--Logging:Apitally:LogLevel:Default=Warning"
        );

        await host.Client.GetAsync("/log-exception");
        await host.Client.GetAsync("/log");
        await host.StopAsync();

        Assert.Equal(["Failed"], receiver.ApplicationLogs().Select(log => log.Body.StringValue));
    }

    [Fact]
    public async Task PerRequestLogLimitKeepsTheEarliestRecords()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(receiver);

        await host.Client.GetAsync("/many-logs");
        await host.StopAsync();

        var logs = receiver.ApplicationLogs();
        Assert.Equal(1_000, logs.Count);
        Assert.Equal("Log 0", logs[0].Body.StringValue);
        Assert.Equal("Log 999", logs[^1].Body.StringValue);
    }

    private static Task<ApplicationHost> StartAsync(
        OtlpReceiver receiver,
        Action<ApitallyOptions>? configure = null,
        params string[] arguments
    ) =>
        ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => configure?.Invoke(options)),
            app =>
            {
                var logger = app
                    .Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("TestApp.Orders");
                app.MapGet(
                    "/log",
                    () =>
                    {
                        logger.LogInformation("Handling order {OrderId}", 42);
                        using (ApplicationSource.StartActivity("child"))
                            logger.LogInformation("Inside child");
                        return "OK";
                    }
                );
                app.MapGet(
                    "/log-exception",
                    () =>
                    {
                        logger.LogError(new InvalidOperationException("Broken"), "Failed");
                        return "OK";
                    }
                );
                app.MapGet(
                    "/log-variants",
                    () =>
                    {
                        logger.LogInformation("Handling order {OrderId}", 42);
                        logger.LogInformation("Please drop this");
                        logger.LogInformation("Please throw");
                        logger.LogInformation("Make it empty");
                        return "OK";
                    }
                );
                app.MapGet(
                    "/long-log",
                    () =>
                    {
                        logger.LogInformation(new string('b', 3_000));
                        return "OK";
                    }
                );
                app.MapGet(
                    "/many-logs",
                    () =>
                    {
                        for (var i = 0; i < 1_005; i++)
                            logger.LogInformation("Log {Index}", i);
                        return "OK";
                    }
                );
            },
            arguments
        );
}
