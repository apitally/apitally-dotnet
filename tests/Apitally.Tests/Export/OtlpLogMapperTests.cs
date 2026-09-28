using System.Diagnostics;
using Apitally.Export;
using Apitally.Logging;
using Apitally.Tests.Support;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;

namespace Apitally.Tests.Export;

public class OtlpLogMapperTests
{
    [Fact]
    public void ApplicationLogsCarryTraceContextAndServerSpanLinkage()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var serverSpanId = ActivitySpanId.CreateRandom();
        var record = new LogRecordSnapshot(
            new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            "Orders.Service",
            LogLevel.Warning,
            new EventId(7, "OrderDelayed"),
            "Order delayed",
            new() { ["order.id"] = 42, [OtlpLogMapper.ServerSpanIdAttribute] = "callback-value" }
        );

        var output = Map(
            LogSnapshot.ForApplicationLog(
                record,
                traceId,
                spanId,
                ActivityTraceFlags.Recorded,
                serverSpanId
            )
        );

        var scopeLogs = Assert.Single(Assert.Single(output.ResourceLogs).ScopeLogs);
        Assert.Equal("Orders.Service", scopeLogs.Scope.Name);
        var log = Assert.Single(scopeLogs.LogRecords);
        Assert.Equal("Order delayed", log.Body.StringValue);
        Assert.Equal(SeverityNumber.Warn, log.SeverityNumber);
        Assert.Equal("OrderDelayed", log.EventName);
        Assert.Equal(traceId.ToHexString(), Hex(log.TraceId));
        Assert.Equal(spanId.ToHexString(), Hex(log.SpanId));
        Assert.NotEqual(0ul, log.TimeUnixNano);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["order.id"] = 42L,
                [OtlpLogMapper.ServerSpanIdAttribute] = serverSpanId.ToHexString(),
            },
            OtlpDecoding.Attributes(log.Attributes)
        );
    }

    [Fact]
    public void ApplicationStringsAreTruncatedAfterConversion()
    {
        var record = new LogRecordSnapshot(
            DateTime.UtcNow,
            "App",
            LogLevel.Information,
            default,
            new string('b', 3_000),
            new()
            {
                ["text"] = new string('t', 3_000),
                ["converted"] = new Uri("https://example.com/" + new string('u', 3_000)),
                ["array"] = new[] { new string('a', 3_000) },
            }
        );

        var log = Map(LogSnapshot.ForApplicationLog(record, default, default, default, default))
            .ResourceLogs[0]
            .ScopeLogs[0]
            .LogRecords[0];

        var attributes = OtlpDecoding.Attributes(log.Attributes);
        Assert.Equal(2_048, log.Body.StringValue.Length);
        Assert.Equal(2_048, ((string)attributes["text"]!).Length);
        Assert.Equal(2_048, ((string)attributes["converted"]!).Length);
        Assert.Equal(3_000, ((string)((object?[])attributes["array"]!)[0]!).Length);
    }

    [Fact]
    public void InternalEventsHaveNameBodyAndNoTraceContext()
    {
        var body = new Dictionary<string, object?>
        {
            ["identifier"] = "acme",
            ["attributes"] = new Dictionary<string, object?> { ["region"] = null },
        };

        var output = Map(
            LogSnapshot.ForInternalEvent("apitally.consumer.update", body, DateTime.UtcNow),
            LogSnapshot.ForInternalEvent(
                "apitally.app.startup",
                new string('s', 5_000),
                DateTime.UtcNow
            )
        );

        var scopeLogs = Assert.Single(output.ResourceLogs[0].ScopeLogs);
        Assert.Equal("apitally", scopeLogs.Scope.Name);
        var consumer = scopeLogs.LogRecords[0];
        Assert.Equal("apitally.consumer.update", consumer.EventName);
        Assert.Equal(body, OtlpDecoding.Value(consumer.Body));
        Assert.True(consumer.TraceId.IsEmpty);
        Assert.True(consumer.SpanId.IsEmpty);
        Assert.Empty(consumer.Attributes);
        Assert.Equal(5_000, scopeLogs.LogRecords[1].Body.StringValue.Length);
    }

    private static ExportLogsServiceRequest Map(params LogSnapshot[] logs) =>
        (ExportLogsServiceRequest)OtlpLogMapper.BuildRequest(logs, TestSpans.Resource);

    private static string Hex(Google.Protobuf.ByteString bytes) =>
        Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
}
