# Implementation plan review, round 4

Date: 2026-09-28. Status: Resolved. All findings are decided and folded into the plan and design. Production code is unchanged.

## Assessment

This round reviewed the plan after three rounds of edits, with four questions:

1. Is the plan still consistent with itself, the design and the shared contract?
2. What is still overengineered or not idiomatic for .NET?
3. Can the planned test code and machinery shrink without losing meaningful coverage?
4. What would surprise a senior .NET developer or lose their trust?

The plan is in good shape. The removals from rounds 2 and 3 landed cleanly: no removed mechanism is still required anywhere, and every local link and anchor resolves. One real gap was found, in privacy: spool files would be created readable by other local users on Linux, although both other SDKs create them owner-only. The other findings fall into three groups:

- consumer-update events are still missing from the design, a known gap that the plan defers;
- the same test coverage is listed in four places;
- a few leftover phrases and small simplifications.

A finding was kept only when it would change what gets built or tested, or when it is a privacy or trust issue that the documents leave unaddressed. Items the shared design mandates were not re-raised as simplifications.

## Baseline and method

| Source | Reviewed revision |
| --- | --- |
| .NET repository | `b476dc372a41ffee9d46e0d6e2698d4046743e99`, branch `v1` |
| Design | `docs/design.md`, SHA-256 `65b3c10719a538afd354ff34fd5a55617373e6d42d77e504b98ad5aa72ba2509` |
| Implementation plan | `docs/implementation-plan.md`, SHA-256 `9a3e0b2333d5f63f0e49677a6b1b8c4293f0e4f44479749e55156ef952aa1685` |
| Shared specification/design | Cloud `f98007ae4e63c08bbc3dab2533d1bc7df730626e` |
| Python SDK | `apitally-py` `43e3e10` |
| JavaScript SDK | `apitally-js` `9f6249e` |
| .NET runtime | `dotnet/runtime` `release/8.0` at `26d2fafe` |

Four read-only subagents reviewed the documents independently, one per question. The parent checked every retained finding against the documents, the shared contracts, the sibling SDKs and upstream source. Candidates that did not hold were rejected, overlapping ones were merged, and the parent added findings of its own. The claims are source-verified; nothing was reproduced. No builds, tests or experiments were run.

## Findings

| ID | Priority | Finding | Needs decision |
| --- | --- | --- | --- |
| P1 | High | Spool files are readable by other local users on Unix | Decided: owner-only files |
| C1 | Medium | Consumer-update events are planned but absent from the design | Decided: add to design |
| D1 | Medium | Release documentation omits operational facts senior developers look for | Decided: checklist removed |
| X1 | Medium | Test coverage is specified in four places | Decided: single list |
| C2 | Low | Leftover wording contradicts decided behavior | Applied |
| C3 | Low | Log records whose body is null or empty are exported, and the server always drops them | Decided: drop locally |
| S1 | Low | Two batch processor subclasses where one generic subclass suffices | Decided: adopt |
| S2 | Low | The v0 reference is preserved in a local worktree instead of a git ref | Decided: adopt |
| X2 | Low | Packed-package consumers on three runtimes | Decided: none in this repository |

### P1. Spool files are readable by other local users on Unix

**Where:** plan section 10, `TelemetrySpool` table (storage selection, orphans); design section 10, filesystem fallback.

The plan writes spool files to temp storage but never says how they are created. The data in them has already been masked, but it still contains captured request and response headers, bodies, log messages and exception stack traces.

