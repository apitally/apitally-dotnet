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

Apitally is a simple API monitoring and analytics tool that makes it easy to understand API usage, monitor performance, and troubleshoot issues.
Get started in minutes by just adding a few lines of code. No infrastructure changes required, no dashboards to build.

Learn more about Apitally on our 🌎 [website](https://apitally.io) or check out
the 📚 [documentation](https://docs.apitally.io).

## Key features

### API analytics

Track traffic, error and performance metrics for your API, each endpoint and
individual API consumers, allowing you to make informed, data-driven engineering
and product decisions.

### Request logs

Drill down from insights to individual API requests or use powerful search and filters to
find specific requests. View correlated application logs and traces for a complete picture
of each request, making troubleshooting faster and easier.

### Error tracking

Understand which validation rules in your endpoints cause client errors. Capture
error details and stack traces for 500 error responses.

### API monitoring & alerts

Get notified immediately if something isn't right using custom alerts, synthetic
uptime checks and heartbeat monitoring. Alert notifications can be delivered via
email, Slack and Microsoft Teams.

## Supported frameworks

This SDK supports [**ASP.NET Core**](https://github.com/dotnet/aspnetcore) on .NET 8, 9, and 10, with both Minimal APIs and MVC controllers.

Apitally also supports many other web frameworks in [JavaScript](https://github.com/apitally/apitally-js), [Python](https://github.com/apitally/apitally-py), [Go](https://github.com/apitally/apitally-go), and [Java](https://github.com/apitally/apitally-java) via our other SDKs.

## Getting started

If you don't have an Apitally account yet, first [sign up here](https://app.apitally.io/?signup). Create an app in the Apitally dashboard and select **ASP.NET Core** as your framework. You'll see detailed setup instructions with code snippets you can copy and paste. These also include your write token.

Install the NuGet package:

```shell
dotnet add package Apitally
```

Then add Apitally to your ASP.NET Core application in your `Program.cs` file. This is all
that's required: the SDK registers its middleware automatically.

```csharp
using Apitally;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApitally(options =>
{
    options.WriteToken = "apt_..."; // or set the APITALLY_WRITE_TOKEN environment variable
});

var app = builder.Build();
```

If your application uses a `Startup` class, call `services.AddApitally()` in `ConfigureServices`.

Options can also be set in the `Apitally` section of your application's configuration, such as
`appsettings.json`. Values set in code take precedence.

```json
{
  "Apitally": {
    "WriteToken": "apt_...",
    "CaptureRequestBody": true,
    "CaptureResponseBody": true
  }
}
```

If you use Serilog, pass `writeToProviders: true` when registering it. Otherwise Serilog doesn't
forward logs to other logging providers, and Apitally can't capture application logs:

```csharp
builder.Services.AddSerilog(
    (services, configuration) => configuration.WriteTo.Console(),
    writeToProviders: true
);
```

### Identifying consumers and adding custom spans

Inject the `IApitally` service to identify the consumer of a request, add attributes, capture
handled exceptions, or create custom spans:

```csharp
app.MapGet("/orders/{id}", (string id, IApitally apitally) =>
{
    apitally.SetConsumer("acme-corp", name: "Acme Corp", group: "enterprise");
    apitally.SetRequestAttribute("order.id", id);
    using var activity = apitally.StartActivity("load-order");
    return Results.Ok(new { id });
});
```

### Existing OpenTelemetry setups

The SDK is built on OpenTelemetry. If your application registers a tracer provider with
`AddOpenTelemetry().WithTracing(...)`, the SDK uses it automatically, and your sampler, exporters
and instrumentation remain unchanged. Otherwise, the SDK traces incoming requests, outgoing
`HttpClient` calls and activities from any `ActivitySource` during requests on its own.

For further instructions, see our
[setup guide for ASP.NET Core](https://docs.apitally.io/frameworks/aspnet-core).

See the [SDK reference](https://docs.apitally.io/sdk-reference/dotnet) for all available configuration options, including how to mask sensitive data, customize request logging, and more.

Upgrading from version 0.x? See the [migration guide](https://docs.apitally.io/sdk-reference/dotnet/v1/migration).

## Getting help

If you need help please
[create a new discussion](https://github.com/orgs/apitally/discussions/categories/q-a)
on GitHub or email us at [support@apitally.io](mailto:support@apitally.io). We'll get back to you as soon as possible.

## License

This library is licensed under the terms of the [MIT license](LICENSE).
