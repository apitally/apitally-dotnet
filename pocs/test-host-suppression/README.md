# Resolved-server suppression probe

An isolated candidate runtime, not a production SDK integration. `Fixture` contains
an executable application, startup filter, candidate runtime, and in-memory exporter.
Only `Driver` references TestHost and Mvc.Testing. The driver also checks the compiled
fixture assembly's references.

The startup filter resolves the actual `IServer` during pipeline construction, after
host registrations finish. The runtime suppresses itself only when both identities match:

```text
Type.FullName: Microsoft.AspNetCore.TestHost.TestServer
Assembly.GetName().Name: Microsoft.AspNetCore.TestHost
```

This decision precedes DI tracing lookup and private fallback construction. Both the
ApplicationStarted callback and middleware request callback retain the suppression
check. The guard uses no loaded-assembly scan, wrapper inspection, process name, or
Development heuristic. Registration-time server types are recorded only as evidence,
not used for the decision.

Allowed hosts without a DI tracing provider build a private OTel provider using public
APIs, AspNetCore instrumentation, AlwaysOnSampler, and SimpleActivityExportProcessor.
The provider is never registered into application DI. Application-owned tracing in the
suppressed cases is configured independently with AddOpenTelemetry().WithTracing().

## Run

From this repository:

```sh
cd pocs/test-host-suppression
python3 run.py
dotnet csharpier check Fixture Driver
```

The runner pins SDK 10.0.301 via local `global.json`, uses package lock files, records
commands/output under `results`, and runs these build and execution commands:

```sh
dotnet restore Driver/Driver.csproj --locked-mode --disable-parallel
dotnet build Driver/Driver.csproj -c Release --no-restore --disable-build-servers
dotnet Driver/bin/Release/net8.0/Driver.dll
dotnet Driver/bin/Release/net9.0/Driver.dll
dotnet Driver/bin/Release/net10.0/Driver.dll
```

Use the runner for the controlled environment: it removes OTEL_* and ASPNETCORE_*
settings and DOTNET_ENVIRONMENT from child processes without changing the parent
or changing environment variables between cases. Fixtures clear application
configuration and logging providers. Requests are synthetic; exports remain in memory.
Kestrel binds only 127.0.0.1:0, and its clients bypass proxies. Restore requires NuGet;
probe execution sends no external telemetry and needs no credentials.

Startup, requests, export completion, shutdown, and disposal have bounded waits.
The runner additionally kills and waits for a timed-out process group (120 seconds
per target). Exports are verified by awaiting an actual matching server span from
the exporter, including completed duration, route, sampled flag, and HTTP status.
No ForceFlush result or arbitrary settling delay is used as export evidence.

## Recorded results

2026-09-24, macOS Arm64, SDK 10.0.301, CSharpier 1.3.0. Build: zero warnings/errors.
OpenTelemetry, OpenTelemetry.Extensions.Hosting, and AspNetCore instrumentation are
pinned to 1.19.0. Framework and test packages match exactly:

| Target | .NET / ASP.NET runtime | Mvc.Testing / TestHost | Passed |
| --- | --- | --- | --- |
| net8.0 | 8.0.13 | 8.0.13 | 11 |
| net9.0 | 9.0.2 | 9.0.2 | 11 |
| net10.0 | 10.0.9 | 10.0.9 | 13 |

Every case sends two requests. These cases run with both ApplicationStarted activation
and the probe-only request activation mode:

- Default WebApplicationFactory of the minimal fixture, using its real TestServer.
  Candidate registration sees Kestrel; pipeline construction sees TestServer after the
  factory's normal replacement. No DI cycle occurred.
- The same factory with application-owned DI tracing: two completed user spans, zero
  candidate fallback constructions, activations, processors, or exports. The second
  request still exports after candidate disposal, with the same user provider instance.
- Direct WebApplicationBuilder with UseTestServer after candidate registration.
- Generic HostBuilder + ConfigureWebHost + UseStartup<FixtureStartup> + UseTestServer.
- Direct WebApplicationBuilder with real loopback Kestrel in Development while TestHost
  is loaded: one private fallback, one activation, and two completed private exports.
- On .NET 10 only, WebApplicationFactory.UseKestrel(0) provides the same active control.
  Its client uses the resolved server's bound loopback address.

One additional direct Kestrel case per target sets the ordinary candidate Enabled flag
false: it still serves requests but constructs no fallback and never activates/exports.
All TestServer cases remain inactive at startup and after both middleware signals.
Owned fallback and host-owned user exporter disposal are observed and asserted.

Initial probe failures and their corrections remain recorded in
`results/development-notes.txt`: an immediate disposal-count assertion raced host
cleanup, and the .NET 10 factory client options used localhost:80 instead of the bound
port. Neither required a change to the guard or resolved-server lookup.

## Limits

The request-only flag omits the ApplicationStarted callback. Requests still run after
ApplicationStarted has been signaled. This validates the request trigger, not a
concurrent early-request/startup race, and is not a public SDK testing override.

This proves the candidate runtime's server lookup and suppression ordering for the
hosting paths above. It does not prove integration with production logs, metrics,
spooling, workers, or all provider-registration modes. The active Kestrel controls use
private providers; application-owned tracing is tested in the suppressed TestServer
path. Other servers, decorated/derived TestServer types, legacy standalone WebHost,
concurrent hosts, and native builds using the .NET 8/9 SDKs are outside this probe.
The exact-name guard intentionally does not unwrap alternative server types.
