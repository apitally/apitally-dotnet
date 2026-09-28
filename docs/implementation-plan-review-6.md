# Implementation plan review, round 6

Date: 2026-09-28. Status: In progress. Findings are being decided one at a time. Production code is unchanged.

## Assessment

This round asked one question: which parts of the design and plan could lead an implementing agent to over-engineer a solution or add unnecessary code, tests or infrastructure? It did not re-check contract consistency, which round 5 covered.

The plan already has strong guardrails against bloat: no placeholders or temporary interfaces, no mocks, `TryAdd*` registration, "no generic cache framework" and "port knowledge, not old architecture". The remaining risks fall into four groups:

- open-ended measurement work with no defined deliverable;
- mechanisms carried over from the shared design or POCs that .NET does not need;
- behavior described at length or ambiguously enough that an agent is likely to build more than the one small mechanism intended;
- guidance that no code acts on, which an agent may still try to satisfy.

## Baseline and method

| Item | Revision |
| --- | --- |
| .NET repository | `v1` at `dfcdd36` |
| Shared SDK documents | cloud `f98007ae` |
| Python SDK | local `apitally-py` checkout |
| JavaScript SDK | local `apitally-js` checkout |
| ASP.NET Core | `dotnet/aspnetcore` `release/8.0` |

The parent reviewed both documents directly and checked each candidate against the shared design, the Python and JavaScript SDKs, ASP.NET Core source and the decisions recorded in review rounds 1-5. Candidates that re-open an earlier decision without new evidence are listed under "Candidates not retained". No builds, tests or experiments were run.

## Findings

| ID | Priority | Finding | Needs decision |
| --- | --- | --- | --- |
| B1 | High | Metric capacity and batch settings require measurement studies with no defined deliverable | Yes |
| B2 | High | Recognizing validation errors by response shape adds a second response-observation path | Yes |
| B3 | Medium | The shutdown prose suggests ownership-tracking machinery beyond the agreed cleanup task | Yes |
| B4 | Medium | A request pipe-reader wrapper is likely redundant with the request stream wrapper | Yes |
| B5 | Medium | Legacy HTTP attribute normalization and the per-message span filter have nothing to act on in .NET | Yes |
| B6 | Medium | The `OTEL_*` fallback configuration layer has no entries | Yes |
| B7 | Medium | Spool file pinning and open-storage reclamation go beyond the shared design and Python | Yes |
| B8 | Low | Map-valued attribute conversion is unspecified and inconsistent between the documents | Yes |
| B9 | Low | One design sentence asks for omitted-versus-explicit tracking that the rest rules out | Yes |
| B10 | Low | Stage 1 builds `AttributeValues` and `SdkDiagnostics` before anything uses them | Yes |
| B11 | Low | "Equivalent instrumentation event" invites exception comparison logic | Yes |
| B12 | Low | Guidance that no code acts on | Yes |
| B13 | Low | The design restates most decisions up to three times, mixed with POC details | Yes |

### B1. Measurement studies with no defined deliverable

**Where:** plan section 9 line 368 and stage 5; plan section 10 line 376 and stage 8; design lines 39 and 518.

Stage 5 asks for a study of startup allocation, active memory and collection time for metric capacities of 10,000, 20,000 and 50,000 across all supported runtimes. Stage 8 asks to "measure and fix" the batch queue, batch size and delay. Neither says what the measurement must show or where its results go, so an agent is likely to build a benchmark project or load harness to satisfy it.

New evidence since round 3 (T4), which kept these measurements:

- Python (`apitally/shared/export.py`) and JavaScript (`src/activation.ts`) both hard-code the batch settings the plan already starts with: queue 2,048, batch 512, delay 1,000 ms.
- The shared design (line 294) prescribes no cardinality limit: "no additional cross-SDK cardinality limit is prescribed". Python keeps the OTel default and reclaims empty histogram points; JavaScript sets no limit.

**Recommendation:** fix the batch settings at 2,048/512/1,000 ms as release values, matching both SDKs. Choose one metric capacity now and remove both measurement steps.

### B2. Validation response-shape fallback

**Where:** design lines 351-357; plan section 7 line 334.

Besides the MVC `InvalidModelStateResponseFactory` and `ProblemDetailsOptions.CustomizeProblemDetails` hooks, the plan recognizes "tested standard complete 400/422 response shapes" by observing and parsing response bytes. This is independent of body capture and retains up to 50,000 bytes. "Share a bounded response observation buffer where useful" is vague enough to invite a general buffer abstraction.

The fallback adds coverage in two cases only: `TypedResults.ValidationProblem` on .NET 8, and .NET 10 `AddValidation` without problem-details services. Everything else is covered by the hooks.

