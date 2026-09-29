using System.IO.Compression;
using System.Text;
using Apitally.Export;
using Apitally.Logging;
using Apitally.Tests.Support;

namespace Apitally.Tests.Export;

public class SpanRedactionTests
{
    [Fact]
    public void QueryParametersAreRedactedInStableAndLegacyAttributes()
    {
        var span = TestSpans.Create(
            new()
            {
                ["url.query"] = "token=abc&page=2&Api-Key=x",
                ["url.full"] = "https://example.com/a?password=p&q=1",
                ["http.target"] = "/a?secret=s",
                ["http.url"] = "https://example.com/a?pwd=1",
                ["url.path"] = "/a",
            }
        );

        Assert.True(Redaction().TryRedact(new SpanExportEntry(span)));

        Assert.Equal("token=[REDACTED]&page=2&Api-Key=[REDACTED]", span.Attributes["url.query"]);
        Assert.Equal("https://example.com/a?password=[REDACTED]&q=1", span.Attributes["url.full"]);
        Assert.Equal("/a?secret=[REDACTED]", span.Attributes["http.target"]);
        Assert.Equal("https://example.com/a?pwd=[REDACTED]", span.Attributes["http.url"]);
        Assert.Equal("/a", span.Attributes["url.path"]);
    }

    [Fact]
    public void CustomQueryPatternsExtendTheDefaults()
    {
        var span = TestSpans.Create(new() { ["url.query"] = "session=1&token=2&page=3" });

        Redaction(options => options.MaskQueryParams = ["session"])
            .TryRedact(new SpanExportEntry(span));

        Assert.Equal("session=[REDACTED]&token=[REDACTED]&page=3", span.Attributes["url.query"]);
    }

    [Fact]
    public void CapturedHeadersAreAttachedAsRedactedLowercaseLists()
    {
        var span = TestSpans.Create();
        var entry = new SpanExportEntry(span)
        {
            RequestHeaders =
            [
                new("Content-Type", ["application/json"]),
                new("Authorization", ["Bearer a", "Bearer b"]),
                new("X-Custom", ["1"]),
            ],
            ResponseHeaders = [new("Location", ["/next?token=t&x=1"]), new("Set-Cookie", ["id=1"])],
        };

        Redaction(options => options.MaskHeaders = ["x-custom"]).TryRedact(entry);

        Assert.Equal(
            new[] { "application/json" },
            span.Attributes["http.request.header.content-type"]
        );
        Assert.Equal(new[] { "[REDACTED]" }, span.Attributes["http.request.header.authorization"]);
        Assert.Equal(new[] { "[REDACTED]" }, span.Attributes["http.request.header.x-custom"]);
        Assert.Equal(
            new[] { "/next?token=[REDACTED]&x=1" },
            span.Attributes["http.response.header.location"]
        );
        Assert.Equal(new[] { "[REDACTED]" }, span.Attributes["http.response.header.set-cookie"]);
    }

    [Fact]
    public void HeaderAttributesFromApplicationInstrumentationAreRedacted()
    {
        var span = TestSpans.Create(
            new()
            {
                ["http.request.header.x_api_key"] = new[] { "secret" },
                ["http.response.header.content_location"] = new[] { "/a?auth=1" },
                ["http.request.header.accept"] = new[] { "*/*" },
            }
        );

        Redaction().TryRedact(new SpanExportEntry(span));

        Assert.Equal(new[] { "[REDACTED]" }, span.Attributes["http.request.header.x_api_key"]);
        Assert.Equal(
            new[] { "/a?auth=[REDACTED]" },
            span.Attributes["http.response.header.content_location"]
        );
        Assert.Equal(new[] { "*/*" }, span.Attributes["http.request.header.accept"]);
    }

    [Fact]
    public void JsonBodyFieldsAreRedactedRecursively()
    {
        var span = TestSpans.Create();
        var body =
            """{"user":{"name":"a","password":"p","tokens":["t1"]},"items":[{"card_number":"4111","cvv":"123","qty":1.50}],"secret":42,"ok":true,"none":null}""";

        Redaction(options => options.MaskBodyFields = ["^name$"])
            .TryRedact(new SpanExportEntry(span) { RequestBody = Body(body) });

        Assert.Equal(
            """{"user":{"name":"[REDACTED]","password":"[REDACTED]","tokens":["t1"]},"items":[{"card_number":"[REDACTED]","cvv":"[REDACTED]","qty":1.50}],"secret":42,"ok":true,"none":null}""",
            span.Attributes[SpanRedaction.RequestBodyAttribute]
        );
    }

    [Fact]
    public void JsonWithDuplicateKeysIsStillRedacted()
    {
        var span = TestSpans.Create();

        Redaction()
            .TryRedact(
                new SpanExportEntry(span)
                {
                    RequestBody = Body("""{"password":"a","password":"b"}"""),
                }
            );

        Assert.Equal(
            """{"password":"[REDACTED]","password":"[REDACTED]"}""",
            span.Attributes[SpanRedaction.RequestBodyAttribute]
        );
    }

    [Fact]
    public void NonJsonTextIsExportedAsTextAndBinaryAsBytes()
    {
        var span = TestSpans.Create();
        var binary = new byte[] { 0xff, 0xfe, 0x00 };

        Redaction()
            .TryRedact(
                new SpanExportEntry(span)
                {
                    RequestBody = Body("password=abc"),
                    ResponseBody = new CapturedBody(binary, null),
                }
            );

        Assert.Equal("password=abc", span.Attributes[SpanRedaction.RequestBodyAttribute]);
        Assert.Equal(binary, span.Attributes[SpanRedaction.ResponseBodyAttribute]);
    }

