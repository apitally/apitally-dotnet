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
        var attributes = OtlpDecoding.Attributes(logs[0].Attributes);
        Assert.Equal(42L, attributes["OrderId"]);
        Assert.Equal("/log", attributes["RequestPath"]);
        Assert.DoesNotContain("{OriginalFormat}", attributes.Keys);
    }

    [Fact]
    public async Task ScopesAreFlattenedWithEntryFieldsWinningOverInnerAndOuterScopes()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(receiver);

        await host.Client.GetAsync("/scopes");
        await host.StopAsync();

        var attributes = OtlpDecoding.Attributes(
            Assert.Single(receiver.ApplicationLogs()).Attributes
        );
        Assert.Equal("entry", attributes["Key"]);
        Assert.Equal("inner", attributes["Shared"]);
        Assert.Equal("outer", attributes["OuterOnly"]);
        Assert.Equal(7L, attributes["Batch"]);
        Assert.DoesNotContain(
            attributes.Keys,
            key => key.Contains("OriginalFormat") || key == "Scope"
        );
    }

    [Fact]
    public async Task ExceptionDetailsAreCopiedWhileOtherProvidersKeepTheOriginal()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await StartAsync(receiver);

        await host.Client.GetAsync("/log-exception");
        await host.StopAsync();

        var attributes = OtlpDecoding.Attributes(
            Assert.Single(receiver.ApplicationLogs()).Attributes
        );
        Assert.Equal("System.InvalidOperationException", attributes["exception.type"]);
        Assert.Equal("Broken", attributes["exception.message"]);
        Assert.Contains("Broken", (string)attributes["exception.stacktrace"]!);
        var original = Assert.Single(host.Logs.GetSnapshot(), record => record.Message == "Failed");
        Assert.IsType<InvalidOperationException>(original.Exception);
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
                    record.Attributes.Remove("OrderId");
                    record.Attributes["masked"] = true;
                    record.Attributes["apitally.request.server_span_id"] = "0000000000000000";
                    record.Body = record.Body.Replace("42", "**");
                    return record;
                }
        );

        await host.Client.GetAsync("/log-variants");
        await host.StopAsync();

        var log = Assert.Single(receiver.ApplicationLogs());
        Assert.Equal("Handling order **", log.Body.StringValue);
        var attributes = OtlpDecoding.Attributes(log.Attributes);
        Assert.Equal(true, attributes["masked"]);
        Assert.False(attributes.ContainsKey("OrderId"));
        Assert.Equal(
            receiver.Spans().Server().SpanId.Hex(),
            attributes["apitally.request.server_span_id"]
        );
        Assert.Contains(host.Logs.GetSnapshot(), record => record.Message == "Handling order 42");
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
                    "/scopes",
                    () =>
                    {
                        using (
                            logger.BeginScope(
                                new Dictionary<string, object?>
                                {
                                    ["Shared"] = "outer",
                                    ["OuterOnly"] = "outer",
                                    ["Key"] = "outer",
                                }
                            )
                        )
                        using (logger.BeginScope("Importing orders"))
                        using (logger.BeginScope("Batch {Batch}", 7))
                        using (
                            logger.BeginScope(
                                new Dictionary<string, object?>
                                {
                                    ["Shared"] = "inner",
                                    ["Key"] = "inner",
                                }
                            )
                        )
                            logger.LogInformation("Scoped {Key}", "entry");
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
