namespace Apitally.Tests;

using System.Text;
using System.Text.Json;
using Apitally.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

public class RequestLoggerTests
{
    [Fact]
    public void LogRequest_ShouldWorkEndToEnd()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeQueryParams = true,
                IncludeRequestHeaders = true,
                IncludeRequestBody = true,
                IncludeResponseHeaders = true,
                IncludeResponseBody = true,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = new[] { new Header("User-Agent", "Test") },
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Size = 13,
            Body = Encoding.UTF8.GetBytes("{\"items\": []}"),
        };

        // Act
        requestLogger.LogRequest(request, response, new Exception("test"));

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);

        var item = items[0];
        Assert.Equal("GET", item.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("/items", item.GetProperty("request").GetProperty("path").GetString());
        Assert.Equal(
            "http://test/items",
            item.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.False(item.GetProperty("request").TryGetProperty("body", out _));
        Assert.Equal(200, item.GetProperty("response").GetProperty("status_code").GetInt32());
        Assert.Equal(
            0.123,
            item.GetProperty("response").GetProperty("response_time").GetDouble(),
            3
        );
        Assert.Equal(
            "{\"items\":[]}",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    item.GetProperty("response").GetProperty("body").GetString()!
                )
            )
        );

        var requestHeadersNode = item.GetProperty("request").GetProperty("headers");
        Assert.Equal(1, requestHeadersNode.GetArrayLength());
        Assert.Equal("User-Agent", requestHeadersNode[0][0].GetString());
        Assert.Equal("Test", requestHeadersNode[0][1].GetString());

        var responseHeadersNode = item.GetProperty("response").GetProperty("headers");
        Assert.Equal(1, responseHeadersNode.GetArrayLength());
        Assert.Equal("Content-Type", responseHeadersNode[0][0].GetString());
        Assert.Equal("application/json", responseHeadersNode[0][1].GetString());

        var exceptionNode = item.GetProperty("exception");
        Assert.Equal("Exception", exceptionNode.GetProperty("type").GetString());
        Assert.Equal("test", exceptionNode.GetProperty("message").GetString());

        requestLogger.Clear();
        Assert.Null(requestLogger.GetFile());
    }

    [Fact]
    public void LogRequest_ShouldExcludeBasedOnOptions()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeQueryParams = false,
                IncludeRequestHeaders = false,
                IncludeRequestBody = false,
                IncludeResponseHeaders = false,
                IncludeResponseBody = false,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "POST",
            Path = "/items",
            Url = "http://test/items?token=my-secret-token",
            Headers = new[]
            {
                new Header("Authorization", "Bearer 1234567890"),
                new Header("Content-Type", "application/json"),
            },
            Size = 16,
            Body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}"),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Size = 16,
            Body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);

        var item = items[0];
        Assert.Equal(
            "http://test/items",
            item.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.Empty(item.GetProperty("request").GetProperty("headers").EnumerateArray());
        Assert.False(item.GetProperty("request").TryGetProperty("body", out _));
        Assert.Empty(item.GetProperty("response").GetProperty("headers").EnumerateArray());
        Assert.False(item.GetProperty("response").TryGetProperty("body", out _));
    }

    [Fact]
    public void LogRequest_ShouldExcludeUsingCallback()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                ShouldExclude = (request, response) =>
                    request.Consumer?.Contains("tester") ?? false,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = Array.Empty<Header>(),
            Size = 13,
            Body = Encoding.UTF8.GetBytes("{\"items\": []}"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Empty(items);
    }

    [Fact]
    public void LogRequest_ShouldExcludeHealthCheckPath()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(new RequestLoggingOptions { Enabled = true });
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/healthz",
            Url = "http://test/healthz",
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = Array.Empty<Header>(),
            Size = 17,
            Body = Encoding.UTF8.GetBytes("{\"healthy\": true}"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Empty(items);
    }

    [Fact]
    public void LogRequest_ShouldExcludeHealthCheckUserAgent()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(new RequestLoggingOptions { Enabled = true });
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/",
            Url = "http://test/",
            Headers = new[] { new Header("User-Agent", "ELB-HealthChecker/2.0") },
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0,
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Empty(items);
    }

    [Fact]
    public void LogRequest_ShouldMaskHeaders()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeRequestHeaders = true,
                HeaderMaskPatterns = new List<string> { "(?i)test" },
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/test",
            Url = "http://localhost:8000/test?foo=bar",
            Headers = new[]
            {
                new Header("Accept", "text/plain"),
                new Header("Authorization", "Bearer 123456"),
                new Header("X-Test", "123456"),
            },
            Body = Array.Empty<byte>(),
        };
        var response = new Response { StatusCode = 200 };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);
        var requestHeaders = items[0].GetProperty("request").GetProperty("headers");

        var headers = new Dictionary<string, string>();
        foreach (var header in requestHeaders.EnumerateArray())
        {
            headers.Add(header[0].GetString()!, header[1].GetString()!);
        }

        Assert.Equal("text/plain", headers["Accept"]);
        Assert.Equal("******", headers["Authorization"]);
        Assert.Equal("******", headers["X-Test"]);
    }

    [Fact]
    public void LogRequest_ShouldMaskQueryParams()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeQueryParams = true,
                QueryParamMaskPatterns = new List<string> { "(?i)test" },
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/test",
            Url = "http://localhost/test?secret=123456&test=123456&other=abcdef",
            Headers = Array.Empty<Header>(),
            Body = Array.Empty<byte>(),
        };
        var response = new Response { StatusCode = 200 };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);
        var url = items[0].GetProperty("request").GetProperty("url").GetString();
        Assert.Contains("secret=******", url);
        Assert.Contains("test=******", url);
        Assert.Contains("other=abcdef", url);
    }

    [Fact]
    public void LogRequest_ShouldMaskBodyUsingCallback()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeRequestBody = true,
                IncludeResponseBody = true,
                MaskRequestBody = (req) =>
                {
                    if (req.Path == "/test")
                        return null;
                    return req.Body;
                },
                MaskResponseBody = (req, resp) =>
                {
                    if (req.Path == "/test")
                        return null;
                    return resp.Body;
                },
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/test",
            Headers = new[] { new Header("Content-Type", "application/json") },
            Body = Encoding.UTF8.GetBytes("test"),
        };
        var response = new Response
        {
            StatusCode = 200,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Body = Encoding.UTF8.GetBytes("test"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);
        var requestBody = Encoding.UTF8.GetString(
            Convert.FromBase64String(
                items[0].GetProperty("request").GetProperty("body").GetString()!
            )
        );
        Assert.Equal("<masked>", requestBody);

        var responseBody = Encoding.UTF8.GetString(
            Convert.FromBase64String(
                items[0].GetProperty("response").GetProperty("body").GetString()!
            )
        );
        Assert.Equal("<masked>", responseBody);
    }

    [Fact]
    public void LogRequest_ShouldMaskBodyFields()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeRequestBody = true,
                IncludeResponseBody = true,
                BodyFieldMaskPatterns = new List<string> { "(?i)custom" },
            }
        );

        var requestBodyJson =
            @"{""username"":""john_doe"",""password"":""secret123"",""token"":""abc123"",""custom"":""xyz789"",""user_id"":42,""api_key"":123,""normal_field"":""value"",""nested"":{""password"":""nested_secret"",""count"":5,""deeper"":{""auth"":""deep_token""}},""array"":[{""password"":""array_secret"",""id"":1},{""normal"":""text"",""token"":""array_token""}]}";
        var responseBodyJson =
            @"{""status"":""success"",""secret"":""response_secret"",""data"":{""pwd"":""response_pwd""}}";

        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "POST",
            Path = "/test",
            Url = "http://localhost:8000/test?foo=bar",
            Headers = new[] { new Header("Content-Type", "application/json") },
            Body = Encoding.UTF8.GetBytes(requestBodyJson),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.1,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Body = Encoding.UTF8.GetBytes(responseBodyJson),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        var items = GetLoggedItems(requestLogger);
        Assert.Single(items);

        var reqBodyBase64 = items[0].GetProperty("request").GetProperty("body").GetString();
        var reqBody = JsonDocument
            .Parse(Encoding.UTF8.GetString(Convert.FromBase64String(reqBodyBase64!)))
            .RootElement;

        var respBodyBase64 = items[0].GetProperty("response").GetProperty("body").GetString();
        var respBody = JsonDocument
            .Parse(Encoding.UTF8.GetString(Convert.FromBase64String(respBodyBase64!)))
            .RootElement;

        // Test fields that should be masked
        Assert.Equal("******", reqBody.GetProperty("password").GetString());
        Assert.Equal("******", reqBody.GetProperty("token").GetString());
        Assert.Equal("******", reqBody.GetProperty("custom").GetString());
        Assert.Equal("******", reqBody.GetProperty("nested").GetProperty("password").GetString());
        Assert.Equal(
            "******",
            reqBody.GetProperty("nested").GetProperty("deeper").GetProperty("auth").GetString()
        );
        Assert.Equal("******", reqBody.GetProperty("array")[0].GetProperty("password").GetString());
        Assert.Equal("******", reqBody.GetProperty("array")[1].GetProperty("token").GetString());
        Assert.Equal("******", respBody.GetProperty("secret").GetString());
        Assert.Equal("******", respBody.GetProperty("data").GetProperty("pwd").GetString());

        // Test fields that should NOT be masked
        Assert.Equal("john_doe", reqBody.GetProperty("username").GetString());
        Assert.Equal(42, reqBody.GetProperty("user_id").GetInt32());
        Assert.Equal(123, reqBody.GetProperty("api_key").GetInt32());
        Assert.Equal("value", reqBody.GetProperty("normal_field").GetString());
        Assert.Equal(5, reqBody.GetProperty("nested").GetProperty("count").GetInt32());
        Assert.Equal(1, reqBody.GetProperty("array")[0].GetProperty("id").GetInt32());
        Assert.Equal("text", reqBody.GetProperty("array")[1].GetProperty("normal").GetString());
        Assert.Equal("success", respBody.GetProperty("status").GetString());
    }

    private static JsonElement[] GetLoggedItems(RequestLogger requestLogger)
    {
        requestLogger.Maintain();
        requestLogger.RotateFile();

        var logFile = requestLogger.GetFile();
        if (logFile == null)
        {
            return Array.Empty<JsonElement>();
        }

        var lines = logFile.ReadDecompressedLines();
        var items = new JsonElement[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            items[i] = JsonDocument.Parse(lines[i]).RootElement;
        }

        logFile.Delete();
        return items;
    }

    private static RequestLogger CreateRequestLogger(RequestLoggingOptions requestLoggingOptions)
    {
        var options = new OptionsWrapper<ApitallyOptions>(
            new ApitallyOptions
            {
                ClientId = "00000000-0000-0000-0000-000000000000",
                Env = "test",
                RequestLogging = requestLoggingOptions,
            }
        );
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        return new RequestLogger(options, loggerFactory.CreateLogger<RequestLogger>());
    }
}