    [Fact]
    public void TooLargeSentinelBypassesMasking()
    {
        var span = TestSpans.Create();
        var calls = 0;

        Redaction(options =>
                options.MaskResponseBody = (_, bytes) =>
                {
                    calls++;
                    return bytes;
                }
            )
            .TryRedact(new SpanExportEntry(span) { ResponseBody = CapturedBody.TooLarge });

        Assert.Equal("[BODY_TOO_LARGE]", span.Attributes[SpanRedaction.ResponseBodyAttribute]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MaskCallbacksSeeRedactedHeadersButNoBodies()
    {
        var span = TestSpans.Create(new() { ["url.query"] = "token=1" });
        var seen = new List<string>();

        Redaction(options =>
            {
                options.MaskRequestBody = (snapshot, bytes) =>
                {
                    seen.AddRange(snapshot.Attributes.Keys);
                    Assert.Equal("token=[REDACTED]", snapshot.Attributes["url.query"]);
                    return Encoding.UTF8.GetBytes("masked");
                };
                options.MaskResponseBody = (snapshot, _) =>
                {
                    Assert.False(
                        snapshot.Attributes.ContainsKey(SpanRedaction.RequestBodyAttribute)
                    );
                    return null;
                };
            })
            .TryRedact(
                new SpanExportEntry(span)
                {
                    RequestHeaders = [new("Accept", ["*/*"])],
                    RequestBody = Body("original"),
                    ResponseBody = Body("original"),
                }
            );

        Assert.Contains("http.request.header.accept", seen);
        Assert.DoesNotContain(SpanRedaction.RequestBodyAttribute, seen);
        Assert.Equal("masked", span.Attributes[SpanRedaction.RequestBodyAttribute]);
        Assert.Equal("[REDACTED]", span.Attributes[SpanRedaction.ResponseBodyAttribute]);
    }

    [Fact]
    public void ThrowingOrOversizedMaskResultsNeverExportTheOriginal()
    {
        var span = TestSpans.Create();

        Redaction(options =>
            {
                options.MaskRequestBody = (_, _) => throw new InvalidOperationException();
                options.MaskResponseBody = (_, _) => new byte[SpanRedaction.MaxBodySize + 1];
            })
            .TryRedact(
                new SpanExportEntry(span) { RequestBody = Body("a"), ResponseBody = Body("b") }
            );

        Assert.Equal("[REDACTED]", span.Attributes[SpanRedaction.RequestBodyAttribute]);
        Assert.Equal("[BODY_TOO_LARGE]", span.Attributes[SpanRedaction.ResponseBodyAttribute]);
    }

    [Fact]
    public void CompressedBodiesAreDecompressedBeforeRedaction()
    {
        var span = TestSpans.Create();

        Redaction()
            .TryRedact(
                new SpanExportEntry(span)
                {
                    RequestBody = new CapturedBody(Gzip("""{"token":"t"}"""), "gzip"),
                    ResponseBody = new CapturedBody(Brotli("text"), "br"),
                }
            );

        Assert.Equal(
            """{"token":"[REDACTED]"}""",
            span.Attributes[SpanRedaction.RequestBodyAttribute]
        );
        Assert.Equal("text", span.Attributes[SpanRedaction.ResponseBodyAttribute]);
    }

    [Fact]
    public void DecompressionIsBoundedAndFailuresAreRedacted()
    {
        var span = TestSpans.Create();

        Redaction()
            .TryRedact(
                new SpanExportEntry(span)
                {
                    RequestBody = new CapturedBody(
                        Gzip(new string('a', SpanRedaction.MaxBodySize + 1)),
                        "gzip"
                    ),
                    ResponseBody = new CapturedBody(Encoding.UTF8.GetBytes("not gzip"), "gzip"),
                }
            );

        Assert.Equal("[BODY_TOO_LARGE]", span.Attributes[SpanRedaction.RequestBodyAttribute]);
        Assert.Equal("[REDACTED]", span.Attributes[SpanRedaction.ResponseBodyAttribute]);
    }

    [Fact]
    public void UnsupportedEncodingIsRedacted()
    {
        var span = TestSpans.Create();

        Redaction()
            .TryRedact(
                new SpanExportEntry(span) { RequestBody = new CapturedBody([1, 2], "zstd") }
            );

        Assert.Equal("[REDACTED]", span.Attributes[SpanRedaction.RequestBodyAttribute]);
    }

    [Fact]
    public void RedactionFailureDropsTheSpan()
    {
        var span = TestSpans.Create(new() { ["url.query"] = new string('a', 40) + "!=1" });

        var redacted = Redaction(options => options.MaskQueryParams = ["^(a+)+$"])
            .TryRedact(new SpanExportEntry(span));

        Assert.False(redacted);
    }

    private static SpanRedaction Redaction(Action<ApitallyOptions>? configure = null) =>
        new(TestConfiguration.Resolve(configure), SdkDiagnostics.None);

    private static CapturedBody Body(string text) => new(Encoding.UTF8.GetBytes(text), null);

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static byte[] Brotli(string text)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
            brotli.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}
