# Apitally .NET v1 code review, round 3

Date: 2026-09-30. Branch: `v1`. Reviewed commit: `7707690`.

## Summary

Round 3 reviewed the code written to resolve the first two rounds' findings, treating each fix as new code. The fixes span about 770 changed lines in `src/` (`git diff 7a099e8..HEAD`).

Most fixes hold up. The review found:

- Two High findings: R2 is a request leak that the C1 fix left open, and B3 is a regression introduced by the C4 fix.
- Two Medium findings: M3 is a process-wide stall caused by the M2 lock, and R3 is a trace-propagation change caused by the new fallback sampler.
- Several Low findings and documentation errors.

All production findings except L1 were reproduced on .NET 10.0.9. No repository files were changed during the review.

Findings from [code-review.md](code-review.md) and [code-review-2.md](code-review-2.md) were treated as settled. They are raised again only where their fix introduced a new defect or left the original problem partly open.

## Method and scope

Four general-purpose reviewers each covered the fixes in one area:

1. Metrics, export, spool and HTTP delivery: M2, P7, E1, R3, R6, S4, S7, S9.
2. Request and response body capture, redaction input and validation capture: B1, B2, P1, P3, D1, V1.
3. Request lifecycle, tracing, sampling, snapshots, consumers and errors: R1, C1, C4, S1, S2, S5, S7, P2, P4, P5, and design question 3.
4. Hosting lifecycle, configuration, logging, public options and packaging: H1, R4, R6, S3, S6, D3, I1, I3, I6, I7, and design questions 1 and 2.

Each reviewer read the original finding, its decision and the resulting diff. The reviewers ran reproductions in copies of the repository. For the tests added with each fix, they checked whether the test fails when the fix is reverted.

### Verification baseline

- `dotnet build Apitally.sln -warnaserror`: zero warnings and errors.
- .NET 10.0.9: 164/164 tests pass.
- .NET 8 and .NET 9: not run. Only the .NET 10 runtime is installed on the review machine, so version-specific concerns for .NET 8 and 9 are reasoned only.
- Release `dotnet pack`: succeeds. The package contains the DLL, XML documentation, README and icon, and the symbols package contains the PDB.

### Severity

- **High:** unbounded resource growth, a data leak, or silent loss of core telemetry in a realistic setup.
- **Medium:** missing or incorrect telemetry in a supported setup, or a failure that is hard to diagnose.
- **Low:** a narrow correctness issue, a small cost, or a documentation error.

## Priority summary

| ID | Severity | Finding | Related fix |
| --- | --- | --- | --- |
| R2 | High | Requests leak when an application processor clears `Recorded` in `OnEnd` | Round 1 C1 |
| B3 | High | Hellang ProblemDetails middleware makes 500s lose route, metrics and server-error events | Round 1 C4 |
| M3 | Medium | A metrics collection stalls request processing across the process | Round 2 M2 |
| R3 | Medium | The fallback sampler turns a sampled upstream `traceparent` into an unsampled one downstream | Round 1 design question 3 |
| H2 | Low | An empty `Env` value replaces the host-derived default | Round 1 design questions 1 and 2 |
| L1 | Low | YARP forwarder logs export unredacted query strings | Round 1 D3 |
| B4 | Low | Chunked brotli and deflate request bodies are neither captured nor sized | Round 1 D1 |
| B5 | Low | Provisional staging ignores an oversized declared `Content-Length` | Round 2 B1 |
| Doc1 | Low | `WriteToken` XML doc contradicts the new precedence | Round 1 design question 1 |
| Doc2 | Low | `IApitally.StartActivity` `<returns>` doc is still wrong | Round 1 I8 |
| Doc3 | Low | design.md describes the pre-question-3 sampler | Round 1 design question 3 |
| T4 | Low | Four tests fail when `ASPNETCORE_ENVIRONMENT=Development` is set | Round 1 design question 2 |

Recommended order: fix R2 and B3 first. Then decide on M3 and R3; each needs a decision on a trade-off the earlier round accepted. The Low findings are small and independent.

## Production findings

### R2. Requests leak when an application processor clears `Recorded` in `OnEnd`

