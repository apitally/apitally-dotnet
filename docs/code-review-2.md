# Apitally .NET v1 code review, round 2

Date: 2026-09-29. Branch: `v1`. Reviewed commit: `0a91e00`.

## Summary

Eight production findings, one misleading registration-order test, and three redundant tests. All eight production findings were reproduced in isolated scratch projects. No production or test files were changed during the review.

The first review's decisions in [code-review.md](code-review.md) were treated as settled. Previously rejected findings are not repeated. M1 demonstrates a synthetic upper-bound limitation of P7, but no representative production scenario was established; it is deferred without SDK changes.

The public API, DI registration, options, and hosting approach are broadly idiomatic .NET after round one. No substantial abstraction was identified that should simply be removed. Most useful simplifications are local allocation reductions and test cleanup.

## Method and scope

Six general-purpose reviewers covered:

1. Request lifecycle, tracing, sampling, association, and concurrency.
2. ASP.NET Core body observation, validation, endpoint behavior, and related tests.
3. Export scheduling, HTTP delivery, encoding, persistence, and shutdown.
4. Telemetry mapping, metrics, logging, redaction, and protocol semantics.
5. Hosting, configuration, public API, packaging, CI, documentation, and test infrastructure.
6. Test duplication, implementation-detail assertions, and unnecessary support code.

The review covered production code, tests, build/package configuration, and relevant documentation. Experimental POCs received a light assessment for separation from the supported package, not an exhaustive correctness review.

Findings were checked against the current source, [design.md](design.md), and the shared SDK specification where relevant. Candidate findings were consolidated and the production reproductions were rerun during verification. Speculative concerns and settled first-round decisions were excluded.

### Verification baseline

- `dotnet build Apitally.sln -warnaserror`: passed with zero warnings and errors.
- .NET 8.0.13: 165/165 tests passed.
- .NET 9.0.2: 165/165 tests passed.
- .NET 10.0.9: 166/166 tests passed.
- Release `dotnet pack -warnaserror`: succeeded, including the symbols package.
- Package contents include the assembly, XML documentation, README, and icon; the symbols package contains the PDB.
- Vendored protobuf schema checksums all passed.

The existing test suite passes. Additional scratch tests expose the findings below; those tests were not added to the repository. Live ingestion, publishing, Windows-specific hosting, and Native AOT were not validated.

### Severity

- **High:** unbounded resource growth in a realistic supported workload.
- **Medium:** missing or incorrect telemetry in a supported setup.
- **Low:** narrower correctness issues, avoidable resource costs, or local quality improvements.

## Priority summary

| ID | Severity | Finding |
| --- | --- | --- |
| R1 | High | Long-lived requests retain unbounded activity associations |
| M1 | Low, deferred | Synthetic metrics collections can exceed ingestion limits |
| M2 | Medium | Concurrent collection separates related metric observations |
| B1 | Medium | Pipe capture ignores content types set in `OnStarting` |
| E1 | Low | Memory spool retains two compressed buffers |
| H1 | Low | Failed pipeline configuration leaks the owned tracing provider |
| B2 | Low | Large native file responses export an unexpected body marker |
| V1 | Low | Adding `CancellationToken` loses MVC validation-source attribution |

R1 is implemented. M1 is deferred as a synthetic upper-bound limitation. M2 needs a measured design decision: synchronizing collection must not introduce excessive request latency. The remaining low-severity findings have small, localized recommendations.

## Production findings

### R1. Long-lived requests retain unbounded activity associations

