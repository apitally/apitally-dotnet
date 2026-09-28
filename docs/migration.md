# Migrating from 0.x to 1.x

This guide is also available in the [Apitally documentation](https://docs.apitally.io/sdk-reference/dotnet/v1/migration).

The .NET SDK now uses OpenTelemetry to collect and send metrics, logs, and traces.

> [!WARNING]
> Request logging, tracing, and application log capture are now enabled by default. If you previously used the SDK for metrics only, set `SampleRate = 0` to keep that behavior.

## Requirements

- .NET 8, 9, or 10. Support for .NET 6 and 7 has been dropped.
- If your application already references OpenTelemetry packages, upgrade them to version 1.19.0 or later.

## Installation and setup

The updated [setup guide](https://docs.apitally.io/sdk-reference/dotnet/v1/setup-guides/aspnet-core) provides the installation steps and initialization code. Follow it to replace your existing SDK integration.

### Write tokens replace client IDs

The SDK now authenticates with a **write token** instead of a client ID. Your existing app's token (`apt_...`) is available under _Setup instructions_ in the [Apitally dashboard](https://app.apitally.io/apps).

Use this token as the `WriteToken` option in place of `ClientId`, or set the `APITALLY_WRITE_TOKEN` environment variable. A missing or invalid token logs an error and disables the SDK. It no longer fails application startup.

### `UseApitally()` has been removed

`AddApitally()` now registers everything the SDK needs, including its middleware. Remove the `app.UseApitally()` call. The SDK places its middleware at the start of the request pipeline automatically, so its position relative to other middleware no longer needs attention.

```csharp
// Before
builder.Services.AddApitally(options =>
{
    options.ClientId = "your-client-id";
    options.Env = "prod";
});

var app = builder.Build();
app.UseApitally();

// After
builder.Services.AddApitally(options =>
{
    options.WriteToken = "apt_...";
    options.Env = "prod";
});

var app = builder.Build();
```

If you use a `Startup` class, keep calling `services.AddApitally()` in `ConfigureServices` and remove `app.UseApitally()` from `Configure`.

## Configuration changes

The `RequestLoggingOptions` class and the `RequestLogging` property have been removed. Their settings are now properties directly on `ApitallyOptions`, with the option changes listed below.

The same applies to the `Apitally` section in `appsettings.json`, which the SDK still binds automatically:

```jsonc
// Before
{
  "Apitally": {
    "ClientId": "your-client-id",
    "Env": "prod",
    "RequestLogging": {
      "Enabled": true,
      "IncludeRequestHeaders": true,
      "IncludeRequestBody": true,
      "IncludeResponseBody": true,
      "CaptureLogs": true,
      "CaptureTraces": true,
      "HeaderMaskPatterns": ["^X-Internal-"],
      "PathExcludePatterns": ["/metrics$"]
    }
  }
}

// After
{
  "Apitally": {
    "WriteToken": "apt_...",
    "Env": "prod",
    "CaptureRequestHeaders": true,
    "CaptureRequestBody": true,
    "CaptureResponseBody": true,
    "MaskHeaders": ["^X-Internal-"],
    "ExcludePaths": ["/metrics$"]
  }
}
```

Values set in code take precedence over the `Apitally` configuration section, which takes precedence over the `APITALLY_WRITE_TOKEN` and `APITALLY_ENV` environment variables. Standard .NET environment variables such as `Apitally__SampleRate` also populate the configuration section.

Callbacks passed to `AddApitally(options => ...)` run after all configuration sources. Existing `services.PostConfigure<ApitallyOptions>(...)` calls registered after `AddApitally()` keep working, but we recommend moving them into the `AddApitally` callback.

### Changed options

The following options have been changed:

| Option | Change |
| --- | --- |
| `ClientId` | Replaced by `WriteToken`, which requires a new credential. |
| `Env` | Default changed from `default` to `dev`. Set it explicitly if you relied on the old default. |
| `RequestLogging.CaptureLogs` | Moved to `CaptureLogs`. Default changed from `false` to `true`. |
| `RequestLogging.IncludeRequestHeaders` | Renamed to `CaptureRequestHeaders`. |
| `RequestLogging.IncludeRequestBody` | Renamed to `CaptureRequestBody`. |
| `RequestLogging.IncludeResponseHeaders` | Renamed to `CaptureResponseHeaders`. |
| `RequestLogging.IncludeResponseBody` | Renamed to `CaptureResponseBody`. |
| `RequestLogging.QueryParamMaskPatterns` | Renamed to `MaskQueryParams`. |
| `RequestLogging.HeaderMaskPatterns` | Renamed to `MaskHeaders`. |
| `RequestLogging.BodyFieldMaskPatterns` | Renamed to `MaskBodyFields`. |
| `RequestLogging.PathExcludePatterns` | Renamed to `ExcludePaths`. Matches actual request paths instead of matched route patterns. |
| `RequestLogging.MaskRequestBody` | Moved to `MaskRequestBody` with new arguments. |
| `RequestLogging.MaskResponseBody` | Moved to `MaskResponseBody` with new arguments. |
| `RequestLogging.ShouldExclude` | Replaced by `SampleOnRequest` or `SampleOnResponse` with new arguments and return values. |

Custom patterns remain case-insensitive by default. You can now make a pattern case-sensitive with the .NET inline option `(?-i:...)`.

### Removed options

| Removed option | Migration |
| --- | --- |
| `RequestLogging` | Set its properties directly on `ApitallyOptions`, applying the changes above. |
| `RequestLogging.Enabled` and `RequestLogging.CaptureTraces` | Previously defaulted to `false`. Request logging and tracing are now enabled by default. Use `SampleRate = 0` to disable request logs and traces. |
| `RequestLogging.IncludeQueryParams` | Query parameters are now always captured. To mask all values, use `MaskQueryParams = [".*"]`. |
| `RequestLogging.IncludeException` | Exceptions are now always captured in request traces. |

### New options

| Option | Description |
| --- | --- |
| `AppVersion` | Version of your application, shown in the Apitally dashboard. |
| `Disabled` | Disables the SDK. The `APITALLY_DISABLED` and `OTEL_SDK_DISABLED` environment variables also disable it. |
| `SampleRate` | Share of requests captured in request logs and traces, between `0` and `1`. Defaults to `1`. Does not affect metrics. |
| `SampleOnRequest`, `SampleOnResponse` | Callbacks that refine sampling per request. See [request exclusion](#request-exclusion). |
| `MaskLogRecord` | Callback to mask or drop captured application logs. See [application logs](#application-logs). |

The [configuration reference](https://docs.apitally.io/sdk-reference/dotnet/v1/configuration) lists all available options.

## Consumer identification

The SDK now provides the `IApitally` service for request-level operations. Setting the `ApitallyConsumer` item in `HttpContext.Items` no longer has any effect, and the `ApitallyConsumer` class has been removed.

Call `SetConsumer()` where the consumer is known, such as in your authentication code. Inject `IApitally` into controllers, Minimal API handlers, or middleware, or resolve it from `HttpContext.RequestServices`:

```csharp
// Before
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        context.Items["ApitallyConsumer"] = new ApitallyConsumer
        {
            Identifier = context.User.Identity.Name,
            Name = context.User.FindFirst(ClaimTypes.Name)?.Value,
            Group = context.User.FindFirst(ClaimTypes.Role)?.Value,
        };
    }
    await next();
});

// After
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var apitally = context.RequestServices.GetRequiredService<IApitally>();
        apitally.SetConsumer(
            context.User.Identity.Name!,
            name: context.User.FindFirst(ClaimTypes.Name)?.Value, // optional
            group: context.User.FindFirst(ClaimTypes.Role)?.Value // optional
        );
    }
    await next();
});
```

The identifier is now always a `string`. Convert numeric identifiers with `ToString()`. `SetConsumer()` also accepts optional custom consumer attributes.

## Body masking callbacks

`MaskRequestBody` and `MaskResponseBody` now both have the signature `Func<SpanSnapshot, byte[], byte[]?>`, rather than receiving the `Request` and `Response` objects. The body is passed as `byte[]` after decompression. Return the masked body, or `null` to mask the entire body.

Callbacks may run later on another thread. Request metadata is available through [`span.Attributes`](https://docs.apitally.io/sdk-reference/dotnet/v1/attributes).

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

As before, callbacks run before pattern-based field masking, so `MaskBodyFields` and the default patterns still apply to the returned body.

## Request exclusion

Use sampling callbacks to exclude requests: `SampleOnRequest` for early decisions based on the request, or `SampleOnResponse` for decisions based on the response status or consumer. Both have the signature `Func<SpanSnapshot, double?>`.

Note that the meaning is reversed from `ShouldExclude`: the callbacks return the probability of **keeping** the request. Return `1.0` to capture the request and `0.0` to exclude it, or any value in between. Returning `null` from `SampleOnRequest` applies `SampleRate`; returning `null` from `SampleOnResponse` preserves the earlier sampling decision.

For example, to capture only failed requests from consumers other than an internal service:

```csharp
// Before
options.RequestLogging.ShouldExclude = (request, response) =>
    request.Consumer == "internal-service" || response.StatusCode < 400;

// After
options.SampleOnResponse = span =>
{
    var consumer = span.Attributes.GetValueOrDefault("apitally.consumer.identifier") as string;
    var statusCode = span.Attributes.GetValueOrDefault("http.response.status_code") as long?;
    return consumer == "internal-service" || statusCode < 400 ? 0.0 : 1.0;
};
```

Captured headers and bodies are not available in sampling callbacks.

As before, sampling and exclusion affect request logs and traces, but not metrics.

See [sampling](https://docs.apitally.io/sdk-reference/dotnet/v1/sampling) for details.

### Path exclusions

`ExcludePaths` now matches request paths rather than matched route patterns. If a pattern contains route parameters, update it to match concrete values. For example, replace `"^/users/\\{id\\}$"` with `"^/users/[^/]+$"` to match `/users/123`.

## Application logs

Application logs emitted during request handling are now captured by default. Set `CaptureLogs = false` to disable this.

Captured logs now also include structured values from message templates and log scopes, as well as exception details. As before, logs from `Microsoft.AspNetCore.*` categories are not captured. Your application's standard log filters apply, and you can narrow capture for Apitally only using the `Apitally` logging provider alias:

```json
{
  "Logging": {
    "Apitally": {
      "LogLevel": {
        "Default": "Information",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  }
}
```

Use the new `MaskLogRecord` callback to redact or drop individual log records before they are sent to Apitally. It doesn't change what other logging providers receive:

```csharp
options.MaskLogRecord = record =>
{
    if (record.CategoryName.StartsWith("MyApp.Payments"))
    {
        return null; // drop the log record
    }
    record.Attributes.Remove("email");
    return record;
};
```

## Tracing

Tracing no longer requires `CaptureTraces`. The SDK now traces incoming requests and outgoing `HttpClient` calls by default.

As before, activities from any `ActivitySource` created during request handling are captured, including your own sources and libraries with built-in instrumentation such as Npgsql. Existing custom spans keep working without changes.

### Custom spans

You can also create custom spans with `IApitally.StartActivity()`, without defining your own `ActivitySource`:

```csharp
using var activity = apitally.StartActivity("ProcessOrder");
activity?.SetTag("order.id", orderId);
```

### Library instrumentation packages

Libraries such as Entity Framework Core and SqlClient require OpenTelemetry instrumentation packages, which you register with the OpenTelemetry SDK using the `OpenTelemetry.Extensions.Hosting` package:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
    .AddSource("MyApp")
    .AddHttpClientInstrumentation()
    .AddEntityFrameworkCoreInstrumentation());

builder.Services.AddApitally();
```

When your application registers its own tracer provider, the SDK uses that provider instead of its default one, and captures only the activities that provider is configured for. Unlike 0.x, activities from other sources are not captured. Register your own sources with `AddSource()`, and add `AddHttpClientInstrumentation()` if you want to keep tracing outgoing HTTP calls.

## Existing OpenTelemetry setups

If your application registers a tracer provider through `AddOpenTelemetry().WithTracing(...)`, the SDK automatically uses it. No manual processor registration is required. Your exporters and other settings are unchanged, and captured headers and bodies never reach your exporters.

If you build a tracer provider separately, for example with `Sdk.CreateTracerProviderBuilder()`, register it with dependency injection so the SDK can find it. Configure it with ASP.NET Core instrumentation and the `apitally.otel` source before building it:

```csharp
builder.Services.AddSingleton<TracerProvider>(tracerProvider);
builder.Services.AddApitally();
```

Review these settings when upgrading:

- **Sampling:** Previously, your provider's sampler affected traces but not Apitally's request logs. It now affects both. Check that its sampling rate provides the request log coverage you want. Metrics remain unsampled.

## Other changes

- **Validation errors:** Validation errors are now also captured for Minimal APIs, in addition to MVC controllers. No setup is required.
- **Handled exceptions:** Exceptions handled by `UseExceptionHandler` are now captured automatically, regardless of middleware order. Use `IApitally.CaptureException()` to capture other exceptions that your code handles itself.
- **Integration tests:** The SDK is automatically disabled in tests using the in-memory `TestServer`, including the default `WebApplicationFactory`. For tests running against a real Kestrel server, set `Disabled = true` or the `APITALLY_DISABLED` environment variable.
- **File responses:** Response bodies served directly from files on disk, for example by `PhysicalFile()` or the static files middleware, are not captured.
- **Network access:** The SDK now sends data to `otlp.apitally.io` instead of `hub.apitally.io`. Update firewall allowlists if necessary. Proxies configured through `HTTPS_PROXY`, `HTTP_PROXY`, and `NO_PROXY` are still respected.
- **Removed types:** The `Apitally.Models` namespace, `ApitallyConsumer`, `RequestLoggingOptions`, `ValidationErrorFilter`, and `ValidationError` have been removed from the public API.
