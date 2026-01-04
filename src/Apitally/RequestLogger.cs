namespace Apitally;

using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Apitally.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

class RequestLogger(IOptions<ApitallyOptions> options, ILogger<RequestLogger> logger)
    : BackgroundService,
        IDisposable
{
    private const int MaxBodySize = 50_000; // 50 KB (uncompressed)
    private const int MaxFileSize = 1_000_000; // 1 MB (compressed)
    private const int MaxFiles = 50;
    private const int MaxPendingWrites = 100;
    private const int MaintainIntervalSeconds = 1;
    private static readonly byte[] BodyTooLarge = Encoding.UTF8.GetBytes("<body too large>");
    private static readonly byte[] BodyMasked = Encoding.UTF8.GetBytes("<masked>");
    private const string Masked = "******";
    private static readonly string[] ExcludePathPatterns =
    [
        "/_?healthz?$",
        "/_?health[_-]?checks?$",
        "/_?heart[_-]?beats?$",
        "/ping$",
        "/ready$",
        "/live$",
        "/favicon(?:-[\\w-]+)?\\.(ico|png|svg)$",
        "/apple-touch-icon(?:-[\\w-]+)?\\.png$",
        "/robots\\.txt$",
        "/sitemap\\.xml$",
        "/manifest\\.json$",
        "/site\\.webmanifest$",
        "/service-worker\\.js$",
        "/sw\\.js$",
        "/\\.well-known/",
    ];
    private static readonly string[] ExcludeUserAgentPatterns =
    [
        "health[-_ ]?check",
        "microsoft-azure-application-lb",
        "googlehc",
        "kube-probe",
    ];
    private static readonly string[] MaskQueryParamPatterns =
    [
        "auth",
        "api-?key",
        "secret",
        "token",
        "password",
        "pwd",
    ];
    private static readonly string[] MaskHeaderPatterns =
    [
        "auth",
        "api-?key",
        "secret",
        "token",
        "cookie",
    ];
    private static readonly string[] MaskBodyFieldPatterns =
    [
        "password",
        "pwd",
        "token",
        "secret",
        "auth",
        "card[-_ ]?number",
        "ccv",
        "ssn",
    ];

    public static readonly string[] AllowedContentTypes =
    [
        MediaTypeNames.Application.Json,
        MediaTypeNames.Text.Plain,
    ];

    private readonly object _lock = new();
    private readonly ConcurrentQueue<RequestLogItem> _pendingWrites = new();
    private readonly ConcurrentQueue<TempGzipFile> _files = new();
    private readonly List<Regex> _compiledPathExcludePatterns = CompilePatterns(
        ExcludePathPatterns,
        options.Value.RequestLogging.PathExcludePatterns
    );
    private readonly List<Regex> _compiledUserAgentExcludePatterns = CompilePatterns(
        ExcludeUserAgentPatterns,
        null
    );
    private readonly List<Regex> _compiledQueryParamMaskPatterns = CompilePatterns(
        MaskQueryParamPatterns,
        options.Value.RequestLogging.QueryParamMaskPatterns
    );
    private readonly List<Regex> _compiledHeaderMaskPatterns = CompilePatterns(
        MaskHeaderPatterns,
        options.Value.RequestLogging.HeaderMaskPatterns
    );
    private readonly List<Regex> _compiledBodyFieldMaskPatterns = CompilePatterns(
        MaskBodyFieldPatterns,
        options.Value.RequestLogging.BodyFieldMaskPatterns
    );
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            | System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };
    private TempGzipFile? _currentFile;
    private long? _suspendUntil;
    private bool _disposed;

    public bool Enabled { get; private set; } = options.Value.RequestLogging.Enabled;
    public bool Suspended =>
        _suspendUntil != null && _suspendUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static List<Regex> CompilePatterns(
        string[] defaultPatterns,
        List<string>? additionalPatterns
    )
    {
        return
        [
            .. defaultPatterns
                .Concat(additionalPatterns ?? Enumerable.Empty<string>())
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ];
    }

    public void LogRequest(
        Request request,
        Response response,
        Exception? exception = null,
        List<LogRecord>? logs = null
    )
    {
        if (!Enabled || Suspended)
        {
            return;
        }

        var requestLoggingOptions = options.Value.RequestLogging;

        try
        {
            var path = !string.IsNullOrEmpty(request.Path)
                ? request.Path
                : new Uri(request.Url).AbsolutePath;
            var userAgent = GetHeaderValue(request.Headers, "user-agent");
            if (
                ShouldExcludePath(path)
                || ShouldExcludeUserAgent(userAgent)
                || requestLoggingOptions.ShouldExclude(request, response)
            )
            {
                return;
            }

            if (
                !requestLoggingOptions.IncludeRequestBody
                || !HasSupportedContentType(request.Headers)
            )
            {
                request.Body = null;
            }
            if (
                !requestLoggingOptions.IncludeResponseBody
                || !HasSupportedContentType(response.Headers)
            )
            {
                response.Body = null;
            }

            var exceptionInfo =
                exception != null && requestLoggingOptions.IncludeException
                    ? new ExceptionInfo
                    {
                        Type = exception.GetType().Name,
                        Message = ServerErrorCounter.TruncateMessage(exception.Message),
                        StackTrace = ServerErrorCounter.TruncateStackTrace(
                            exception.StackTrace ?? string.Empty
                        ),
                    }
                    : null;

            if (!requestLoggingOptions.CaptureLogs)
            {
                logs = null;
            }

            // Create log item and enqueue
            var item = new RequestLogItem
            {
                Request = request,
                Response = response,
                Exception = exceptionInfo,
                Logs = logs,
            };
            _pendingWrites.Enqueue(item);

            if (_pendingWrites.Count > MaxPendingWrites)
            {
                _pendingWrites.TryDequeue(out _);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while logging request");
        }
    }

    private void WriteToFile()
    {
        if (!Enabled || _pendingWrites.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            _currentFile ??= new TempGzipFile();
            while (_pendingWrites.TryDequeue(out var item))
            {
                ApplyMasking(item);
                var serializedItem = JsonSerializer.Serialize(item, _serializerOptions);
                _currentFile.WriteLine(Encoding.UTF8.GetBytes(serializedItem));
            }
        }
    }

    public TempGzipFile? GetFile()
    {
        return _files.TryDequeue(out var file) ? file : null;
    }

    public void RetryFileLater(TempGzipFile file)
    {
        _files.Enqueue(file);
    }

    public void RotateFile()
    {
        lock (_lock)
        {
            if (_currentFile != null)
            {
                _currentFile.Dispose();
                _files.Enqueue(_currentFile);
                _currentFile = null;
            }
        }
    }

    public void Maintain()
    {
        WriteToFile();

        if (_currentFile != null && _currentFile.Size > MaxFileSize)
        {
            RotateFile();
        }

        while (_files.Count > MaxFiles)
        {
            if (_files.TryDequeue(out var file))
            {
                file.Delete();
            }
        }

        if (_suspendUntil != null && !Suspended)
        {
            _suspendUntil = null;
        }
    }

    public void SuspendFor(TimeSpan duration)
    {
        _suspendUntil = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
    }

    public void Clear()
    {
        _pendingWrites.Clear();
        RotateFile();
        while (_files.TryDequeue(out var file))
        {
            file.Delete();
        }
    }

    private void ApplyMasking(RequestLogItem item)
    {
        var requestLoggingOptions = options.Value.RequestLogging;
        var request = item.Request;
        var response = item.Response;

        if (request.Body != null)
        {
            // Apply user-provided masking callback for request body
            request.Body = requestLoggingOptions.MaskRequestBody(request) ?? BodyMasked;

            if (request.Body.Length > MaxBodySize)
            {
                request.Body = BodyTooLarge;
            }

            // Mask request body fields (if JSON)
            if (
                !request.Body.SequenceEqual(BodyTooLarge)
                && !request.Body.SequenceEqual(BodyMasked)
                && RequestLogger.HasJsonContentType(request.Headers)
            )
            {
                request.Body = MaskJsonBody(request.Body);
            }
        }

        if (response.Body != null)
        {
            // Apply user-provided masking callback for response body
            response.Body = requestLoggingOptions.MaskResponseBody(request, response) ?? BodyMasked;

            if (response.Body.Length > MaxBodySize)
            {
                response.Body = BodyTooLarge;
            }

            // Mask response body fields (if JSON)
            if (
                !response.Body.SequenceEqual(BodyTooLarge)
                && !response.Body.SequenceEqual(BodyMasked)
                && RequestLogger.HasJsonContentType(response.Headers)
            )
            {
                response.Body = MaskJsonBody(response.Body);
            }
        }

        // Mask headers
        request.Headers = requestLoggingOptions.IncludeRequestHeaders
            ? MaskHeaders(request.Headers)
            : [];
        response.Headers = requestLoggingOptions.IncludeResponseHeaders
            ? MaskHeaders(response.Headers)
            : [];

        // Mask query params
        if (!string.IsNullOrEmpty(request.Url))
        {
            var uri = new Uri(request.Url);
            var query = uri.Query.TrimStart('?');
            if (!requestLoggingOptions.IncludeQueryParams)
            {
                query = string.Empty;
            }
            else if (!string.IsNullOrEmpty(query))
            {
                query = MaskQueryParams(query);
            }
            var uriBuilder = new UriBuilder(uri) { Query = query };
            request.Url = uriBuilder.Uri.ToString();
        }
    }

    private bool ShouldExcludePath(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && _compiledPathExcludePatterns.Any(p => p.IsMatch(path));
    }

    private bool ShouldExcludeUserAgent(string? userAgent)
    {
        return !string.IsNullOrEmpty(userAgent)
            && _compiledUserAgentExcludePatterns.Any(p => p.IsMatch(userAgent));
    }

    private bool ShouldMaskQueryParam(string name)
    {
        return _compiledQueryParamMaskPatterns.Any(p => p.IsMatch(name));
    }

    private bool ShouldMaskHeader(string name)
    {
        return _compiledHeaderMaskPatterns.Any(p => p.IsMatch(name));
    }

    private bool ShouldMaskBodyField(string name)
    {
        return _compiledBodyFieldMaskPatterns.Any(p => p.IsMatch(name));
    }

    private byte[] MaskJsonBody(byte[] body)
    {
        try
        {
            var json = Encoding.UTF8.GetString(body);
            var node = JsonNode.Parse(json);
            if (node != null)
            {
                MaskJsonNode(node);
                return Encoding.UTF8.GetBytes(node.ToJsonString(_serializerOptions));
            }
            return body;
        }
        catch
        {
            return body;
        }
    }

    private void MaskJsonNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                var propertiesToMask = new List<string>();
                foreach (var property in jsonObject)
                {
                    if (
                        property.Value is JsonValue jsonValue
                        && jsonValue.TryGetValue<string>(out _)
                        && ShouldMaskBodyField(property.Key)
                    )
                    {
                        propertiesToMask.Add(property.Key);
                    }
                    else if (property.Value != null)
                    {
                        MaskJsonNode(property.Value);
                    }
                }
                foreach (var propertyKey in propertiesToMask)
                {
                    jsonObject[propertyKey] = JsonValue.Create(Masked);
                }
                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    if (item != null)
                    {
                        MaskJsonNode(item);
                    }
                }
                break;
        }
    }

    private string MaskQueryParams(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return query;
        }

        var pairs = query.Split('&');
        var maskedPairs = pairs.Select(pair =>
        {
            var parts = pair.Split('=', 2);
            var name = parts[0];
            var value = parts.Length > 1 ? parts[1] : string.Empty;
            return $"{name}={(ShouldMaskQueryParam(name) ? Masked : value)}";
        });
        return string.Join('&', maskedPairs);
    }

    private Header[] MaskHeaders(Header[] headers)
    {
        return
        [
            .. headers.Select(header => new Header(
                header.Name,
                ShouldMaskHeader(header.Name) ? Masked : header.Value
            )),
        ];
    }

    private static bool HasSupportedContentType(Header[] headers)
    {
        var contentType = GetHeaderValue(headers, "content-type");
        return contentType != null
            && AllowedContentTypes.Any(ct =>
                contentType.StartsWith(ct, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static bool HasJsonContentType(Header[] headers)
    {
        var contentType = GetHeaderValue(headers, "content-type");
        return contentType != null
            && Regex.IsMatch(contentType, @"\bjson\b", RegexOptions.IgnoreCase);
    }

    private static string? GetHeaderValue(Header[] headers, string name)
    {
        return headers
            .Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .FirstOrDefault();
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Maintain();
            await Task.Delay(TimeSpan.FromSeconds(MaintainIntervalSeconds), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Enabled = false;
        await base.StopAsync(cancellationToken);
        Clear();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                base.Dispose();
                _currentFile?.Dispose();
                foreach (var file in _files)
                {
                    file.Dispose();
                }
            }
            _disposed = true;
        }
    }

    public override void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~RequestLogger()
    {
        Dispose(false);
    }
}
