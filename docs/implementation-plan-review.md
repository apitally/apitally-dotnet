# Implementation plan review

Date: 2026-09-28. Status: All three findings are resolved in the implementation plan after approval. Implementation and validation remain pending; production code is unchanged.

## Assessment

The plan is broadly correct, coherent and feasible. Its architecture is appropriately small for the required behavior: one host-owned runtime, shared request state, stock batching and metric aggregation, a bounded spool and one delivery worker. The revised source/test layout and naming rules are consistent.

The approved resolutions establish unconditional cleanup ownership during canceled shutdown, use terminal metric collection before spool closure, and schedule build/test CI updates with the target-framework changes. These are incorporated into the plan without another architectural layer.

No material unnecessary divergence from the current Python/JavaScript SDKs was verified. Most additional .NET machinery addresses concrete framework or dependency behavior. Some cross-SDK review candidates were rejected after checking the approved adaptations and actual source.

## Baseline and method

Four read-only subagents reviewed shared-contract consistency, .NET/OTel feasibility, Python implementation alignment, and JavaScript implementation alignment. The parent verified retained findings against the plan, repository files, POC evidence and pinned upstream source. No builds, tests or new experiments were run; dependency behavior below is source-verified, not a newly reproduced production defect.

| Source | Reviewed revision |
| --- | --- |
| .NET repository | `23f4dd01855a817af076418bae64fce8c5663a98`, branch `v1` |
| Implementation plan | Untracked working-tree file; SHA-256 `327f55e1781358ab55fe6779c21fc1f5711d602df079ab3ca521175b470c23f8` |
| Python SDK | `43e3e1073a23d1c64bcc84bcc8fa0781494a0372`, branch `main` |
| JavaScript SDK | `9f6249e74b1ccf249a41125bcde089f95dcc2e61`, branch `main` |
| Shared specification/design | Cloud `9ff1af09eca4c959d2f65b4b2d9726245f629e3d` |
| Shared test harness | `420f328dca940f270155a2a8404351880cf98348` |
| OpenTelemetry .NET | Version 1.19.0, source commit `dac1573ece52e8c275c3db5282bc57e3d5eff5cf`, confirmed from the installed package's repository metadata |

Findings and line references below describe the original reviewed bytes above; approved resolutions describe the updated plan. Section links lead to the current plan. Explicit .NET adaptations and the approved scope boundaries in [the design](design.md) and [its review](design-review.md) take precedence over copying another SDK's implementation.

## Findings

| ID | Priority | Finding | Status | Implementation validation |
| --- | --- | --- | --- | --- |
| R1 | High | Cleanup ownership is missing when cancellation precedes shutdown-task creation; the terminal join also needs an explicit timeout choice | Resolved in plan | Stage 3 integrated lifetime tests |
| R2 | Medium | Final metric collection must terminate the reader before spool sealing | Resolved in plan | Stage 3 integrated final-export tests |
| R3 | Medium | The .NET 10 CI toolchain update is scheduled too late | Resolved in plan | Stage 1 build/test CI |

### R1. Establish cleanup ownership before any cancellable wait

