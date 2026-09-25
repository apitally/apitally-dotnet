# Integrated request-association experiment

Standalone research for design review R2. This is experimental code, not production SDK code or an approved production architecture. It does not participate in the solution.

## Run

```sh
cd pocs/request-association
python3 run.py
```

Required: SDK 10.0.301, both Microsoft.NETCore.App and Microsoft.AspNetCore.App at 8.0.13, 9.0.2 and 10.0.9, and NuGet access for restore. The project pins OpenTelemetry, Extensions.Hosting, Instrumentation.AspNetCore and Instrumentation.Http to 1.19.0 with dependency hashes in `packages.lock.json`. CSharpier is 1.3.0.

The recorded corrected matrix passes 1533 assertions per runtime; see RESULTS.md for negative evidence and development misses. The runner checks SDK/tool versions, formatting, locked restore and all three builds. It writes a separate runtimeconfig per target with BOTH shared frameworks pinned and `rollForward: Disable`, then checks the loaded framework versions in output. It removes `OTEL_*`, `APITALLY_*`, `ASPNETCORE_*` and `DOTNET_ENVIRONMENT`, disables CLI telemetry, and kills the command process group on timeout. Logs go to ignored `results/`. HTTP traffic is loopback-only; exporters are in-memory.

## Candidate being exercised

- `Candidate.cs` configures an existing host builder additively. The independent processor is passed into builder callbacks; it never resolves `TracerProvider`. Startup-filter construction resolves the provider before the first request, creates a fallback only when absent, or attaches to `AddSingleton<TracerProvider>(existing)`. Existing-instance providers are already instrumented and subscribed, and remain externally owned.
- `RequestState.cs` obtains `IHttpContextAccessor.HttpContext` in the real ASP.NET SERVER processor `OnStart`. A context feature holds one request state. Method, path and user agent are copied privately while HTTP Activity tags are still absent. Middleware ensures the same state exists when a dropped/unrecorded SERVER never reaches processor callbacks.
- A `(TraceId, SpanId)` map inherits association through parent IDs at child `OnStart`. It does not walk `Activity.Parent` and never associates by trace ID alone. The fixture only records observed IDs for assertions; it never inserts associations or constructs request state.
- Injected helpers use the HTTP context feature even with a nested child current. Consumer/custom values and the first exception's immutable type/message become independent transport inputs and private SERVER attributes. Out-of-request calls are no-ops. The exception object itself is not retained.
- Real `OnCompleted` captures final transport inputs. Real SERVER `OnEnd` copies the Activity. One request lock coordinates the two processing paths, final transport/helper merge, response decision, once-only release, cumulative descendant/log caps and late keep/drop. Initial release submits ended descendants in arrival order, SERVER once, then buffered logs in arrival order. Actual exporter output verifies that order; genuine late spans/logs follow the cached decision directly. Request-stage drop wins over response keep. A short admission lock serializes map admission, cleanup and final cutoff.
- Before the response decision, the owned SERVER copy receives final method, status, route and known request/response content-length sizes from transport. Absent route and unknown sizes remain absent. Decision and actual exported SERVER assertions cover these fields in native, reverse and race rows, while the independent app exporter still has no body-size attributes for these fixtures.
- `Pipeline.cs` uses the public `OpenTelemetryLoggerProvider(IOptionsMonitor<OpenTelemetryLoggerOptions>)` constructor behind an additive `ILoggerProvider`. Actual native `LogRecord.TraceId` and `SpanId` resolve the same map. The native masking callback changes Body and the secret attribute; its changes are copied synchronously into owned data. Every retained log explicitly identifies `apitally.request.server_span_id`.
- Owned span/log envelopes go through stock `BatchExportProcessor<Owned>` into memory. Tests wait for the exporter to record completion, not just `ForceFlush`. Positive-timeout `Shutdown(5000)` joins the stock batch worker before disposal. App-provider/exporter disposal is independently checked.

## Integrated scenarios

