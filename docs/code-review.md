# Apitally .NET v1 code review

Date: 2026-09-29. Scope: `src/Apitally` and `tests/Apitally.Tests` on branch `v1` (commit `7a099e8`, "Initial v1 implementation"). `pocs/` is out of scope.

## Method

Six independent reviewers each covered one angle across the whole codebase:

1. Correctness and concurrency
2. Robustness, failure isolation and lifecycle
3. Performance and per-request overhead
4. Simplicity and overengineering
5. Idiomatic .NET and developer expectations
6. Data and protocol correctness, and privacy (redaction)

Each reviewer checked findings against [design.md](design.md), [implementation-plan.md](implementation-plan.md), and the shared [spec](../../cloud/docs/sdks/spec.md) and [design](../../cloud/docs/sdks/design.md). The findings marked **Reproduced** were confirmed with scratch programs or tests in copies of the repository under `/tmp`. The High findings were then checked a second time against the source during consolidation. Duplicate findings from different reviewers have been merged.

Baseline: `dotnet build -warnaserror` has 0 warnings, and `dotnet test --framework net9.0` passes 159 of 159 tests.

Severity scale:

- **High**: data leak, unbounded resource growth, or silent loss of core telemetry in a realistic setup.
- **Medium**: wrong or missing telemetry, a failure that is hard to diagnose, or friction every user will hit.
- **Low**: an edge case, a cost that is small, or a quality improvement.

## Priority summary

