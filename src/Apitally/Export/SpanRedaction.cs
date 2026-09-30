using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
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
internal sealed record CapturedBody(byte[] Bytes, string? ContentEncoding)
{
    public static readonly CapturedBody TooLarge = new([], null);
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

    private string RedactQuery(string query)
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

    private string RedactUrl(string url)
    {
        var separator = url.IndexOf('?');
        return separator < 0 ? url : url[..(separator + 1)] + RedactQuery(url[(separator + 1)..]);
    }

    private void RedactQueryAndHeaderAttributes(Dictionary<string, object?> attributes)
    {
        List<KeyValuePair<string, object?>>? replacements = null;
        foreach (var (key, value) in attributes)
        {
            object? replacement;
            if (key == "url.query" && value is string query)
                replacement = RedactQuery(query);
            else if (QueryAttributes.Contains(key) && value is string url)
                replacement = RedactUrl(url);
            else if (key.StartsWith(RequestHeaderPrefix, StringComparison.Ordinal))
                replacement = RedactHeaderValue(key[RequestHeaderPrefix.Length..], value);
            else if (key.StartsWith(ResponseHeaderPrefix, StringComparison.Ordinal))
                replacement = RedactHeaderValue(key[ResponseHeaderPrefix.Length..], value);
            else
                continue;
            (replacements ??= []).Add(new(key, replacement));
        }
        if (replacements is null)
            return;
        foreach (var (key, value) in replacements)
            attributes[key] = value;
    }

    private void AddHeaderAttributes(
        Dictionary<string, object?> attributes,
        string prefix,
        IReadOnlyList<KeyValuePair<string, string[]>>? headers
    )
    {
        if (headers is null)
            return;
        attributes.EnsureCapacity(attributes.Count + headers.Count);
        foreach (var (name, values) in headers)
        {
            var lowercaseName = name.ToLowerInvariant();
            attributes[prefix + lowercaseName] = RedactHeaderValue(lowercaseName, values);
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
        if (!Utf8.IsValid(bytes))
            return bytes;
        try
        {
            return RedactJson(bytes);
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    // Rewrites JSON token by token, replacing string values of matching object keys at any depth.
    // Only escaped string values are converted to .NET strings, which limits allocations.
    private string RedactJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var output = new ArrayBufferWriter<byte>(bytes.Length);
        using (var writer = new Utf8JsonWriter(output, JsonWriterOptions))
        {
            Span<char> nameBuffer = stackalloc char[256];
            // Initialized from the buffer so the compiler allows it to reference stack memory.
            ReadOnlySpan<char> propertyName = nameBuffer[..0];
            var hasPropertyName = false;
            while (reader.Read())
            {
                var isPropertyValue = hasPropertyName;
                hasPropertyName = false;
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
                        // An unescaped name never has more UTF-16 characters than UTF-8 bytes.
                        Span<char> name =
                            reader.ValueSpan.Length <= nameBuffer.Length
                                ? nameBuffer
                                : new char[reader.ValueSpan.Length];
                        propertyName = name[..reader.CopyString(name)];
                        hasPropertyName = true;
                        writer.WritePropertyName(propertyName);
                        break;
                    case JsonTokenType.String:
                        if (isPropertyValue && ShouldRedactBodyField(propertyName))
                            writer.WriteStringValue(Redacted);
                        else if (reader.ValueIsEscaped)
                            writer.WriteStringValue(reader.GetString());
                        else
                            writer.WriteStringValue(reader.ValueSpan);
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

    private bool ShouldRedactBodyField(ReadOnlySpan<char> name) =>
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
        "password|pwd|token|secret|auth|card[-_ ]?number|ccv|cvv|cvc|ssn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        PatternTimeoutMilliseconds
    )]
    private static partial Regex DefaultBodyFieldPattern();
}
