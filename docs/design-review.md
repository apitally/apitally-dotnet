# .NET v1 adversarial design review

Status: Reviewed and independently verified; findings await resolution. This review is not implementation approval.

## Review baseline and method

- Review date: 2026-09-25.
- Reviewed design: [design.md](design.md), commit `a198e084fd74a895238962ad545d1694fdfd26be`.
- Authoritative contracts: [shared specification](../../cloud/docs/sdks/spec.md) and [shared design](../../cloud/docs/sdks/design.md). Both files are unchanged from `f22ee6c0bd4c71ba32f75664d091193fdfecd379`, although the cloud checkout is now at `a08be4d8094bee392f24f7cfadb77f4333e7218c`.
- Four independent read-only reviews cover hosting/providers/configuration, request capture/privacy/logging, export/shutdown/metrics, and cross-cutting contracts/API coverage.
- The parent independently checks each candidate finding against the design, authoritative contracts, actual POC source and, where needed, pinned upstream source or a bounded experiment. Unverified claims and speculative edge cases are excluded.
- Initial support means an ordinary single-host ASP.NET Core application. Approved deferrals of Sentry, full OpenAPI and Native AOT, and the limited multi-host guarantee, remain intact.

Line references below apply to the reviewed revision. A verified validation gap is not an observed production defect: the v1 SDK has not been implemented. Existing POCs establish individual mechanisms, not their complete integration.

## Findings and resolution status

| ID | Priority | Finding | Classification | Status |
| --- | --- | --- | --- | --- |
| R1 | Out of scope | Standard Serilog host registration bypasses the proposed application-log capture provider | Later documentation improvement; no SDK-specific work | Closed for the rewrite |
| R2 | High | Request association needs early HTTP data and integrated completion handling | Integrated POC verified; bounded cache policy selected, implementation and qualification remain | POC validation complete |
| R3 | High | Body capture needed explicit completeness and native file boundaries | Simple direct checks and native file omission approved; production integration remains | Design resolved |
| R4 | High | Batch completion must be coordinated with spool closure and shutdown | Reproduced dependency limitation; previously acknowledged integration gate | Open |

R1 is closed as outside the SDK rewrite. R2-R4 are engineering design and validation items under already-approved requirements.

## R1. Serilog provider forwarding - closed, outside rewrite scope

**Scenario and consequence:** an application configures Serilog using its normal host/service registration and writes request logs through `ILogger<T>`. Its Serilog sinks keep receiving logs, but the proposed additive Apitally `ILoggerProvider` is bypassed. Enabled-by-default log capture would therefore not work in this common composition without an additional integration requirement.

**Verified evidence:**