- **Severity:** High.
- **Verification:** Reproduced on .NET 10.0.9. Ten filtered requests left 10 registry associations, and `ApplicationHost.StopAsync` failed its `RequestRegistry.IsEmpty` assertion.
- **Location:** [ApitallySpanProcessor.cs:55-66](../src/Apitally/Tracing/ApitallySpanProcessor.cs#L55), [RequestState.cs:270-273](../src/Apitally/Requests/RequestState.cs#L270).
- **Related fix:** Round 1 C1, which releases requests whose SERVER activity was dropped by the instrumentation `Filter` at activity start. This case, where `Recorded` is cleared at activity end, was left open.

The OTel .NET documentation presents a filtering processor that clears `Activity.Recorded` in `OnEnd`. It is the only way to filter on the response, for example to drop fast, successful SERVER spans. An application adds it with `AddOpenTelemetry().WithTracing(t => t.AddProcessor(...))` before calling `AddApitally()`, so its processor runs ahead of Apitally's.

On Kestrel, transport completion (`OnCompleted`) arrives before the SERVER activity ends. At that point `Recorded` is still true, so the request state waits for the SERVER end. The application's processor then clears `Recorded`, and Apitally's `OnEnd` returns early at the `!activity.Recorded` check. As a result, `CompleteServer` never runs.

The request state stays in the registry until shutdown. It retains the activity, child snapshots, logs and transport completion, including captured headers and up to two 50 KB bodies. With a response-based filter, nearly every request leaks. Registering Apitally's processor first avoids the leak.

**Recommendation:** Look up the request state before checking `Recorded`. Complete the SERVER span without a snapshot when it is not recorded:

```csharp
var snapshot = activity.Recorded ? SpanSnapshots.Copy(activity, GetExportResource(provider)) : null;
if (activity.SpanId == state.ServerSpanId)
    registry.CompleteServer(state, snapshot);
else if (snapshot is not null)
    state.AddDescendant(snapshot);
```

Make `CompleteServer` accept `SpanSnapshot?`. `Release` already returns early when `detail.Server` is null. Add the reproduction as a regression test next to `RequestsFilteredByApplicationInstrumentationAreReleased`.

The reviewer applied this change in a copy: the reproduction passed and the existing suite stayed green.

**Decision:** Fixed as recommended. `ApitallySpanProcessor.OnEnd` looks up the request before checking `Recorded` and completes an unrecorded SERVER span without a snapshot. `RequestsFilteredByApplicationProcessorAreReleased` fails without the fix.

### B3. Hellang ProblemDetails middleware makes 500s lose route, metrics and server-error events

- **Severity:** High. It affects only users of this third-party middleware, but for them it silently removes all error telemetry for unhandled exceptions.
- **Verification:** Reproduced on .NET 10.0.9 through Kestrel with `Hellang.Middleware.ProblemDetails` 6.5.1.
- **Location:** [EndpointMetadata.cs:52-60](../src/Apitally/AspNetCore/EndpointMetadata.cs#L52).
- **Related fix:** Round 1 C4, the exception-handler fallback.

`ResolveRoute` uses `IExceptionHandlerFeature.Endpoint` whenever the feature exists, even when its value is null. Hellang's `UseProblemDetails()` is common in .NET 6 to 8 APIs. It catches the exception and sets its own `ExceptionHandlerFeature { Path, Error }` without an `Endpoint`. It does not re-execute the pipeline or clear the matched endpoint.

The route therefore resolves to null, so a throwing `/orders/{id:int}` endpoint is treated as unmatched. That request gets:

- no request metrics
- no `apitally.request.server_error` event
- no `http.route` on the SERVER span

The reproduction measured the following:

| Code | `http.route` | Server-error events | Duration metrics |
| --- | --- | ---: | ---: |
| Current | empty | 0 | 0 |
| Pre-C4 fallback expression | `/orders/{id:int}` | 1 | 1 |

ASP.NET Core's built-in exception handler and developer exception page both set `Endpoint`, according to the ASP.NET Core 8.0, 9.0 and 10.0 source. The problem is therefore limited to third-party and hand-written exception handlers. Hellang uses Microsoft's `ExceptionHandlerFeature` class, so the feature type cannot distinguish it.

**Options:**

1. Return to `context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint()`, and remove `ExceptionsBeforeRoutingAreNotAttributedToTheErrorPage`. This brings back C4's Low case: an exception thrown before routing, handled by path-based `UseExceptionHandler("/error")`, is attributed to the error page. Recommended, because it trades a High regression for the original Low issue and is the simplest code.
2. Use `handled.Endpoint ?? (handled.RouteValues is null ? context.GetEndpoint() : null)`. This passes both the C4 test and the Hellang reproduction on Kestrel. However, it depends on the server providing `IRouteValuesFeature` before routing, which was not checked for IIS or HttpSys.

**Decision:** Fixed with option 2. The feature's route values tell a re-executing handler apart from one like Hellang's. Kestrel always provides route values, so an exception thrown before routing keeps no route. IIS and HttpSys do not provide the feature, so that case falls back to the error page's route there. `ExceptionsHandledWithoutReExecutionKeepTheRoute` uses a Hellang-style middleware, fails without the fix, and passes alongside `ExceptionsBeforeRoutingAreNotAttributedToTheErrorPage`.

### M3. A metrics collection stalls request processing across the process

- **Severity:** Medium. It is not a deadlock and loses no data, but it causes a recurring, process-wide latency spike that is hard to attribute.
- **Verification:** Reproduced on .NET 10.0.9 (macOS arm64, 10 cores), with a direct `ApitallyMetrics` probe and end to end on Kestrel.
- **Location:** [ApitallyMetrics.cs:101-107](../src/Apitally/Metrics/ApitallyMetrics.cs#L101) (lock in `RecordRequest`) and [ApitallyMetrics.cs:125-130](../src/Apitally/Metrics/ApitallyMetrics.cs#L125) (`SynchronizedMetricReader.OnCollect`). The callers are `RequestRegistry`, whose recording runs in `Response.OnCompleted` on a thread-pool thread, and the export worker's collection every 15 seconds.
- **Related fix:** Round 2 M2. That decision accepted that recording waits for the complete collection. It did not consider that each waiting request blocks a thread-pool thread.

During a collection, every request that completes blocks its thread-pool thread on the lock. At moderate throughput, those blocked requests soon occupy every available thread-pool thread. After that, Kestrel I/O and unrelated endpoints stall as well. The .NET thread pool injects extra threads for blocked threads only after about 500 ms, which is longer than a collection, so the stall lasts for the entire collection.

The realistic trigger is an API that identifies consumers and has a few thousand active consumer, route and status combinations per interval. CPU-limited containers make this worse. On Windows, `Process.WorkingSet64` in the process gauges, which now runs under the lock, enumerates processes (reasoned only).

| Setup | Collection time | Worst wait of unrelated work | Without the lock |
| --- | --- | --- | --- |
| Direct probe, 1,000 combinations | 20-28 ms | 29.5 ms | 0.1 ms |
| Direct probe, 10,000 combinations | 130-158 ms | 157.7 ms | 0.3 ms |
| Kestrel, about 45k requests/s, 5,000 consumers | 67-200 ms | `/hello` 65-198 ms | 4-6 ms |

In every case the stall matched the full collection time. At 10,000 combinations, the time under the lock broke down as:

- mapping: about 50-80 ms
- protobuf encoding: about 35 ms
- spool gzip append: about 28 ms

Moving only encoding and the spool append out of the lock, the staged handoff that M2 rejected, would cut the stall by less than half. The reviewer found no deadlock. The lock order is OTel's collect lock, then the metrics lock, then the spool lock, and recording takes only the metrics lock.

**Recommendation:** Keep M2's atomicity, but do not block request threads on a collection in progress. While a collection runs, `RecordRequest` enqueues the request's three observations. The collection records the queued entries under the lock before collecting:

```csharp
private volatile bool collecting;
private readonly ConcurrentQueue<(double Seconds, long? RequestSize, long? ResponseSize, TagList Tags)> deferred = new();

// RecordRequest
if (collecting) { deferred.Enqueue((duration.TotalSeconds, requestSize, responseSize, tags)); return; }
lock (sync) Record(...);

// SynchronizedMetricReader.OnCollect
collecting = true;
try { lock (sync) { RecordDeferred(); return base.OnCollect(timeoutMilliseconds); } }
finally { collecting = false; }

// RecordDeferred drains only the entries present on entry.
for (var n = deferred.Count; n > 0 && deferred.TryDequeue(out var item); n--) Record(item);
```

A request's three observations stay together. A request queued during a collection lands in the following interval. The queue grows only while a collection runs, so its size is bounded by the request rate times the collection time.

Measured with this change in a copy:

- The worst wait of unrelated `/hello` requests during collections was 6.7-10.6 ms at about 55k requests/s.
- An atomicity probe with 6 threads and 41 collections found 0 split intervals. The same probe finds 36 without any lock and 0 with the current lock.

A `Monitor.TryEnter` variant, where requests that lose the lock to another request also enqueue, was rejected. Under saturated synthetic load its queue grew without bound.

**Decision:** Fixed by narrowing the lock instead of queueing. Only the snapshot must be atomic with recording; the export works on the snapshot, as in any OpenTelemetry exporter. The reader takes the lock for `OnCollect`, and `SpoolExporter.Export`, which the reader calls synchronously after the snapshot, releases it before mapping, encoding and spooling. Measured lock hold time on .NET 10: about 0.4 ms at 1,000 combinations and 6-10 ms at 10,000, against 2.5-5 ms and 50-80 ms for the export that now runs unlocked. No test was added, consistent with the M2 test scope decision. A scratch probe with 6 recording threads, 5,000 consumers and 42 collections recorded about 260,000 requests in each of three runs and exported identical duration, request-size and response-size totals in every collection.

### R3. The fallback sampler turns a sampled upstream `traceparent` into an unsampled one downstream

- **Severity:** Medium.
- **Verification:** Reproduced on .NET 10.0.9. The incoming request carried `traceparent ...-01`, and the endpoint made an `HttpClient` call. With `SampleRate = 0`, the downstream request received `...-00`. With `SampleRate = 1`, it received `...-01`.
- **Location:** [TracingIntegration.cs:93-109](../src/Apitally/Tracing/TracingIntegration.cs#L93).
- **Related fix:** Round 1 design question 3. Without a `SampleOnRequest` callback, the Apitally-owned sampler now drops hosting activities that `SampleRate` would discard.

This applies to an application that relies on Apitally's owned tracing, has `SampleRate < 1`, and sits behind a traced gateway, service mesh or upstream OTel service. Its outgoing calls go to services that use parent-based samplers, which is the OTel default. For `1 - SampleRate` of requests, this service cuts off the upstream's recorded trace, and downstream spans are dropped. Before question 3, the hosting activity was always recorded and propagated `-01`. Apitally gains nothing from the drop in this case, because a trace ID sampled upstream says nothing about Apitally's own sampling.

There is a second consequence (reasoned only). A root request dropped by this sampler now propagates `-00`. A downstream Apitally service whose application owns its tracer provider with a parent-based sampler therefore loses Apitally request logs for those calls, even at `SampleRate = 1`. The recommended change below does not address this second effect; only reverting question 3 does.

**Options:**

1. Also record the hosting activity when its remote parent is sampled. Recommended: it is a one-line change, the savings for root requests remain, and an SDK should not cut off traces started by another system. Request-level sampling still drops Apitally detail for these requests, so Apitally output is unchanged. The reviewer verified that the reproduction passes and existing tests, including `FallbackDoesNotRecordSampledOutRequests`, still pass. Trade-off: an upstream that always sends `-01` removes the instrumentation savings for this service.

   ```csharp
   parameters.Name == ApitallySpanProcessor.HostingOperationName
       && (
           RequestSampling.ShouldKeep(parameters.TraceId, sampleRate)
           || (parent.IsRemote && parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded))
       )
   ```

2. Revert question 3, so the hosting activity is always recorded. This removes both consequences but gives up the instrumentation savings.
3. Accept both consequences and document them.

**Decision:** Fixed with option 1. The owned sampler also records the hosting activity when its parent is sampled. Requests that start at this service and that `SampleRate` drops still propagate unsampled, as design question 3 intended. `FallbackRecordsSampledOutRequestsWithSampledParent` fails without the fix and confirms that Apitally exports nothing for such a request.

### H2. An empty `Env` value replaces the host-derived default

- **Severity:** Low. Production telemetry is reported under `dev` with no diagnostic.
- **Verification:** Reproduced on .NET 10.0.9.
- **Location:** [RuntimeConfiguration.cs:18-30](../src/Apitally/Hosting/RuntimeConfiguration.cs#L18) and [RuntimeConfiguration.cs:84](../src/Apitally/Hosting/RuntimeConfiguration.cs#L84).
- **Related fix:** Round 1 design questions 1 and 2.

`BaseOptionsConfiguration.Configure` assigns the host-derived `Env` and then binds the configuration section. The binder copies an empty string over the host value, and `Resolve` maps a blank value to `DefaultEnv` (`dev`).

Examples of empty values:

- An `appsettings.json` placeholder such as `"Env": ""`, with the write token supplied through `APITALLY_WRITE_TOKEN`. The new precedence encourages this pattern.
- A deployment variable `Apitally__Env: ${ENV_NAME:-}` that resolves to an empty string.

In either case, a Production app reports as `dev`. JSON `null` and an empty `APITALLY_ENV` already behave correctly.

**Recommendation:** Compute the host-derived name once in `Configure`. After binding, replace a blank `Env` with it. The reviewer's version also applies it only while `Env` still equals `DefaultEnv`, which preserves an `Env` set by an earlier `Configure<ApitallyOptions>` registration. That registration pattern is unlikely and can be omitted.

**Decision:** Pending.

### L1. YARP forwarder logs export unredacted query strings

- **Severity:** Low.
- **Verification:** Reasoned only. The YARP source was checked.
- **Location:** [ApitallyLoggerProvider.cs:33-38](../src/Apitally/Logging/ApitallyLoggerProvider.cs#L33).
- **Related fix:** Round 1 D3, which excluded `System.Net.Http.HttpClient` categories for the same class of leak. YARP was outside that fix's scope.

`Yarp.ReverseProxy.Forwarder.HttpForwarder` logs `Proxying to {targetUrl}` at Information, where `targetUrl` includes the query string, on every .NET version. In a YARP gateway monitored by Apitally, that log is captured by default, so query secrets that are redacted on the SERVER span appear unredacted in the log body.

**Recommendation:** Exclude categories starting with `Yarp.ReverseProxy.Forwarder`, or accept and document this next to D3.

**Decision:** Pending.

### B4. Chunked brotli and deflate request bodies are neither captured nor sized

- **Severity:** Low.
- **Verification:** Reproduced on .NET 10.0.9.
- **Location:** [ObservedStream.cs:161](../src/Apitally/AspNetCore/ObservedStream.cs#L161), [RequestRegistry.cs:202-204](../src/Apitally/Requests/RequestRegistry.cs#L202).
- **Related fix:** Round 1 D1. This case was left open, not introduced; D1's test covers only gzip with `Content-Length`.

Behind `UseRequestDecompression`, a chunked request body counts as complete only when Apitally sees a zero-byte read at the end of the stream. `BrotliStream` and `ZLibStream` stop at the end of the compressed data and never make that read, so both the body and `http.request.body.size` are omitted. `GZipStream` probes for another member and reaches the end of the stream, so gzip works. Bodies with a `Content-Length` were captured and redacted for all three encodings.

**Recommendation:** Accept and document. A fix would require reading request bytes that the application did not consume.

**Decision:** Pending.

### B5. Provisional staging ignores an oversized declared `Content-Length`

- **Severity:** Low.
- **Verification:** Reasoned only. The code path was confirmed with a scratch route.
- **Location:** [BodyCapture.cs:55-67](../src/Apitally/AspNetCore/BodyCapture.cs#L55), [RequestRegistry.cs:118](../src/Apitally/Requests/RequestRegistry.cs#L118).
- **Related fix:** Round 2 B1.

Before B1, an eligible response with a declared `Content-Length` above 50,000 bytes became `[BODY_TOO_LARGE]` at the first write, without allocating a buffer. Pipe writes before the response starts are now staged provisionally, and `Stage` does not check the declared length. It allocates the full 50 KB buffer and copies up to the cap before discarding the copy.

`Results.Content` and `Results.Text` with a large string follow this path. The exported result is still correct.

**Recommendation:** In `Stage`, stage nothing when the declared length exceeds the maximum body size.

**Decision:** Pending.

## Documentation and test findings

### Doc1. `WriteToken` XML doc contradicts the new precedence

[ApitallyOptions.cs:11](../src/Apitally/ApitallyOptions.cs#L11) says the token "Falls back to `APITALLY_WRITE_TOKEN`". The environment variable now overrides the configuration section.

**Recommendation:** Say that `APITALLY_WRITE_TOKEN` overrides the configuration section, matching the wording on `Env`.

**Decision:** Pending.

### Doc2. `IApitally.StartActivity` `<returns>` doc is still wrong

[IApitally.cs:33-36](../src/Apitally/IApitally.cs#L33) says the method returns `null` "if no listener records it". With Apitally's owned provider, it returns a non-null activity that is not recorded in two cases: outside requests, and in requests dropped by the new sampler. It returns `null` only when nothing listens to `apitally.otel`, for example when Apitally is disabled and the app has no tracer provider. Round 1 I8 updated this text, but the wording is still inaccurate.

**Recommendation:** Say that the method returns `null` when no tracer provider listens, for example when Apitally is disabled, and that activities outside a monitored request are not recorded or exported.

**Decision:** Pending.

### Doc3. design.md describes the pre-question-3 sampler

[design.md:98](design.md#L98) still says the owned sampler records monitored requests "regardless of upstream sampling" and that "Apitally's own sampling remains a request/export decision". The shared `cloud/docs/sdks/design.md` also says request-level sampling stays out of the provider sampler. The confirmed fallback sampler paragraph at [design.md:102](design.md#L102) is current. Update these statements together with the R3 decision.

**Decision:** Fixed with R3. design.md now describes the sampler, including the sampled-parent condition, and the propagation behavior for dropped requests. The shared `cloud/docs/sdks/design.md` is in another repository and is left for a separate change.

### T4. Four tests fail when `ASPNETCORE_ENVIRONMENT=Development` is set

The following tests assert `prod`, but [ApplicationHost.cs:34](../tests/Apitally.Tests/Support/ApplicationHost.cs#L34) does not pin the host environment, so the result depends on the developer's shell:

- [TelemetryRuntimeTests.cs:63](../tests/Apitally.Tests/Hosting/TelemetryRuntimeTests.cs#L63)
- [RequestTelemetryTests.cs:37](../tests/Apitally.Tests/Integration/RequestTelemetryTests.cs#L37)
- [TracingIntegrationTests.cs:160](../tests/Apitally.Tests/Tracing/TracingIntegrationTests.cs#L160) (two theory rows)

Many .NET developers export `ASPNETCORE_ENVIRONMENT=Development`. With that setting, the suite has 4 failures.

**Recommendation:** Add `--environment=Production` to the `ApplicationHost` arguments. The reviewer verified 164/164 passing with `ASPNETCORE_ENVIRONMENT=Development` after the change.

**Decision:** Pending.

## Considered and not raised

- **No test for routes excluding the path base (C4).** The change only removed a string concatenation, so no test is needed.
- **`ExceptionStacktrace` with one exception object shared across requests (P2).** A cached faulted `Task` rethrown by many requests would report the first request's rethrow frames for every request. The code before the fix already produced inconsistent output for concurrent rethrows. Accepted as is.

## Fixes verified sound

Metrics and export:

- **M2 coverage:** every collection path reaches the `OnCollect` override: periodic collection, shutdown, and provider disposal after a bind failure. Process gauges are observed under the lock. An exception during collection releases the lock. No recording can trigger a collection.
- **P7:** the ingester joins histograms per `ResourceMetrics`, so several collections concatenated in one spool file during an outage ingest correctly.
- **E1:** no new null-reference path or race. `StoredSize` reads `closedSize` after `Close`, `Delete` uses `?.`, and `ReadStoredBytes` never touches `memoryStream`.
- **S4:** a server that stalls before headers or during the body yields `Retryable` after exactly 10 seconds. Caller cancellation returns within 0.5 seconds. Request and response content are disposed.
- **R6:** `PooledConnectionLifetime` of 5 minutes is correct, including with a proxy.
- **S7, S9:** `ServerExportIntervalIsClamped` fails with the clamp removed.

Body capture and validation:

- **B1:**
  - When a handler re-executes after unflushed pipe writes, the exported body matches the bytes the client received.
  - An unhandled exception after such writes exports no body.
  - Exceeding the cap before the response starts still yields the marker.
  - `SendFileAsync`, aborted and incomplete requests clear the provisional buffer.
  - The paired stream and pipe test fails without the fix.
- **B2:** `IsBypassed` is checked first in both `GetBody` and `GetRetainedBytes`.
- **P1:**
  - The cap is exact: 50,000 bytes is captured and 50,001 bytes yields the marker, for pipe, stream and chunked request bodies.
  - A declared request length above the cap never allocates beyond it.
  - Validation parsing uses the shared buffer synchronously before the span is queued, so no other code reuses or modifies the buffer during parsing.
- **P3:** `Utf8.IsValid` matches the previous strict decoder.
- **D1:**
  - `deflate` decoding matches ASP.NET Core's provider.
  - Multiple encodings are skipped by both ASP.NET Core and Apitally.
  - `DecompressedRequestBodyIsDecodedAndRedacted` fails without the fix.
- **V1:** the binding-source filter excludes `CancellationToken` and service parameters and still counts parameters with an unknown binding source. The validation test fails without the fix.

Request lifecycle and tracing:

- **R1:**
  - The bound is exactly the SERVER plus 1,000 descendants.
  - All retained descendants fit the snapshot buffer, and the SERVER span is stored separately, so nothing displaces it.
  - Grandchildren of rejected children are not associated.
  - Association and finalization are serialized.
  - Logs under rejected children are dropped consistently.
- **C1 (start-time `Filter`):** the test fails without the fix. The registry assertion runs after the server drains.
- **S2:**
  - `IsDetailKept` is equivalent to the SERVER being associated.
  - The shutdown cutoff covers every unfinalized kept state.
  - Children still running at request end or shutdown are dropped without being retained.
- **Design question 3, apart from R3:**
  - The sampler's decision matches the later request-level decision: 0 mismatches over 200 requests at rate 0.5.
  - Dropped requests still produce metrics, validation errors, server errors and consumer updates.
  - An app-owned provider bypasses the sampler.
- **P2:** `ConditionalWeakTable.GetValue` is thread-safe. The ingester does not read `exception.*` log attributes.
- **C4 (route excludes path base):** consistent with `GetPaths`. The exception-before-routing test fails without the fix.
- **P4, P5:**
  - The `\0`-joined hash is unambiguous, because `\0` is rejected in all fields.
  - Attributes are sorted by key.
  - Moving the least-recently-used node keeps the dictionary and list consistent.
- **S1, S5, S7:** object initializers preserve every value.

Hosting, configuration, logging and packaging:

- **H1:**
  - Every lifecycle transition checked is safe, including disposal racing activation, stop without start, and stop racing disposal.
  - The final collection and flush still run on shutdown.
  - The new test fails without the fix.
- **R4:** the test fails without the fix.
- **S6:** a disabled configuration resolves to `null`. An invalid test-only endpoint is still caught and logged.
- **Precedence:**
  - Code callbacks, then `APITALLY_*` variables, then the `Apitally` section, then the host environment name. This matches README, design.md and migration.md.
  - Environment names such as `QA-EU` become valid lowercase values, and no `Env` value can disable telemetry.
- **Logging:**
  - `MaskLogRecord` is the only log-masking path and runs before buffering.
  - The 2,048-character limit applies after the callback.
  - The `System.Net.Http.HttpClient` prefix covers named and typed clients.
  - Polly and `Microsoft.Extensions.Http` categories do not log request URIs.
- **I1, I3, I6, I7:** the package and symbols package contents are correct. The build has no missing-documentation, trimming or AOT warnings.

## Local reproduction artifacts

These paths are temporary verification artifacts, not committed tests or durable project documentation:

| Findings | Artifact |
| --- | --- |
| R2, R3 | `/private/tmp/apitally-r3-requests/tests/Apitally.Tests/Round3/Round3RequestTests.cs` |
| B3 | `/private/tmp/apitally-r3-body/tests/Apitally.Tests/AspNetCore/Round3RouteTests.cs` (the copy references `Hellang.Middleware.ProblemDetails` 6.5.1) |
| B4, B5, B1 checks | `/private/tmp/apitally-r3-body/tests/Apitally.Tests/AspNetCore/Round3Tests.cs` |
| M3 | `/private/tmp/apitally-r3-export/tests/Apitally.Tests/Scratch/MetricsLockStallTests.cs`, `KestrelStallTests.cs`, `DeferModeTests.cs`; logs `/private/tmp/r3-defer.log`, `/private/tmp/r3-defer3.log`, `/private/tmp/r3-kestrel-defer.log` |
| S4 checks | `/private/tmp/apitally-r3-export/tests/Apitally.Tests/Scratch/HttpTimeoutTests.cs` |
| H2 | `/private/tmp/apitally-r3-hosting/tests/Apitally.Tests/Round3/HostingRound3Tests.cs` |
| T4 | `ASPNETCORE_ENVIRONMENT=Development dotnet test tests/Apitally.Tests --framework net10.0` |