On Unix, a .NET `FileStream` creates files with mode `0666`, reduced by the process umask ([`SafeFileHandle.Unix.cs` `DefaultCreateMode`](https://github.com/dotnet/runtime/blob/release/8.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)). With the usual umask of `022`, that gives `0644`: any local user can read the file, and `/tmp` is shared across users.

Both sibling SDKs create these files owner-only:

- JavaScript opens spool and probe files with mode `0o600` (`apitally-js/src/spool.ts` lines 218 and 314).
- Python uses `tempfile.NamedTemporaryFile` (`apitally-py/apitally/shared/spool.py` line 37), which creates files as `0600`.

**Realistic scenario:** a shared VM or CI runner, or a container that other processes can inspect. Captured health-related payloads sit in world-readable files for up to an hour. This is a privacy regression compared with the other SDKs, and a compliance reviewer would flag it.

**Recommendation:**

- On non-Windows, create the probe and spool files with `FileStreamOptions { Mode = FileMode.CreateNew, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }`. The setter throws on Windows, where `%TEMP%` is already per-user.
- Add one Unix-only test asserting the mode.
- In the release documentation, state that masked telemetry is written to `Path.GetTempPath()`, and that a read-only filesystem falls back to 10 MB of memory (see D1).

**Decision (2026-09-28):** create probe and spool files owner-only (`0600`) on non-Windows. No dedicated test and no release documentation. Folded into [plan section 10](implementation-plan.md#encoding-and-storage) and design section 10.

### C1. Consumer-update events are planned but absent from the design

**Where:**

- Plan line 29 says to "explicitly reconcile this addition with the .NET design" at implementation approval.
- The plan's public API (lines 191-195), section 8 (lines 325-327), stage 1 ("Reconcile the current shared consumer contract") and the release checklist ("Resolve the consumer-contract baseline") all refer to it.
- The design mentions consumers only as identity: helpers table line 595 and metrics line 521.

Spec section 9.3 requires `apitally.consumer.update` events, and the plan already specifies how to build them. Nothing is left to decide. Leaving the design silent means the plan implements a MUST-level feature that its governing document doesn't describe, and the gap is flagged as a pending decision in three places.

**Recommendation:** add consumer-update events to the design now: the `SetConsumer` signature, patch normalization and the 10,000-identifier change-detection cache. Then remove the "reconcile at approval" sentence, the stage 1 reconcile step and the checklist item from the plan.

**Decision (2026-09-28):** added a "Consumer updates" section to design section 9 and updated the `SetConsumer` helper row; removed the plan's reconcile paragraph, stage 1 step and checklist item. The stale "FIFO span-ID cache" clause (C2) was removed in the same edit.

### D1. Release documentation omits operational facts senior developers look for

**Where:** the release checklist documentation item, plan line 457; design section 13, migration contract, line 608.

The planned documentation covers setup, the log default, callbacks and masking. It leaves out several behaviours that a senior developer evaluating an observability SDK checks for, and that the design has already decided:

- **Callback execution context:**
  - `MaskLogRecord` runs synchronously on the application's logging thread (plan line 317). Body masks run later on the batch worker.
  - Callbacks must not block or do I/O, and must not keep the record after returning, because records are not copied (plan lines 268 and 319).
- **Configuration keys:** a table of `Apitally:*` and `APITALLY_*` keys, which `OTEL_*` fallbacks apply, and that `OTEL_EXPORTER_OTLP_*` does not configure Apitally delivery (design lines 163-181).
- **Disk writes:** the temp-storage location, owner-only files and the memory fallback (P1).
- **Native AOT and trimming:** unsupported and untested (design lines 25-29).
- **Shutdown:** delivery at shutdown is best effort, and cleanup can outlive the host's shutdown timeout (plan lines 399-401).

None of this changes behaviour. But a developer who finds any of these by reading the source, rather than the docs, is the trust loss the question asks about. The regulated-domain users in particular will ask about disk writes and masking threads.

**Recommendation:** extend the checklist item with these five points. Mirror them in the design's migration contract.

**Decision (2026-09-28):** remove the plan's release checklist entirely. Publishing is the product owner's decision, not a list an implementer ticks off, and every other checklist item already appears in the stage table, section 12 or the design's migration contract. README content is left to stage 7. No documentation topics are added.

### X1. Test coverage is specified in four places

**Where:**

1. Plan layout lines 128-164 pre-list 26 test files, roughly one per production type.
2. The stage table's completion-evidence column, lines 409-415.
3. Plan section 12, "Automated behavior", lines 427-436.
4. Design section 16, "Required behavioral coverage", lines 677-690.

These lists overlap heavily and have already drifted. Round 3 T5 had to edit the same wording in two of them, and C2 below finds more. The pre-listed test tree also works against the plan's own rule, "Add test files when the corresponding module has meaningful observable behavior". An implementer who follows the tree creates a test file per type.

No coverage depends on the duplication. Each behaviour needs to be stated once.

**Recommendation:**

- **Plan section 12:** make it the single coverage list.
- **Layout:** reduce the test tree to its folders plus `Support/`.
- **Stage table:** keep only the stage-specific exit gates (for example R4 file completeness), without restating section 12's scenarios.
- **Design:** replace the "Required behavioral coverage" list with one sentence of test strategy that points to the plan.

**Decision (2026-09-28):** adopted. The test tree lists folders only; the stage table drops its completion-evidence column in favor of one completion rule, with the public-API and packed-consumer checks moved into the stage 1 and 7 work; section 12 absorbed the few scenarios only the stage table named; the design's list is a pointer to plan section 12. The stage 5 "masks seeing exactly the exported values" wording (C2) went with the column.

### C2. Leftover wording contradicts decided behavior

Each item needs a one-line edit, with no behaviour change:

- **Plan line 327:** says `ConsumerUpdates` "is distinct from the FIFO span-ID cache", but that cache was removed in G1. Delete the clause.
- **Plan line 268:** "Copy from the `Activity` once for the request stage" reads as unconditional, which contradicts G6 (line 243). Add "when `SampleOnRequest` is configured".
- **"Exactly what will be exported":** plan line 270, stage 5's "masks seeing exactly the exported values" and design line 390 all say this. But truncation and conversion of callback-added values happen after the mask. Reword to say the mask sees the normalized values, and that truncation happens at encoding.
- **Plan line 333:** records the request histograms with no exclusions. The design (line 521) and spec section 7 skip `OPTIONS`, websockets and unmatched routes. Add the skip rule.
- **Plan line 448:** lists "no per-request background task" as verified by integration tests. A test for the absence of a task would pin an internal detail. Keep it as a design rule and remove it from the tested list. The tests that matter, that requests don't wait on body processing and that memory stays bounded, are already listed.

**Applied (2026-09-28):** all five edits, as wording only.

### C3. Log records whose body is null or empty are exported, and the server always drops them

**Where:** design line 396 ("`Body` is the message field for masking and export, including when it is null"); plan section 8, step 5.

The spec's LogRecord table says an empty body is dropped at ingest (spec section 8, `body` row). The SDK would still encode, spool and send records the server discards, including a record whose mask cleared `Body` but kept its attributes. The user expects those attributes to show up, and they never will.

**Recommendation:** drop an accepted application record locally when its post-mask `Body` is null or empty, and document it next to the mask's drop semantics. This is one condition. Whether it is worth stating at all is a judgment call. The alternative is to do nothing and let the server drop these records.

**Decision (2026-09-28):** drop locally. Folded into plan section 8 step 4 and design section 9.

### S1. One generic batch processor subclass

**Where:** plan line 344, "two small subclasses of `BatchExportProcessor<T>`"; layout line 106 already names one `BatchProcessor.cs`.

`BatchExportProcessor<T>` is abstract with a protected constructor, so a subclass is required. The span and log processors differ only in `T` and the exporter they receive.

**Recommendation:** one `internal sealed class ApitallyBatchProcessor<T> : BatchExportProcessor<T> where T : class`, instantiated twice. It keeps separate queues and exporters, and loses no behaviour.

### S2. Preserve the v0 reference with a git ref

**Where:** stage 1, line 409: "Preserve the v0 reference at `65e25ed...` in a detached sibling worktree"; line 417.

`65e25ed` is on `main`, and it is later than the newest tag, `v0.6.3`. A sibling worktree exists only on one machine, and it preserves nothing for other contributors or CI once `v1` replaces `main`.

**Recommendation:** push a `v0` branch (or a tag) at `65e25ed` before `v1` replaces `main`, and reference it by name.

### X2. One packed-package consumer

**Where:** stage 7 completion evidence, "clean packed-package consumers" (the release checklist that named net8/9/10 was removed under D1).

There is one `net8.0` package assembly. The runtime matrix already runs the project-referenced test application on 8, 9 and 10, which resolves the same package dependencies. Bugs that only show up after packing are independent of the target framework: missing assets, wrong nuspec dependencies, the public-API surface.

**Recommendation:** one clean packed consumer on the newest runtime. That also exercises the net8 assembly under a newer shared framework. Record the dependency floor as planned.

**Decision (2026-09-28):** S1 and S2 adopted. For X2, this repository has no packed-package tests at all; package-consumer validation belongs to the `../sdk-tests` harness, whose language adapters install the SDK into isolated variant environments. Folded into plan sections 10 and 11 and design section 17.

## Keep these

| Item | Reason |
| --- | --- |
| Single `AddApitally`, Options pattern, `IStartupFilter`, `IHostedLifecycleService`, stock `BatchExportProcessor<T>`, `TimeProvider`, `SocketsHttpHandler` | Idiomatic .NET; these reassure a senior reviewer. |
| Native `Activity`, `ActivityEvent`, `ActivityLink` and OTel `Resource` in `SpanSnapshot` | Familiar types, no parallel model. |
| Request-local lock and once-only finalization | Prevents duplicate export and response sampling with incomplete attributes. |
| Spool append/rotation lock and terminal `Shutdown(Timeout.Infinite)` | Keeps closed gzip files complete. |
| ASP.NET body and feature wrappers | No stock API observes consumed bytes while preserving native streaming. |
| Hand-written consumer LRU | Shared contract; .NET has no LRU type with these semantics. |
| Loopback OTLP receiver, real Kestrel tests, TestServer suppression, sibling harness smoke | Catch wire, streaming, setup and backend-ingestion bugs in our code. |

## Candidates not retained

| Candidate | Disposition |
| --- | --- |
| Remove the 0.1-0.5 s pause between sends | Rejected: shared design send loop (line 277) for fleet-wide recovery smoothing. |
| Replace "32 records per chunk" with a pure byte-bounded builder | Rejected: shared design names 32 as the reference (line 276); the plan already measures exact size. |
| Remove the prescribed production file tree | Rejected: a module map is useful in an implementation plan; the test tree is addressed by X1. |
| Copy callback records at the asynchronous handoff | Rejected: decided in round 2 (F7, Q7) and round 3 (G5); documenting ownership is covered by D1. |
| Resolve process-wide `ActivityListener` sampling promotion across hosts | Rejected: inherent to .NET `ActivitySource`, documented in design "Multiple hosts", and multi-host is outside v1. |
| Update the outdated README now | Rejected: stage 7 rewrites it before any v1 publish. |
| Document that activation precedes a successful bind | Rejected: decided in round 2 (F5); the runtime is disposed on bind failure. |
| Named delegate types instead of `Func<...>` | Rejected: OTel .NET instrumentation options use `Func`/`Action` properties (for example `Filter`). |
| Shutdown step 4 "while budget remains" gates terminal shutdown | Rejected: the sentence scopes only aggregate draining; step 3 states that cleanup is not cancelled. |
| Remove dedicated validation, endpoint-metadata and process-metrics tests as dependency tests | Rejected: they test our adapters; the plan already forbids asserting upstream facts. |
| Run only one hosting composition | Rejected: the plan already limits compositions to setup and lifecycle cases. |