- The design selects an additive provider forwarding into a private OTel logger pipeline, while explicitly leaving replacement logging factories untested: design section 9, lines 361-365.
- Shared design section 9 selects the standard .NET `ILogger` abstraction and favors capture without changes to the application's logging setup. This concern is about `ILogger<T>` calls, not an invented requirement to capture direct Serilog APIs.
- Serilog's hosting registration defaults `writeToProviders` to `false`, creates a replacement `ILoggerFactory`, and only forwards other registered providers when enabled: [Serilog.Extensions.Hosting source](https://github.com/serilog/serilog-extensions-hosting/blob/0d142314fdef1b60e3b7703fa5cd84ddc74c4ffc/src/Serilog.Extensions.Hosting/SerilogServiceCollectionExtensions.cs#L119-L212).
- Adding the provider directly to that factory does not bypass the limitation: without a provider collection, `AddProvider` ignores it: [SerilogLoggerFactory](https://github.com/serilog/serilog-extensions-logging/blob/d220ae75c7f1150d58f228f210f2ff8f6a36bcde/src/Serilog.Extensions.Logging/Extensions/Logging/SerilogLoggerFactory.cs#L69-L81).
- The [private logging POC](../pocs/private-logging/README.md) uses an ordinary `LoggerFactory`. Preserving its independent sinks in both registration orders does not validate replacement-factory dispatch.
- The parent independently reproduced the dispatch behavior using `Serilog.Extensions.Hosting` 8.0.0/9.0.0/10.0.0 on .NET 8.0.13/9.0.2/10.0.9. All four cases per runtime passed: both registration orders, with default forwarding versus `writeToProviders: true`. The existing Serilog sink received exactly one unchanged message in every case; the additional provider received zero by default and one with forwarding. These are factory-dispatch results, not full Apitally integration results.

**Approved resolution:** retain the SDK's existing generic `ILoggerProvider` design. Applications may enable Serilog's standard `writeToProviders: true` setting; this adds no Serilog-specific code to the SDK. Guidance about that setting and other registered providers can be a later documentation improvement. Serilog-specific integration work and release gates are outside the rewrite, and this observation does not change `docs/design.md`.

## R2. Early request data and request association need one integrated path

**Scenario and consequence:** a normal endpoint creates an async child activity, makes an outgoing HTTP call, sets a consumer or request attribute, and logs through `ILogger<T>`. The SERVER activity starts before middleware; transport completion and SERVER end later release detail. If these stages do not share the same request state, helper values or log linkage can be lost, or detail can be released twice or under the wrong sampling decision.

**Verified evidence:**

- Design sections 2, 5, 6 and 13 explicitly leave early activity association, cleanup and helper resolution open: lines 131, 246-262, 284 and 578-587.
- OTel's processor `OnStart` runs from its [activity-started callback](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L191-L199), before ASP.NET instrumentation's [diagnostic start handler](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/dda21df71c8ccd247ab1051760b13f1db9353cfa/src/OpenTelemetry.Instrumentation.AspNetCore/Implementation/HttpInListener.cs#L98-L114) adds HTTP fields. A parent loopback check with OTel 1.19.0 confirms method, path and user-agent tags are absent at processor start on .NET 8/9/10, then present at outer middleware entry. It covers a health path, `OPTIONS`, a health-check user agent and an ordinary request. Exclusion/sampling cannot rely only on those activity tags at that earlier point.
- The reviewer's stronger claim that request-stage decisions must move into middleware was not retained. The standard HTTP-context factory publishes `IHttpContextAccessor.HttpContext` before hosting diagnostics starts the activity: [factory initialization](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/Hosting/src/Http/DefaultHttpContextFactory.cs#L47-L67), [hosting order](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/Hosting/src/Internal/HostingApplication.cs#L68-L89). The parent registered the public accessor and verified correct method, path, user agent and the same request identity at processor start and middleware entry in all four cases on .NET 8/9/10. This is a demonstrated public source of early request data, not proof of the full association/callback pipeline.
- The provider POC associates the SERVER activity in middleware. Its `OnStart` records an ID; its export filter later walks `Activity.Parent`: [Candidate.cs](../pocs/provider-registration/Candidate.cs), lines 69-113. It is not the required complete request-map implementation.
- The private logging test manually associates both SERVER and child before logging: [Program.cs](../pocs/private-logging/Program.cs), lines 37-44. The processor relies on that supplied map: [Pipeline.cs](../pocs/private-logging/Pipeline.cs), lines 182-224.
- The [snapshot coordination model](../pocs/activity-snapshots/README.md#coordination-model-evidence-only) checks both completion orders sequentially. It does not prove concurrent Kestrel/provider/middleware coordination.
- Shared design sections 5-6 and 13 require inherited request identity, once-only release, sampling-independent request state and helpers targeting SERVER rather than the current child. Shared spec section 8 requires explicit SERVER linkage on every application log.

**Recommended direction:** use supported early HTTP-context access to construct private request data rather than assuming activity tags are populated. Specify and test one request association and completion mechanism across these components, leaving the application's activity untouched. Verify an ordinary first request, nested async activities, outgoing HTTP, helper calls, concurrent requests, keep-alive reuse, response sampling and late detail. Preserve the already-selected API and sampling behavior; a callback-timing change is not established as necessary.

**Closure:** complete exported counts, attributes and log linkage are correct through the integrated path, including with a user-owned provider; the request map and completion state are not manually supplied by the test.

**Approved follow-up and verified result (2026-09-25):** the user chose a focused integrated POC before implementation planning. The [request-association experiment](../pocs/request-association/README.md) was inspected and independently rerun: 1533 assertions passed on each exact .NET/ASP.NET runtime pair 8.0.13, 9.0.2 and 10.0.9, with SDK 10.0.301, OTel 1.19.0, locked dependencies and zero build warnings/errors. Actual SERVER/child callbacks, an HTTP-context feature and an ID-based association map feed real native log capture, helpers, final transport data and owned export. No fixture supplies associations or fabricates SERVER completion.

The matrix covers fallback, application-owned tracing in both registration orders, and existing-instance DI registration with preserved external disposal ownership. Before-middleware children/logs, first requests before ApplicationStarted, nested and explicit-parent activities, outgoing HTTP, concurrent requests, keep-alive reuse, shared trace IDs, sampled-out helper state, response keep/drop/abstain, late detail and explicit cutoff were exercised. Normal retained requests exported five descendants, SERVER and five logs with exact identity/linkage. Native completion order was transport then SERVER; reverse/racing processing uses labeled test-only handoffs of already-observed real events.

Parent review caught two gaps in the first prototype: mixed-buffer submission placed logs before SERVER, and transport size remained outside the owned SERVER copy. New assertions reproduced both failures before correction. The independently rerun final code proves descendants/SERVER/log ordering, private final transport enrichment before decision/export, and unchanged application output. See [recorded results](../pocs/request-association/RESULTS.md).

This resolves the missing integrated experiment at POC level, not production qualification. The manually swept 60-second association retention and whole-request eviction are experimental, not the selected SDK policy. The subsequent approved direction preserves late telemetry using a bounded FIFO cache of completed, kept-request span IDs, with individual-ID eviction and a 10,000-ID initial internal capacity to validate. Active requests stay outside that cache; retained IDs share the request's final decision and cumulative counters. Eviction can drop later telemetry requiring an evicted ID, including a still-running child of a completed request. The selected cache has no time-based expiry; production implementation, concurrency/lifecycle integration and capacity validation remain open. Primitive-only snapshots/logs, path-selected decision fixtures and transport/helper observations do not establish the full public callbacks, normalizer, body capture, metric/error export, or R4 spool/shutdown coordination. The R2 follow-up left `docs/design.md` and production code unchanged.

## R3. Body completeness and native file capture - design resolved

**Reviewed scenario and consequence:** a client disconnects during a streamed response, or an endpoint returns an allowed small text/JSON file. The SDK must omit partial bytes while capturing complete eligible content without changing application delivery. The original transport experiment does not establish that full behavior.

**Verified evidence:**

- Shared spec section 6.3 and shared design section 7 require complete, never-truncated captured bodies. An already-established oversized sentinel is a separate case and can survive an abort.
- Design section 7, lines 320-324, explicitly records the unresolved completeness rule and file-payload capture.
- The [transport POC report](../pocs/transport-lifecycle/README.md), lines 45-52 and 81-86, records `OnCompleted` after aborts and length mismatches. The tested predicate combines observed abort/escape and declared-length agreement; it does not establish complete captured bytes across all response paths. Client receipt is a separate question.
- [Observation.cs](../pocs/transport-lifecycle/Observation.cs), lines 45-63, contains that experimental predicate. It protects the exercised paths but does not establish all server-originated abort handling.
- The native file-send path delegates delivery, then marks capture missing and clears payload bytes: the same file, lines 152-158 and 187-199. Even an eligible file below 50,000 bytes therefore has no captured payload in the experiment.

**Original review direction:** retain the complete-body contract and validate a concrete completeness rule, bringing any file-capture scope exception back for approval. The approved resolution below narrows that scope rather than introducing whole-response buffering or special file handling.

**Implementation validation:** ordinary Kestrel body-writer/compression paths must preserve streaming and client-visible output while omitting known incomplete captures. Native file sends preserve delivery and omit the entire capture, including mixed output. This is not a claim that the current SDK leaks partial bodies.

**Approved follow-up and verified result (2026-09-25):** the user approved a focused transport-completeness POC using subagents. The [new experiment](../pocs/transport-completeness/README.md) preserves the original transport/lifecycle POC. Parent source inspection and the final independent exact-runtime rerun passed 3564 assertions per .NET/ASP.NET pair 8.0.13, 9.0.2 and 10.0.9: 68 scenarios in uninstrumented, native-send-plus-side-read and experimental single-pass modes, each with fresh and pooled connections. SDK 10.0.301, locked framework-only restore and CSharpier 1.3.0 checks passed with zero build warnings/errors. A separate read-only assertion review found no additional material issue within the stated fixture scope.

The completion callback publishes an owned result using evidence available at that point. The observer tracks public abort, original/current cancellation, write/flush/advance/completion failures and applicable length. It copies leased writer bytes before Advance and commits only accepted operations. Tests preserve streaming, including a client-decoded gzip prefix before endpoint release, and cover size boundaries, handled errors, request consumption, file/range responses and failure containment. Healthy replacement of RequestAborted does not itself suppress capture. Explicit writer completion errors are retained even when the native implementation ignores the argument; source and runtime checks confirm that behavior on all three versions.

The file comparison establishes a concrete limitation: a real OnStarting callback replaces a small file after Kestrel has read its first chunk. Native delivery returns the original bytes, but the bounded post-send read captures replacement bytes while ordinary completion evidence passes. Deletion/truncation instead causes omission. A second file read therefore cannot serve as an exact-body capture mechanism. The single-pass experiment captures the original copy bytes in these cases, but bypasses the inner SendFile feature and uses the public SendFileFallback infrastructure helper, whose documentation cautions against application use. It is not a selected production mechanism or a qualification of other servers.

The objective remains complete captured bytes, not TCP acknowledgment. Some conservative error fixtures omit a fully observed prefix; those are evidence-handling tests, not proof of an unobserved missing byte. Unknown internal network success alone is not a new release blocker. A separate connection-reuse investigation and parent rerun verified actual healthy reuse and recovery after writer errors by replacing the aborted connection. The full checked-in matrix now passes with both fresh and pooled connections. An earlier development timeout remains unexplained; neither a cause nor a fix is claimed. See [results and development failures](../pocs/transport-completeness/RESULTS.md). These results establish experimental mechanisms and limitations, not a production file-capture choice.

**Approved resolution (2026-09-25):** prioritize ordinary REST text/JSON bodies. Keep bounded stream/body-reader/writer observation and simple incomplete flags for directly observed failures, visible cancellation and applicable length mismatches. Explicit abort/completion errors may use existing wrappers or a lightweight hook. Finalize at transport completion without token-replacement tracking, settling delays or a separate system for discovering internal server failures. Complete handled error responses remain eligible. The guarantee is omission of known incomplete captures, not certification of every transport outcome.

Delegate the native file-send path unchanged and omit its entire response-body capture, even for eligible small text/JSON files. Omit mixed stream/file output as a whole. Eligible file content already passing through ordinary observed streams can be captured incidentally, including through application compression middleware. Add no file rereading, replacement file copy or download detection. This is an explicit v1 capture-scope deviation, now recorded in design section 7 and the adaptations table; response headers, monitoring and independent size observations remain in scope.

R3's design choice is resolved. The richer POC failure diagnostics and both experimental file-capture modes remain research, not production requirements or additional release gates. Production implementation and ordinary integrated validation remain pending; no SDK code was changed.

## R4. Spool closure needs completed export, not only a successful flush

**Scenario and consequence:** an ordinary export cycle or graceful shutdown runs while a batch exporter has dequeued its final item but is still encoding or writing. Closing or sending the active spool file at that point can race the write. Late intake and provider disposal must also obey the final cutoff.

**Verified evidence:**

- Design sections 4, 6 and 10 already recognize this requirement: lines 220-222, 296-298 and 468-488. The remaining host shutdown budget and unfinished-request discard policies are approved.
- The [batch lifecycle report](../pocs/activity-snapshots/README.md#batch-lifecycle-evidence-and-negative-results) and [executable checks](../pocs/activity-snapshots/Program.cs), lines 409-503, show that `ForceFlush` can return true before synchronous export finishes, its timeout does not cancel synchronous export, generic intake can accept records after shutdown, and standalone `Dispose` does not drain or join the worker. Successful `Shutdown` does drain and join.
- The parent reran `bash pocs/activity-snapshots/run.sh` during this review: 109 assertions passed on each of .NET 8.0.13, 9.0.2 and 10.0.9, using SDK 10.0.301 and OTel 1.19.0. Build: zero warnings/errors. The negative lifecycle behaviors were reproduced, not repaired.
- The [transport lifecycle experiment](../pocs/transport-lifecycle/README.md), lines 73-79, can reach the final phase with a canceled host token and an unfinished request. Its drain is a cancellable delay, not actual spool/export work.

**Recommended direction:** coordinate intake closure, in-flight exports and spool writes explicitly. Only completed files become sendable; cancellation must not cause writes to race file closure or resource disposal. Use the already-approved remaining host budget rather than inventing a new flush window.

**Closure:** an integrated final cycle proves completed writes and once-only release, including shutdown during active export and a request completing near cutoff. The existing tests reproduce the prerequisite hazard, not spool corruption in a production implementation.

## Independent checks and coverage

| Check performed during this review | Result | Evidence boundary |
| --- | --- | --- |
| Existing activity-snapshot/batch POC | 109 assertions passed per runtime on .NET 8.0.13/9.0.2/10.0.9 | Reproduces the batch limitations; request coordination remains a sequential model. |
| Temporary Serilog provider-dispatch check | Four cases passed per runtime with matching hosting package major versions 8/9/10 | Default forwarding loses the additional provider's output; explicit forwarding restores it. No full Apitally request pipeline was exercised. |
| Temporary early-request-data check | Four real loopback requests passed per runtime with OTel 1.19.0 | HTTP fields are absent from initial activity tags but available through the registered accessor; middleware sees the same request with enriched tags. No production sampling/association implementation was exercised. |
| Approved R2 request-association follow-up | Independently rerun: 1533 assertions per exact .NET 8.0.13/9.0.2/10.0.9 runtime | Real integrated request/span/log/helper association and completion processing; test-only retention/controlled handoffs and primitive snapshots. No production cleanup, body, metric/error export or spool proof. |
| Approved R3 transport-completeness follow-up | Independently rerun: 3564 assertions per exact .NET 8.0.13/9.0.2/10.0.9 runtime, across fresh and pooled connections | Fixed completion-time decisions and real body/file comparisons. Native post-send rereading captures wrong bytes under mutation; single-pass feature bypass remains experimental. Actual reuse verified; historical timeout unexplained. |

Temporary checks used SDK 10.0.301, locked dependencies, pinned .NET and ASP.NET Core shared-framework versions, bounded execution, synthetic data and in-memory observation. Builds had zero warnings/errors. No external telemetry was sent. Initial temporary-program launches failed before application execution because only the .NET runtime, not the ASP.NET shared-framework version, had been pinned; the corrected runner pins both. This was a harness configuration failure, not an SDK finding.

All four reviewers completed their assigned areas. No additional material contradiction was verified in configuration precedence and freezing, repeated registration, the accepted provider-selection boundaries, TestServer suppression, sampling probabilities and keep/drop semantics, the selected native masking API, exception eligibility, official protobuf/continuous-gzip encoding, or native private metric aggregation. Their documented implementation/qualification limits remain in force, including full value-normalizer validation, metric-capacity measurements, dependency qualification and shared backend acceptance. Passing isolated checks is not a release-readiness claim.

## Candidates not retained as substantive findings

| Candidate | Verification and disposition |
| --- | --- |
| Missing sampler warning | The shared warning rule is already inherited, and the .NET design requires supported introspection and actionable-loss diagnostics. The pinned SDK's concrete provider and `Sampler` property are internal; the reviewed public provider extensions expose no sampler getter. No missing supported introspection path was established. Do not add private reflection for this. |
| Recovery/replay of an interrupted gzip file after process death | The shared spool design specifies same-runtime retries and aged orphan cleanup, not restart replay of incomplete streams. No design path makes such an orphan sendable. A new crash-recovery format would add an unrequested guarantee. |
| One custom span larger than the entire 4 MB file cap | The POC reproduces indivisible oversized-record rejection. The approved policy drops an individual record that cannot fit after ordinary batch splitting, warns with deduplication and continues with other records. No special fragmentation or repair is added; the file limit remains binding. |
| NLog has the same default provider-dispatch failure as Serilog | Source inspection does not support that claim. Standard NLog registration adds a provider; replacing the factory is an explicit option, disabled by default. |
| Deferred integrations or special multi-host guarantees | Sentry, full OpenAPI, Native AOT and independent multi-host sampling are agreed scope boundaries, not review defects. |

## Verification references

- [Pinned OTel SDK provider visibility](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L16) and [internal sampler property](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L300).
- [Public OTel provider extensions](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderExtensions.cs).
- [NLog provider registration](https://github.com/NLog/NLog.Extensions.Logging/blob/ba78edbf68715fffcec1bcbb665118fbc55e67c2/src/NLog.Extensions.Logging/Internal/RegisterNLogLoggingProvider.cs#L18-L33) and [factory-replacement default](https://github.com/NLog/NLog.Extensions.Logging/blob/ba78edbf68715fffcec1bcbb665118fbc55e67c2/src/NLog.Extensions.Logging/Logging/NLogProviderOptions.cs#L95-L101).

Production code remains unchanged. Approved resolutions are incorporated into the design explicitly, including R3's simple completeness checks and native file exclusion. Other recommendations require approval; runtime results retain their stated scope limits.
