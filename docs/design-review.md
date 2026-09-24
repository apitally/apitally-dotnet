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

## Findings and discussion order

| ID | Priority | Finding | Classification | Status |
| --- | --- | --- | --- | --- |
| R1 | High | Standard Serilog host registration bypasses the proposed application-log capture provider | Newly verified compatibility limitation; support decision needed | Open |
| R2 | High | Request association needs early HTTP data and integrated completion handling | Newly verified timing constraint; previously acknowledged integration gate | Open |
| R3 | High | Complete-body guarantees still need a supported transport implementation | Previously acknowledged capture gate | Open |
| R4 | High | Batch completion must be coordinated with spool closure and shutdown | Reproduced dependency limitation; previously acknowledged integration gate | Open |

R1 requires a user-facing support decision. R2-R4 are engineering design and validation items under already-approved requirements.

## R1. Standard Serilog host registration bypasses application-log capture

**Scenario and consequence:** an application configures Serilog using its normal host/service registration and writes request logs through `ILogger<T>`. Its Serilog sinks keep receiving logs, but the proposed additive Apitally `ILoggerProvider` is bypassed. Enabled-by-default log capture would therefore not work in this common composition without an additional integration requirement.

**Verified evidence:**

- The design selects an additive provider forwarding into a private OTel logger pipeline, while explicitly leaving replacement logging factories untested: design section 9, lines 361-365.
- Shared design section 9 selects the standard .NET `ILogger` abstraction and favors capture without changes to the application's logging setup. This concern is about `ILogger<T>` calls, not an invented requirement to capture direct Serilog APIs.
- Serilog's hosting registration defaults `writeToProviders` to `false`, creates a replacement `ILoggerFactory`, and only forwards other registered providers when enabled: [Serilog.Extensions.Hosting source](https://github.com/serilog/serilog-extensions-hosting/blob/0d142314fdef1b60e3b7703fa5cd84ddc74c4ffc/src/Serilog.Extensions.Hosting/SerilogServiceCollectionExtensions.cs#L119-L212).
- Adding the provider directly to that factory does not bypass the limitation: without a provider collection, `AddProvider` ignores it: [SerilogLoggerFactory](https://github.com/serilog/serilog-extensions-logging/blob/d220ae75c7f1150d58f228f210f2ff8f6a36bcde/src/Serilog.Extensions.Logging/Extensions/Logging/SerilogLoggerFactory.cs#L69-L81).
- The [private logging POC](../pocs/private-logging/README.md) uses an ordinary `LoggerFactory`. Preserving its independent sinks in both registration orders does not validate replacement-factory dispatch.
- The parent independently reproduced the dispatch behavior using `Serilog.Extensions.Hosting` 8.0.0/9.0.0/10.0.0 on .NET 8.0.13/9.0.2/10.0.9. All four cases per runtime passed: both registration orders, with default forwarding versus `writeToProviders: true`. The existing Serilog sink received exactly one unchanged message in every case; the additional provider received zero by default and one with forwarding. These are factory-dispatch results, not full Apitally integration results.

**Recommended direction:** support the existing Serilog provider-forwarding option as a documented integration requirement, then validate it with the private pipeline. Forwarding targets all registered providers, not just Apitally; examples must account for duplicate output from other providers rather than silently changing application logging. If automatic capture with unchanged Serilog setup is a v1 requirement, investigate a dedicated supported integration before claiming it works.

**Closure:** agree the supported Serilog setup and verify a real request's `ILogger<T>` event reaches Apitally once, remains correctly linked and maskable, and preserves the application's intended output. Provider dispatch alone does not establish the full callback/request/export path.

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

## R3. Complete-body guarantees need integrated transport validation

**Scenario and consequence:** a client disconnects during a streamed response, or an endpoint returns an allowed small text/JSON file. The SDK must omit partial bytes while capturing complete eligible content without changing application delivery. The current transport experiment does not establish that full behavior.

**Verified evidence:**

- Shared spec section 6.3 and shared design section 7 require complete, never-truncated captured bodies. An already-established oversized sentinel is a separate case and can survive an abort.
- Design section 7, lines 320-324, explicitly records the unresolved completeness rule and file-payload capture.
- The [transport POC report](../pocs/transport-lifecycle/README.md), lines 45-52 and 81-86, records `OnCompleted` after aborts and length mismatches. The tested predicate combines observed abort/escape and declared-length agreement; it is not a general proof of complete delivery.
- [Observation.cs](../pocs/transport-lifecycle/Observation.cs), lines 45-63, contains that experimental predicate. It protects the exercised paths but does not establish all server-originated abort handling.
- The native file-send path delegates delivery, then marks capture missing and clears payload bytes: the same file, lines 152-158 and 187-199. Even an eligible file below 50,000 bytes therefore has no captured payload in the experiment.

**Recommended direction:** retain the complete-body contract and validate a concrete supported completeness rule. Keep native file sending and bounded capture behavior explicit; if a real transport limitation requires a scope exception, bring that exception back for approval. Do not treat the POC's deliberate omission as an approved v1 exception or use whole-response buffering as a fallback.

**Closure:** actual Kestrel responses demonstrate complete eligible capture and suppression of partial buffered bytes, including the supported body-writer/compression/file paths, without disrupting streaming or client-visible output. This is not a claim that the current SDK leaks partial bodies.

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

Temporary checks used SDK 10.0.301, locked dependencies, pinned .NET and ASP.NET Core shared-framework versions, bounded execution, synthetic data and in-memory observation. Builds had zero warnings/errors. No external telemetry was sent. Initial temporary-program launches failed before application execution because only the .NET runtime, not the ASP.NET shared-framework version, had been pinned; the corrected runner pins both. This was a harness configuration failure, not an SDK finding.

All four reviewers completed their assigned areas. No additional material contradiction was verified in configuration precedence and freezing, repeated registration, the accepted provider-selection boundaries, TestServer suppression, sampling probabilities and keep/drop semantics, the selected native masking API, exception eligibility, official protobuf/continuous-gzip encoding, or native private metric aggregation. Their documented implementation/qualification limits remain in force, including full value-normalizer validation, metric-capacity measurements, dependency qualification and shared backend acceptance. Passing isolated checks is not a release-readiness claim.

## Candidates not retained as substantive findings

| Candidate | Verification and disposition |
| --- | --- |
| Missing sampler warning | The shared warning rule is already inherited, and the .NET design requires supported introspection and actionable-loss diagnostics. The pinned SDK's concrete provider and `Sampler` property are internal; the reviewed public provider extensions expose no sampler getter. No missing supported introspection path was established. Do not add private reflection for this. |
| Recovery/replay of an interrupted gzip file after process death | The shared spool design specifies same-runtime retries and aged orphan cleanup, not restart replay of incomplete streams. No design path makes such an orphan sendable. A new crash-recovery format would add an unrequested guarantee. |
| One custom span larger than the entire 4 MB file cap | The POC's synthetic oversized-record rejection and pending production policy are already explicit. This remains bounded encoding/error-handling work, not a major ordinary-app design decision to reopen in the interview. The file limit remains binding. |
| NLog has the same default provider-dispatch failure as Serilog | Source inspection does not support that claim. Standard NLog registration adds a provider; replacing the factory is an explicit option, disabled by default. |
| Deferred integrations or special multi-host guarantees | Sentry, full OpenAPI, Native AOT and independent multi-host sampling are agreed scope boundaries, not review defects. |

## Verification references

- [Pinned OTel SDK provider visibility](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L16) and [internal sampler property](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L300).
- [Public OTel provider extensions](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderExtensions.cs).
- [NLog provider registration](https://github.com/NLog/NLog.Extensions.Logging/blob/ba78edbf68715fffcec1bcbb665118fbc55e67c2/src/NLog.Extensions.Logging/Internal/RegisterNLogLoggingProvider.cs#L18-L33) and [factory-replacement default](https://github.com/NLog/NLog.Extensions.Logging/blob/ba78edbf68715fffcec1bcbb665118fbc55e67c2/src/NLog.Extensions.Logging/Logging/NLogProviderOptions.cs#L95-L101).

The design and production code remain unchanged. Recommendations become design decisions only after approval; runtime results retain their stated scope limits.
