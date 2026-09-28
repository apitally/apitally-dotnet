# HttpClient source subscription POC

Executed 2026-09-28. Standalone experiment, not production SDK code. It does not participate in the solution.

## Purpose

The private fallback `TracerProvider` is planned to call `AddAspNetCoreInstrumentation()`, `AddHttpClientInstrumentation()` and `AddSource("*")`, with a request-scoped sampler. This POC answers whether the wildcard produces exactly one CLIENT span per outgoing `HttpClient` call on .NET 8, 9 and 10. It also checks the sampler's assumptions. A baseline variant replaces the wildcard with `AddSource("PocApp", "apitally.otel")` so any difference can be attributed to the wildcard.

Sampler under test (`RequestSampler` in `Program.cs`): `RecordAndSample` when `SamplingParameters.Name == "Microsoft.AspNetCore.Hosting.HttpRequestIn"`, or when the parent context is local and has the `Recorded` flag; otherwise `Drop`.

## Environment actually used

- macOS ARM64. SDK 10.0.301 plus exact runtimes installed with the official `dotnet-install.sh` into a private `DOTNET_ROOT` at `/tmp/dotnet-poc`: Microsoft.NETCore.App and Microsoft.AspNetCore.App 8.0.13, 9.0.2 and 10.0.9. The Homebrew installation was not used for runs.
- Each process prints `RuntimeInformation.FrameworkDescription`, the loaded ASP.NET Core assembly path and the DiagnosticSource assembly. Observed: .NET 8.0.13, 9.0.2 and 10.0.9 with matching ASP.NET Core paths. The net8/net9 targets load System.Diagnostics.DiagnosticSource 10.0.0 from NuGet (a dependency of OpenTelemetry 1.19.0); net10 loads the shared-framework copy.
- OpenTelemetry, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Instrumentation.AspNetCore, OpenTelemetry.Instrumentation.Http and OpenTelemetry.Exporter.InMemory pinned to 1.19.0, locked in `packages.lock.json`. CSharpier 1.3.0 from the repository tool manifest.

## Run

```sh
# One-time private runtime install (does not touch the system installation)
curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 10.0.301 --install-dir /tmp/dotnet-poc --no-path
for version in 8.0.13 9.0.2; do
  bash /tmp/dotnet-install.sh --runtime dotnet --version "$version" --install-dir /tmp/dotnet-poc --no-path
  bash /tmp/dotnet-install.sh --runtime aspnetcore --version "$version" --install-dir /tmp/dotnet-poc --no-path
done

cd pocs/http-client-source-subscription
python3 run.py            # both variants; or: python3 run.py wildcard
```

`POC_DOTNET_ROOT` overrides the private root. The runner removes `OTEL_*`, `APITALLY_*`, `ASPNETCORE_*` and `DOTNET_*` from the inherited environment, then sets `DOTNET_ROOT`, `PATH` and CLI telemetry opt-out. It checks formatting, restores in locked mode and builds once. For each target it writes a runtimeconfig pinning both shared frameworks with `rollForward: Disable`, runs each variant in a separate process, and asserts the loaded runtime and ASP.NET Core versions. Output goes to ignored `results/`. A timeout kills the process group.

## Scenario

One Kestrel host on `localhost` with an ephemeral port and an in-memory exporter. `/api` starts a `PocApp` child and an `apitally.otel` child, then calls `http://localhost:{port}/sink` through a shared `HttpClient` and reads the body. `/sink` records the received `traceparent`. The driver sends raw HTTP/1.1 over TCP, so the driver creates no HttpClient activity or propagation. Calls in order:

1. `no-traceparent` (first outgoing call; opens the pooled connection and resolves `localhost`)
2. `remote-unsampled` (`traceparent` flag `00`)
3. `remote-sampled` (`traceparent` flag `01`)
4. Outside requests, in `Task.Run` with `Activity.Current = null`: a `PocApp` root activity, then an HttpClient call to `/sink`

The program waits until both SERVER spans of each trace are exported before the next call. It then stops the host, disposes the client and provider, and asserts on the final exported set, so late-ending activities are included.