**Recommendation:** remove the fallback and rely on the framework hooks, documenting the two uncovered cases. If it stays, state exactly which buffer it reads from.

### B3. Shutdown prose

**Where:** plan section 10 lines 384 and 421-434; design section 4.

Round 3 kept the mechanism: one cleanup task started with `Task.Run` and awaited with `WaitAsync(token)`. The prose describing it has grown to three passages, with phrases such as "retains ownership of resource disposal until existing synchronous work returns", "resources stay alive until that work returns rather than being disposed underneath it" and "exporters and the spool have no separate abandoned state". An agent reading these may build leases, reference counts or state flags. The agreed mechanism needs none: disposal at the end of one sequential task already runs after any synchronous work.

**Recommendation:** replace the passages with a short ordered sequence and one rule for `DisposeAsync`. The mechanism is unchanged.

### B4. Request pipe-reader wrapper

**Where:** plan layout line 85 (`ObservedBodyFeatures.cs`), plan section 7 line 323; design line 323.

The plan wraps both the request stream and `IRequestBodyPipeFeature`, "covering `BodyReader` and `BodyWriter` without double-counting shared stream paths". Kestrel's `IRequestBodyPipeFeature.Reader` (`HttpProtocol.FeatureCollection.cs`, `release/8.0`) and the default `RequestBodyPipeFeature` both create a new `PipeReader` over `Request.Body` whenever the body stream has been replaced. Replacing `Request.Body` with the observing stream therefore already covers `BodyReader` reads. The cost is that `BodyReader` consumers read through a stream adapter instead of Kestrel's native pipe. MVC and `ReadFromJsonAsync` read through `Request.Body` anyway.

The response side still needs the `IHttpResponseBodyFeature` wrapper, to pass native `SendFileAsync` through unchanged. The request-lifetime (abort) wrapper also stays; see "Candidates not retained".

**Recommendation:** wrap only `Request.Body` on the request side, after confirming this against the transport-completeness POC matrix.

### B5. Legacy HTTP normalization and per-message span filter

**Where:** design lines 247 and 259; plan section 6 line 310.

The design asks to normalize stable and legacy HTTP attribute forms, derive missing path/query fields from full URLs, and filter "known kind/name/scope combinations" of framework per-message spans. Both come from the shared design, which targets instrumentations that emit legacy names or per-message spans. In .NET:

- ASP.NET Core and HttpClient instrumentation at the 1.19.0 minimum emit only stable attribute names, and the server already reads legacy fallbacks (spec section 6.1). Shared design line 96 requires stable output only where the language's instrumentation defaults to old names.
- Stock ASP.NET Core instrumentation emits no per-message websocket spans, and websocket requests are excluded with their descendants. There is no known combination to filter, so an agent would have to invent one.

Shared design line 189 does require redacting legacy query-bearing attributes, and that stays.

**Recommendation:** drop the normalization, URL derivation and per-message filter, and record this as a .NET adaptation. Keep one list of query-bearing attribute keys, stable and legacy, for redaction.

### B6. Empty `OTEL_*` fallback layer

**Where:** design lines 168 and 181; plan section 4 line 243.

The precedence lists "semantically equivalent `OTEL_*` fallbacks" as a layer, and the base configure step "applies the applicable `OTEL_*` fallbacks". The shared design's environment table (lines 73-80) has no `OTEL_*` option fallbacks. `OTEL_SDK_DISABLED` is an additive disable control, and `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` flow through the standard resource builder. Python and JavaScript read no other `OTEL_*` variable for options. An agent may invent mappings such as `OTEL_TRACES_SAMPLER_ARG` to `SampleRate`.

**Recommendation:** remove the layer and name the exact variables the base step maps: `APITALLY_WRITE_TOKEN` and `APITALLY_ENV`. `APITALLY_DISABLED`, `OTEL_SDK_DISABLED` and `APITALLY_OTLP_ENDPOINT` keep their existing separate handling.

### B7. Spool file pinning and open-storage reclamation

**Where:** plan section 10 lines 403 and 407.

- "If open storage alone needs reclamation, close a current file under the same lock so the absolute bound remains enforceable." Current files are capped at 4 MB uncompressed each, one per signal, and are measured compressed against a 10 MB (memory) or 50 MB (disk) bound. Exceeding that from open files alone would need nearly incompressible protobuf. Python evicts closed files only and stops when none remain.
- "Pin a selected closed file while sending so storage reclamation cannot invalidate the active read" implies pin state on files. Python has none: deleting a file already removed is a no-op. In .NET, reading the file's bytes before the POST, or opening it with `FileShare.Delete`, makes eviction during a send harmless in both storage modes.

**Recommendation:** match Python. Evict closed files only; read the file before sending and treat a file already evicted after the send as a no-op.

### B8. Map-valued attribute conversion