| Scenario group | Real events and assertions |
| --- | --- |
| Registration | Fallback, both AddOpenTelemetry.WithTracing orders, existing-instance DI; first request from `IHostedLifecycleService.StartedAsync` before ApplicationStarted; preserved app resource/enrichment/exporter/sampler; external provider works after host disposal |
| Request contents | Async manual child/nested spans, instrumented HttpClient loopback request, helper calls while nested child is current, native ILogger logs at children and SERVER; exact IDs, counts, parentage and private SERVER linkage |
| Early association | Before-middleware child/log in ordinary EnrichWithHttpRequest; explicit ActivityContext parent with null Activity.Parent; unrelated same-trace work excluded |
| Isolation | Deterministically overlapping requests, sequential HTTP/1.1 requests with the same observed connection ID, two inbound roots sharing a remote trace ID |
| Decisions and sampling | Request-drop/response-keep, response-drop, keep/abstain using final status/route/helpers; AlwaysOff, RecordOnly and ParentBased remote-unsampled preserve independent consumer/custom/first-exception/transport inputs without detail |
| Late detail and cleanup | A real child starts while the request is live, then its log/end occur after both actual completions; cached keep/drop, cumulative caps, POC-only metadata expiry, and final cutoff while a real request is gated |
| Completion processing | Native order recorded separately; TEST-ONLY reverse and async racing handoffs process already-captured actual completion data; release observes both processed completions |

The outgoing call targets `/sink/{id}-sink` on the same Kestrel host. That endpoint is an explicit request-detail-drop fixture, so its SERVER detail cannot hide missing originating-request spans. It still appears in the independent application exporter and transport observations.

## Retention and test-only boundaries

**This POC alone uses a 60-second retention interval after final processing**, with an explicit cleanup sweep and an injected manually advanced clock. It retains IDs, counters and the final decision to route late detail. The sweep removes every associated ID of an expired completed request, including IDs of children still running. A subsequent log/end from that child is ignored; no request is recreated. Before the sweep, late detail follows the cached decision and the same cumulative caps. The span cap counts descendants; SERVER has its own once-only slot.

Unresolved requests are not expired by that completed-request sweep. They remain live until framework completion or explicit final cutoff. Cutoff disables capture, clears associations, and drops unresolved detail immediately; transport/helper inputs may still complete independently. All fixture requests/gates are bounded. This experiment introduces no background expiry scheduler or production active-request timeout.

The subsequent [approved design](../../docs/design.md#late-telemetry-associations) preserves late telemetry using a bounded FIFO cache of completed, kept-request span IDs, with individual-ID eviction and 10,000 IDs as the initial internal capacity to validate. Active requests remain outside the cache; retained IDs reference shared sampling decisions and cumulative counters. There is no time-based expiry or public setting. The executable POC remains unchanged and does not validate that cache or its capacity.

`Observation` and fixture/exporter ledgers are TEST-ONLY output observers. They intentionally retain snapshots for assertions and never supply association or completion data. The retained-payload assertion checks the candidate's request payload fields/buffers, excluding these observer/exporter output copies. No allocation, GC or memory-pressure benchmark is claimed.

The native log adapter accepts only the named `RequestAssociation.Application` category and immutable primitive fixture values. Framework noise is excluded so exact-count failures cannot be hidden. This does not establish general logging-state normalization, scope capture, general masking, body capture or a public helper API. The private exception representation is two fixture attributes, not a complete exception-event design. Transport enrichment covers this primitive fixture snapshot shape, not full public SpanSnapshot validation.

The response-policy fixture selects drop/keep/abstain by path; assertions examine the exact final SERVER/transport/helper snapshot available at that decision. The first request for each sampled registration case uses the full normal workload. Sampling-disabled cases omit the outgoing call. The full interaction suite runs under candidate-first registration, avoiding redundant permutations across every registration mode.

`CompletionHandoff` is TEST-ONLY. Reverse processing waits until both real completion inputs have arrived, then processes SERVER before transport. Racing processing uses two tasks with an async start gate. Neither fabricates SERVER completion, calls Activity.Stop on SERVER, or waits for SERVER inside OnCompleted. Native callback exceptions are accumulated and checked by Main; requests, gates, host start/stop, handoffs and export waits are bounded.

Duration, content-length sizes, status, route, consumer/custom and first-exception observations are **inputs** for independent metrics/error pipelines, not proof of metric/error export. Mixed owned envelopes are a POC convenience, not a proposed production signal schema. Spool/delivery coordination, R4 shutdown budgeting, abort/error transport coverage and implementation/validation of the selected production cache remain outside this experiment.
