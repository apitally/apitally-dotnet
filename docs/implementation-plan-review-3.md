# Implementation plan review, round 3

Date: 2026-09-28. Status: Resolved. All findings are decided and folded into the plan and design. Production code is unchanged.

## Assessment

This round asked two narrow questions of the design and plan:

1. Where does machinery exist only to satisfy a guarantee we do not need?
2. Which proposed tests do not verify behavior of our own code that users care about?

The answer to the first is a handful of shutdown and ownership rules that make rare races exact, plus one confirmed product feature, late-telemetry retention, that is the largest single piece of state in the request model. The answer to the second is mostly tests of .NET, OTel or already-relaxed guarantees that are still named in the test lists and acceptance gates.

A guarantee counted as needed when at least one of these held: the shared spec or shared design requires it at MUST level or as a wire contract; it protects captured payload privacy; it prevents realistic harm to the host application (crash, blocked requests, unbounded memory, changed user telemetry, hung shutdown); or without it users would see noticeably wrong data in Apitally. A test counted as valuable when it would catch a realistic bug in Apitally code with one of those consequences.

## Baseline and method

| Source | Reviewed revision |
| --- | --- |
| .NET repository | `781d1f8964ba0207cca88f132b80e00bbd00a80f`, branch `v1` |
| Design | `docs/design.md`, SHA-256 `1f051b6b8d3ed9ec98273786cc8e43294a4126e1ca2035c96aede248ae29e3e0` |
| Implementation plan | `docs/implementation-plan.md`, SHA-256 `2d612484d050df985d320200e95ea18b3843d05f0bd8f8f01e9c8bd823ad6953` |
| Shared specification/design | Cloud `6e480ace5da22fae426baddffaa84435ce007e46` |
| OpenTelemetry .NET | 1.19.0, source commit `dac1573ece52e8c275c3db5282bc57e3d5eff5cf` |
| Google.Protobuf | `main` branch source at review time |

Two read-only subagents reviewed the documents independently, one per question. The parent verified every retained finding against the documents, the shared contracts and upstream source, rejected or reshaped candidates that did not hold, and added findings of its own. No builds, tests or experiments were run.

## Findings

| ID | Priority | Finding | Needs decision |
| --- | --- | --- | --- |
| G1 | High | Late-telemetry retention is not needed in ASP.NET Core | Decided: drop |
| G2 | Medium | The shutdown admission lock guarantees exact drain of racing entries | Decided: remove |
| G3 | Medium | Exporter "abandonment" on host cancellation adds state for no user benefit | Decided: remove |
| G4 | Low | Unicode-scalar truncation goes beyond the shared "characters" rule | Decided: adopt |
| G5 | Low | Body-mask output is copied defensively | Decided: adopt |
| G6 | Low | The request-stage span snapshot is built even without `SampleOnRequest` | Decided: adopt |
| T1 | Medium | Configuration tests verify .NET Options behavior | Decided: adopt |
| T2 | Medium | "Full value normalization" invites a converter test suite | Decided: adopt |
| T3 | Medium | Stage 3 shutdown tests target stock and relaxed behavior | Decided: adopt |
| T4 | Medium | Unbounded performance recordings are release acceptance gates | Decided: adopt |
| T5 | Low | Remaining wording targets relaxed log isolation and physical proxy testing | Decided: adopt |

### G1. Late-telemetry retention is optional

**Where:** plan section 5, lines 250-254; design summary line 47, section 6 and decision table line 642.

After release, the plan moves each request's span IDs into a dictionary plus FIFO queue of up to 10,000 IDs, with shared cumulative counters, so that spans and logs ending after the request is released can still inherit its decision and export. This is a separate "completed retention" model alongside active tracking, with its own eviction rules, cutoff clearing and tests.

The shared design says late telemetry "can then export without buffering" ([shared design line 149](../../cloud/docs/sdks/design.md)); it is not MUST-level. The wire contract only says telemetry without a reachable SERVER span is never surfaced ([spec section 6.5](../../cloud/docs/sdks/spec.md)). Dropping late telemetry is not a privacy, host-safety or wire issue.

**Realistic scenario:** work started inside a request finishes after the response, for example a fire-and-forget `Task.Run`, a background queue hand-off that keeps `Activity.Current`, or a streaming response whose child spans end after SERVER. Without the cache these late spans and logs are dropped; the request's SERVER span and in-request telemetry are unaffected.

**Trade-off:** this reverses a decision confirmed two rounds ago, and the JavaScript SDK keeps late telemetry. Applications that routinely finish work after the response would see less detail in .NET than in JavaScript.

**Recommendation (decision required):** drop late-telemetry retention. Release removes the request's associations; late telemetry follows the existing "missing association drops locally" rule. This deletes the FIFO cache, cumulative cross-release counters (per-request caps remain), cutoff clearing of late associations, and the 10,000-ID measurement gate.