## Results

32 assertions per run; all passed in all six runs.

| Runtime | Variant | HTTP CLIENT spans per call (1/2/3) | Client-kind spans per call | Extra spans inside requests |
| --- | --- | --- | --- | --- |
| 8.0.13 | wildcard | 1 / 1 / 1 | 1 / 1 / 1 | none |
| 8.0.13 | baseline | 1 / 1 / 1 | 1 / 1 / 1 | none |
| 9.0.2 | wildcard | 1 / 1 / 1 | 1 / 1 / 1 | call 1: `Experimental.System.Net.Http.Connections.WaitForConnection` x1 |
| 9.0.2 | baseline | 1 / 1 / 1 | 1 / 1 / 1 | none |
| 10.0.9 | wildcard | 1 / 1 / 1 | 1 / 1 / 1 | call 1: `Experimental.System.Net.Http.Connections.WaitForConnection` x1 |
| 10.0.9 | baseline | 1 / 1 / 1 | 1 / 1 / 1 | none |

The single CLIENT span always has source `System.Net.Http`, name `System.Net.Http.HttpRequestOut`, is parented to the `/api` SERVER span and is the parent of the `/sink` SERVER span. The sampler received exactly one `HttpRequestOut` call per outgoing request on every runtime, including .NET 8.

| Assumption | 8.0.13 | 9.0.2 | 10.0.9 |
| --- | --- | --- | --- |
| A1: all 7 SERVER sampler calls use `Name == Microsoft.AspNetCore.Hosting.HttpRequestIn` | pass | pass | pass |
| A2: unsampled remote parent still yields a recorded SERVER parented to the remote span (also the outside-request `/sink` call) | pass | pass | pass |
| A3: `PocApp` and `apitally.otel` children recorded and parented to SERVER | pass | pass | pass |
| A4: outside requests nothing recorded except the target's own incoming SERVER span | pass | pass | pass |
| A5: exactly one SERVER span per incoming request | pass | pass | pass |

Informational sampler calls in the wildcard variant on .NET 9 and 10 (same on both):

| Name | Parent at sampling | Decision |
| --- | --- | --- |
| `Experimental.System.Net.Http.Connections.WaitForConnection` | local, recorded (the CLIENT span) | RecordAndSample, only on the call that opens a connection |
| `Experimental.System.Net.Http.Connections.ConnectionSetup` | root | Drop |
| `Experimental.System.Net.NameResolution.DnsLookup` | local, not recorded (ConnectionSetup) | Drop |
| `Experimental.System.Net.Sockets.Connect` | local, not recorded (ConnectionSetup) | Drop |
| `Experimental.System.Net.Sockets.Connect` x3 | root (raw TCP driver connections) | Drop |

On .NET 8 no additional sources were sampled. Reused connections add no spans on any runtime.

## Observations

- Outside requests, OTel `Drop` still creates activities with propagation data only. The `PocApp` root activity was non-null with `Recorded=False` and `IsAllDataRequested=False`, not null. The outside HttpClient call injected `traceparent` with flag `00`, so the downstream `/sink` saw an unsampled remote parent, and the sampler still recorded that SERVER span.
- .NET 9+ creates `ConnectionSetup` as a new root trace, so DNS and socket spans for new connections are dropped by this sampler. Only `WaitForConnection` joins the request trace.

## Conclusion

`AddSource("*")` alongside stock `AddHttpClientInstrumentation()` produced exactly one CLIENT span per outgoing call on .NET 8.0.13, 9.0.2 and 10.0.9, identical to the baseline. No duplication was reproduced, so no source exclusion is needed. The wildcard's only visible effect was one `WaitForConnection` INTERNAL span on .NET 9+ for calls that open a new connection. All five sampler assumptions held on all three runtimes.

Limits: HTTP/1.1 on loopback with the default `SocketsHttpHandler`, a shared client, and no `IHttpClientFactory`, HTTP/2, proxy, redirect or failure cases. The net8/net9 runs use NuGet DiagnosticSource 10.0.0, as any app referencing OpenTelemetry 1.19.0 would.
