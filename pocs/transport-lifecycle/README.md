# Automatic transport and host lifecycle POC

Isolated experiment, not production SDK code. Uses public ASP.NET Core hosting and HTTP feature APIs, actual loopback Kestrel, synthetic traffic, bounded observations, timestamps, and failing assertions. No external collector, credentials, SDK implementation, or user Activity mutation. Each run starts and disposes six hosts sequentially.

## Reproduce

From this directory, with SDK 10.0.301 and the three installed runtimes:

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet --version
dotnet build TransportLifecycle.csproj
dotnet run --project TransportLifecycle.csproj --no-build -f net8.0
dotnet run --project TransportLifecycle.csproj --no-build -f net9.0
dotnet run --project TransportLifecycle.csproj --no-build -f net10.0
```

Exact evidence-capture commands also run in this directory:

```sh
mkdir -p logs
for framework in net8.0 net9.0 net10.0; do
  dotnet run --project TransportLifecycle.csproj --no-build -f "$framework" > "logs/$framework.log" 2>&1 || exit
done
```

The agent ran these in the foreground with a 180-second command timeout. HTTP, gates, starts, stops, and socket waits have finite timeouts. Servers listen only on `127.0.0.1:0`; clients disable proxy use. The generated file-send fixture lives under this project's build output and is deleted during disposal. `logs/`, `bin/`, and `obj/` are ignored by the existing repository rules.

| Target | Actual runtime / ASP.NET shared framework | Result |
| --- | --- | --- |
| net8.0 | 8.0.13 / 8.0.13 | 271 assertions pass |
| net9.0 | 9.0.2 / 9.0.2 | 271 assertions pass |
| net10.0 | 10.0.9 / 10.0.9 | 271 assertions pass |

Dependencies: only the `Microsoft.NET.Sdk.Web` implicit `Microsoft.AspNetCore.App` framework reference and runtime libraries. **No NuGet or OpenTelemetry packages.** The SDK compiles all three targets; this is not a three-compiler matrix. A native `ActivityListener` observes the framework's `Microsoft.AspNetCore` ActivitySource for timing only. This does not prove OTel provider/processor integration or preserve sampling against other listeners; those are separate POCs.

## Mechanism and verified coverage

`AddTransportPoc` is one setup call on either `WebApplicationBuilder` or Generic Host's `IHostBuilder`. It registers an `IStartupFilter` which inserts middleware before the application's pipeline. No `UseApitally` call, private pipeline property, or reflection is used. The `IServer` decorator records public start/stop boundaries solely for the experiment; it is not a proposed SDK requirement.

Both hosting styles execute the same tests:

- Minimal endpoint routing, one MVC controller, and a genuinely unmatched path. At observer entry no endpoint is selected; the final parameterized route is available later.
- `UseExceptionHandler("/error")` re-execution produces the final JSON body and status 500. The visible endpoint becomes `/error`, while public `IExceptionHandlerPathFeature.Endpoint` retains `/fail/{id:int}` and `Path` retains `/fail/42`. An outer catch alone never sees this handled exception.
- Response `Body` synchronous/asynchronous writes, synchronous/async flushing, native `BodyWriter` memory/span writes, `Advance`, flushing, and an unflushed writer completed by Kestrel. `IHttpResponseBodyFeature.StartAsync`, `CompleteAsync`, `DisableBuffering`, and `SendFileAsync` are delegated.
- Direct file range sending delivers exactly `2345`, with observed count 4. It delegates the native send without rereading or buffering the file. **File payload capture is deliberately unavailable**, including mixed captured/file output.
- Body and writer streaming, plus gzip streaming: the client reads exactly `first\n` while the endpoint is blocked on a gate. Only the client then releases the endpoint to write `last\n`. Assertions require client receipt before endpoint return; matching final bytes alone is not the evidence.
- Outer placement sees compressed content bytes and the final gzip trailer. The 11-byte logical stream was 42 observed gzip bytes on .NET 8 and 43 on .NET 9/10. Decompressing the captured synthetic response reproduces all 11 bytes. Compression removes Content-Length. Decompression here is a tiny harness assertion, not an implemented capture-processing pipeline.
- Capture retains at most 50,000 bytes in a fixed-capacity array. Declared 60,000-byte responses allocate no capture buffer. A 65,536-byte response crossing the cap discards retained bytes while its complete counter continues. This bounds observation, not the application's own buffers.
- Request stream reads, chunked `BodyReader` reads with repeated partial `AdvanceTo`, EOF, a 60,001-byte chunked body, unread declared oversized bodies, partial reads, and a disconnected incomplete chunked upload. The observer adds no request reads. Declared lengths remain separate from consumed counts; incomplete unknown-length prefixes do not establish a total size.
- Explicit application aborts, a client-disconnected streaming response, and a response declaring 10 bytes but writing only 4. Partial capture is omitted; an already-established oversized flag survives abort. OnCompleted still executes in these failure cases.

The normal suite joins 24 request records for modern hosting and 25 for Startup hosting; the latter includes an early-startup request. A join is an in-memory counter after both native Activity and transport callbacks, **not telemetry export**. Capture checks cover only `text/plain` and `application/json`; they are intentionally not the complete shared allowlist/privacy implementation.

## Ordering and lifecycle observations

For ordinary body writes, the recorded order is:

```text
app OnStarting -> observer OnStarting
app finally -> observer finally
app OnCompleted -> observer OnCompleted -> ActivityStopped -> joined
```

An unflushed BodyWriter instead triggers OnStarting **after middleware finally**, when Kestrel finalizes the response. This is asserted. A middleware finally block alone is consequently not the final observation boundary. The outer OnCompleted callback sees inner callbacks first because callbacks execute in reverse registration order. These experiments observed transport-first ordering only; they do not prove every instrumentation emits Activity end in that order.

Building a host does not activate the probe. Modern hosting activates through ApplicationStarted. A Generic Host service registered after the server sends a real request before ApplicationStarted; the first-request fallback activates once and observes that request. This proves the fallback is necessary, not that an increment-only activation probe proves full pipeline initialization. Arbitrary user ApplicationStarted callback ordering and concurrent activation failure remain untested.

An active request held across StopAsync finishes normally when released. Kestrel stops after its OnCompleted and ActivityStopped callbacks; all completed records are present at the final phase, once. Ordinary `IHostedService.StopAsync` ordering differs: the probe stops **after** the server with WebApplication, but **before** it with this Generic Host registration order. Depending on that registration position is unsafe.

`IHostedLifecycleService.StoppedAsync` runs after all service StopAsync calls in both tested compositions. It is a viable candidate for the host-owned final phase, using the passed cancellation token. The tests use the default sequential host stop behavior. Host stopped precedes server disposal and then listener disposal.

With a 300 ms HostOptions shutdown timeout and an endpoint deliberately ignoring cancellation, RequestAborted fires near 300 ms, but StopAsync returns around **1.30 seconds**. Kestrel's abort-all-connections path has an additional one-second wait. At StoppedAsync the token is canceled and one request still lacks both completion callbacks. Only releasing the endpoint afterward ends its Activity. The POC does not end that Activity, fabricate an end time, discard/export the unfinished request, or select a new SDK shutdown deadline.

A separate simulated five-second final drain with no active requests is canceled at approximately 300 ms by the host's remaining budget. The simulation is only a cancellable delay plus counters. It does not include processors, spool writes, exporters, or provider shutdown.

**Final-drain coordination remains open.** The separate [activity snapshots POC](../activity-snapshots/README.md) found that OTel 1.19.0 ForceFlush may return true while its last dequeued Export is still running, and standalone generic Dispose does not drain/join; Shutdown does. This experiment does not duplicate those probes. A final phase must coordinate completed exporter/spool work and standalone worker shutdown within the remaining host budget before closing/sending files or disposing resources. Neither this callback order nor successful ForceFlush proves that coordination is complete.

## Failed hypotheses and boundaries

1. **RequestAborted at OnCompleted reliably detects application Abort:** false. The initial .NET 8/9 runs failed this assertion. Later runs also observed a false token on .NET 10. Cancellation callbacks are scheduled and race completion. The current observer additionally wraps public `IHttpRequestLifetimeFeature.Abort` and marks application-initiated abort synchronously. It forwards token get/set unchanged. This fixes the exercised path without delaying requests.
2. **OnCompleted means complete body:** false; it also runs for explicit aborts and length mismatches. The experimental completeness predicate combines callback, observed abort/escape, and length agreement. It is not a universal delivery guarantee or TCP acknowledgment. Server-originated late abort races, swallowed write errors, and application replacement of abort tokens/features need further work before complete-body privacy guarantees can be claimed.
3. **A hosted StopAsync always runs after Kestrel:** false across these registration orders. The later lifecycle phase avoids that specific assumption, but actual provider/exporter lifetime integration is still untested.
4. **Host shutdown timeout is an exact wall-clock deadline:** false for an uncooperative Kestrel request. The host supplies cancellation; components still control how promptly they finish.

Untested: arbitrary third-party startup filters and earlier short-circuits, Development exception pages, HTTP/2/3, TLS, HEAD/204/304/trailers, upgrades/websockets, slow-client backpressure stress, request decompression, other compression providers, mixed/replaced/cached body streams/readers/features, rereads and seeking, synchronous request reads, reader TryRead/cancellation and writer completion/cancellation variants, file mutation/send failures, late header changes, concurrent requests/hosts, and reverse Activity/transport order under OTel. Wrappers expose several of these methods by delegation; that is not coverage. No framework validation, Sentry, OpenAPI, snapshots, redaction, or export implementation is included.

## Requirements and sources

Read local design sections 4, 7, 8, 18 and shared SDK design sections 4, 6-8, 10. Context7 `/dotnet/aspnetcore.docs` supplied public startup-filter and Generic Host guidance. Relevant source fetched with `gh api`:

- [IHttpResponseBodyFeature public contract, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Http/Http.Features/src/IHttpResponseBodyFeature.cs)
- [Startup-filter composition and server stop, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Hosting/Hosting/src/GenericHost/GenericWebHostService.cs)
- [Modern host pipeline construction, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/DefaultBuilder/src/WebApplicationBuilder.cs)
- [Exception handler original endpoint feature, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs)
- [Compression feature and finalization, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Middleware/ResponseCompression/src/ResponseCompressionBody.cs)
- [Kestrel completion and context disposal, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs)
- [Scheduled request cancellation, v8.0.13](https://github.com/dotnet/aspnetcore/blob/v8.0.13/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs)
- [Additional abort wait, v10.0.9](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Servers/Kestrel/Core/src/Internal/Infrastructure/TransportConnectionManager.cs)
- [Host lifecycle phases and shared budget, v10.0.9](https://github.com/dotnet/runtime/blob/v10.0.9/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs)
