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
> **Upgrading from 0.x?** Version 1.0 is a full rewrite with a new setup API. See the [migration guide](MIGRATION.md) for a full 0.x to 1.x mapping.

## Key features

- **API analytics**: Traffic, error and performance metrics for your API, each endpoint, and per API consumer. Drill down from metrics to individual API requests.
- **Request logs and traces**: Every request as a searchable log entry, with optional capture of headers and request/response bodies. Requests are exported as OpenTelemetry spans, including spans from any other instrumentations you have.
- **Application logs**: Logs written via `ILogger` are captured automatically and correlated with the requests they belong to.
- **Error tracking**: Validation errors and exceptions with stack traces for server errors.
- **Server metrics**: CPU and memory usage of your app's processes.
- **API monitoring & alerts**: Get notified if something isn't right using custom alerts, synthetic uptime checks and heartbeat monitoring. Alert notifications can be delivered via email, Slack and Microsoft Teams.

## Supported frameworks

The SDK supports **.NET** `8`, `9` and `10`.

| Framework | Supported versions | Setup guide |
| --- | --- | --- |
| [**ASP.NET Core**](https://github.com/dotnet/aspnetcore) \* | `8.x`, `9.x`, `10.x` | [Link](https://docs.apitally.io/sdk-reference/dotnet/v1/setup-guides/aspnet-core) |

\* Including both Minimal APIs and MVC controllers.

Apitally also supports many other web frameworks in [JavaScript](https://github.com/apitally/apitally-js), [Python](https://github.com/apitally/apitally-py), [Go](https://github.com/apitally/apitally-go) and [Java](https://github.com/apitally/apitally-java) via our other SDKs.

## Getting started

If you don't have an Apitally account yet, first [sign up here](https://app.apitally.io/?signup). Then create an app in the Apitally dashboard. You'll see detailed setup instructions with code snippets you can copy and paste. These also include your write token.

To install the SDK as a dependency in your project run:

```bash
dotnet add package Apitally
```

See the [SDK reference](https://docs.apitally.io/sdk-reference/dotnet/v1/configuration) for all available configuration options, including how to mask sensitive data, capture request and response payloads, and more.

### ASP.NET Core

Call `AddApitally()` when registering services in your `Program.cs` file:

```csharp
using Apitally;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApitally(options =>
{
    options.WriteToken = "your-write-token"; // or set APITALLY_WRITE_TOKEN
});

var app = builder.Build();
```

This is all that's required. The SDK adds its middleware at the start of the request pipeline automatically. If your application uses a `Startup` class, call `services.AddApitally()` in `ConfigureServices`.

For further instructions, see our [setup guide for ASP.NET Core](https://docs.apitally.io/sdk-reference/dotnet/v1/setup-guides/aspnet-core).

## Configuration

The write token can also be provided via the `APITALLY_WRITE_TOKEN` environment variable instead of the `WriteToken` option.

The environment is derived from the ASP.NET Core environment name (e.g. `ASPNETCORE_ENVIRONMENT`), lowercased, with `Production` shortened to `prod` and `Development` to `dev`. You can override it with the `Env` option or the `APITALLY_ENV` environment variable.

Options can also be set in the `Apitally` section of your application's configuration, such as `appsettings.json`. Environment variables take precedence over the configuration section, and values set in code take precedence over both.

```json
{
  "Apitally": {
    "WriteToken": "your-write-token"
  }
}
```

By default, Apitally captures response headers but not request headers or request and response bodies. You can opt in with options:

```csharp
builder.Services.AddApitally(options =>
{
    options.WriteToken = "your-write-token";
    options.CaptureRequestHeaders = true;
    options.CaptureRequestBody = true;
    options.CaptureResponseBody = true;
});
```

Sensitive values in query parameters, headers, and body fields are masked automatically based on built-in patterns, and you can add your own via the `MaskQueryParams`, `MaskHeaders`, and `MaskBodyFields` options.

On high-traffic applications you can capture logs and traces for only a fraction of requests by setting `SampleRate` (e.g. `0.1` for 10%), or decide per request with the `SampleOnRequest` and `SampleOnResponse` callbacks. Metrics always count every request, regardless of sampling.

Application logs written via `ILogger` are captured and correlated with requests by default. Use `MaskLogRecord` to transform or drop Apitally's captured copy, or opt out with `CaptureLogs = false`.

If you use Serilog, pass `writeToProviders: true` when registering it. Otherwise Serilog doesn't forward logs to other logging providers, and Apitally can't capture them:

```csharp
builder.Services.AddSerilog(
    (services, configuration) => configuration.WriteTo.Console(),
    writeToProviders: true
);
```

See the [SDK reference](https://docs.apitally.io/sdk-reference/dotnet/v1/configuration) for all configuration options.

## Identifying consumers and more

The `IApitally` service provides methods you can call from anywhere in your request handling code. Inject it into your controllers, Minimal API handlers, or middleware:

```csharp
app.MapGet("/orders/{id}", (string id, IApitally apitally) =>
{
    // Associate the current request with an API consumer, with custom attributes
    apitally.SetConsumer(
        user.Identifier,
        name: user.Name,
        group: user.Group,
        attributes: new Dictionary<string, string?> { ["plan"] = user.Plan }
    );

    // Attach a custom attribute to the current request
    apitally.SetRequestAttribute("tenant", tenantId);

    // Capture a handled exception for the current request
    apitally.CaptureException(exception);

    // Create a custom span within the current request
    using var activity = apitally.StartActivity("load-order");

    // ...
});
```

For further details, check out our [documentation](https://docs.apitally.io).

## Existing OpenTelemetry setup

If your app doesn't already use OpenTelemetry, you don't need to know it's there. The Apitally SDK configures OpenTelemetry automatically. It traces incoming requests, outgoing `HttpClient` calls, and activities from any `ActivitySource` during requests.

If your app already registers a tracer provider via `AddOpenTelemetry().WithTracing(...)`, Apitally automatically adds its span processor to your provider, keeping your existing exporters. Apitally then captures only the activities your provider is configured for, so register your own sources with `AddSource()` and add instrumentations such as `AddHttpClientInstrumentation()` as needed.

Your tracer provider's sampling settings also affect Apitally. Requests excluded by the sampler will not have request logs or traces in Apitally. Metrics still include all requests, regardless of sampling.

## Trusted proxies

If your application runs behind a reverse proxy or load balancer, configure ASP.NET Core's [Forwarded Headers Middleware](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer) with your trusted proxies so Apitally can record the real client IP for GeoIP. Apitally uses the client IP reported by ASP.NET Core. It does not read forwarding headers itself to determine the client IP.

## Getting help

If you need help please [create a new discussion](https://github.com/orgs/apitally/discussions/categories/q-a) on GitHub or email us at [support@apitally.io](mailto:support@apitally.io). We'll get back to you as soon as possible.

## License

This library is licensed under the terms of the [MIT license](LICENSE).
