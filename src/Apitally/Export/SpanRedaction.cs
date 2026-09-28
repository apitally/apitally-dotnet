using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apitally.Hosting;
using Apitally.Logging;

namespace Apitally.Export;

// A span queued for export, with the headers and bodies captured for it. Captured payloads
// are never attached to a live activity.
internal sealed class SpanExportEntry(SpanSnapshot span)
{
    public SpanSnapshot Span { get; } = span;
    public IReadOnlyList<KeyValuePair<string, string[]>>? RequestHeaders { get; init; }
    public IReadOnlyList<KeyValuePair<string, string[]>>? ResponseHeaders { get; init; }
    public CapturedBody? RequestBody { get; init; }
    public CapturedBody? ResponseBody { get; init; }
}

// Complete captured body bytes, or the marker for a body over the size limit.
internal sealed class CapturedBody
{
    public static readonly CapturedBody TooLarge = new([], null);

    public CapturedBody(byte[] bytes, string? contentEncoding)
    {
        Bytes = bytes;
        ContentEncoding = contentEncoding;
    }

    public byte[] Bytes { get; }
    public string? ContentEncoding { get; }
    public bool IsTooLarge => ReferenceEquals(this, TooLarge);
}

// The privacy boundary for everything exported to Apitally. Runs on the span batch worker.
internal sealed partial class SpanRedaction(
    RuntimeConfiguration configuration,
    SdkDiagnostics diagnostics
)
{
    public const int MaxBodySize = 50_000;
    public const string Redacted = "[REDACTED]";
    public const string BodyTooLarge = "[BODY_TOO_LARGE]";
    public const string RequestBodyAttribute = "apitally.request.body";
    public const string ResponseBodyAttribute = "apitally.response.body";
    public const string RequestHeaderPrefix = "http.request.header.";
    public const string ResponseHeaderPrefix = "http.response.header.";

    private const int PatternTimeoutMilliseconds = 100;

    // Stable and legacy attributes that can carry a query string. url.query has no "?".
    private static readonly string[] QueryAttributes = ["url.full", "http.target", "http.url"];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Returns false if the span must be dropped because it could not be redacted safely.
    public bool TryRedact(SpanExportEntry entry)
    {
        try
        {
            var attributes = entry.Span.OwnedAttributes;
            RedactQueryAndHeaderAttributes(attributes);
            AddHeaderAttributes(attributes, RequestHeaderPrefix, entry.RequestHeaders);
            AddHeaderAttributes(attributes, ResponseHeaderPrefix, entry.ResponseHeaders);
            // Both masks see the span with headers attached but without body attributes.
            var requestBody = ProcessBody(
                entry.Span,
                entry.RequestBody,
                configuration.MaskRequestBody,
                nameof(ApitallyOptions.MaskRequestBody)
            );
            var responseBody = ProcessBody(
                entry.Span,
                entry.ResponseBody,
                configuration.MaskResponseBody,
                nameof(ApitallyOptions.MaskResponseBody)
            );
            if (requestBody is not null)
                attributes[RequestBodyAttribute] = requestBody;
            if (responseBody is not null)
                attributes[ResponseBodyAttribute] = responseBody;
            return true;
        }
        catch (Exception exception)
        {
            diagnostics.SpanRedactionFailed(exception);
            return false;
        }
    }

    public string RedactQuery(string query)
    {
        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            var name = separator < 0 ? parts[i] : parts[i][..separator];
            if (ShouldRedactQueryParam(Uri.UnescapeDataString(name.Replace('+', ' '))))
                parts[i] = name + "=" + Redacted;
        }
        return string.Join('&', parts);
    }

    public string RedactUrl(string url)
    {
        var separator = url.IndexOf('?');
        return separator < 0 ? url : url[..(separator + 1)] + RedactQuery(url[(separator + 1)..]);
    }

    private void RedactQueryAndHeaderAttributes(Dictionary<string, object?> attributes)
    {
        foreach (var (key, value) in attributes.ToList())
        {
            if (key == "url.query" && value is string query)
                attributes[key] = RedactQuery(query);
            else if (QueryAttributes.Contains(key) && value is string url)
                attributes[key] = RedactUrl(url);
            else if (key.StartsWith(RequestHeaderPrefix, StringComparison.Ordinal))
                attributes[key] = RedactHeaderValue(key[RequestHeaderPrefix.Length..], value);
            else if (key.StartsWith(ResponseHeaderPrefix, StringComparison.Ordinal))
                attributes[key] = RedactHeaderValue(key[ResponseHeaderPrefix.Length..], value);
        }
    }

    private void AddHeaderAttributes(
        Dictionary<string, object?> attributes,
        string prefix,
        IReadOnlyList<KeyValuePair<string, string[]>>? headers
    )
    {
        if (headers is null)
            return;
        foreach (var (name, values) in headers)
        {
            var key = prefix + name.ToLowerInvariant();
            attributes[key] = RedactHeaderValue(key[prefix.Length..], values);
        }
    }

    private object? RedactHeaderValue(string name, object? value)
    {
        if (ShouldRedactHeader(name) || ShouldRedactHeader(name.Replace('_', '-')))
            return value is string ? Redacted : new[] { Redacted };
        if (name is not ("location" or "content-location" or "content_location"))
            return value;
        return value switch
        {
            string url => RedactUrl(url),
            string[] urls => urls.Select(RedactUrl).ToArray(),
            _ => value,
        };
    }

    private object? ProcessBody(
        SpanSnapshot span,
        CapturedBody? body,
        Func<SpanSnapshot, byte[], byte[]?>? mask,
        string optionName
    )
    {
        if (body is null)
            return null;
        if (body.IsTooLarge)
            return BodyTooLarge;
        var bytes = Decompress(body.Bytes, body.ContentEncoding, out var failure);
        if (failure is not null)
            return failure;
        if (bytes.Length == 0)
            return null;
        if (mask is not null)
        {
            try
            {
                bytes = mask(span, bytes)!;
            }
            catch (Exception exception)
            {
                diagnostics.BodyMaskFailed(optionName, exception);
                return Redacted;
            }
            if (bytes is null)
                return Redacted;
            if (bytes.Length > MaxBodySize)
                return BodyTooLarge;
        }
        return RedactBody(bytes);
    }

    // Returns the decoded bytes bounded to MaxBodySize, or sets the sentinel to export instead.
    public static byte[] Decompress(byte[] bytes, string? contentEncoding, out string? failure)
    {
        failure = null;
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(encoding) || encoding == "identity")
            return bytes;
        try
        {
            using var input = new MemoryStream(bytes);
            using Stream decoder = encoding switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => throw new NotSupportedException(),
            };
            var buffer = new byte[MaxBodySize + 1];
            var length = 0;
            int read;
            while (
                length < buffer.Length
                && (read = decoder.Read(buffer, length, buffer.Length - length)) > 0
            )
                length += read;
            if (length > MaxBodySize)
            {
                failure = BodyTooLarge;
                return [];
            }
            return buffer[..length];
        }
        catch
        {
            failure = Redacted;
            return [];
        }
    }

    // Whether a body is JSON is decided by a parse attempt, never by content type.
    private object RedactBody(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return bytes;
        }
        try
        {
            return RedactJson(bytes);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    // Rewrites JSON token by token, replacing string values of matching object keys at any depth.
    private string RedactJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var output = new ArrayBufferWriter<byte>(bytes.Length);
        using (var writer = new Utf8JsonWriter(output, JsonWriterOptions))
        {
            string? propertyName = null;
            while (reader.Read())
            {
                var valuePropertyName = propertyName;
                propertyName = null;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        propertyName = reader.GetString()!;
                        writer.WritePropertyName(propertyName);
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(
                            valuePropertyName is not null
                            && ShouldRedactBodyField(valuePropertyName)
                                ? Redacted
                                : reader.GetString()
                        );
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(reader.GetBoolean());
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                }
            }
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private bool ShouldRedactQueryParam(string name) =>
        DefaultQueryParamPattern().IsMatch(name)
        || RuntimeConfiguration.MatchesAny(configuration.MaskQueryParams, name);

    private bool ShouldRedactHeader(string name) =>
        DefaultHeaderPattern().IsMatch(name)
        || RuntimeConfiguration.MatchesAny(configuration.MaskHeaders, name);

    private bool ShouldRedactBodyField(string name) =>
        DefaultBodyFieldPattern().IsMatch(name)
        || RuntimeConfiguration.MatchesAny(configuration.MaskBodyFields, name);

    [GeneratedRegex(
        "auth|api[-_]?key|secret|token|password|pwd",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex DefaultQueryParamPattern();

    [GeneratedRegex(
        "auth|api[-_]?key|secret|token|cookie",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex DefaultHeaderPattern();

    [GeneratedRegex(
        "password|pwd|token|secret|auth|card[-_ ]?number|ccv|ssn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex DefaultBodyFieldPattern();
}
