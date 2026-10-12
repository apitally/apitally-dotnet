<p align="center">
  <a href="https://apitally.io" target="_blank">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://assets.apitally.io/logos/logo-horizontal-new-dark.png">
      <source media="(prefers-color-scheme: light)" srcset="https://assets.apitally.io/logos/logo-horizontal-new-light.png">
      <img alt="Apitally logo" src="https://assets.apitally.io/logos/logo-horizontal-new-light.png" width="220">
    </picture>
  </a>
</p>
<p align="center"><b>API monitoring & analytics made simple</b></p>
<p align="center" style="color: #ccc;">Metrics, logs, traces, and alerts for your APIs — with just a few lines of code.</p>
<br>
<p>
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://assets.apitally.io/screenshots/overview-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="https://assets.apitally.io/screenshots/overview-light.png">
  <img alt="Apitally dashboard" src="https://assets.apitally.io/screenshots/overview-light.png">
</picture>
</p>
<br>

# Apitally SDK for .NET

[![Tests](https://github.com/apitally/apitally-dotnet/actions/workflows/tests.yaml/badge.svg?event=push)](https://github.com/apitally/apitally-dotnet/actions)
[![Codecov](https://codecov.io/gh/apitally/apitally-dotnet/graph/badge.svg?token=NJzC7yKV6V)](https://codecov.io/gh/apitally/apitally-dotnet)
[![NuGet](https://img.shields.io/nuget/v/Apitally?logo=nuget&color=%23004880)](https://www.nuget.org/packages/Apitally)

Apitally is a simple API monitoring and analytics tool that makes it easy to understand API usage, monitor performance, and troubleshoot issues.
Get started in minutes by just adding a few lines of code. No infrastructure changes required, no dashboards to build.

The SDK is an [OpenTelemetry](https://opentelemetry.io) distribution and works alongside an existing OpenTelemetry setup.

Learn more about Apitally on our 🌎 [website](https://apitally.io) or check out the 📚 [documentation](https://docs.apitally.io).

> [!IMPORTANT]
> **Upgrading from 0.x?** Version 1.0 is a full rewrite with a new setup API. See the [migration guide](MIGRATION.md) for how to upgrade.

## Key features

- **API analytics**: Traffic, error and performance metrics for your API, each endpoint, and per API consumer. Drill down from metrics to individual API requests.
- **Request logs and traces**: Every request as a searchable log entry, with optional capture of headers and request/response bodies. Requests are exported as OpenTelemetry spans, including spans from any other instrumentations you have.
- **Application logs**: Logs written via `ILogger` are captured automatically and correlated with the requests they belong to.
- **Error tracking**: Validation errors and exceptions with stack traces for server errors.
- **Server metrics**: CPU and memory usage of your app's processes.
- **API monitoring & alerts**: Get notified if something isn't right using custom alerts, synthetic uptime checks and heartbeat monitoring. Alert notifications can be delivered via email, Slack and Microsoft Teams.

## Compatibility

The SDK supports [**ASP.NET Core**](https://github.com/dotnet/aspnetcore) on **.NET** `8`, `9` and `10`, including both Minimal APIs and MVC controllers.

Apitally also supports many other web frameworks in [JavaScript](https://github.com/apitally/apitally-js), [Python](https://github.com/apitally/apitally-py) and [Go](https://github.com/apitally/apitally-go) via our other SDKs.

## Getting started

If you don't have an Apitally account yet, first [sign up here](https://app.apitally.io/?signup). Then create an app in the Apitally dashboard. You'll see detailed setup instructions with code snippets you can copy and paste. These also include your write token.

Add the `Apitally` NuGet package to your project:

```bash
dotnet add package Apitally
```

Then call `AddApitally()` when registering services in your `Program.cs` file:

```csharp
using Apitally;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApitally(options =>
{
    options.WriteToken = "your-write-token";
});

var app = builder.Build();
```

Apitally adds its middleware to the start of the request pipeline automatically. If your app uses a `Startup` class, call `services.AddApitally()` in `ConfigureServices` instead.

For further instructions, see our [setup guide for ASP.NET Core](https://docs.apitally.io/sdk-reference/dotnet/v1/setup-guides/aspnet-core).

## Configuration

You can set options in the `AddApitally()` callback, in the `Apitally` section of your app's configuration, or with environment variables. Environment variables take precedence over the `Apitally` configuration section, and the `AddApitally()` callback takes precedence over both. See the [SDK reference](https://docs.apitally.io/sdk-reference/dotnet/v1/configuration) for all available options.

### appsettings.json

The keys in the `Apitally` section match the option names:

```json
{
  "Apitally": {
    "WriteToken": "your-write-token",
    "CaptureRequestHeaders": true
  }
}
```

### Environment variables

Apitally reads these environment variables:

- `APITALLY_WRITE_TOKEN`: the write token (`WriteToken`)
- `APITALLY_ENV`: the environment name (`Env`)
- `APITALLY_DISABLED`: set to `true` to disable Apitally (`Disabled`)

### Request and response data

Apitally captures response headers by default. To also capture request headers and request and response bodies, set these options:

```csharp
builder.Services.AddApitally(options =>
{
    options.WriteToken = "your-write-token";
    options.CaptureRequestHeaders = true;
    options.CaptureRequestBody = true;
    options.CaptureResponseBody = true;
});
```

Apitally automatically masks common sensitive values, such as `password` fields and the `Authorization` header. To mask additional values, add regular expressions to `MaskQueryParams`, `MaskHeaders`, and `MaskBodyFields`. See our [documentation on masking](https://docs.apitally.io/sdk-reference/dotnet/v1/masking) for details.

### Exclusions and sampling

Apitally automatically excludes common health checks and static assets, such as `/healthz` and `/robots.txt`, from logs and traces. To exclude additional paths, add regular expressions to `ExcludePaths`.

To capture logs and traces for only a fraction of the remaining requests, set `SampleRate`, for example to `0.1` for 10%. To decide for each request individually, set `SampleOnRequest` or `SampleOnResponse` to a callback.

Metrics always include all requests. See our [documentation on sampling](https://docs.apitally.io/sdk-reference/dotnet/v1/sampling) for details.

### Application logs

Apitally captures logs written with `ILogger` and correlates them with the requests they belong to. If you use Serilog, pass `writeToProviders: true` when registering it, so Serilog forwards its logs to Apitally:

```csharp
builder.Services.AddSerilog(
    (services, configuration) => configuration.WriteTo.Console(),
    writeToProviders: true
);
```

To mask or drop individual log records, set `MaskLogRecord` to a callback. To disable log capture, set `CaptureLogs` to `false`. See our [documentation on masking](https://docs.apitally.io/sdk-reference/dotnet/v1/masking#log-record-masking-callback) for details.

## Adding context to requests

The following methods of the `IApitally` service add context to the current request. Inject `IApitally` and call them while handling a request, for example in a Minimal API handler, controller action, middleware, or authentication code.

### Consumers

To associate the request with the API consumer making it, call `SetConsumer()`. Only the identifier is required.

```csharp
apitally.SetConsumer(
    user.Identifier,
    name: user.Name,
    group: user.Group,
    attributes: new Dictionary<string, string?> { ["plan"] = user.Plan }
);
```

### Custom attributes

To attach custom data to the request's trace, call `SetRequestAttribute()`. The `SampleOnResponse`, `MaskRequestBody`, and `MaskResponseBody` callbacks can also read these attributes.

```csharp
apitally.SetRequestAttribute("tenant.id", tenantId);
```

See our [documentation on attributes](https://docs.apitally.io/sdk-reference/dotnet/v1/attributes#set-custom-attributes) for details.

### Handled exceptions

To capture an exception that your code handles itself, call `CaptureException()`. Unhandled exceptions are captured automatically, including when `UseExceptionHandler()` turns them into error responses.

```csharp
try
{
    ProcessOrder();
}
catch (Exception exception)
{
    apitally.CaptureException(exception);
}
```

## Existing OpenTelemetry setup

If your app already registers a tracer provider with `AddOpenTelemetry().WithTracing()`, Apitally adds its span processor to it and keeps your existing exporters. Apitally then captures only the activities your tracer provider is configured for, so register your own sources with `AddSource()` and add instrumentations such as `AddHttpClientInstrumentation()` as needed. See our [documentation on tracing](https://docs.apitally.io/sdk-reference/dotnet/v1/tracing) for details.

Your tracer provider's sampler also applies to Apitally. Requests it drops have no request logs or traces in Apitally, but still count in metrics.

## Trusted proxies

If your app runs behind a reverse proxy or load balancer, configure trusted proxies with ASP.NET Core's [Forwarded Headers Middleware](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer). Apitally records the client IP reported by ASP.NET Core and uses it for GeoIP.

## Getting help

If you need help please [create a new discussion](https://github.com/orgs/apitally/discussions/categories/q-a) on GitHub or email us at [support@apitally.io](mailto:support@apitally.io). We'll get back to you as soon as possible.

## License

This library is licensed under the terms of the [MIT license](LICENSE).
