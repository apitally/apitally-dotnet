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
<img alt="Apitally screenshots" src="https://assets.apitally.io/screenshots/overview.png">
<br>

# Apitally SDK for .NET

[![Tests](https://github.com/apitally/apitally-dotnet/actions/workflows/tests.yaml/badge.svg?event=push)](https://github.com/apitally/apitally-dotnet/actions)
[![Codecov](https://codecov.io/gh/apitally/apitally-dotnet/graph/badge.svg?token=NJzC7yKV6V)](https://codecov.io/gh/apitally/apitally-dotnet)

Apitally is a simple API monitoring and analytics tool that makes it easy to understand how your APIs are used
and helps you troubleshoot API issues faster. Setup is easy and takes less than 5 minutes.

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
error details and stack traces for 500 error responses, and have them linked to
Sentry issues automatically.

### API monitoring & alerts

Get notified immediately if something isn't right using custom alerts, synthetic
uptime checks and heartbeat monitoring. Alert notifications can be delivered via
email, Slack and Microsoft Teams.

## Supported frameworks

This SDK supports [**ASP.NET Core**](https://github.com/dotnet/aspnetcore) on .NET 6, 7, 8, and 9.

Apitally also supports many other web frameworks in [JavaScript](https://github.com/apitally/apitally-js), [Python](https://github.com/apitally/apitally-py), [Go](https://github.com/apitally/apitally-go), and [Java](https://github.com/apitally/apitally-java) via our other SDKs.

## Getting started

If you don't have an Apitally account yet, first [sign up here](https://app.apitally.io/?signup). Create an app in the Apitally dashboard and select **ASP.NET Core** as your framework. You'll see detailed setup instructions with code snippets you can copy and paste. These also include your client ID.

Install the NuGet package:

```shell
dotnet add package Apitally
```

Then add Apitally to your ASP.NET Core application by registering the required
services and middleware in your `Program.cs` file:

```csharp
using Apitally;

var builder = WebApplication.CreateBuilder(args);

// Add Apitally services
builder.Services.AddApitally(options =>
{
    options.ClientId = "your-client-id";
    options.Env = "dev"; // or "prod" etc.

    // Optional: Configure request logging
    options.RequestLogging.Enabled = true;
    options.RequestLogging.IncludeRequestHeaders = true;
    options.RequestLogging.IncludeRequestBody = true;
    options.RequestLogging.IncludeResponseBody = true;
    options.RequestLogging.CaptureLogs = true;
    options.RequestLogging.CaptureTraces = true;
});

var app = builder.Build();

// Add Apitally middleware
app.UseApitally();

// ... rest of your middleware configuration
```

For further instructions, see our
[setup guide for ASP.NET Core](https://docs.apitally.io/frameworks/aspnet-core).

See the [SDK reference](https://docs.apitally.io/sdk-reference/dotnet) for all available configuration options, including how to mask sensitive data, customize request logging, and more.

## Getting help

If you need help please
[create a new discussion](https://github.com/orgs/apitally/discussions/categories/q-a)
on GitHub or email us at [support@apitally.io](mailto:support@apitally.io). We'll get back to you as soon as possible.

## License

This library is licensed under the terms of the [MIT license](LICENSE).