| ID | Severity | Title | Angle |
| --- | --- | --- | --- |
| [D1](#d1-compressed-request-bodies-bypass-redaction-with-userequestdecompression) | High | Compressed request bodies bypass redaction with `UseRequestDecompression` | Privacy |
| [C1](#c1-requests-leak-when-the-apps-aspnet-core-instrumentation-filter-drops-the-server-activity) | High | Requests leak when an instrumentation `Filter` drops the SERVER activity | Correctness |
| [C2](#c2-non-w3c-propagators-leak-every-request-and-export-no-traces) | High | Non-W3C propagators (B3) leak every request and export no traces | Correctness |
| [P1](#p1-body-capture-allocates-50-kb-per-body-and-copies-it-twice) | High | Body capture allocates 50 KB per body and copies it twice | Performance |
| [P2](#p2-exceptiontostring-runs-two-or-more-times-per-failing-request) | High | `Exception.ToString()` runs 2 or more times per failing request | Performance |
| [R1](#r1-exceptions-in-the-apitally-logger-reach-the-applications-ilogger-call) | Medium | Exceptions in the Apitally logger reach the application's `ILogger` call | Robustness |
| [R2](#r2-a-non-ascii-env-makes-every-export-fail-silently) | Medium | A non-ASCII `Env` makes every export fail silently | Robustness |
| [R3](#r3-delivery-failures-cannot-be-diagnosed) | Medium | Delivery failures cannot be diagnosed | Robustness |
| [D2](#d2-ndjson-bodies-are-captured-but-never-field-redacted) | Medium | NDJSON bodies are captured but never field-redacted (all SDKs) | Privacy |
| [C3](#c3-with-two-hosts-in-one-process-one-host-claims-the-others-requests) | Medium | With two hosts, one host claims the other's requests | Correctness |
| [I1](#i1-xml-docs-are-not-shipped-and-24-public-members-are-undocumented) | Medium | XML docs are not shipped; 24 public members are undocumented | Idioms |
| [I2](#i2-callback-attribute-value-types-surprise-net-developers) | Medium | Callback attribute value types surprise .NET developers | Idioms |
| [I3](#i3-packaging-gaps-symbols-analyzers-deterministic-build-icon) | Medium | Packaging gaps: symbols, analyzers, deterministic build, icon | Idioms |
| [I4](#i4-first-run-experience-and-readme-gaps) | Medium | First-run experience and README gaps | Idioms |
| [P3](#p3-body-redaction-always-decodes-the-body-to-a-string) | Medium | Body redaction always decodes the body to a string | Performance |
| [S1](#s1-snapshot-types-use-long-positional-constructors) | Medium | Snapshot types use long positional constructors | Simplicity |
| [S2](#s2-requeststate-finalization-keeps-redundant-state) | Medium | `RequestState` finalization keeps redundant state | Simplicity |
| Remaining | Low | See each section | All |

Recommended order: fix D1, C1 and C2 first, since they leak data or memory in common production setups. Next fix R1, R2, R3 and P2, which are all small. Then I1 and I3 before the first public release, because they are cheap and hard to change once published.

## Regulatory note

D1, D2 and D4 each export secrets or personal data that the shared spec says must be redacted. Customers building healthcare APIs subject to HIPAA or GDPR rely on built-in redaction, and this is not an opt-in path: D4 applies with default settings on .NET 8. Spooled data is also written to local disk (owner-only permissions) before export, so a leak also exists at rest. Treat D1 as a release blocker, and add end-to-end redaction tests through real middleware, not just unit tests of `SpanRedaction`.

---

## Correctness and concurrency

### C1. Requests leak when the app's ASP.NET Core instrumentation `Filter` drops the SERVER activity

- **Severity**: High. **Reproduced.**
- **Location**: `Requests/RequestRegistry.cs:58-65`, `Requests/RequestState.cs:280-284`, `Tracing/ApitallySpanProcessor.cs:54-58`
- **Problem**: A common OTel setup filters out health checks: `AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))`. The sequence is:
  1. `ApitallySpanProcessor.OnStart` runs while `activity.Recorded` is still true.
  2. `GetOrCreate` keeps detail and associates the server span.
  3. The instrumentation then applies the filter. This clears `Recorded` and sets `IsAllDataRequested = false`.
  4. OTel never calls `OnEnd` for that activity.
  5. `TryClaimFinalization` waits forever, because `isAwaitingServer = isServerAssociated && !isServerComplete`.

  The `RequestState` stays in `inFlight` and `associations` until shutdown, and it holds a reference to the `Activity`. A load-balancer health probe every few seconds grows memory without bound. Ten filtered requests left `inFlight=10 associations=10`. Metrics are unaffected.
- **Recommendation**: At transport completion, stop waiting for a SERVER end that cannot arrive: add `&& ServerActivity is { IsAllDataRequested: true, Recorded: true }` to the `isAwaitingServer` condition. The filter always runs at activity start, and Kestrel calls `OnCompleted` before `DisposeContext`, so the flag is final by then. Add a test helper that asserts the registry is empty after requests, and use it in the integration tests. That assertion would have caught both C1 and C2.
- **Decision**: Fixed as recommended. `ApplicationHost.StopAsync` now stops the server first and asserts `RequestRegistry.IsEmpty` for every integration test; covered by `TracingIntegrationTests.RequestsFilteredByApplicationInstrumentationAreReleased`.

### C2. Non-W3C propagators leak every request and export no traces

- **Severity**: High for affected users (Zipkin/B3 setups). **Reproduced.**
- **Location**: same as C1, plus `Requests/RequestRegistry.cs:47` and `Tracing/ApitallySpanProcessor.cs:31-41`
- **Problem**: When the application sets `Sdk.SetDefaultTextMapPropagator(new B3Propagator())` and a request carries B3 headers, OTel's ASP.NET Core instrumentation does two things:
  - It creates a replacement SERVER activity with the extracted parent.
  - It sets `IsAllDataRequested = false` on the original activity, which is the one Apitally associated.

  `GetOrCreate` returns the existing state for the replacement, so the replacement is never associated. Every request leaks as in C1, and no spans are exported (5 requests left `inFlight=5` and 0 spans were received).
- **Recommendation**: The C1 fix stops the leak. To keep traces, when a hosting activity starts for a context whose state's server activity has `IsAllDataRequested == false`, point the state's server activity and its association at the new activity, under the state's lock. If that is deferred, document B3 as unsupported.
- **Decision**: Rejected. The leak is fixed by C1; traces for requests carrying non-W3C propagation headers remain unsupported.

### C3. With two hosts in one process, one host claims the other's requests

- **Severity**: Medium. **Reproduced.**
- **Location**: `Tracing/ApitallySpanProcessor.cs:36-37`, `Requests/RequestRegistry.cs:47`
- **Problem**: `ActivityListener`s are process-wide, and `HttpContextAccessor` uses a static `AsyncLocal`. As a result, host A's processor creates the `RequestState` on host B's `HttpContext`. B's middleware then finds that state and uses it. B's metrics, traces and error aggregates go through A's configuration, redaction rules, write token and env. With different `SampleOnResponse` callbacks, a request to B ran A's callback. Multi-host coordination is outside v1 scope (design.md "Hosting support boundary"). However, design.md §2 states that "host association correctly filters foreign requests", and the implementation does not. A realistic trigger is a public host and an admin host in the same process.
- **Recommendation**: In `OnStart`, ignore contexts that belong to another runtime (`context.RequestServices.GetService<TelemetryRuntime>() != runtime`). In `Get`, ignore a `RequestState` created by a different registry.

### C4. Route attribution under the exception handler, status-code pages and PathBase

- **Severity**: Low. The first two are **Reproduced**.
- **Location**: `AspNetCore/EndpointMetadata.cs:30-31, 51-58`, `Requests/RequestRegistry.cs:48-53, 201`
- **Problem**:
  - **Exception thrown before routing**: `IExceptionHandlerFeature?.Endpoint ?? context.GetEndpoint()` falls back to the error handler's endpoint. A 500 from a middleware before `UseRouting()` is recorded as `GET /error`, when it should have no route.
  - **Status-code-page re-execution**: `UseStatusCodePagesWithReExecute` replaces the original route. For example, a 403 from `/forbidden/{id}` is recorded as `/status/{code}`.
  - **PathBase**:
    - `ResolveRoute` prepends the PathBase seen at request entry, but `GetPaths` sends raw templates in the startup event. Under an IIS or App Service virtual directory, request routes and registered paths never match.
    - With `app.UsePathBase("/api")` or `app.Map("/v1", ...)`, the entry PathBase is empty, so the mount prefix is lost. The shared design §8 requires mount prefixes. Two `Map` branches can therefore merge into one route.
- **Recommendation**:
  - Use `IExceptionHandlerFeature is { } handled ? handled.Endpoint : context.GetEndpoint()`.
  - For status pages, save `IStatusCodeReExecuteFeature.Endpoint` when it is present, or document the limitation.
  - For PathBase, choose one convention and apply it to both `ResolveRoute` and `GetPaths`. The simplest option is to capture `Request.PathBase` when routing runs, not at entry. Whether mount prefixes are needed is a product decision.

### Test gaps (correctness)

- There is no assertion that `inFlight` and `associations` are empty after requests complete.
- These setups are not tested: an application-side instrumentation `Filter`, a non-W3C propagator, and two hosts in one process.
- There are no tests for HEAD, 204 or 304 responses, a chunked request body the app never reads, a Content-Length mismatch, or HTTP/2 stream resets.
- Neither completion order (transport first, or SERVER end first) is exercised under concurrency.

## Robustness and lifecycle

### R1. Exceptions in the Apitally logger reach the application's `ILogger` call

- **Severity**: Medium. **Reproduced.**
- **Location**: `Logging/ApitallyLoggerProvider.cs:58-95`, `Logging/LogMasking.cs:22`
- **Problem**: When a provider throws, `Microsoft.Extensions.Logging.Logger.Log` throws an `AggregateException` back to the code that logged. `ApitallyLogger.Log` guards only the user's mask callback. The call to `formatter(state, exception)`, the scope enumeration and `exception.ToString()` are all unguarded. Consider `_logger.LogInformation("User {Id} did {Action}", id)`, where the template has more placeholders than arguments. It is harmless under providers that never format, such as Serilog or the OTel logger with default settings. Once Apitally is added, log capture is on by default, and that log line throws `FormatException` into the request and returns a 500. This violates the rule that the SDK must never break the host application.
- **Recommendation**: Wrap the body of `Log`, after the cheap association checks, in `try`/`catch` and report the exception with `diagnostics.RequestProcessingFailed(e)`. The `Apitally` log category is already excluded from capture, so this cannot recurse. Add a test.
- **Decision**: Rejected. The trigger is an application bug flagged at build time by CA2017, and the default console provider already throws for it without Apitally. Microsoft's and OTel's providers do not guard against it either.
- **Follow-up decision**: Structured log values and scopes are no longer captured, because the server's log ingestion stores only the rendered message, level, logger and code location. `LogRecordSnapshot.Attributes`, scope support and `LogMasking` are removed; `MaskLogRecord` works on `Body`, `CategoryName`, `LogLevel`, `EventId` and `Timestamp`. design.md and migration.md are updated.

### R2. A non-ASCII `Env` makes every export fail silently

- **Severity**: Medium. **Reproduced.**
- **Location**: `Hosting/RuntimeConfiguration.cs:82`, `Export/ExportHttpClient.cs:65-75, 103`
- **Problem**: `Env` is sent unchanged as the `Apitally-Env` header. With a value such as `"Produktion Süd"`, `SocketsHttpHandler` throws `HttpRequestException` because request headers must be ASCII. The exporter classifies this as a retryable connection error. Every POST fails, the oldest spool file blocks every signal, and the application never shows as online. The user sees only Debug messages, plus an hourly "could not be delivered" warning that gives no cause. The spec says the server slugifies env, so users can reasonably expect non-ASCII names to work.
- **Recommendation**: Validate `Env` in `Resolve`. If it contains non-ASCII or control characters, log an Error and disable the SDK, the same as for an invalid token. Do not silently fall back to `dev`. An alternative, if the product prefers it, is to slugify on the client exactly as the server does.

### R3. Delivery failures cannot be diagnosed

- **Severity**: Medium
- **Location**: `Export/ExportHttpClient.cs:71-75`, `Export/ExportWorker.cs:89-92, 155`, `Logging/SdkDiagnostics.cs:147, 153, 165`, `Export/ApitallyBatchProcessor.cs:36-39`
- **Problem**: The shared design §12 requires that "the SDK never quietly does nothing". Three places fall short:
  - Connection-level exceptions are discarded (`catch ... { return new(ExportOutcome.Retryable); }`). Blocked egress, a TLS-intercepting proxy, DNS failure or R2 therefore produce only a vague warning about an hour later, with no cause, even at Debug level.
  - `ExportCycleFailed` is logged at Debug. A cycle that fails the same way every time is invisible.
  - `DelegatingExporter` returns `Failure` without any diagnostic, which drops up to 512 records silently.
- **Recommendation**:
  - Carry the exception in `ExportResponse` and pass it to `ExportRetryable`.
  - Add one deduplicated Warning, including the exception, when a connection failure keeps recurring. Reset it on the next accepted export, the same pattern `SpoolWriteFailed` uses.
  - Make `ExportCycleFailed` a deduplicated Warning.
  - Log the exception in `DelegatingExporter`.

### R4. Suppressed TestServer hosts still log "write token is missing" at Error

- **Severity**: Low
- **Location**: `Hosting/TelemetryRuntime.cs:73-77`
- **Problem**: `Prepare` calls `Resolve`, which logs token errors, before it checks `IsTestServer`. A test suite using `WebApplicationFactory` without a token logs an Error for every host. CI pipelines that fail on Error logs will break.
- **Recommendation**: Check for the TestServer first and return early. Assert in `TestServerHostsAreSuppressed` that no Error diagnostics are logged.

### R5. `AddApitally` on a host without a web pipeline does nothing silently

- **Severity**: Low
- **Location**: `Hosting/ApitallyHostedService.cs`, `Hosting/ApitallyStartupFilter.cs`
- **Problem**: Activation only happens inside `IStartupFilter`. A Generic Host worker that calls `AddApitally()` compiles and runs, but collects no telemetry and logs nothing.
- **Recommendation**: In `ApitallyHostedService.StartedAsync`, if the runtime is still `Created`, log one Warning such as "Apitally requires an ASP.NET Core web host; no telemetry is collected."

### R6. The export connection never re-resolves DNS

- **Severity**: Low
- **Location**: `Export/ExportHttpClient.cs:36-44`
- **Problem**: The idle timeout is 30 seconds and the export interval is about 15 seconds, so the pooled connection never goes idle. With no `PooledConnectionLifetime`, a long-running process keeps posting to the old IP address after an endpoint migration.
- **Recommendation**: Set `PooledConnectionLifetime = TimeSpan.FromMinutes(5)`.

### R7. `TelemetryRuntime` supports only asynchronous disposal

- **Severity**: Low. **Reproduced.**
- **Location**: `Hosting/TelemetryRuntime.cs:18, 186-199`
- **Problem**: Calling `((IDisposable)app.Services).Dispose()` throws `InvalidOperationException` ("only implements IAsyncDisposable"). `IHost.Dispose()` and `WebApplication.Dispose()` work, so only code that disposes the service provider directly is affected.
- **Recommendation**: Also implement `IDisposable` with a best-effort synchronous stop.

### Verified sound (robustness)

- **Disk full**: tested on a 2 MB RAM disk. The failure is contained, the warning is deduplicated, and no files are left behind.
- **Export worker**: the worker loop cannot die, and flushes and POSTs have time limits.
- **Shutdown**: shutdown is idempotent, and it handles `StopAsync` without `StartAsync`, a failed server bind, and an already-canceled token.
- **User callbacks**: all user callbacks are contained, and each fails open or closed as the spec requires.
- **Spool**: the spool is bounded, and its files are owner-only.
- **HTTP status handling**: 4xx, 408, 429 and 5xx responses are classified as the design specifies.
- **Configuration errors**: they are caught in `Prepare` and logged at Error, and the host keeps serving.

## Performance

Context: Kestrel awaits `OnCompleted` callbacks before it reads the next HTTP/1.1 request on the same connection. All of `RequestRegistry.CompleteTransport` therefore adds latency to keep-alive connections. It does not delay the response already sent.

### P1. Body capture allocates 50 KB per body and copies it twice

- **Severity**: High when body capture is on. It also applies to every 400/422 JSON response when capture is off.
- **Location**: `AspNetCore/BodyCapture.cs:56, 107, 111-114`, `Requests/RequestRegistry.cs:218-227`
- **Problem**: `buffer ??= new byte[MaxBodySize]` allocates and zeroes 50,000 bytes on the first captured write, whatever the body size. That is about 3.5 µs per body. Typical API bodies are 0.2 to 5 KB. With both request and response capture on, that is about 100 KB per request, or roughly 100 MB/s at 1,000 requests per second. Slow requests promote these buffers to gen1/gen2. On top of that:
  - `GetBody` copies the buffer (`buffer![..used]`).
  - `ValidationResponse` makes a second copy for every complete captured response, even though only 400/422 JSON responses are parsed.
- **Recommendation**:
  - Size the buffer from `Content-Length` when it is known. Otherwise start at about 4 KB and grow by doubling up to the cap.
  - Compute `ValidationResponse` only when `ValidationCapture.IsValidationResponse(...)` is true.
  - When both the body and the validation bytes are needed, share one copy.
- **Decision**: Fixed as recommended. The buffer starts at the declared length or 4 KB and doubles up to the cap; the trimmed buffer is shared by the exported body and validation parsing; validation bytes are retained only for validation responses. Growth is covered by `ApitallyMiddlewareTests.ChunkedRequestBodyLargerThanInitialBufferIsCaptured`.

### P2. `Exception.ToString()` runs two or more times per failing request

- **Severity**: High during error spikes. **Measured.**
- **Location**: `Requests/ErrorAggregates.cs:52`, `Tracing/SpanSnapshots.cs:102`, `Logging/LogMasking.cs:40`
- **Problem**: Both `AddServerError` and `AddExceptionEvent` format the same exception. `LogMasking` formats it a third time when the application also logs it. One call with a 20-frame async stack costs about 176 µs and allocates about 77 KB. During a database outage at 2,000 requests per second, that is about 0.7 cores and 300 MB/s of allocation on request threads, at the moment the application is already failing.
- **Recommendation**: Format each exception once. Either cache the string next to the captured exception in `RequestState`, or use a static `ConditionalWeakTable<Exception, string>` shared by all three call sites.
- **Decision**: Fixed with the static `ConditionalWeakTable` in `Requests/ExceptionStacktrace.cs`. Log records no longer carry `exception.*` attributes, because the server's log ingestion does not store them; design.md and migration.md are updated.

### P3. Body redaction always decodes the body to a string

- **Severity**: Medium. Applies only when body capture is on, and runs on the batch worker.
- **Location**: `Export/SpanRedaction.cs:215, 238-255`
- **Problem**: `StrictUtf8.GetString(bytes)` runs for every body. For JSON, the result is discarded, because `RedactJson` builds its own output. A 50 KB body becomes a 100 KB string on the large-object heap, and the redacted output is another one. Gen2 collections pause request threads. `Decompress` also allocates a new 50 KB buffer for each compressed body.
- **Recommendation**: Validate with `System.Text.Unicode.Utf8.IsValid(bytes)`, and decode to a string only in the non-JSON fallback. Optionally reuse one decompression buffer; the worker is single-threaded.

### P4. Consumer change detection serializes and hashes on every request

- **Severity**: Low. **Measured**: about 1.3 to 2.3 µs and 0.8 to 1.3 KB per request.
- **Location**: `Requests/ConsumerUpdates.cs:80-122`
- **Problem**: For each request that has consumer metadata, the unchanged steady state still pays for all of this:
  - reflection-based `JsonSerializer.Serialize(object?[])`
  - sorting with LINQ `OrderBy`
  - SHA-256 and a Base64 string
  - a global lock that allocates a new `LinkedListNode`
- **Recommendation**: Store the last normalized values in the LRU entry and compare field by field. Move the existing node instead of allocating a new one. This also removes one of the two AOT warnings (I7).

### P5. A global lock is taken on every routed request for empty validation details

- **Severity**: Low
- **Location**: `Requests/RequestRegistry.cs:247-260`, `Requests/ErrorAggregates.cs:15-38`, `Requests/RequestState.cs:149-153`
- **Problem**: Every routed request allocates a `List` and takes the process-wide `ErrorAggregates` lock just to loop over an empty sequence. This is the only global lock on the success path.
- **Recommendation**: Skip the call when there are no validation details, and return a shared empty list.

### P6. Replacing `Request.Body` disables the zero-copy `BodyReader` path on .NET 10

- **Severity**: Low. Not measured.
- **Location**: `AspNetCore/ApitallyMiddleware.cs:91`
- **Problem**: On .NET 10, Minimal API JSON binding reads from `BodyReader`. Because `Request.Body` has been replaced, Kestrel wraps the replacement in a `StreamPipeReader`. That adds one extra copy of every request body.
- **Recommendation**: Accept this for v1. Revisit only if profiling shows a real cost.

### P7. The metrics payload limit could drop a whole interval near the cardinality cap

- **Severity**: Low. Medium confidence: estimated from the encoding, not reproduced.
- **Location**: `Export/OtlpEncoder.cs:15, 57-77`
- **Problem**: Each `OtlpMetric` is an indivisible record. One histogram with up to 10,000 exponential-histogram points could exceed `MaxRequestSize` (4 MB). The metric is then dropped with `OversizedRecordDropped`, which loses all counts for that interval in exactly the high-traffic case.
- **Recommendation**: Measure the encoded size at 10,000 points. If it can exceed the limit, split the data points across requests.

### Already done well (do not regress)

- **Unsampled requests**: requests without kept detail skip snapshots, header copies and body retention.
- **Logger**: it checks request association before it formats a message.
- **Response pipe writer**: it passes `GetMemory` and `Advance` straight through, and delegates `SendFileAsync` unchanged.
- **No heavy work on request threads**: decompression, masking, redaction, encoding, gzip and I/O all run on worker threads.
- **Regex patterns**: built-in patterns use source-generated regexes, and user patterns are compiled once.
- **Locking**: locks are per request, and the association maps are `ConcurrentDictionary`.
- **Metrics**: they use native OTel histograms with a stack-allocated `TagList`.
- **Diagnostics**: they use `[LoggerMessage]` source-generated methods and are deduplicated.

## Data and protocol correctness, and privacy

### D1. Compressed request bodies bypass redaction with `UseRequestDecompression`

- **Severity**: High. **Reproduced**, and confirmed again against the source during consolidation.
- **Location**: `Requests/RequestRegistry.cs:220-223`, `Requests/RequestRegistry.cs:83-87`, `Export/SpanRedaction.cs:199-203, 233-253`. Spec §6.3: "Compressed bodies MUST be decompressed before masking and field redaction."
- **Problem**: Apitally's middleware runs outermost, so it captures the compressed bytes. ASP.NET Core's `RequestDecompressionMiddleware` runs later and removes the `Content-Encoding` request header. `CompleteTransport` reads `request.Headers.ContentEncoding` after the request completes, finds it empty, and treats the gzip bytes as uncompressed. UTF-8 decoding fails, so the raw bytes are exported as a bytes-valued `apitally.request.body`. The masking callbacks also receive gzip bytes. For example, a gzip POST of `{"user":"a","password":"hunter2"}` was exported as bytes that decompress to the original JSON, password included. The body is written to the spool on disk and then sent to Apitally. Any API that accepts gzip JSON from mobile or IoT clients is affected.
- **Recommendation**: Record the request `Content-Encoding` at middleware entry, in `RequestEntry`. Use that value for both `IsSupportedContentEncoding` and `GetBody`. Add an end-to-end test with `UseRequestDecompression`.
- **Decision**: Fixed as recommended. `RequestEntry.ContentEncoding` is read at entry; covered by `ApitallyMiddlewareTests.DecompressedRequestBodyIsDecodedAndRedacted`.

### D2. NDJSON bodies are captured but never field-redacted

- **Severity**: Medium. **Reproduced.** The same gap exists in the Python and JavaScript SDKs.
- **Location**: `Export/SpanRedaction.cs:222-236`, `AspNetCore/BodyCapture.cs:11-20`. Spec §6.3 (allow-list) and §6.7 (body field redaction).
- **Problem**: `application/x-ndjson` is on the capture allow-list. `Utf8JsonReader` rejects the second top-level value, and the code falls back to the raw text. `{"password":"hunter2"}\n{"token":"abc"}` is exported unchanged. Python's `json.loads` and JavaScript's `JSON.parse` fall through in the same way.
- **Recommendation**: Raise this against the shared spec. The smaller useful change is to redact each non-empty line separately, and fall back to text only if a line fails to parse. The alternative is to remove NDJSON from the allow-list in all SDKs.

### D3. Outgoing URLs with query secrets are captured unredacted from `HttpClient` logs on .NET 8

- **Severity**: Low. Not reproduced; based on Microsoft's breaking-change documentation.
- **Location**: `Logging/ApitallyLoggerProvider.cs:35-39`, `Logging/LogMasking.cs:27-40`
- **Problem**: On .NET 8, `IHttpClientFactory`'s `LogicalHandler` logs `Start processing HTTP request GET https://partner/api?api_key=...` at Information, and it pushes a `{Uri}` scope. Query redaction in these logs only arrived in .NET 9. With default logging levels and `CaptureLogs = true`, the key is exported both in the log body and in the `Uri` attribute. This matches the shared design, which applies automatic redaction only to query parameters, headers and body fields, and Python behaves the same way with `httpx`. It is still the most likely credential leak in a default .NET 8 installation.
- **Recommendation**: Exclude the `System.Net.Http.HttpClient.` log categories by default. The request already has the outgoing CLIENT span, which is redacted. At a minimum, document this next to the "log capture on by default" note in the migration guide.

### Verified conformant (data and privacy)

- **Redaction patterns and coverage**:
  - Query, header and body patterns match spec §6.7, are case-insensitive, and user patterns are added to the defaults.
  - URL attributes are redacted on all spans, including `HttpClient` child spans.
  - Redaction failures and regex timeouts drop the span (fail closed).
- **Wire format**: correct in every aspect checked:
  - resource attributes, the `Apitally-Env` header and authentication
  - protobuf with gzip encoding
  - trace and span IDs, span kinds, UTC nanosecond timestamps
  - status codes and log severity numbers
- **Metrics**: scope, temporality, exponential-histogram scale, names, units and attributes match the spec.
- **Internal events**: truncation limits, group caps and `config` exclusions match the spec.
- **Write token**: it is never logged. Only its first 8 characters appear, and only for an invalid format.

### Spec-level notes

- Spec §6.7 replaces only string values, so `{"ssn": 123456789}` is exported unchanged in every SDK. For healthcare customers, consider redacting numeric values for matched keys as well.
- Any body that fails to parse falls through to raw text, for example JSON with a UTF-8 BOM. This is by design, but customer-facing docs should state it.

## Idiomatic .NET and developer expectations

### I1. XML docs are not shipped, and 24 public members are undocumented

- **Severity**: Medium. **Verified by packing.**
- **Location**: `Apitally.csproj`, `ApitallyOptions.cs:24-30`, `SpanSnapshot.cs:51-69`, `LogRecordSnapshot.cs:31-33`, `ApitallyExtensions.cs:16`
- **Problem**: `GenerateDocumentationFile` is off, so the NuGet package ships no `Apitally.xml`, and IntelliSense shows nothing. Several callback rules exist only in those comments, for example that `null` means "use `SampleRate`" or "redact". Enabling the file produces 24 CS1591 warnings for undocumented members.
- **Recommendation**: Enable `GenerateDocumentationFile`, document the 24 members, and let `TreatWarningsAsErrors` (I3) keep the documentation complete.

### I2. Callback attribute value types surprise .NET developers

- **Severity**: Medium
- **Location**: `SpanSnapshot.cs:64`, `Export/AttributeValues.cs:44-56`, `Tracing/SpanSnapshots.cs:78`
- **Problem**: Attribute values are normalized to OTLP types, so `int` becomes `long`. A developer who writes `(int)span.Attributes["http.response.status_code"]` gets an `InvalidCastException`. The SDK catches it and keeps the request with a single warning, so the filter silently does nothing.
- **Recommendation**: Keep the design and document the value types on `Attributes`: `string`, `bool`, `long`, `double`, arrays of these, or `null`. Name the common keys, and add a `SampleOnResponse` example to the README.

### I3. Packaging gaps: symbols, analyzers, deterministic build, icon

- **Severity**: Medium
- **Location**: `Apitally.csproj`, `.github/workflows/publish.yaml`
- **Problem**:
  - No symbols ship: no `.snupkg` and no embedded PDB.
  - `ContinuousIntegrationBuild` is not set.
  - No `AnalysisLevel` or `TreatWarningsAsErrors`. With `latest-recommended`, the analyzers report CA1001 on `ExportWorker` (an undisposed `CancellationTokenSource`), `SpoolFile` and `TracingIntegration`.
  - No `PackageIcon`.
  - `Description` contains a hard line break and indentation.
  - Publishing uses a long-lived `NUGET_API_KEY` even though the workflow already grants `id-token: write`.
- **Recommendation**:
  - Set `IncludeSymbols` with `SymbolPackageFormat=snupkg`.
  - Set `ContinuousIntegrationBuild` when running in GitHub Actions.
  - Set `AnalysisLevel=latest-recommended` and `TreatWarningsAsErrors`, and fix CA1001.
  - Add a 128x128 icon.
  - Put the description on one line.
  - Switch to NuGet trusted publishing.

### I4. First-run experience and README gaps

- **Severity**: Medium
- **Location**: `README.md:74-140`, `Logging/SdkDiagnostics.cs:26-50`, `Hosting/TelemetryRuntime.cs:76-80`
- **Problem**:
  - A developer who sets the token only in production gets `fail: ... write token is missing` on every local run. The README does not show `"Apitally": { "Disabled": true }` in `appsettings.Development.json`.
  - The README doesn't mention TestServer suppression, or how to disable the SDK in tests.
  - The `appsettings.json` example places the token in a committed file. Standard .NET guidance is `dotnet user-secrets` or environment variables.
  - "Your sampler ... remain[s] unchanged" hides the consequence: with a 10% user sampler, 90% of requests never appear in request logs.
  - A working setup logs nothing, so developers can't tell whether the SDK is active.
- **Recommendation**:
  - Add README sections "Disabling in development and tests" and "Storing the write token".
  - Add one sentence explaining that the user's sampler controls which requests get request logs and traces.
  - Log one Debug or Information line on activation, such as `Apitally started (env=prod)`, and one Debug line when TestServer suppression applies.

### I5. The NuGet README won't render correctly

- **Severity**: Low
- **Location**: `README.md:1-18`, `Apitally.csproj:20`
- **Problem**: nuget.org does not render `<picture>` or `<p align>`, and it only shows images from allow-listed domains, which excludes `assets.apitally.io`.
- **Recommendation**: Pack a short `docs/nuget-readme.md` in plain Markdown instead.

### I6. The extension class name breaks the `{Feature}ServiceCollectionExtensions` convention

- **Severity**: Low. The API is pinned in the plan, so this is a suggestion to consider before 1.0.
- **Location**: `ApitallyExtensions.cs:14-16`
- **Problem**: Microsoft.Extensions and OpenTelemetry use `{Feature}ServiceCollectionExtensions`. The name `ApitallyExtensions` doesn't say what type it extends.
- **Recommendation**: Rename the class to `ApitallyServiceCollectionExtensions` before GA. Moving it to the `Microsoft.Extensions.DependencyInjection` namespace is optional.

### I7. Cheap trimming and AOT compatibility wins

- **Severity**: Low
- **Location**: `Hosting/RuntimeConfiguration.cs:23`, `Requests/ConsumerUpdates.cs:109`, `Apitally.csproj`
- **Problem**: `IsAotCompatible=true` reports only two sources of warnings: reflection-based `ConfigurationBinder.Bind`, and `JsonSerializer.Serialize(object?[])`. Design.md says to prefer compatibility-friendly choices when they add no complexity.
- **Recommendation**: Set `EnableConfigurationBindingGenerator=true` and `IsAotCompatible=true`, and replace the consumer hash serialization (see P4). This makes no support claim; it only stops the SDK from adding warnings to users' trimmed builds.

### I8. Minor idiom nits

- **`ConfigureAwait`**: `ApitallyMiddleware.cs:21, 30` omits `ConfigureAwait(false)`, while every other await in the library has it. Pick one convention.
- **`IApitally` docs**: the summary says the methods "do nothing" outside a request. `StartActivity` can still return a non-null activity (propagation-only, or recorded by the user's provider). Document this in `<returns>`.
- **Clock**: `LogMasking.cs:43` uses `DateTime.UtcNow`, while the rest of the code uses the injected `TimeProvider`.
- **Culture**: `ExportHttpClient.cs:112` parses a protocol header with `int.TryParse` without `CultureInfo.InvariantCulture`.
- **Log wording**: messages print the enum as-is ("rejected buffered Logs"). Lowercase the signal name.
- **Warning deduplication**: the key `"export-rejected-" + statusCode` omits the signal. After the first 401, rejections for the other signals are silent. If that is intended, say so in a comment.
- **Timing-dependent tests**: `ExportWorkerTests.cs:46, 71, 92, 109, 135` use `Task.Delay(100)` to assert that nothing else happened.

## Simplicity

Overall: the codebase is about 5.5k lines, close to apitally-js (about 5.6k), even though .NET also hand-writes the OTLP mapping. No large abstraction is unneeded, and no BCL or OTel feature is reimplemented beyond the HTTP timeout (S4). The items below are local cleanups that remove roughly 150 to 200 lines in total.

### S1. Snapshot types use long positional constructors

- **Severity**: Medium
- **Location**: `SpanSnapshot.cs:12-49`, `Logging/LogSnapshot.cs:10-56`, `LogRecordSnapshot.cs:12-27`, `tests/Apitally.Tests/Support/TestSpans.cs:18-36`
- **Problem**: `SpanSnapshot` has a 17-parameter constructor with adjacent parameters of the same type (`spanId`, `parentSpanId`, and a run of nullable strings), so swapping two of them still compiles. `LogSnapshot` has an 8-parameter constructor and two factories that pass `null, null` or `default` four times.
- **Recommendation**: Use `{ get; internal init; }` properties with an internal parameterless constructor and object initializers. The public surface is unchanged, and this removes about 50 to 60 lines.

### S2. `RequestState` finalization keeps redundant state

- **Severity**: Medium
- **Location**: `Requests/RequestState.cs:57, 104-111, 176-197, 251-256, 283`, `Requests/RequestRegistry.cs:34, 63-65, 127-134, 278`
- **Problem**:
  - `isServerAssociated` always equals `IsDetailKept`.
  - The `IsDetailKept ? server : null` check in `TakeDetail` is redundant.
  - `TryAssociate` returns a `bool` that no caller reads.
  - `Exception` and `GetCapturedException()` return the same field.
  - `inFlight` mostly duplicates `associations`.
- **Recommendation**:
  - Remove the flag and the redundant check.
  - Make `TryAssociate` return `void`.
  - Merge the two exception accessors.
  - Optionally derive the cutoff set from `associations`.

  This saves about 15 to 20 lines and makes the release-once logic easier to reason about, which matters given C1 and C2.

### S3. `TelemetryRuntime` has more lifecycle states than it uses

- **Severity**: Low
- **Location**: `Hosting/TelemetryRuntime.cs:28, 39-59, 164-199, 219-224`, `Hosting/ApitallyStartupFilter.cs:16-18`
- **Problem**: No code distinguishes `Stopping` from `Stopped`. The `cleanup` field is only read right after it is assigned. `IsPrepared` has a single production reader.
- **Recommendation**:
  - Remove `Stopping` and the `cleanup` field.
  - Have `Prepare` return `bool`: `if (runtime.Prepare(...)) app.UseMiddleware<ApitallyMiddleware>();`.

### S4. `ExportHttpClient` reimplements the request timeout and reads the body twice

- **Severity**: Low
- **Location**: `Export/ExportHttpClient.cs:45, 96-97, 106`
- **Problem**: The client uses `Timeout.InfiniteTimeSpan` plus a linked `CancellationTokenSource` for each request, which is exactly what `HttpClient.Timeout` already does. `ReadAsByteArrayAsync` also re-reads content that is already buffered.
- **Recommendation**: Set `Timeout = RequestTimeout` on the client, and remove the linked token source and the second read.

### S5. Captured payload fields are declared twice and copied field by field

- **Severity**: Low
- **Location**: `Requests/RequestState.cs:21-24`, `Export/SpanRedaction.cs:14-37`, `Requests/RequestRegistry.cs:289-297`
- **Problem**: `TransportCompletion` and `SpanExportEntry` declare the same four header and body properties, and `Release` copies them one at a time.
- **Recommendation**: Introduce one `CapturedPayloads` record shared by both types, and make `CapturedBody` a record with a static `TooLarge` instance.

### S6. `RuntimeConfiguration` has code a test-only setting doesn't need

- **Severity**: Low
- **Location**: `Hosting/RuntimeConfiguration.cs:30, 34-36, 68-76, 126-134, 149-156`, `Logging/SdkDiagnostics.cs:38-42`
- **Problem**:
  - The test-only `APITALLY_OTLP_ENDPOINT` override has its own validation and diagnostic, although `Prepare` already catches and logs a bad URI.
  - The disabled path builds a placeholder configuration object.
  - `DefaultEnv` repeats the default already set on `ApitallyOptions`.
  - `MatchesAny` hand-writes what `Any` already does.
- **Recommendation**: Return `RuntimeConfiguration?`, with `null` meaning disabled. Inline `MatchesAny`. Optionally drop the endpoint validation.

### S7. Dead, test-only and overexposed members

- **Unused**: `SpoolFile.IsClosed` has no references.
- **Constant parameter**: `BodyCapture.GetRetainedBytes(bool isComplete)` is always called with `true`.
- **Test-only**: `ExportWorker.Interval` is exposed only for one test assertion.
- **Should be private**: `SpanRedaction.RedactQuery` and `RedactUrl`, `ConsumerUpdates.NormalizeIdentifier`, and `TelemetrySpool.MaxSize` are only used inside their own classes.
- **Redundant check**: the `registry is null` check in `ApitallyMiddleware.TryObserveBodies` can never be true.
- **Leaked key**: `SdkDiagnostics.SpoolWriteFailedKey` and `ResetWarning(string)` expose a deduplication key string to the spool. Replace them with a single `SpoolWriteSucceeded()` method.

### S8. Small duplicated helpers

- **Regex timeout**: defined in three places (`RequestSampling.cs:16`, `SpanRedaction.cs:53`, `RuntimeConfiguration.cs:32`). Point all three at `RuntimeConfiguration.PatternMatchTimeout`.
- **String truncation**: three separate helpers (`ErrorAggregates.cs:89`, `OtlpLogMapper.cs:69`, `ConsumerUpdates.cs:124`).
- **Version parsing**: `InformationalVersion` is parsed twice (`OtlpEncoder.cs:23-27`, `InternalEvents.cs:122-127`).
- **Content-encoding normalization**: duplicated in `BodyCapture.cs:41-48` and `SpanRedaction.cs:202-214`.
- **Request attributes**: `SpanSnapshots.CopyAtRequestStart` re-reads method, path, query and user agent from `HttpContext`, although the `RequestEntry` built just before it already holds those values. Pass the entry in instead.

### S9. Some tests check wiring instead of behavior

- **Location**: `ExportWorkerTests.cs:101-112`, `ApitallyExtensionsTests.cs:81-94`
- **Problem**:
  - `ServerExportIntervalIsClamped` asserts the internal `Interval` value, not the schedule that actually results from it.
  - `RepeatedCallsRegisterServicesOnce` counts DI descriptors. It misses the logger provider and the tracing callback, which is the registration that actually needs the guard.
- **Recommendation**:
  - For the interval, advance the fake clock and assert when POSTs arrive.
  - For repeated registration, call `AddApitally()` twice on a real host and assert exactly one SERVER span and one copy of each log.

### S10. Style nits

- **`ApitallyLogger.Log`**: it calls `IsEnabled` and then reads the same state again. Merge the checks into one pattern expression.
- **`GetOrCreate`**: `resource: null` also means "not seen by the span processor". Use an explicit parameter for that.
- **`OtlpEncoder.EncodeRequests`**: it takes both a `signalName` string and an `append` lambda. Pass `(spool, signal)` instead.
- **`ExecutionContext.SuppressFlow()`**: applied twice (`TelemetryRuntime.cs:102`, `ExportWorker.cs:44`). Keep only the outer one.
- **`ExportWorker` constructor**: an explicit constructor that only assigns fields, while the rest of the codebase uses primary constructors.
- **Duplicated comment**: the same comment appears twice (`ApitallyMiddleware.cs:82`, `ValidationCapture.cs:46`). Keep it in one place.

## Design questions raised

These findings challenge decisions recorded in design.md. They are listed separately because they need a product decision, not just a code fix.

1. **`APITALLY_*` environment variables lose to `appsettings.json`** (design.md §3, precedence). .NET developers expect environment variables to override JSON configuration. With the current order, committing `"Env": "prod"` in `appsettings.json` and overriding it per deployment with `APITALLY_ENV=staging` silently has no effect; only `Apitally__Env` works. Binding the section first and then applying the `APITALLY_*` fallbacks keeps the shared rule (code callbacks still win) and matches .NET convention. Recommendation: change the order. If the order stays, document it prominently.
2. **`Env` defaults to `dev` and ignores `IHostEnvironment`**. A production application with `ASPNETCORE_ENVIRONMENT=Production` reports as `dev`. Sentry and Application Insights default to the host environment name. Recommendation: keep `dev`, since it is the shared cross-SDK default, but state in the README that `Env` does not follow `ASPNETCORE_ENVIRONMENT`.
3. **The fallback sampler records requests that Apitally will discard**. When `SampleRate < 1` and there is no `SampleOnRequest` callback, the owned sampler could apply the same trace-ID sampling test at the hosting activity and return `Drop`. That avoids full instrumentation cost, and avoids sending a sampled `traceparent` downstream, for about 90% of requests at a rate of 0.1. Metrics are unaffected. This only changes the Apitally-owned provider.
4. **NDJSON redaction and numeric-value redaction** (D2 and the spec notes). Both need a change to the shared spec across all SDKs.
