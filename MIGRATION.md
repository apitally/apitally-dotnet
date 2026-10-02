# Migrating from 0.x to 1.x

This guide is also available in the [Apitally documentation](https://docs.apitally.io/sdk-reference/dotnet/v1/migration).

The .NET SDK now uses OpenTelemetry to collect and send metrics, logs, and traces.

> [!WARNING]
> Request logging, tracing, and application log capture are now enabled by default. If you previously used the SDK for metrics only, set `SampleRate = 0` to keep that behavior.

## Installation and setup

The updated [setup guide](https://docs.apitally.io/sdk-reference/dotnet/v1/setup-guides/aspnet-core) provides the installation steps and initialization code. Follow it to replace your existing SDK integration.

The SDK now requires .NET 8 or later. If your application references OpenTelemetry packages directly, upgrade them to version 1.19.0 or later.

### Write tokens replace client IDs

The SDK now authenticates with a **write token** instead of a client ID. Your existing app's token (`apt_...`) is available under _Setup instructions_ in the [Apitally dashboard](https://app.apitally.io/apps).

Use this token as the `WriteToken` option in place of `ClientId`, or set the `APITALLY_WRITE_TOKEN` environment variable. A missing or invalid token no longer fails application startup. Instead, the SDK logs an error and disables itself.

### `UseApitally()` has been removed

`AddApitally()` now registers everything the SDK needs, including its middleware. Remove the `app.UseApitally()` call. The SDK adds its middleware at the start of the request pipeline automatically.

```csharp
// Before
builder.Services.AddApitally(options =>
{
    options.ClientId = "your-client-id";
});

var app = builder.Build();
app.UseApitally();

// After
builder.Services.AddApitally(options =>
{
    options.WriteToken = "your-write-token";
});

var app = builder.Build();
```

If you use a `Startup` class, keep calling `services.AddApitally()` in `ConfigureServices` and remove `app.UseApitally()` from `Configure`.

## Configuration changes

The `RequestLoggingOptions` class and `RequestLogging` property have been removed. Their settings are now properties directly on `ApitallyOptions`, with the option changes listed below.

The same applies to the `Apitally` section in `appsettings.json`:

```jsonc
// Before
{
  "Apitally": {
    "ClientId": "your-client-id",
    "RequestLogging": {
      "Enabled": true,
      "IncludeRequestBody": true,
      "IncludeResponseBody": true,
      "HeaderMaskPatterns": ["^X-Internal-"]
    }
  }
}

// After
{
  "Apitally": {
    "WriteToken": "your-write-token",
    "CaptureRequestBody": true,
    "CaptureResponseBody": true,
    "MaskHeaders": ["^X-Internal-"]
  }
}
```

### Changed options

The following options have been changed:

| Option | Change |
| --- | --- |
| `ClientId` | Replaced by `WriteToken`, which requires a new credential. |
| `Env` | Defaults to the lowercased ASP.NET Core environment name instead of `default`, with `Production` shortened to `prod` and `Development` to `dev`. Remove the option if the derived name fits your setup. |
| `RequestLogging.CaptureLogs` | Moved to `CaptureLogs`. Default changed from `false` to `true`. |
| `RequestLogging.IncludeRequestHeaders` | Renamed to `CaptureRequestHeaders`. |
| `RequestLogging.IncludeRequestBody` | Renamed to `CaptureRequestBody`. |
| `RequestLogging.IncludeResponseHeaders` | Renamed to `CaptureResponseHeaders`. |
| `RequestLogging.IncludeResponseBody` | Renamed to `CaptureResponseBody`. |
| `RequestLogging.QueryParamMaskPatterns` | Renamed to `MaskQueryParams`. |
| `RequestLogging.HeaderMaskPatterns` | Renamed to `MaskHeaders`. |
| `RequestLogging.BodyFieldMaskPatterns` | Renamed to `MaskBodyFields`. |
| `RequestLogging.MaskRequestBody` | Moved to `MaskRequestBody` with new arguments. |
| `RequestLogging.MaskResponseBody` | Moved to `MaskResponseBody` with new arguments. |
| `RequestLogging.ShouldExclude` | Replaced by `SampleOnRequest` or `SampleOnResponse` with new arguments and return values. |
| `RequestLogging.PathExcludePatterns` | Renamed to `ExcludePaths`. Matches actual request paths instead of matched route patterns. |

### Removed options

These options have been removed:

| Removed option | Migration |
| --- | --- |
| `RequestLogging` | Set its properties directly on `ApitallyOptions`, applying the changes above. Remove its `Enabled` flag. |
| `RequestLogging.Enabled` and `RequestLogging.CaptureTraces` | Previously defaulted to `false`. Request logging and tracing are now enabled by default. Use `SampleRate = 0` to disable request logs and traces. |
| `RequestLogging.IncludeQueryParams` | Query parameters are now always captured. To mask all values, use `MaskQueryParams = [".*"]`. |
| `RequestLogging.IncludeException` | Unhandled exceptions are now always captured in request traces. |

The [configuration reference](https://docs.apitally.io/sdk-reference/dotnet/v1/configuration) lists all available options.

## Consumer identification

The SDK now provides the `IApitally` service with a `SetConsumer()` method. Setting the `ApitallyConsumer` item in `HttpContext.Items` no longer has any effect, and the `ApitallyConsumer` class has been removed.

Call `SetConsumer()` where the consumer is known, such as in your authentication code. Inject `IApitally` into your controllers, Minimal API handlers, or middleware, or resolve it from `HttpContext.RequestServices`:

```csharp
// Before
context.Items["ApitallyConsumer"] = new ApitallyConsumer
{
    Identifier = user.Identifier,
    Name = user.Name,
    Group = user.Group,
};

// After
var apitally = context.RequestServices.GetRequiredService<IApitally>();
apitally.SetConsumer(
    user.Identifier,
    name: user.Name, // optional
    group: user.Group // optional
);
```

The identifier is now always a `string`. Convert numeric identifiers with `ToString()`.

## Body masking callbacks

`MaskRequestBody` and `MaskResponseBody` now both receive `(span, body)`, rather than the `Request` and `Response` objects. The body is passed as `byte[]` after decompression.

Callbacks may run later on another thread against an ended span snapshot. Request metadata is available through [`span.Attributes`](https://docs.apitally.io/sdk-reference/dotnet/v1/attributes).

For example, a callback that masks bodies for admin routes becomes:

```csharp
// Before
options.RequestLogging.MaskRequestBody = request =>
    request.Path?.StartsWith("/admin/") == true ? null : request.Body;

// After
options.MaskRequestBody = (span, body) =>
    span.Attributes.GetValueOrDefault("http.route") is string route && route.StartsWith("/admin/")
        ? null
        : body;
```

## Request exclusion

Use sampling callbacks to exclude requests: `SampleOnRequest` for early decisions based on the request, or `SampleOnResponse` for decisions based on the response status or consumer. Both receive the span as their only argument.

The callbacks should return `1.0` to capture the request, and `0.0` to exclude it. Callbacks can also return any probability between 0 and 1. Returning `null` from `SampleOnRequest` applies `SampleRate`, and returning `null` from `SampleOnResponse` preserves the earlier sampling decision.

For example, to capture only error responses:

```csharp
// Before
options.RequestLogging.ShouldExclude = (request, response) => response.StatusCode < 400;

// After
options.SampleOnResponse = span =>
    span.Attributes.GetValueOrDefault("http.response.status_code") is long statusCode
    && statusCode >= 400
        ? 1.0
        : 0.0;
```

Replace the `ShouldExclude` option with the appropriate sampling callback. Note that captured headers and bodies are not available in sampling callbacks.

Sampling affects request logs and traces, but not metrics.

See [sampling](https://docs.apitally.io/sdk-reference/dotnet/v1/sampling) for details.

### Path exclusions

`ExcludePaths` now matches request paths rather than matched route patterns. If a pattern contains route parameters, update it to match concrete values. For example, replace `"^/users/\\{id\\}$"` with `"^/users/[^/]+$"` to match `/users/123`.

## Existing OpenTelemetry setups

If your application registers a tracer provider via `AddOpenTelemetry().WithTracing(...)`, the SDK automatically adds its span processor. No manual registration is required.

If you build a tracer provider separately, for example with `Sdk.CreateTracerProviderBuilder()`, add the ASP.NET Core instrumentation and the `apitally.otel` source to it, and register it with dependency injection so the SDK can find it:

```csharp
builder.Services.AddSingleton<TracerProvider>(tracerProvider);
builder.Services.AddApitally();
```

Review these settings when upgrading:

- **Sampling:** Previously, your provider's sampler affected traces but not Apitally's request logs. It now affects both. Check that its sampling rate provides the request log coverage you want. Metrics remain unsampled.
- **Sources:** Previously, the SDK captured activities from any `ActivitySource` during requests. It now captures only the activities your provider is configured for. Register your own sources with `AddSource()`, and add `AddHttpClientInstrumentation()` to keep tracing outgoing HTTP calls.

## Other changes

- **Integration tests:** The SDK is automatically disabled in tests using the in-memory `TestServer`, including the default `WebApplicationFactory`. For tests running against a real Kestrel server, set `Disabled = true` or the `APITALLY_DISABLED` environment variable.
- **Network access:** The SDK now sends data to `otlp.apitally.io` instead of `hub.apitally.io`. Update firewall allowlists if necessary.
- **Removed types:** The `Apitally.Models` namespace, `ApitallyConsumer`, `RequestLoggingOptions`, `ValidationErrorFilter`, and `ValidationError` have been removed from the public API.