**Plan:** [stock intake and spool closure](implementation-plan.md#stock-intake-and-safe-spool-closure), lines 338-346; [shutdown](implementation-plan.md#shutdown), lines 378-388.

The sequence makes request cutoff unconditional, but step 2 can stop at a canceled wait for the ordinary cycle before step 3 establishes the shutdown task. The cancellation paragraph then assigns eventual disposal to an *existing* shutdown task. Closing intake and detaching adapters do not themselves terminate stock batch workers.

**Realistic consequence:** a request exceeds the host's shutdown budget, so `StoppedAsync` receives an already-canceled token. An implementation following the numbered sequence can skip terminal cleanup, retain private workers/resources, or attempt ordinary disposal while an export is still running. The existing [transport lifecycle POC](../pocs/transport-lifecycle/README.md#ordering-and-lifecycle-observations) already records this canceled-token entry condition.

The statement that successful stock `Shutdown` proves a worker join also needs qualification. In pinned OTel, [thread-worker shutdown](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Internal/BatchExportThreadWorker.cs#L134-L168) returns true for `Shutdown(0)` without joining. A positive finite timeout can return false before completion, and the [processor's one-shot shutdown guard](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/BaseProcessor.cs#L128-L145) prevents retrying shutdown to obtain a later join. Standalone disposal is not a substitute.

**Smallest correction:**

- After the unconditional cutoff, always establish the one shutdown/cleanup task before any cancellable wait, including when the token is already canceled. Do not pass the host token as cancellation of task scheduling.
- That task owns serialization with the ordinary cycle, terminal worker shutdown and eventual owned-resource disposal.
- Use `Shutdown(Timeout.Infinite)` for the batch processors inside that task. Apply host cancellation to awaiting the task and to further delivery, not to the terminal join.
- On cancellation, mark exporters abandoned so pending payloads are discarded and subsequent appends/sends are skipped. Existing synchronous work may finish; resources remain alive until it does.

This makes the plan's existing deferred-disposal approach precise. It adds neither a second worker nor an extra export window. The host still returns when its wait is canceled, even if a synchronous callback has not returned.

**Acceptance:** extend the planned integrated shutdown test to cover cancellation before the cleanup task would otherwise be created, as well as cancellation during export. Verify cutoff, bounded host waiting, no new append/send after abandonment, and eventual termination/disposal after the gated synchronous operation returns. Test Apitally's behavior, not merely the dependency in isolation.

**Approved resolution:** honor the host's shutdown budget. The [shutdown sequence](implementation-plan.md#shutdown) establishes cleanup ownership before any cancellable wait and uses terminal infinite-timeout joins inside that task. Cleanup may outlive the host's wait without extending delivery; a callback that never returns may retain resources until process exit. Production validation remains in stage 3.

### R2. Make metric-reader shutdown the final collection

**Plan:** [native metrics](implementation-plan.md#9-native-metrics), lines 321-330; [shutdown](implementation-plan.md#shutdown), lines 384-386.

The plan collects final metrics, seals/sends the remaining spool files, and then disposes owned providers. It says disposal must not initiate another flush, but does not specify the metric-reader operation that enforces this.

For the non-periodic reader used in the [metrics POC](../pocs/encoding-metrics/MetricsProbe.cs#L402-L472), plain collection does not end its lifecycle:

- [MeterProviderSdk.Dispose](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/MeterProviderSdk.cs#L471-L498) invokes `Reader.Shutdown(5_000)` before disposing it.
- [BaseExportingMetricReader.OnShutdown](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/Reader/BaseExportingMetricReader.cs#L134-L145) performs another collection.
- [Synchronous metric export](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/Reader/BaseExportingMetricReader.cs#L80-L87) does not enforce that timeout around `Export`.

**Realistic consequence:** ordinary graceful shutdown can export metrics again after the final send. This is not dependent on a racing request: the uptime gauge deliberately keeps collections nonempty. Depending on disposal order, the extra export either creates an unsent file or reaches an already-closed spool, and can perform synchronous work outside the intended final phase.

**Smallest correction:** use the owned metric reader's terminal `Shutdown` as the final collection, rather than a standalone `Collect`, before sealing files. Perform it in the same cleanup task as the other terminal operations. The [reader's one-shot guard](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/Reader/MetricReader.cs#L381-L403) makes the later provider-disposal shutdown a no-op. Include the metric exporter in cancellation abandonment so cleanup cannot resume delivery after the host budget expires.

**Acceptance:** the integrated final-cycle test includes an idle process gauge, reaches the final receiver-visible metric payload, and then disposes the provider without new spool data or post-final export work. Keep an active metric-export cancellation case within the existing lifetime test coverage.

**Approved resolution:** use terminal metric-reader shutdown for the final collection, within R1's cleanup task and before spool sealing. Source verification is sufficient to adopt this correction; validate the production path through the existing stage-3 integration tests rather than requiring a separate pre-implementation experiment.

### R3. Move CI toolchain changes into the foundation stage

**Plan:** [project choices](implementation-plan.md#2-project-and-dependency-choices), lines 33-34; [implementation stages](implementation-plan.md#11-implementation-sequence-and-acceptance-gates), lines 392-402.

Stage 1 introduces `net8.0;net9.0;net10.0` test targets and requires them to compile. Updating CI is listed only in stage 7. The existing [test workflow](../.github/workflows/tests.yaml#L31-L51) explicitly installs SDKs only through .NET 9, then restores/builds the whole solution.

**Realistic consequence:** a runner with only the declared SDKs cannot restore/build the new .NET 10 targets during stages 1-6. Any success relying on an additional preinstalled runner SDK would not establish the intended toolchain. This conflicts with keeping the vertical slices buildable and validated throughout implementation.

**Smallest correction:** move the test workflow's supported SDK/runtime setup and build/test matrix into stage 1, alongside the project target/dependency changes. Release publishing changes and final package qualification can remain in stage 7.

**Acceptance:** stage 1 CI restores, builds and runs the available production-linked tests on the supported runtime matrix using explicitly installed toolchains. No new CI framework is needed.

**Approved resolution:** stage 1 includes the supported SDK/runtime installation and build/test matrix alongside project changes. Publishing-workflow changes and final package qualification remain in stage 7. This scheduling decision does not authorize a release.

## Simplicity and cross-SDK consistency

### Keep these choices

| Choice | Assessment |
| --- | --- |
| One production project; responsibility-based folders; mirrored tests | Clear ownership without extra assemblies or abstraction layers. The folder/file count alone is not evidence of overengineering. |
| Stock batch processors plus a synchronized spool | Consistent with the shared delivery model. The append/rotation lock makes ordinary closed files safe without an enqueue acknowledgement protocol. R1-R2 complete lifecycle ownership rather than replacing this design. |
| One shared request state and separate transport/SERVER completion | Required to reconcile ASP.NET and Activity lifecycles. Python and JS also defer detail until the request's final decision; their runtime mechanisms are not direct .NET substitutes. |
| Owned span snapshots and synchronous native log masking | Necessary to preserve user telemetry and avoid retaining pooled/mutable dependency data. Full value/ownership qualification is correctly still an implementation gate. |
| Native .NET metric aggregation and measured fixed capacity | Simpler than importing Python-specific manipulation of aggregator internals or introducing custom histograms. |
| Separate completed-span FIFO and consumer LRU | Different approved contracts and eviction semantics. A generic cache framework would add complexity rather than remove it. |
| Official protobuf generation and continuous-gzip spool | Implements the shared wire/storage contract without a handwritten serializer or restart-replay system. |
| Simple incomplete-body checks and native-file omission | Approved scope choices. Do not reintroduce file rereading, whole-response buffering or transport-success certification. |

The Python implementation provides the same starting batch defaults of 2,048 queued items, 512 per batch and a one-second delay: [export.py](../../apitally-py/apitally/shared/export.py#L35-L38). Its bounded spool and private pipelines support the plan's overall approach, not a substantially smaller alternative architecture.

### Performance choice to retain in the existing qualification gate

JavaScript selects a span batch of 32 when body capture is enabled, otherwise 512: [constants](../../apitally-js/src/activation.ts#L48-L50) and [registration](../../apitally-js/src/activation.ts#L254-L262). The .NET plan starts at 512 with separate 32-record encoding chunks.

This is worth comparing during the already-planned body-enabled memory/throughput measurements, but is not a verified memory defect or a reason to copy JS automatically. In pinned .NET, [batch enumeration reads from the circular queue incrementally](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Batch.cs#L52-L58), [one item at a time](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Batch.cs#L117-L128). A 512-item batch does not by itself allocate a second 512-item payload collection. Queue size, retained request data and encoder implementation all matter. Preserve incremental bounded encoding and select constants from measurements.

### Candidates not retained as findings

| Candidate | Verification and disposition |
| --- | --- |
| Sampling callbacks must accept booleans like Python/JS | `double?` is an explicitly confirmed [C# adaptation](design.md#L271-L277). Zero/one express boolean decisions without a weakly typed union or extra overload family. |
| Consumer attributes must accept numeric and boolean values | The [shared spec](../../cloud/docs/sdks/spec.md#L300-L308) permits scalar-to-string conversion at the API boundary; it does not require it. The typed string-or-null API is a legitimate simplification. Document the expected values rather than adding coercion machinery. |
| The plan permits replacement log records | It explicitly drops replacements at [line 309](implementation-plan.md#L309), matching [Python](../../apitally-py/apitally/shared/log_processor.py#L188-L200) and the approved .NET callback contract. |
| Consumer change detection requires an unnecessarily stable hash protocol | The plan requires canonical submitted payloads and stable key ordering, not a persistent or cross-process hash format. Python uses an order-independent process-local hash; [JS](../../apitally-js/src/consumers.ts#L62-L87) uses sorted attributes and SHA-256. Ordinary in-process change detection is sufficient; do not introduce a hash compatibility contract. |
| Missing metric exclusion rules are a new behavior gap | The shared spec/design and the plan's validation matrix already cover eligibility. A local sentence in section 9 spelling out exclusion of OPTIONS, websockets and unmatched routes would improve clarity, but is not a new design decision. |
| Deferred integrations are readiness defects | Sentry, full OpenAPI, Native AOT, Serilog-specific integration and special multi-host coordination are deliberately outside v1. |

## Implementation readiness

R1-R3 are incorporated into the plan, which is suitable for staged implementation subject to its existing approval and acceptance gates. No further architectural expansion is recommended.

- Resolve the public snapshot/member/value and Unicode-truncation approval items before publishing or depending on that API shape. Reconcile the newer consumer contract with the .NET design as already scheduled in stage 1.
- Keep integrated provider activation, request completion, privacy ownership and final-write tests early. Existing POCs establish mechanisms, not correctness of the combined SDK.
- Preserve measured batch/cardinality selection, real backend ingestion and clean packed-package consumers as release gates. Static review cannot replace those checks.
- Approval covers these plan revisions only. Production implementation and publishing still require explicit approval.