**Where:** plan section 6 line 298; design section 6 (attribute value list) and the value table at lines 398-410.

The plan lists "normalized maps" as a snapshot value type, but the design's type list for the snapshot does not include them. The design table documents stock three-level map recursion, while round 2 decided not to copy stock depth quirks. An agent is left to invent a depth and cycle policy.

**Recommendation:** state one rule. For example, dictionaries convert one level deep with scalar and array values, and anything deeper uses the invariant string fallback. Alternatively, all dictionaries use the string fallback.

### B9. Omitted-versus-explicit sentence

**Where:** design line 171.

"An omitted value must remain distinguishable from an explicit `false`, `0`, or `dev`" restates the shared design's requirement (line 70) as a property of values. Design line 181 and plan section 4 already satisfy it by layering, without nullable properties or assignment tracking. Read in isolation, the sentence invites building that tracking.

**Recommendation:** replace it with "Layering satisfies the shared absent-versus-default rule: an unassigned value keeps the value of the layer below."

### B10. Stage 1 modules without consumers

**Where:** plan stage 1 (line 446); layout entry for `SdkDiagnostics` ("SDK logging and warning deduplication").

Stage 1 builds `AttributeValues` and `SdkDiagnostics`, but nothing converts values or logs diagnostics until later stages. That conflicts with "add each member in the stage that first uses it" and invites writing every `[LoggerMessage]` method and a general warning-deduplication class up front.

**Recommendation:** create both in the first stage that uses them, and state that deduplication is a flag at each warning site.

### B11. Exception event equivalence

**Where:** plan section 7 line 336.

"Add at most the first SDK exception event to the SERVER representation without duplicating an already-observed equivalent instrumentation event" leaves "equivalent" undefined, which invites comparing type, message and stack.

**Recommendation:** "Skip the SDK exception event if the SERVER activity already has an `exception` event."

### B12. Guidance that no code acts on

- Design line 109, "Inspect sampler settings only through available supported APIs": the design review found the pinned SDK's provider and `Sampler` property are internal, so no supported API exists and nothing depends on the result.
- Design line 378, "Capture code-location attributes when supplied by the logging interface": `ILogger` never supplies them.
- Plan line 36 and stage 8, "record the qualified dependency graph": the artifact is not defined. Either name it, for example a committed NuGet lock file, or remove it.
- Plan line 317, "Centralize shared names, patterns, content types and limits with their owning modules": contradicts itself and invites a shared constants file.

**Recommendation:** delete the first two, define or delete the third, and reword the fourth to "Keep each shared name, pattern and limit in the module that uses it."

### B13. Repeated decisions and POC details in the design

**Where:** design top table, section bodies, and section 15 table.

Most decisions appear three times with slightly different wording, and section 15 repeats a row ("Log callback type" and "Log-mask record type"). POC details such as assertion counts and the 1.3-second abort timing sit next to requirements. Agents try to satisfy every restatement and may turn POC observations into tests of upstream behavior.

**Recommendation:** add one line to the plan's scope section: "This plan is the implementation authority; design.md records rationale and evidence." Optionally drop the section 15 table.

## Keep these

| Item | Reason |
| --- | --- |
| No placeholders, no-op members or temporary interfaces between stages | Prevents scaffolding that later stages must remove. |
| No mocks of Apitally classes; decode memory-mode spool contents | Tests exercise production code without seams. |
| "Do not build a generic cache framework or a separate consumer delivery queue" | Direct guard against a common over-build. |
| "Port knowledge, not old architecture" | Stops the v0 Hub design from creeping back in. |
| No completed-request cache, admission lock or cancellation settling | Each negation names a mechanism an agent would plausibly add. |

## Candidates not retained

| Candidate | Disposition |
| --- | --- |
| Use runtime `Regex` for built-in patterns instead of `[GeneratedRegex]` | Not retained: decided in round 2; `Regex.ToString()` returns the pattern for the startup event, so the list is not duplicated. |
| Replace "32 records per chunk" with a pure byte-bounded builder | Not retained: rejected in round 4; the shared design names 32. |
| Drop the loopback proxy test and the blocked-append/overlapping-rotation tests | Not retained: kept deliberately in round 3 (T3, T5). |
| Drop the request-lifetime (abort) wrapper in favor of the `RequestAborted` check | Withdrawn: Kestrel schedules `RequestAborted` cancellation asynchronously and suppresses it after the final response write (`HttpProtocol.cs` `CancelRequestAbortedToken` and `PreventRequestAbortedCancellation`), so the token is not a reliable abort signal at `OnCompleted`. |
| Remove the detachable forwarding processor | Not retained: round 3 found it is one nullable field. |
| The stage 8 soak reference | Resolved in round 5 (H1). |