**Research follow-up:** in ASP.NET Core, Kestrel finishes the response, runs `OnCompleted` callbacks and writes the "Request finished" log before stopping the SERVER activity, so streaming and end-of-request telemetry arrive before release. Sentry.AspNetCore captures before activity end; Python keeps Sentry event IDs in a separate map. JavaScript added its cache for late spans and logs generally; Python has none for children started after release. Correction: shared spec section 6.5 says descendants "MUST be exported", which is stricter than the shared design's "can"; the spec wording needs clarification.

**Decision (2026-09-28):** drop late-telemetry retention. Recorded in the plan and design only; no user-facing documentation. Shared spec section 6.5 and the shared design were then updated in the cloud repository to make telemetry arriving after release best effort. Folded into [plan section 5](implementation-plan.md#one-request-state) and design section 6.

### G2. Remove the shutdown admission lock

**Where:** plan section 10, line 348.

The plan protects batch admission with a lock and closed flag so every in-flight submission finishes before terminal drain. The guarantee is exactness for an entry submitted concurrently with shutdown. The cutoff already detaches the tracing and logging adapters, so no new producers start. A racing entry is either drained or lost, which the shared best-effort shutdown contract permits. Submitting after stock `Shutdown` is safe: `TryExport` buffers the item and `TriggerExport` swallows `ObjectDisposedException` ([source](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Internal/BatchExportThreadWorker.cs#L53-L64)).

**Recommendation:** delete the admission lock and closed flag. Adapter detachment at cutoff is the admission boundary. Round 2 kept this lock because of the in-flight guarantee; under the current "no unneeded guarantees" rule that reason no longer holds.

**Decision (2026-09-28):** remove the admission lock and closed flag. Folded into [plan section 10](implementation-plan.md#stock-intake-and-safe-spool-closure).

### G3. Remove exporter abandonment on host cancellation

**Where:** plan section 10, shutdown, line 399.

On host cancellation the plan marks every exporter, including metrics, "abandoned": pending payloads are discarded and later appends and sends are rejected. The host has already stopped waiting and further POSTs are already gated by the token (step 5). Abandonment only makes the background cleanup throw away local encoding work sooner. Orphaned spool files are not sent by later processes ([shared design line 280](../../cloud/docs/sdks/design.md)), so nothing is saved either way.

**Recommendation:** delete the abandoned state. Cancellation ends the host's wait and prevents new POSTs; drain, seal and disposal finish in the background cleanup task as already planned.

**Decision (2026-09-28):** remove the abandoned state. Folded into [plan section 10](implementation-plan.md#shutdown).

### G4. Use a plain UTF-16 hard cut for log truncation

**Where:** plan section 6, line 270, and test list; design lines 430 and 434.

The plan counts Unicode scalar values and never splits a surrogate pair, and asks for wire-level fixtures before documenting it. The shared rule is "truncated to 2,048 characters (hard cut, no marker)" ([shared design line 242](../../cloud/docs/sdks/design.md)). JavaScript cuts UTF-16 units with `slice` ([logRecordProcessor.ts](../../apitally-js/src/logRecordProcessor.ts)); Python cuts code points. A lone surrogate is harmless on the wire: .NET Google.Protobuf writes strings with `Encoding.UTF8`, which replaces invalid sequences instead of throwing ([source](https://github.com/protocolbuffers/protobuf/blob/main/csharp/src/Google.Protobuf/WritingPrimitives.cs)).

**Recommendation:** truncate to 2,048 UTF-16 code units, matching JavaScript and `string.Length`. Delete the Unicode fixtures and the open truncation question.

### G5. Do not copy body-mask output

**Where:** plan section 6, line 282; design section 7.

"Copy callback output before retaining it" defends against a callback that returns a shared buffer and mutates it later. The returned array is consumed immediately in the same worker step (parse, redact, serialize). This is the same kind of callback-misuse defense removed for span and log snapshots.

**Recommendation:** use the returned array directly and retain only the serialized result.

### G6. Build the request-stage snapshot only when needed

**Where:** plan section 5, line 243; section 6.

The request-stage `SpanSnapshot` exists only as input to `SampleOnRequest`, but the plan populates it at every SERVER start. The final snapshot is built at completion regardless.

**Recommendation:** build the request-stage snapshot only when `SampleOnRequest` is configured.

**Decision (2026-09-28):** G4, G5 and G6 adopted as recommended and folded into plan sections 5 and 6 and design sections 7 and 9.

### T1. Delete configuration tests that verify .NET Options behavior

**Where:** plan section 12 setup bullet ("direct `Configure<ApitallyOptions>` ordering, frozen configuration"); stage 1 acceptance ("mutation isolation").

Relative ordering of `Configure` and `PostConfigure`, and `IOptions<T>` computing once, are .NET behavior. Our code's contribution is which registration method each step uses, which the precedence and callback-order tests already exercise.

**Recommendation:** delete the direct-`Configure` ordering, frozen-configuration and mutation-isolation tests. Keep precedence, repeated registration, callback order, invalid credentials and disable variables.

### T2. Narrow value-normalization coverage

**Where:** plan section 12 ("Full value normalization/ownership"); stage 1 acceptance ("callback/value semantics").

Round 2 decided the stock converter's edge cases are not a contract, but this wording still invites a test per CLR type and failure mode.

**Recommendation:** test the documented type mapping for the value kinds applications log and tag in practice (scalars, arrays, dictionaries, fallback to string), detachment of arrays before callbacks, and conversion of callback-added log values.

### T3. Trim stage 3 shutdown tests

**Where:** plan section 10, line 354 ("queue overflow, delayed entry into `Export`, encoding overlapping rotation, blocked append, failure, cancellation before cleanup scheduling and cancellation during export"); stage 3 acceptance; shutdown section, line 401.

- Queue overflow tests stock `BatchExportProcessor` dropping.
- Delayed entry into `Export` and near-cutoff completion test the exactness removed by G2.
- "No collection or append during subsequent provider disposal" tests the OTel metric reader's one-shot shutdown guard.
- Cancellation tests of abandonment go with G3.

**Recommendation:** keep the tests that catch real spool bugs: encoding overlapping rotation and blocked append still produce complete, decodable files; a write failure discards only the current file; shutdown with an already-canceled token returns promptly without throwing; an idle host still delivers the uptime gauge. Delete the rest.

### T4. Replace unconditional performance recordings with decision-driven measurements

**Where:** plan section 12, "Measured performance"; stages 6 and 7; release checklist.

"Record p50/p95 latency, allocations/GC, CPU, RSS and collection/export times" has no threshold, baseline or decision attached. As a release gate it produces noisy work without catching a defect.

**Recommendation:** keep measurements that select a constant (metric capacity, batch settings) and one bounded soak that shows retained memory is stable after traffic. Remove the generic latency/GC/CPU/RSS recordings from acceptance and the release checklist.

**Decision (2026-09-28):** keep only decision-driven measurements (metric capacity, batch settings, one memory soak). Folded into [plan section 12](implementation-plan.md#measured-performance) and stages 6 and 7.

### T5. Clean up remaining wording

- Stage 5 "log-mask normalization/isolation" and the design test list's "log state isolation": narrow to "other logging providers, scopes and exception objects are unchanged". Retained-record isolation is not tested.
- Stage 7 "physical delivery/proxy tests": proxy coverage is the single injected loopback-proxy binding test from section 10; physical delivery is the loopback OTLP receiver.

**Decision (2026-09-28):** T1, T2, T3 and T5 adopted as recommended and folded into plan sections 10, 11 and 12 and the design's test list.

## Keep these

| Item | Reason |
| --- | --- |
| Two-completion release and once-only finalization under a short request lock | Without it, spans export twice or response sampling misses final attributes. |
| Descendant, then SERVER, then log release order | Shared design wording, and it is the natural order of the existing buffers; no extra machinery. |
| Spool append/rotation lock and terminal `Shutdown(Timeout.Infinite)` joins | Without them a file can be sealed mid-append and the backend rejects the whole gzip file. |
| Cleanup task started before awaiting the host token | One `Task.Run` plus `WaitAsync(token)`; prevents a hung user callback from hanging host shutdown. |
| Recopying the final snapshot at completion | Merging into the request-stage copy would be more code than a second copy. |
| Per-request 1,000 span/log caps, consumer LRU, metric capacity | Shared requirements or unbounded-memory protection. |
| Test-project multi-targeting for every test | Splitting unit and integration suites by runtime adds project structure; pure unit tests cost seconds per runtime. |
| "One request SERVER despite instrumentation reuse" | Catches duplicate SERVER spans in the user's backend caused by our registration. |
| Loopback OTLP receiver, real Kestrel transport tests, TestServer suppression | Catch wire-format, streaming/privacy and setup bugs in our code. |

## Candidates not retained

| Candidate | Disposition |
| --- | --- |
| Remove the strict descendant/SERVER/log release order | Rejected: it costs no machinery. |
| Enrich the request-stage snapshot in place instead of recopying | Rejected: merging is more complex than copying; reshaped as G6. |
| Run pure unit tests on net8 only | Rejected: saves CI seconds at the cost of test-project structure. |
| Detachable forwarding processor for external providers | Not retained: it is one nullable field and prevents a retained external provider from holding host resources. |
