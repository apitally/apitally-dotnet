using System.IO.Compression;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace EncodingMetrics;

internal static class EncodingProbe
{
    internal static string Run(ExportMetricsServiceRequest metrics)
    {
        var binary = new byte[50_000];
        new Random(42).NextBytes(binary);
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(
                Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")
            ),
            SpanId = ByteString.CopyFrom(Convert.FromHexString("0102030405060708")),
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("1112131415161718")),
            Name = "POST /orders/{id}",
            Kind = OtlpSpan.Types.SpanKind.Server,
            StartTimeUnixNano = 1_800_000_000_000_000_000,
            EndTimeUnixNano = 1_800_000_000_100_000_000,
            Flags = 1,
            TraceState = "poc=synthetic",
            Status = new Status { Code = Status.Types.StatusCode.Error, Message = "synthetic" },
            Attributes =
            {
                Wire.Attribute("apitally.request.body", binary),
                Wire.Attribute("apitally.response.body", new string('x', 50_000)),
                Wire.Attribute(
                    "http.response.header.content-type",
                    new object?[] { "application/json" }
                ),
                Wire.Attribute("http.request.method", "POST"),
                Wire.Attribute("http.response.status_code", 500),
                Wire.Attribute("http.route", "/orders/{id}"),
            },
            Events =
            {
                new OtlpSpan.Types.Event
                {
                    Name = "exception",
                    TimeUnixNano = 1_800_000_000_050_000_000,
                    Attributes = { Wire.Attribute("exception.type", "SyntheticException") },
                },
            },
            Links =
            {
                new OtlpSpan.Types.Link
                {
                    TraceId = ByteString.CopyFrom(
                        Convert.FromHexString("2122232425262728292a2b2c2d2e2f3031")
                    ),
                    SpanId = ByteString.CopyFrom(Convert.FromHexString("3132333435363738")),
                    Flags = 1,
                },
            },
        };
        var traces = Traces([span]);
        var decoded = ExportTraceServiceRequest.Parser.ParseFrom(traces.ToByteArray());
        Program.Check(
            traces.Equals(decoded),
            "Trace round trip must preserve every generated field"
        );
        var body = decoded
            .ResourceSpans[0]
            .ScopeSpans[0]
            .Spans[0]
            .Attributes.Single(attribute => attribute.Key == "apitally.request.body")
            .Value;
        Program.Check(
            body.ValueCase == AnyValue.ValueOneofCase.BytesValue
                && body.BytesValue.Span.SequenceEqual(binary),
            "Complete non-UTF8 binary AnyValue body"
        );

        var logs = Logs();
        var decodedLogs = ExportLogsServiceRequest.Parser.ParseFrom(logs.ToByteArray());
        Program.Check(logs.Equals(decodedLogs), "Logs round trip");
        var records = decodedLogs.ResourceLogs[0].ScopeLogs[0].LogRecords;
        Program.Check(
            records[0].EventName == "apitally.app.startup"
                && records[0].Body.ValueCase == AnyValue.ValueOneofCase.StringValue,
            "Startup body is a JSON string"
        );
        Program.Check(
            records[1].EventName == "apitally.request.server_error"
                && records[1].Body.ValueCase == AnyValue.ValueOneofCase.KvlistValue,
            "Error body is an object"
        );
        Program.Check(
            records[1].Body.KvlistValue.Values.Single(field => field.Key == "count").Value.IntValue
                == uint.MaxValue,
            "UInt32 maximum count encoded as OTLP int64"
        );
        Program.Check(
            records.All(record => record.TraceId.IsEmpty && record.SpanId.IsEmpty),
            "Internal events are context-free"
        );
        var mixed = Wire.Value(
            new Dictionary<string, object?>
            {
                ["nested"] = new object?[]
                {
                    "string",
                    true,
                    123L,
                    1.25,
                    null,
                    new Dictionary<string, object?> { ["key"] = "value" },
                },
            }
        );
        Program.Check(
            mixed.Equals(AnyValue.Parser.ParseFrom(mixed.ToByteArray())),
            "Recursive AnyValue oneof types"
        );
        Program.Check(
            metrics.Equals(ExportMetricsServiceRequest.Parser.ParseFrom(metrics.ToByteArray())),
            "Official SDK histogram round trip"
        );
        Console.WriteLine(
            "PASS protobuf: all three requests round-trip; 50,000-byte binary and string bodies; header arrays; IDs/events/links/status; native log event_name and structured uint32 count"
        );

        var replay = Concatenate(
            "traces",
            traces,
            ExportTraceServiceRequest.Parser,
            request => request.ResourceSpans.Count
        );
        Concatenate(
            "logs",
            logs,
            ExportLogsServiceRequest.Parser,
            request => request.ResourceLogs.Count
        );
        Concatenate(
            "metrics",
            metrics,
            ExportMetricsServiceRequest.Parser,
            request => request.ResourceMetrics.Count
        );
        Rotation(span);
        return replay;
    }

    private static ExportTraceServiceRequest Traces(IEnumerable<OtlpSpan> spans) =>
        new()
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = Wire.Resource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope
                            {
                                Name = "apitally.otel",
                                Version = "poc",
                            },
                            Spans = { spans },
                        },
                    },
                },
            },
        };

    private static ExportLogsServiceRequest Logs() =>
        new()
        {
            ResourceLogs =
            {
                new ResourceLogs
                {
                    Resource = Wire.Resource(),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = new InstrumentationScope { Name = "apitally" },
                            LogRecords =
                            {
                                new LogRecord
                                {
                                    TimeUnixNano = 1_800_000_000_000_000_000,
                                    EventName = "apitally.app.startup",
                                    Body = Wire.Value(
                                        "{\"framework\":\"aspnetcore\",\"versions\":{},\"config\":{},\"paths\":[]}"
                                    ),
                                },
                                new LogRecord
                                {
                                    TimeUnixNano = 1_800_000_000_000_000_000,
                                    EventName = "apitally.request.server_error",
                                    Body = Wire.Value(
                                        new Dictionary<string, object?>
                                        {
                                            ["consumer"] = "synthetic",
                                            ["method"] = "POST",
                                            ["path"] = "/orders/{id}",
                                            ["type"] = "SyntheticException",
                                            ["message"] = "synthetic",
                                            ["stacktrace"] = "synthetic",
                                            ["count"] = uint.MaxValue,
                                        }
                                    ),
                                },
                            },
                        },
                    },
                },
            },
        };

    private static string Concatenate<T>(
        string signal,
        T request,
        MessageParser<T> parser,
        Func<T, int> resourceCount
    )
        where T : IMessage<T>
    {
        var path = Path.Combine(Program.Artifacts, $"{signal}-continuous.gz");
        var bytes = request.ToByteArray();
        Program.Check(bytes.Length == request.CalculateSize(), "Exact encoded append size");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(bytes);
            gzip.Flush();
            gzip.Write(bytes);
        }
        var decompressed = Decompress(path);
        Program.Check(
            decompressed.SequenceEqual(bytes.Concat(bytes)),
            "Continuous gzip contains unframed protobuf concatenation"
        );
        var merged = parser.ParseFrom(decompressed);
        Program.Check(
            resourceCount(merged) == 2 * resourceCount(request),
            "Concatenated requests merge repeated resource fields"
        );
        Console.WriteLine(
            $"PASS {signal} concatenation: 2 requests in one GZipStream; encoded={bytes.Length} each, stored={new FileInfo(path).Length}"
        );
        return path;
    }

    private static void Rotation(OtlpSpan template)
    {
        const int cap = 4_000_000;
        var spans = Enumerable
            .Range(0, 65)
            .Select(index =>
            {
                var span = template.Clone();
                span.Name = $"synthetic-{index}";
                span.Attributes.Add(Wire.Attribute("synthetic.padding", new string('a', 65_536)));
                return span;
            })
            .ToArray();
        var approximateChunk = Traces(spans.Take(32)).CalculateSize();
        Program.Check(approximateChunk > cap, "32 records must demonstrate encoded-byte overshoot");
        using var files = new BoundedTraceFiles(cap);
        for (var start = 0; start < spans.Length; start += 32)
            files.Append(spans.Skip(start).Take(32).ToArray());
        files.Close();
        var count = 0;
        foreach (var path in files.Paths)
        {
            var bytes = Decompress(path);
            Program.Check(
                bytes.Length <= cap && new FileInfo(path).Length <= 4 * 1024 * 1024,
                "File respects both uncompressed and wire caps"
            );
            count += ExportTraceServiceRequest
                .Parser.ParseFrom(bytes)
                .ResourceSpans.SelectMany(resource => resource.ScopeSpans)
                .Sum(scope => scope.Spans.Count);
        }
        Program.Check(
            count == spans.Length && files.Paths.Count > 1 && files.SplitCount > 0,
            "Every record survives exact-byte splitting/rotation"
        );
        var oversized = template.Clone();
        oversized.Attributes.Add(Wire.Attribute("synthetic.oversize", new string('z', cap)));
        var rejected = false;
        try
        {
            files.Append([oversized]);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Program.Check(
            rejected,
            "Indivisible oversized request rejected instead of overshooting cap"
        );
        Console.WriteLine(
            $"PASS rotation: 32-record estimate={approximateChunk} > {cap}; recursive exact-size splits={files.SplitCount}; files={files.Paths.Count}; decoded records={count}; single oversized record explicitly rejected"
        );
    }

    internal static byte[] Decompress(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private sealed class BoundedTraceFiles(int cap) : IDisposable
    {
        internal List<string> Paths { get; } = [];
        internal int SplitCount { get; private set; }
        private GZipStream? gzip;
        private int uncompressedSize;

        internal void Append(OtlpSpan[] spans)
        {
            var request = Traces(spans);
            if (request.CalculateSize() > cap)
            {
                if (spans.Length == 1)
                    throw new ArgumentException(
                        "Indivisible encoded request exceeds file cap; production drop policy not selected"
                    );
                SplitCount++;
                var midpoint = spans.Length / 2;
                Append(spans[..midpoint]);
                Append(spans[midpoint..]);
                return;
            }
            var bytes = request.ToByteArray();
            Program.Check(
                bytes.Length == request.CalculateSize(),
                "Immutable request encoded length"
            );
            if (uncompressedSize + bytes.Length > cap)
                Close();
            if (gzip == null)
            {
                var path = Path.Combine(Program.Artifacts, $"rotation-{Paths.Count}.gz");
                Paths.Add(path);
                gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest);
            }
            gzip.Write(bytes);
            gzip.Flush();
            uncompressedSize += bytes.Length;
        }

        internal void Close()
        {
            gzip?.Dispose();
            gzip = null;
            uncompressedSize = 0;
        }

        public void Dispose() => Close();
    }
}