- **Severity:** High.
- **Verification:** Reproduced on .NET 10.0.9.
- **Location:** [RequestState.cs:175-186](../src/Apitally/Requests/RequestState.cs#L175), [ApitallySpanProcessor.cs:55-65](../src/Apitally/Tracing/ApitallySpanProcessor.cs#L55). Cleanup occurs at [RequestState.cs:280-286](../src/Apitally/Requests/RequestState.cs#L280).

Every recorded descendant adds an association and a cleanup key. Neither is removed when the descendant ends. The 1,000-span limit bounds retained snapshots, not these collections.

A realistic trigger is an SSE endpoint that remains connected for hours while performing instrumented polling, outgoing requests, or per-update work. Memory grows with the connection's activity history, even after the trace buffer is full.

A real Kestrel SSE reproduction observed:

| Completed children | Associations | Cleanup keys | Snapshots normalized |
| ---: | ---: | ---: | ---: |
| 10,000 | 10,001 | 10,001 | 10,000 |
| 100,000 | 100,001 | 100,001 | 100,000 |

Only 1,001 spans were exported. Children discarded by the buffer cap still incurred snapshot normalization and allocation.

This is growth during an unfinished request, not a leak after request completion: the associations clear when the response closes. The reproduction accelerated activity generation; a multi-hour RSS profile was not measured.

**Decision:** Accepted option 1: retain the SERVER association plus the first 1,000 child operations started. Drop later children and logs emitted under those untracked children. Request metrics, the SERVER span, and logs under retained associations remain unaffected. This intentionally changes child selection from the first 1,000 completed children to the first 1,000 started. Retain accepted associations until request finalization so explicit-parent linkage continues to work within the bound.

**Test scope decision:** Keep regression coverage for bounded associations, start-order selection, and log behavior. Exercise only 1,001 children to cross the limit; omit the unrelated request-metrics assertion. The trimmed test passes on .NET 10; formatting and diff checks pass.

**Implementation:** Applied. `RequestState.TryAssociate` now bounds associations to the SERVER plus 1,000 descendants; rejected children never reach snapshot construction. Updated the existing limit test rather than adding a duplicate: it exercises 1,001 started children, holds the first open until last, checks association rejection, and verifies retained child, dropped child, and SERVER log behavior. Updated the design's retention policy. Warning-as-error build and formatting checks pass; full suites pass on .NET 8 (165), .NET 9 (165), and .NET 10 (166).

### M1. Synthetic metrics collections can exceed ingestion limits

- **Severity:** Low, deferred.
- **Verification:** Synthetic capacity stress test using the actual `ApitallyMetrics` implementation; no representative production workload or live ingestion test.
- **Location:** [ApitallyMetrics.cs:134-139](../src/Apitally/Metrics/ApitallyMetrics.cs#L134), [TelemetrySpool.cs:51-70](../src/Apitally/Export/TelemetrySpool.cs#L51).
- **Prior review:** Establishes that P7's cardinality limit is not an absolute byte-size guarantee, rather than establishing a practical failure requiring a new fix.

The first review's fix appends each metrics collection whole so the server can join duration and body-size histograms. The spool rotates an existing file when the next append would exceed its ordinary limit, but permits a single oversized metrics append into a new file.

The reproduction called `RecordRequest` directly, without HTTP traffic, and collected once after:

- 10,000 distinct UUID consumer identifiers, with method, route, status, and scheme otherwise identical. The consumer `group` property was not involved.
- An artificial route consisting of `/api/v1/` followed by repeated characters.
- Two observations per consumer: one with a 1 ms duration and 100-byte request and response, another with a 30-second duration and 1 MB request and response.

| Route length | Uncompressed bytes | Compressed bytes |
| ---: | ---: | ---: |
| 20 characters | 11,240,932 | 894,478 |
| 100 characters | 13,640,932 | 895,917 |
| 210 characters | 17,030,932 | 931,812 |

Attributes are repeated across three histograms, and the deliberately wide measurement ranges expand their bucket data. Actual request and response bodies are not part of this payload.

The 210-character case exceeds the ingester's **16 MiB decompression cap** in [otlp_utils.py:79-91](../../cloud/apitally_cloud/ingester/otlp_utils.py#L79). This is an application-imposed gzip-bomb guard, not an OTLP requirement. The HTTP handler queues compressed bytes before returning success; the asynchronous ingester skips an over-cap payload, including its process gauges.

The test combines maximum consumer cardinality, a very long route, and widely separated measurements for every consumer in one collection. It proves that such input can exceed the cap, but does not establish that the combination occurs in a realistic deployment. The original medium severity and recommendation to add splitting overstated the evidence.

**Decision:** Defer as a low-priority upper-bound limitation. Leave the SDK and backend unchanged. Reconsider only if a credible production scenario establishes that the additional splitting logic or a backend-cap change is warranted.

### M2. Concurrent collection separates related metric observations

- **Severity:** Medium.
- **Verification:** Reproduced on .NET 10.0.9 with deterministic and concurrent probes.
- **Location:** [ApitallyMetrics.cs:100-107](../src/Apitally/Metrics/ApitallyMetrics.cs#L100).

A request's duration and two body sizes are recorded separately. Each histogram is thread-safe, but the complete recording operation is not atomic relative to collection.

Collection can occur after duration recording and before size recording. For a consumer/route/status combination absent from the next collection, ingestion drops the orphan size points. Frequently active combinations can instead have sizes attributed to a different collection interval. Ingestion requires the matching duration point in [otlp_metrics.py:252-268](../../cloud/apitally_cloud/ingester/otlp_metrics.py#L252).

The deterministic reproduction placed duration in collection 1 and both sizes in collection 2. A concurrent probe without interception hooks recorded 9,000 requests and exported all 9,000 duration points, but produced 108 orphan request-size points and 256 orphan response-size points across 56 accelerated collections. Those numbers demonstrate the race, not a production loss-rate estimate.

Appending all mapped histograms in one payload does not fix measurements already separated during collection.

**Recommendation:** Ensure related observations stay together across ordinary and final collection. Assess contention before choosing synchronization: a global lock around collection could add request latency. This deserves a measured design decision rather than an unconditional locking change.

### B1. Pipe capture ignores content types set in `OnStarting`

- **Severity:** Medium.
- **Verification:** Reproduced on .NET 8.0.13, 9.0.2, and 10.0.9.
- **Location:** [ObservedBodyFeatures.cs:85-95](../src/Apitally/AspNetCore/ObservedBodyFeatures.cs#L85), [BodyCapture.cs:131-139](../src/Apitally/AspNetCore/BodyCapture.cs#L131).

`BodyWriter.Advance()` calls `Stage()`, which permanently caches capture eligibility before `OnStarting` finalizes response headers.

When an `OnStarting` callback sets `ContentType = "text/plain"`, an initially unset content type causes an irreversible rejection. The client receives the complete response, but Apitally omits its body. The equivalent response written through `Response.Body.WriteAsync` is captured correctly because observation occurs after the write starts the response.

**Recommendation:** Account for response-header finalization when deciding pipe-capture eligibility. Preserve bounded staging, streaming, and backpressure. Add a paired stream/pipe regression test using the same `OnStarting` callback.

### E1. Memory spool retains two compressed buffers

- **Severity:** Low.
- **Verification:** Reproduced.
- **Location:** [SpoolFile.cs:91-95](../src/Apitally/Export/SpoolFile.cs#L91).

`Close()` copies the `MemoryStream` into `closedMemory`, then disposes the stream but retains its readonly reference. Disposing a `MemoryStream` does not release its backing array.

A 3 MB incompressible payload retained:

- 3,000,933 bytes in `closedMemory`.
- 4,194,304 bytes in the disposed stream's backing buffer.

Three such queued files account for about 9 MB in spool bookkeeping while retaining about 21.6 MB of compressed buffers. This is unnecessary overhead in memory fallback, such as a read-only container during an export outage.

**Recommendation:** Release the stream reference after sealing, or retain only one buffer representation. No additional lifecycle abstraction is needed.

### H1. Failed pipeline configuration leaks the owned tracing provider

- **Severity:** Low.
- **Verification:** Reproduced on .NET 8.0.13, 9.0.2, and 10.0.9; also verified using the packed package on .NET 10.
- **Location:** [TelemetryRuntime.cs:184-189](../src/Apitally/Hosting/TelemetryRuntime.cs#L184), [ApitallyStartupFilter.cs:18](../src/Apitally/Hosting/ApitallyStartupFilter.cs#L18).

`Prepare()` creates the owned tracer provider and enters `Prepared`. If application pipeline configuration then throws, `Activate()` never runs. Shutdown and disposal return immediately unless the runtime is `Active`, so disposing the failed host leaves that provider alive.

A realistic example is a Generic Host startup configuration error such as `UseEndpoints` without `UseRouting`. A test runner or embedding process catches the startup exception and disposes the host, but its process-wide activity listeners remain registered.

The reproduction observed:

- Before startup: `ActivitySource.HasListeners() == false`.
- After failed startup and host disposal: `HasListeners() == true`.
- A new hosting-named activity still had `Recorded == true`.

This is distinct from the existing failed-server-bind test: that path reaches `Active` and already cleans up correctly.

**Recommendation:** Dispose owned tracing resources when the runtime is `Prepared`, without accessing the not-yet-created registry or worker. Add one failed-pipeline-configuration disposal test.

### B2. Large native file responses export an unexpected body marker

- **Severity:** Low.
- **Verification:** Reproduced on .NET 8.0.13, 9.0.2, and 10.0.9.
- **Location:** [BodyCapture.cs:106-113](../src/Apitally/AspNetCore/BodyCapture.cs#L106).

Native file delivery deliberately bypasses body capture, but `GetBody()` evaluates capture eligibility and returns `[BODY_TOO_LARGE]` before checking `IsBypassed`.

With response-body capture enabled, `Results.File(path, "text/plain")` therefore exports a body attribute when the file exceeds 50,000 bytes, despite the documented complete omission of native-file bodies. The existing native-file test covers only small files.

**Recommendation:** Return `null` for `IsBypassed` before initializing eligibility or returning the oversized sentinel. This also preserves omission for an oversized stream prefix followed by a native file send.

### V1. Adding `CancellationToken` loses MVC validation-source attribution

- **Severity:** Low.
- **Verification:** Reproduced on .NET 10.0.9.
- **Location:** [ValidationCapture.cs:109-119](../src/Apitally/AspNetCore/ValidationCapture.cs#L109).

The body-source fallback checks the total action-parameter count. Adding a normal cancellation parameter changes property-validation errors from source `"body"` to `""`:

```csharp
Create(ItemInput input, CancellationToken cancellationToken)
```

The error itself is still captured. The loss is avoidable because `CancellationToken` cannot be the source of the body property's validation failure. This is an idiomatic ASP.NET Core action signature, not an unusual application workaround.

**Recommendation:** Exclude special/service-bound parameters when determining whether the body is the sole possible binding source. Preserve conservative attribution when multiple actual request-binding sources could own the field.

## Test findings

### T1. The registration-order theory executes Apitally-first twice

- **Location:** [TracingIntegrationTests.cs:112](../tests/Apitally.Tests/Tracing/TracingIntegrationTests.cs#L112).
- **Test:** `ApplicationProviderKeepsItsPipelineAndSharesServerSpans`.

The theory claims to exercise both registration orders. However, [Program.cs:13-15](../tests/Apitally.TestApp/Program.cs#L13) calls `AddApitally()` before invoking the test's configuration callback.

Consequently, `isApplicationFirst = true` registers application tracing before only the second `AddApitally()` call. Production tracing registration is guarded by the first call, so both rows exercise the same initial registration order.

**Recommendation:** Correct the setup and retain both theory rows. This is duplicate execution and missing coverage, not evidence of a production registration defect.

### T2. Delete three tests whose behavior is already covered

| Delete | Retain | Why coverage remains |
| --- | --- | --- |
| `RequestBodyReadThroughPipeReaderIsCaptured`, [ApitallyMiddlewareTests.cs:57](../tests/Apitally.Tests/AspNetCore/ApitallyMiddlewareTests.cs#L57) | `ChunkedRequestBodyLargerThanInitialBufferIsCaptured`, same file, line 70 | Both POST chunked JSON to `/read-pipe` and assert the exact exported body and byte count. The larger test exercises the same reader/completion path plus buffer growth. |
| `WebSocketRequestsAreExcluded`, [RequestSamplingTests.cs:37](../tests/Apitally.Tests/Requests/RequestSamplingTests.cs#L37) | `WebSocketRequestsProduceNoRequestTelemetry`, [RequestTelemetryTests.cs:115](../tests/Apitally.Tests/Integration/RequestTelemetryTests.cs#L115) | The unit test only checks the `isWebSocket` short-circuit. The integration test reaches it through a real upgrade and verifies no spans, consumer updates, or request metrics. |
| `EndpointOverrideIsReadFromEnvironment`, [RuntimeConfigurationTests.cs:135](../tests/Apitally.Tests/Hosting/RuntimeConfigurationTests.cs#L135) | `StartedHostDeliversStartupEventAndProcessMetrics`, [TelemetryRuntimeTests.cs:21](../tests/Apitally.Tests/Hosting/TelemetryRuntimeTests.cs#L21) | The integration test requires actual delivery to the endpoint supplied through the same environment variable by `ApplicationHost`. Removing endpoint resolution would already break that behavioral test. |

These recommendations remove three test methods without removing distinct behavior coverage. They are not a general recommendation to delete unit tests whenever integration tests exist.

### T3. Remove resource-group ordering assumptions from mapper tests

- **Location:** [OtlpTraceMapperTests.cs:99-124](../tests/Apitally.Tests/Export/OtlpTraceMapperTests.cs#L99).
- **Test:** `GroupsSpansByResourceAndScope`.

Assertions select resource groups by positions zero and one, although resource-group order is not the behavior under test.

**Recommendation:** Select groups by resource attributes. Retain the test because its multiple-resource case has useful coverage not replaced by the integration suite. Its current input also has only one scope per resource, so its name overstates scope-grouping coverage.

### Tests to retain

The suggested consolidation of `StartupHostDeliversStartupEvent` with `StartupHostExportsRequestSpans` was not adopted in the consolidated review. The former tests a Startup-style host without any requests. Making a request before asserting startup telemetry would weaken its traffic-independent activation coverage.

The main test-support infrastructure is justified. Broad deletion of mapper, lifecycle, or unit-level boundary tests is not warranted.

## Simplicity and documentation cleanup

### Avoid an allocation for consumer updates without attributes

[ConsumerUpdates.cs:58](../src/Apitally/Requests/ConsumerUpdates.cs#L58) allocates `new Dictionary<string, string?>()` when `attributes` is null, including ordinary `SetConsumer(identifier)` calls.

Return the consumer after applying name/group updates when attributes are null, then iterate the supplied attributes normally. This removes an allocation without introducing a helper or abstraction.

### Remove unused or duplicated test support

- Delete unused `OtlpDecoding.Concat` at [OtlpDecoding.cs:45](../tests/Apitally.Tests/Support/OtlpDecoding.cs#L45).
- Remove unused `name`, `parentSpanId`, and `traceId` parameters from [TestSpans.Create](../tests/Apitally.Tests/Support/TestSpans.cs#L11).
- Replace the local `Hex` helpers in [OtlpTraceMapperTests.cs:127](../tests/Apitally.Tests/Export/OtlpTraceMapperTests.cs#L127) and [OtlpLogMapperTests.cs:106](../tests/Apitally.Tests/Export/OtlpLogMapperTests.cs#L106) with the existing helper in [Spans.cs:10](../tests/Apitally.Tests/Support/Spans.cs#L10).

### Remove stale log-attribute and scope descriptions

[design.md:417-421](design.md#L417) still describes values added by a log callback and log-entry/inner-scope/outer-scope precedence. Round one removed log attributes and scope capture; the current `LogRecordSnapshot` callback can modify only the body.

Update those passages to describe the current span-value behavior and remove the obsolete logging claims.

## Local reproduction artifacts

These paths are temporary verification artifacts, not committed tests or durable project documentation:

| Findings | Artifact |
| --- | --- |
| R1 | `/tmp/apitally-request-review-sKPuzg/tests/Apitally.Tests/Requests/SecondReviewTests.cs` |
| M1, E1 | `/tmp/apitally-export-review/Program.cs` |
| M2 | `/tmp/apitally-review-export.0PitnT/tests/Apitally.Tests/Export/SecondReviewTests.cs` |
| B1, B2, V1 | `/tmp/apitally-second-aspnet-review/tests/Apitally.Tests/AspNetCore/SecondReviewTests.cs` |
| H1 | `/tmp/apitally-hosting-review/repro/Program.cs` |

On macOS, running the copied test projects from their canonical `/private/tmp` directories avoids inconsistent restore/build paths through the `/tmp` symlink.
