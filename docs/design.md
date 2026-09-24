# Apitally .NET v1 design

Status: Draft with first-round POC results, 2026-09-23. Production SDK implementation is not yet approved.

This document adapts the shared SDK design to ASP.NET Core and the .NET OpenTelemetry SDK. It is a living design for interviews, reviews, and focused POCs, not an implementation plan.

## Sources and decision status

The [shared specification](../../cloud/docs/sdks/spec.md) owns the ingestion contract. The [shared design](../../cloud/docs/sdks/design.md) owns the cross-SDK architecture and behavior. Sections 1-16 below follow the shared design's section numbering. These links assume the local sibling-repository layout.

Python and JavaScript are implementation references, not additional requirements. Preserve shared telemetry behavior while choosing idiomatic .NET APIs, configuration, and lifecycle mechanisms. Record architectural departures explicitly rather than either copying another runtime's mechanisms or silently changing the contract.

This draft distinguishes:

- **Confirmed:** agreed during the design interview.
- **Inherited:** a requirement from the shared documents, unless an adaptation is explicitly identified.
- **Proposed:** a .NET mechanism requiring review or validation.
- **Open:** a decision or technical question not yet resolved.

An approved product/API direction does not establish that its proposed implementation works. Focused POC results are recorded below; combining middleware, provider integration and export lifetimes still requires validation.

### Confirmed decisions

| Area | Decision |
| --- | --- |
| Runtime support | .NET 8 minimum; test .NET 8, 9, and 10. |
| Native AOT | Outside the initial support guarantee. Prefer compatibility-friendly choices when they add no complexity. |
| Hosting | Support modern `WebApplicationBuilder` hosting and Generic Host with `Startup`. Modern hosting is the primary documented path. |
| Setup | One builder-level call with automatic middleware registration, subject to integrated-pipeline validation. |
| Existing tracing | Automatic integration with DI-registered tracing; register separately constructed providers as existing `TracerProvider` instances in DI. |
| Tracing support boundary | Normal single-host integration is the initial supported baseline. Additional hosts are not prohibited; independent sampling across overlapping providers and broader multi-host guarantees are outside initial scope. |
| Configuration | Populate typed options from defaults, environment fallbacks and the `Apitally` section before running code callbacks. Defer callbacks until startup configuration is resolved, then validate and freeze before activation. |
| Repeated setup | Within one host, compose code callbacks in registration order; later explicit assignments win. Register SDK components once and freeze resolved configuration before activation. |
| Runtime ownership | The application host owns configuration, buffers, workers, and shutdown through DI. |
| Unfinished requests at shutdown | At the final SDK cutoff, discard detail for requests still awaiting transport completion or SERVER activity end. Flush finalized requests normally; recorded metrics and eligible error aggregates remain independent. |
| Shutdown budget | Use the host's remaining shutdown budget and honor its cancellation. Add no separate Apitally flush window; final delivery may remain incomplete when the budget expires. |
| Test-host activation | Prefer reliable, straightforward automatic suppression of application integration-test telemetry. Bring complex detection mechanisms back for review; the .NET guard is still under investigation. |
| Request helpers | An injectable `IApitally` service is the primary API. |
| Default instrumentation | When Apitally owns tracing, instrument ASP.NET Core and outgoing `HttpClient` calls automatically. Database instrumentation is opt-in. |
| Tracing customization | Use standard OTel provider registration for database instrumentation and additional activity sources. Apitally-specific tracing-configuration callbacks are outside the initial API. |
| Metric capacity | Use a generous, internally selected fixed capacity through native OTel views and reclamation. Select the number after memory and collection-cost measurements; no public capacity setting or runtime resizing. |
| Span-based callbacks | All request/response sampling and body-masking callbacks receive the same complete, read-only span snapshot type, populated for the callback's stage. |
| Sampling callback result | Both sampling callbacks return `double?`: a keep probability in `[0, 1]`, or `null` to abstain. |
| Body-mask callbacks | Both use `Func<SpanSnapshot, byte[], byte[]?>`: snapshot first, decompressed body bytes second, replacement bytes returned. `null` produces `[REDACTED]`. |
| Log-mask callback | Use standard `OpenTelemetry.Logs.LogRecord` synchronously in the private logger pipeline, with isolated inputs and an owned copy afterward. The native callback record must not be retained. |
| Manual tracing | `IApitally.StartActivity(...)` returns the native .NET `Activity` type for a `using` scope. |
| Monitored scope | The whole HTTP application, subject to shared eligibility, sampling, and exclusion rules. |
| Sentry integration | Deferred beyond .NET SDK v1. Ordinary exception/error capture remains in scope; retain the Sentry POC as future research. |
| Endpoint documentation | Read native route summaries/descriptions on .NET 8/9/10. Full OpenAPI document capture is deferred beyond v1, including the native .NET 10 path. |

The older `Startup` approach has not been removed. Microsoft still supports it with Generic Host in .NET 10. This is distinct from the legacy `WebHostBuilder` and `WebHost` APIs, which became obsolete in .NET 10. Separate support for every obsolete hosting API is not part of the agreed scope. See the [hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/generic-host?view=aspnetcore-10.0) and [deprecation notice](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/10/webhostbuilder-deprecated?view=aspnetcore-10.0).

## 1. Product shape

**Inherited:** v1 is an OpenTelemetry distribution under the existing `Apitally` package name. It configures the official .NET SDK and sends OTLP/HTTP protobuf directly to Apitally using a write token. Legacy versions continue using the Hub; v1 has one delivery architecture.

The primary integration is ASP.NET Core HTTP request monitoring, including controller and Minimal API applications. Monitoring the whole application means there is no initial API-only path or route-group selection feature. Do not guess whether an endpoint is an API based on its response content type.

Shared defaults apply:

| Setting | Default |
| --- | --- |
| Request traces, recorded descendants, request metrics, process metrics, exceptions, and eligible error aggregates | Enabled |
| Request-scoped application logs | Enabled |
| Request headers, request bodies, response bodies | Disabled |
| Response headers | Enabled |
| Request sampling rate | `1.0` |
| Environment | `dev` |
| Built-in redaction and trace exclusion patterns | Enabled |

Log capture becoming enabled by default must be called out in the migration guide. Application log content is unchanged except for the shared truncation rules unless the user supplies a masking callback.

**Confirmed:** Native AOT is not a v1 release requirement. This is a support boundary, not a claim that an unsupported application will publish successfully or merely lose telemetry. An incompatible dependency can fail during publishing or execution. Conventional managed deployment is the initial supported mode.

**Open:** exact NuGet target frameworks, C# language version, and minimum OTel package versions. Testing runtimes 8/9/10 does not by itself require three target frameworks in the package. Dependency floors must follow behavioral validation, not API availability alone.

## 2. Integration with existing OpenTelemetry setups

### Tracing registration

**Confirmed:** use the host's normal tracing registration when available. Register a separately constructed provider as an existing `TracerProvider` instance in the host's DI container. Preserve user-owned providers, processors, exporters, resources, samplers, and instrumentation configuration.

**Proposed:** participate in provider construction through the supported .NET builder/DI APIs. Prefer one additive registration path over creating a competing provider. The OTel library registration API, `ConfigureOpenTelemetryTracerProvider`, can add configuration without independently creating a provider.

The registration POC must establish ownership in both registration orders. The presence of a `TracerProvider` service alone is not sufficient evidence that the user configured tracing: Apitally's own registration may have added it. In particular, do not apply an always-on sampler to an existing user pipeline simply because both configurations reach the same builder.

When Apitally owns tracing:

- Use an explicit sampler that records monitored requests and descendants regardless of upstream sampling. Apitally's own sampling remains a request/export decision.
- Enable suitable stock ASP.NET Core and `HttpClient` instrumentation.
- Subscribe to the manual tracing source `apitally.otel`.
- Use the default source subscriptions; enabling additional application/library `ActivitySource` instances is part of application-owned tracing configuration.

When the application owns tracing:

- Its sampler governs recorded request detail. Metrics and eligible error capture remain independent.
- Reuse its request activities and adapt to existing instrumentation without duplicate SERVER spans.
- Keep the default outbound-instrumentation decision scoped to Apitally-owned tracing.
- Inspect sampler and attribute-limit settings only through available supported APIs. A lack of introspection is not itself a warning condition.
- Do not dispose the user's provider during Apitally shutdown.

**POC evidence:** the [provider experiment](../pocs/provider-registration/README.md) preserves explicit and implicit user sampling in both DI registration orders by contributing configuration without enabling a host provider itself. At startup-filter construction it resolves an enabled user provider or constructs a private owned fallback. This also captures a request issued before `ApplicationStarted`. Repeated same-name ASP.NET instrumentation registration is deduplicated in the tested version. The fallback does not apply host tracing callbacks that were registered without enabling a provider.

**Confirmed customization boundary:** `AddApitally()` provides default tracing when the application has not enabled its own provider. To add database instrumentation, application activity sources or other tracing customization, enable and configure the application's provider through standard `AddOpenTelemetry().WithTracing(...)` registration. Apitally joins that provider and preserves its sampler and instrumentation configuration. Configure-only callbacks without an enabled provider do not customize Apitally's private default pipeline. Document complete customization examples, including the application's responsibility for its sampler and optional instrumentation. An Apitally-specific tracing-configuration callback is outside the initial API.

**Confirmed external-provider path:** use `builder.Services.AddSingleton<TracerProvider>(existingProvider)` before building the host, followed by the normal `AddApitally()` setup. Existing-instance registration leaves disposal with the original owner; Apitally does not take ownership. The supplied official SDK provider must already have the required instrumentation and source subscriptions configured before it is built. This uses standard DI discovery rather than a dedicated Apitally provider parameter or attachment API.

Public post-build `AddProcessor` works for an official SDK provider that already subscribes to the required sources. It cannot add missing source subscriptions, and there is no public processor-detachment counterpart. Disabling the attached path preserves the external provider's lifetime, but does not remove its retained processor.

**Open:** minimum instrumentation versions and the provider-selection/attachment timing boundary. The external-provider POC used an explicit experimental parameter; the exact existing-instance DI path still needs integration verification, including shutdown and retained-processor state. The confirmed public configuration paths do not by themselves validate the full activation implementation.

### Multiple hosts

**Confirmed:** Apitally runtime ownership is per host. This is not permission to create one unrestricted tracing provider per host.

.NET `ActivityListener` subscriptions operate process-wide. The provider POC reproduces sampling interference in both host startup orders: a user sampler still returns `Drop`, but another host's always-on listener causes that user's exporter to receive recorded SERVER activities. Host association correctly filters foreign requests and one owned host can stop while another keeps serving, but export filtering does not undo sampling promotion or its effect on user exporters. Independent multi-host sampling is not established by host-owned runtime state.

**Confirmed:** normal single-host integration is the initial supported baseline. Do not prohibit additional hosts or build special multi-host tracing coordination. Independent sampling across providers listening to the same sources is not guaranteed, even within a single host; broader multi-host guarantees remain outside initial scope. Retain host-owned configuration and lifecycle rather than introducing a process-global Apitally configuration singleton.

This boundary follows the distinction in official OTel guidance: repeated hosting registration creates one provider per service collection, while separately constructed providers are supported without establishing host or sampling isolation. Real Azure Monitor reports describe overlapping test hosts and multiple providers; they establish actual usage, not its production prevalence. See the research references below.

**Open:** the tested middleware association does not establish ownership of descendants ending before middleware entry or children with only an explicit parent context. These request-association questions also matter in a single host. Do not treat the POC's association filter as a complete request algorithm.

If the public APIs cannot preserve the agreed ownership and tracing behavior in a particular composition, document the limitation and bring the decision back for review. Do not silently substitute a process-global configuration singleton.

### Private providers and resources

**Inherited:** Apitally owns private meter and logger pipelines. Do not register them as replacements for application-owned pipelines or pass the private meter provider to framework instrumentation.

**POC evidence:** distinct meters/providers named `apitally` observe each other's measurements. The [metrics experiment](../pocs/encoding-metrics/README.md) isolates private outputs using `MeterOptions.Scope` (or the owning `IMeterFactory`) and an explicit view that drops foreign scopes. Factory ownership alone is insufficient. An unfiltered user provider subscribed to `apitally` still observes those instruments; private pipeline ownership is not process-level confidentiality. This metric mechanism does not resolve tracing's separate sampling interference.

Build an Apitally resource using standard OTel resource configuration, then override:

- `service.instance.id`
- `deployment.environment.name`
- `telemetry.distro.name = apitally-dotnet`
- `telemetry.distro.version`

The resolved Apitally environment must also be used for the `Apitally-Env` HTTP header. Generic resource configuration never overrides it.

On the Apitally-only export of user-owned spans, override the instance ID and environment while preserving other resource attributes and the original user's telemetry.

**Open:** reconcile host-owned runtimes with the specification's process identity and process-wide limits. A candidate is one UUID regenerated per process start and shared by that process's host resources, while operational state remains host-owned. Decide how multiple hosts account for process gauges, once-only events, and aggregate/spool limits before claiming full multi-host support. Do not implicitly change every occurrence of "per process" in the shared documents to "per host".

**Inherited:** captured bodies must not be silently truncated by OTel limits. **Open:** verify which length settings the supported .NET SDK actually exposes and where limits apply to export snapshots. Pin applicable owned settings to 65,536 and warn about observable lower user limits as required by the shared design; do not invent a .NET equivalent of Python's `SpanLimits` or use private APIs solely to inspect it.

## 3. Configuration

### .NET configuration sources

**Confirmed:** automatically bind the `Apitally` section from the application's `IConfiguration`. This naturally includes the application's chosen JSON files, environment-specific configuration, environment variables, and other providers.

Precedence, highest first:

1. Values explicitly assigned through code options.
2. Values present in the `Apitally` configuration section, treated as setup options.
3. Shared `APITALLY_*` environment-variable fallbacks.
4. Semantically equivalent `OTEL_*` fallbacks allowed by the shared design.
5. Apitally defaults.

An omitted value must remain distinguishable from an explicit `false`, `0`, or `dev`. The application configuration providers resolve precedence within the section; Apitally resolves precedence between the layers above.

The SDK is disabled when the resolved `Disabled` option is true, or either `APITALLY_DISABLED` or `OTEL_SDK_DISABLED` is truthy. A code-level `false` cannot override those environment variables. Shared truthy values are `1`, `true`, and `yes`, ignoring case and surrounding whitespace.

`APITALLY_OTLP_ENDPOINT` remains a testing-only environment override, not a public options property. User `OTEL_EXPORTER_OTLP_*` endpoint, protocol, and header settings do not redirect or authenticate Apitally's delivery pipeline.

The write token must match `apt_` followed by 24 alphanumeric characters. Missing or invalid credentials log an error with at most a masked short prefix and disable telemetry without preventing the application from starting.

### Options and immutability

**Confirmed:** code configuration callbacks receive populated options rather than an override-only object. Apply defaults, allowed `OTEL_*` fallbacks, `APITALLY_*` fallbacks and the `Apitally` section in increasing precedence, then run the collected code callbacks in registration order. Each callback sees the populated values and earlier callbacks' changes. Unassigned settings retain their existing values; ordinary boolean and numeric settings do not require nullable properties or assignment tracking to distinguish omission from an explicit value.

Collect callbacks during registration and execute them once when the host's startup configuration is resolved, not immediately inside `AddApitally()`. Apply the additive environment disable controls, validate the resulting settings and copy them into immutable runtime configuration before activation. Later mutations to the options object or configuration sources must not alter the running SDK.

**Proposed names:** use PascalCase names corresponding to the shared settings: `WriteToken`, `Env`, `AppVersion`, `Disabled`, `CaptureLogs`, the four directional capture toggles, `SampleRate`, the sampling and masking callbacks, and the redaction/exclusion pattern collections.

Callbacks are configured in code. The startup event serializes their presence as `true`, not their implementation. Pattern serialization includes their effective flags where relevant.

**Inherited:** invalid static sampling rates resolve to full capture; invalid patterns are individually rejected with an error while valid patterns remain active. Default patterns remain case-insensitive and user patterns extend them.

**Open:** exact option property names and layout, genuinely optional values, regex input types/flag semantics, and the DI resolution hook and interaction with standard options registrations. The selected deferred resolution and freezing behavior still requires integrated validation; using `IConfiguration` does not introduce dynamic reload.

**Confirmed:** repeated registration within one host composes code configuration callbacks in registration order. Later explicit assignments override earlier assignments; a later callback or setup call leaves settings it does not assign unchanged. Resolve the composed code overrides using the source precedence and additive disable rules above, then freeze runtime configuration before activation. Repeated setup must not duplicate middleware, processors, workers or logging providers.

## 4. Lifecycle: configure, activate, shut down

**Confirmed:** DI and the application host own the runtime. A host's shutdown drains and disposes its Apitally components without stopping another host's components.

**Inherited:** configuration and serving activation are separate. Registration must not start Apitally export workers, send telemetry, or report the process online. Route metadata preparation may happen once the framework has finalized that information.

**Proposed lifecycle:**

1. Builder registration wires options, services, tracing registration, logging capture, and middleware/lifecycle hooks.
2. Populate configuration from its source layers, run the collected code callbacks, apply additive disable controls, validate and freeze the resulting settings through the supported host construction path.
3. Activate on completed web-server startup, with first-request fallback. Early tracing registration must ensure the first SERVER activity is observed even if it starts before middleware executes.
4. Serialize concurrent activation attempts. Request handling must not proceed through a partially initialized Apitally pipeline.
5. On ordinary host shutdown, flush finalized requests and run the shared final export cycle before disposing owned providers and transport resources. At the final SDK cutoff, discard request detail that still lacks transport completion or SERVER activity end, as described in section 6.

**POC evidence:** the [transport/lifecycle experiment](../pocs/transport-lifecycle/README.md) observes an early Generic Host request before `ApplicationStarted`, exercising first-request activation once. The separate provider experiment establishes tracing readiness for an early request. The transport activation probe is only a counter; concurrent initialization, failures and real worker activation are not yet proven.

**Proposed adaptation:** one activation attempt per host runtime. An activation failure logs an error and leaves that host serving without telemetry. This follows host ownership rather than Python's process-global activation state.

**POC evidence:** ordinary hosted-service `StopAsync` ordering relative to Kestrel differs between the two tested hosting compositions. `IHostedLifecycleService.StoppedAsync` follows all service stop calls and is a candidate final-drain phase. A request ignoring cancellation can remain unfinished after host stop returns; a 300 ms host budget took about 1.3 seconds in the tested Kestrel abort path. The final phase receives the already-canceled token in that case. Host cancellation is not an exact wall-clock termination guarantee.

**Confirmed shutdown budget:** the application host controls the available shutdown time. Apitally uses the remaining host budget and honors its cancellation rather than starting an additional SDK flush window. Drain, flush and delivery share that budget; each phase does not receive a fresh allowance. If request draining exhausts it, final telemetry delivery may remain incomplete. This is cooperative cancellation, not a guarantee that framework or blocking synchronous operations terminate at an exact wall-clock deadline.

**Open:** actual OTel provider/worker disposal ordering, completed-spool-write coordination within the host budget, and implementation of the confirmed unfinished-request cutoff. The lifecycle POC's drain is a counter/cancellable delay, not export evidence. Validate that blocking processor/export work and cleanup do not introduce deliberate extra waiting beyond host cancellation.

**Confirmed test-suppression direction:** automatically suppress telemetry activation for application integration tests when a reliable, straightforward detector is available. Requiring users to disable every test host explicitly is not the preferred default. Bring any complex mechanism back for review rather than adding broad test-framework detection. The SDK's own tests must remain able to exercise real activation deliberately.

**Reference behavior:** Python checks `PYTEST_CURRENT_TEST` and the Django `manage.py test` argument shape at activation. Its telemetry tests deliberately clear the pytest marker. This is targeted coverage, not a universal detector for every test runner.

**Open:** establish a simple .NET guard using supported runner markers or recognition of the actual test server, with clear coverage for in-memory versus real-server tests. Verify its timing relative to private-provider construction and activation. Do not assume Python-style markers exist in .NET, scan arbitrary loaded assemblies or infer testing merely from the Development environment. No new public testing override or production detection mechanism is selected yet.

Host lifetime integration is the default direction. Python fork handling and JavaScript signal re-delivery are not mechanisms to port into this SDK.

## 5. Request model: span filtering and exclusion

**Inherited:** Apitally exports request-rooted telemetry only. The SERVER span is the request boundary even when an upstream service supplies a remote parent.

At activity start, classify recording activities by their local parent relationship. A local-root SERVER activity is a candidate request; descendants inherit its request identity; unrelated roots and missing-parent associations are dropped from Apitally's path. A user-provider SERVER activity must be associated with a request observed by this integration before export.

Apply shared exclusions before request sampling: `OPTIONS`, websocket requests, excluded paths, and excluded user agents. Normalize stable and legacy HTTP attribute forms, deriving missing path/query fields from full URLs where necessary. Keep stable HTTP semantic conventions on Apitally's exported representation without changing the user's original representation.

**Proposed:** maintain host-owned request state associated with `HttpContext` through an internal feature, plus an activity-to-request association for processors and log linkage. The state exists even when no recording SERVER activity exists. Its responsibilities are:

- The request's SERVER activity handle and identity, independent of `Activity.Current` becoming a child.
- Consumer identity and request attributes.
- First captured exception and validation details.
- Sampling/exclusion decisions and bounded trace/log buffers.
- Transport completion, body-capture state, and final response measurements.

Consumer identity must survive sampling and be adoptable when set before the SERVER handle is available. Request helpers resolve this state rather than writing indiscriminately to `Activity.Current`.

Suppress framework per-message spans at the source where supported and filter their known kind/name/scope combinations in Apitally's processor. Do not treat websocket messages as HTTP requests.

**Open:** exact association mechanics, context behavior before middleware entry, and cleanup for late-ending descendants and background work retaining request context. Verify isolation across concurrent and keep-alive requests without adding a blanket context reset that destroys legitimate upstream propagation.

## 6. Sampling and per-request buffering

**Inherited:** request and response sampling refine the static rate. Both stages compare the low 64 bits of the trace ID against the shared rounded probability threshold, so their effective combined rate is the minimum rather than the product. An invalid or throwing sampling callback warns and fails open.

Request-stage drops skip trace-detail capture work. Excluded requests never invoke sampling callbacks. Metrics and eligible error aggregates remain independent of all trace-detail decisions.

Response sampling runs once with final route, status, sizes, consumer, and custom request attributes. Abstention preserves the request-stage decision rather than sampling again at the static rate.

**Confirmed .NET result type:** both sampling callbacks return `double?`. Zero means drop, one means keep, and a value between them is the keep probability. `null` at request stage falls back to the configured static rate; `null` at response stage preserves the earlier decision. Express boolean conditions as numeric probabilities, such as `condition ? 1.0 : 0.0`, rather than introducing a custom result type or a weakly typed boolean/numeric union. This adapts the shared API's return shape to C# without changing probability, abstention or invalid-result behavior. A response-stage keep cannot recover detail already dropped at request stage.

Hold ended descendants and application logs until both transport observation and the SERVER activity complete. Keep at most 1,000 spans and 1,000 application log records per request, retaining the earliest arrivals. Release descendants, then the SERVER span, then the request's logs once. A drop discards buffered detail and raw payloads and ensures late detail also drops. Late descendants/logs for a released request remain eligible for export.

### Export snapshots

**Research finding:** .NET `Activity` is not a detached immutable span representation, and public APIs do not provide a faithful clone with the same IDs, source, and kind. A retained, stopped activity remains usable; the problem is shared mutable state and the inability to represent a privately enriched export view without changing the original. The tested .NET SDK has no equivalent public `ReadableSpan` abstraction. The specialized `BatchActivityExportProcessor` accepts `Activity` objects, not an arbitrary export snapshot.

**Proposed:** copy the data required for Apitally export into an SDK-owned snapshot. Preserve IDs, parents, times, status, events, links, scope, and resource. Apply late enrichment and privacy processing to that owned representation. Do not fabricate a second live activity to represent the original request or modify user-owned activities to finish export.

**POC evidence:** the [snapshot experiment](../pocs/activity-snapshots/README.md) demonstrates public generic `BatchExportProcessor<T>` intake of owned records, tested metadata/value copying, and private 50,000-byte body processing without changing a simultaneous user export. Mutable array values are copied rather than shared. Worker construction suppresses execution-context flow so first-request activation does not carry request context into body processing. Arbitrary value types and full scope metadata remain unproven.

Generic batching does not enforce span sampling semantics: an explicit `Activity.Recorded` check is needed to match the specialized activity processor's treatment of `RecordOnly`. The request-buffer checks are a sequential model, not proof of concurrent framework completion. Log ownership, production lifecycle guards, and detailed snapshot ownership/value semantics remain open.

**Confirmed callback shape:** all four span-based callbacks (`SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody`, `MaskResponseBody`) receive the same complete, read-only span snapshot type. `SpanSnapshot` is the working name. Expose available identity/parent, name, kind, timestamps, status, attributes, events, links, resource and instrumentation-scope metadata, using standard .NET/OTel value types where appropriate. This is an inspection type, not another span-creation or mutation API.

Each invocation receives an owned view appropriate to its stage, not a shared live `Activity` or `HttpContext`. Request sampling sees currently available information; response sampling includes final transport and custom attributes, including values learned after span end; body masking sees query/header redaction and captured headers before body attributes are attached. Information not yet available is represented as unset. Callback views must remain isolated from user telemetry and later private export mutations. The same public type is used throughout rather than mixing `Activity`, request-specific contexts and snapshots.

Python constructs a new instance of its standard OTel `ReadableSpan` class; JavaScript constructs a plain object implementing the standard `ReadableSpan` interface. Both preserve full span metadata while supplying private attributes. The .NET-owned snapshot is an explicit adaptation to preserve that behavior and consistency across .NET callbacks when the standard SDK lacks an equivalent abstraction.

**Open:** the final type/member names, detailed value normalization and ownership, and complete scope/resource copying. The existing POC does not establish the complete public callback implementation. Log masking operates on a different signal and uses the separate native `LogRecord` API described in section 9.

**Confirmed unfinished-request shutdown policy:** at the final SDK cutoff, discard trace and application-log detail for requests still awaiting either transport completion or SERVER activity end. Discard their buffered descendants/logs together, release captured payloads unprocessed and ensure later telemetry cannot revive those requests. Do not create partial SERVER exports or synthetic end times, and leave application-owned activities untouched.

Requests finalized before the cutoff follow the normal response-sampling, complete-body and once-only release rules and remain eligible for the final export cycle. Already-recorded metrics and eligible error aggregates remain independent of the request-detail discard policy. The cutoff's coordination with completion callbacks, closed intake and exporter/spool shutdown still requires integrated validation. This is a permitted per-SDK choice under the shared best-effort shutdown contract.

## 7. Capture pipeline: bodies, headers, sizes, redaction

**Inherited privacy boundary:** captured headers and body payloads remain private to Apitally. Request-serving code collects bounded data; decompression, masking, JSON processing, and redaction execute outside request handling before attributes are attached to an export snapshot. User exporters must never see Apitally-captured payloads.

Apply the canonical content-type allowlist and 50,000-byte limit. Check headers before body I/O. A known oversized body yields `[BODY_TOO_LARGE]` without reading it; crossing the cap discards buffered bytes. Empty bodies are omitted. Partial bytes from an aborted stream are omitted, while an already-established oversized sentinel can still be exported.

Process bodies in the shared order: bounded decompression, mask callback, parse, field redaction, and serialization. Parse to identify JSON regardless of content type once capture is allowed. Unsupported/failed decompression must not export the original bytes. A failed or dropping mask callback yields `[REDACTED]`; an oversized masked result yields `[BODY_TOO_LARGE]`. The oversized sentinel bypasses body processing.

**Confirmed .NET body-mask signature:** `MaskRequestBody` and `MaskResponseBody` both use `Func<SpanSnapshot, byte[], byte[]?>`, with `SpanSnapshot` as the working name. The first argument is the complete read-only span snapshot; the second is the decompressed body bytes before JSON parsing. The returned array replaces those bytes, and `null` produces `[REDACTED]`. Ordinary byte arrays keep the two callbacks consistent without another buffer abstraction. Preserve private ownership of input and accepted output data.

The body-mask callback sees the export snapshot after query/header redaction and captured-header attachment, but before body attributes are attached. Document that execution may happen later on another thread. A failure in the export redaction boundary drops the affected span rather than sending raw sensitive data.

Header attributes are list-valued, lowercase, and retain dashes. A masked header exports one `[REDACTED]` value. Redact query strings in request URLs and captured `Location`/`Content-Location` values, including stable/legacy query-bearing attributes on descendant spans and attributes supplied by user instrumentation. Defaults and allowlists come from the shared specification, not a separately maintained .NET variant.

**Proposed transport mechanism:** transparent bounded observation of request reads and response writes. Investigate ASP.NET Core body features, including `IHttpResponseBodyFeature`, rather than assuming replacing `Response.Body` covers `BodyWriter`, `SendFileAsync`, flushing, and completion. Preserve the application's streaming and backpressure behavior.

Body size observations are independent of content capture. Use trustworthy declared lengths or complete observed byte counts as the shared design allows; do not read a body merely to determine its size. Unknown size remains unknown. The same resolved sizes feed spans and histograms, including when captured bytes have been discarded after crossing the cap.

**POC evidence:** bounded stream/feature wrappers observe the tested request `Body`/`BodyReader` and response `Body`/`BodyWriter` paths without whole-response buffering. Client-visible streaming is preserved while the endpoint remains gated, including gzip. Outer observation sees compressed bytes and the final gzip trailer. Native file sending preserves delivery/counts but the POC deliberately omits file payload capture, including mixed file output.

`OnCompleted` does not establish complete body delivery: it also runs after aborts and length mismatches. `RequestAborted` can still be false at completion after an application abort. Wrapping public `IHttpRequestLifetimeFeature.Abort` marks that exercised path synchronously; broader server-originated abort detection remains unproven. An unflushed `BodyWriter` can trigger `OnStarting` after middleware finally.

**Open:** a production completeness rule, file payload capture, other body-feature compositions/protocols, and size semantics for incomplete delivery. The experiment covers selected content types and paths, not the complete shared capture/privacy policy. Whole-response buffering is not the fallback implementation.

## 8. Transport observation, routes, frameworks

**Confirmed:** automatically register the transport integration for modern and `Startup`-based hosting. The user should not need a second middleware call or knowledge of OTel ordering.

**POC evidence:** public `IStartupFilter` registration inserts the observer before the application pipeline in both modern and Generic Host/`Startup` hosting. It observes the tested final exception-handler response, unmatched route and streams. `IExceptionHandlerPathFeature.Endpoint` retains the original parameterized route after re-execution selects the error endpoint. Earlier short-circuits, third-party startup filters and Development exception-page placement remain untested.

Prefer stock ASP.NET Core request instrumentation when it satisfies one SERVER activity per request. Apitally-specific capture, metrics, consumer attribution, and errors remain in SDK-owned paths, so reusing user instrumentation does not remove those features.

Route resolution must produce parameterized endpoint templates with applicable path/group prefixes. Preserve the original matched route through exception-handler re-execution where the framework exposes it. Unmatched requests export trace detail without a route and contribute no request histograms or error aggregates. Client address and scheme attribution follow ASP.NET Core's configured forwarding/trust behavior; Apitally does not add a second forwarding-header trust policy.

### Validation and server errors

**Inherited:** automatic framework recognition is the validation API. No public validation-capture method or response-parser callback is added.

**POC evidence:** the [error/integration experiments](../pocs/error-integrations/README.md) observe automatic MVC validation by wrapping the existing `ApiBehaviorOptions.InvalidModelStateResponseFactory`, preserving its behavior. `ProblemDetailsOptions.CustomizeProblemDetails` exposes typed validation objects when the registered problem-details service runs. Registering these options callbacks does not itself install MVC; Minimal-only hosts remain without MVC services.

Known response shapes provide a conservative fallback. `TypedResults.ValidationProblem` bypasses the problem-details service on net8 but uses it on net9/net10. Built-in Minimal API parameter validation appears with `AddValidation` on net10; without problem-details services its tested response is compact 400 JSON containing title/errors. The probe recognizes tested 400/422 defaults, preserves opaque field strings and skips ordinary 400s. Localized/custom formats and comprehensive binding-source inference remain open. Recognizing bytes does not prove their transport capture is bounded or complete.

Validation response observation is independent of trace sampling and response-body logging. Parsing requires a complete eligible response and retains at most 50,000 bytes. Retaining bytes for validation never enables exporting those bytes as a captured response body.

Normalize `source`, `field`, `message`, and `type` at the adapter boundary. Preserve useful field strings; do not split and reconstruct dotted model keys. Use empty values when source/field information is genuinely unavailable.

Request-local error state retains the first captured exception, independent of a recording span. Automatic hooks and `IApitally.CaptureException(...)` update the same state and record at most the first SDK exception event on the SERVER span. Exclude request cancellation and unwrap a single-leaf aggregate where appropriate. Commit error data once at transport completion:

- Validation details contribute their normalized groups for eligible routed requests.
- A captured exception contributes a server error only when final status is exactly 500.
- An intentional 500 without a captured exception contributes no server-error group.
- `OPTIONS`, websockets, and unmatched routes contribute neither category.

**POC evidence:** `IExceptionHandlerFeature.Error` remains available after handled responses even when net10 suppresses handled-exception diagnostics. A non-handling `IExceptionHandler` observer depends on registration order, and an outer catch sees no escaping exception for these handled requests. Feature observation therefore avoids relying solely on either mechanism. Tests preserve the first exception, distinguish request cancellation from an unrelated `OperationCanceledException`, and apply the exact final-500 eligibility predicate.

**Open:** composition with transport completion, response-started failures and duplicate exception events from user instrumentation. These experiments establish capture/eligibility, not production aggregation or OTel exception-event emission.

## 9. Logs

### Application logs

**Inherited:** capture through the standard `ILogger` abstraction into Apitally's private logging pipeline. Preserve the user's logging output, providers, and applicable category thresholds. `CaptureLogs = false` disables application-log capture, not the private pipeline or internal events.

**Proposed:** an additive `ILoggerProvider` capture adapter forwarding to a separately owned OTel logger provider. The [logging POC](../pocs/private-logging/README.md) demonstrates public construction through `OpenTelemetryLoggerProvider(IOptionsMonitor<OpenTelemetryLoggerOptions>)`, with an external scope provider forwarded by the adapter. Both registration orders preserve the tested independent user sinks, resources and output. Do not call the application's OTel logging registration as a shortcut to creating a private provider.

Provider-independent minimum/category rules apply to the adapter. Filters targeting the user's OTel provider remain specific to that provider; they do not automatically become Apitally capture filters. Third-party logging-factory replacements remain untested.

Resolve `apitally.request.server_span_id` through the activity-to-request association, preserving the emitting child span ID separately. Application logs without a request association are dropped. Exclude Apitally's and the OTel SDK's own diagnostic logs from capture to prevent feedback loops. Capture code-location attributes when supplied by the logging interface; do not invent stack inspection solely to manufacture them.

Run `MaskLogRecord` synchronously on the private captured record before buffering. It may return the supplied record or drop it; exceptions or a replacement record drop the record. Isolate mutable state so the callback cannot change what other application logging providers receive. Truncate string bodies and string attributes to 2,048 characters after masking and before buffering/export.

**Research finding:** OTel .NET log records can be pooled and returned after synchronous processing. Their public mutability does not make arbitrary delayed retention safe. Original structured state can also be shared with other providers.

**POC evidence:** retaining a raw `LogRecord` across calls observes pool reuse and changed content. The logging experiment copies tested structured attributes/scopes synchronously, masks a private owned object, detaches retained callback state, and forwards only accepted snapshots to request buffers and stock generic batching. User output and delayed copies remain unchanged after later mutations in the tested shapes. Returning from a processor does not cancel subsequent processors; explicit forwarding provides exact drops. Child context remains separate from SERVER linkage, and the request map is a fixture rather than proven middleware integration.

**Confirmed callback direction:** `MaskLogRecord` receives the standard `OpenTelemetry.Logs.LogRecord` created by Apitally's private logger provider. Invoke the callback synchronously after isolating mutable input data, then copy accepted data into an SDK-owned record before the native record is recycled. The callback must not retain the native record or use it asynchronously. Preserve the supplied-record-or-drop behavior above.

The private provider creates the native record through public APIs, so a public `LogRecord` constructor or clone method is not required for this path. Its inputs can still reference application-owned state; a separate provider alone does not establish isolation. The existing POC masks an owned custom object, not a native record over pre-isolated inputs, so the selected callback path requires focused validation.

**Open:** rendered-body/formatted-message behavior, exception representation, normalization of other CLR values and duplicate keys, scope flattening/collisions, production request association, and integration with other logging factories. Verify callback changes reach export, mutable values do not affect other providers, and accepted buffered data is detached from subsequent callback/native-record mutations. The POC's Unicode-scalar truncation policy is experimental, not a new shared-contract decision. If the native callback cannot satisfy the isolation contract through supported APIs, return the issue for review rather than silently changing the public type.

### Startup event

Emit `apitally.app.startup` through the private logs pipeline with scope `apitally` and no trace/span context. The body is a JSON string containing:

- `framework = aspnetcore`
- Runtime/framework versions and `versions["app"]` when configured.
- Resolved SDK settings, including defaults, with the shared secret/metadata exclusions and callback/pattern representations.
- Registered route templates and method metadata, with optional `summary` and `description` strings from native endpoint metadata.

**Confirmed v1 scope deviation:** omit the startup event's `openapi` field. Full OpenAPI document capture is deferred on all supported runtimes, including .NET 10. Endpoint registration and native summary/description enrichment remain in scope.

**POC evidence:** finalized `EndpointDataSource` entries retain tested nested group prefixes, route constraints and method metadata. Built-in net8 OpenAPI exposes operation metadata rather than full document generation. The tested net9 document-generation providers are internal; net10 exposes keyed `IOpenApiDocumentProvider` for in-process document generation, including transformers. Swashbuckle's public `ISwaggerProvider` generates a document on all three tested runtimes without an HTTP endpoint or self-request.

**Additional POC evidence:** the [native endpoint-metadata probe](../pocs/endpoint-metadata/README.md) passed eight path/method cases on .NET 8/9/10 without OpenAPI package references or loaded generator assemblies. `IEndpointSummaryMetadata` and `IEndpointDescriptionMetadata` supply strings during existing route enumeration, including Minimal API group inheritance/endpoint overrides and MVC action annotations. Unannotated endpoints have no values. The two attributes are method-only; controller-class placement failed compilation on all three targets. This is metadata-read evidence, not integrated startup-event export validation. XML-only comments and OpenAPI-only transformer changes do not populate these route metadata interfaces: .NET 10's generated XML-comment transformer writes the OpenAPI operation during document generation.

**Confirmed implementation direction:** read `IEndpointSummaryMetadata.Summary` and `IEndpointDescriptionMetadata.Description` during existing startup route enumeration and copy available values into each corresponding method/path entry. Leave unavailable metadata absent. This uses the ASP.NET Core shared framework on .NET 8/9/10, with no additional generator dependencies or per-request documentation work. Documentation supplied only through XML comments or OpenAPI transformers is outside v1 capture. The full-schema POCs remain future research rather than release requirements.

**Deferred full-schema research:** the native .NET 10 public provider lives in the optional OpenAPI package, not the ASP.NET Core shared framework. Typed integration introduces package dependencies; dependency-free invocation/serialization still needs a reflection path. Standard DI's `GetKeyedServices(Type, KeyedService.AnyKey)` offers provider enumeration without names, but does not identify document keys or deduplicate repeated registrations. Serialization requires an OpenAPI specification version, whose configured value belongs to named options. These costs must be assessed together rather than treating generation as one method call. The existing generation experiments use a known `v1` document name, not automatic discovery. HTTP self-requests and schema reconstruction from routes are not the intended approach.

The shared contract says once per serving process. Emitting once per serving host is the natural host-owned adaptation, but its interaction with process identity and multiple hosts must be reviewed explicitly under section 2.

### Error aggregates

Use the shared validation/server aggregation identities, truncation rules and positive `UInt32` count range. Sentry event-ID enrichment is deferred from v1 as described in section 14. Limits are 100 validation and 100 server groups between drains in the shared process model; their multi-host scope remains open as noted in section 2.

Drain atomically, then emit outside the synchronization boundary immediately before the logs pipeline flushes in ordinary and final cycles. Each aggregate has the native event name and a structured OTLP object body, not the startup event's JSON-string body. It carries no request trace context and bypasses application-log masking/truncation.

**POC evidence:** public `EventId.Name` carries the event name; inspected stock serializer code maps it to native OTLP `event_name`. A direct `LogRecord.EventName` property is unnecessary and absent in the tested stable API. `LogRecord.Body` is string-only, and the stock serializer emits a string body, so it cannot directly represent an aggregate object body.

The logging POC sends a private body-carrier attribute through the official provider, then extracts and removes it into an owned string-or-object body before batching. Internal output is detached, context-free and bypasses application masking/truncation, even with application capture disabled. The separate encoding POC round-trips native event names, startup string bodies and structured error bodies through official protobuf messages. Composing these experiments into one pipeline and implementing full aggregate/startup behavior remain unproven.

## 10. Export pipeline

**Inherited:** stock batching machinery feeds SDK-owned OTLP encoding, a write-through spool, and one export worker per runtime. Delivery is HTTP/protobuf; there is no stock OTLP network exporter and no additional retry policy layered underneath.

### Encoding and batching

**Research finding:** the .NET OTLP exporter's protobuf serializers are internal. The package does not expose a public encode-only API. Its network exporter is not a drop-in spool encoder.

**POC evidence:** generated official OTLP v1.11.0 message classes and `Google.Protobuf` round-trip the tested trace, log and real SDK metric data, including binary bodies and structured internal events. Metric points are mapped and serialized synchronously before exporter return, avoiding retention of reusable SDK storage. Two unframed requests per signal merge correctly from a single continuous gzip stream, independently checked with Python zlib.

A 32-record trace chunk exceeds the 4,000,000-byte cap in the experiment; exact encoded-size checks and splitting preserve the tested records within it. A single oversized request is explicitly rejected in the POC, not assigned a production loss policy. Generated messages are a demonstrated encoding mechanism; the full mapper, package layout, memory bounds and backend acceptance remain open.

Use stock batch queue/worker machinery with explicit settings and approximately one-second intake delay. The snapshot POC demonstrates `BatchExportProcessor<T>` intake without private reflection. Bound encoded appends by actual size; a record-count chunk limit alone is not proof that a file stays below the cap.

**POC evidence:** in the tested stock batch processor, `ForceFlush` can return true after dequeue but before synchronous `Export` finishes. Its export timeout does not cancel a blocked synchronous exporter. Successful `Shutdown` drains and joins; standalone `Dispose` alone does not, and generic intake does not itself reject calls after shutdown. The production design still needs completed-spool-write coordination and lifecycle handling. Do not treat a successful flush as proof a file is ready to close and send.

### Delivery contract to preserve

| Area | Shared behavior |
| --- | --- |
| Endpoint | `/v1/traces`, `/v1/metrics`, `/v1/logs` at `https://otlp.apitally.io`, subject to the testing override. |
| Authentication | `Authorization: Bearer <write token>` and the resolved `Apitally-Env` on every POST. |
| Encoding | Protobuf, stored/sent with gzip. The server caps wire payloads at 4 MiB and drops decompressed payloads above 16 MiB. |
| Spool format | One continuous gzip stream per file containing concatenated same-signal protobuf requests. Retries replay identical stored bytes. |
| Rotation | At most 4 MB uncompressed per file, checked before append. Rotate a signal's current file at send time only when no closed files are already waiting. |
| Schedule | First attempt about two seconds after activation; subsequent cycles wait 15 seconds by default, with +/-10% jitter, after the preceding cycle completes. |
| Server adjustment | Read integer `Apitally-Export-Interval` and clamp it to 5-60 seconds. |
| Send bounds | Ten files per ordinary cycle, oldest first, with 0.1-0.5 seconds between sends; ten-second timeout per POST. |
| Retryable failures | Connection errors, timeouts, 408, 429, and 5xx leave the file queued and stop that cycle's send sequence. One immediate retry on connection error covers a stale connection. |
| Permanent rejection | Other 4xx discard the file and warn once per status under the shared warning policy. Trace quota rejection does not stop metrics or eligible error capture. |
| Retention | Expire files 59 minutes after first send attempt. Never-attempted files have no age expiry. |
| Storage bounds | 50 MB disk or 10 MB memory, measured as compressed bytes. Evict oldest closed non-metrics files first, then metrics if necessary to enforce the bound. |
| Filesystem fallback | Probe at spool construction; a failed probe selects memory with one warning. Later write failure discards the current affected file with deduplicated warning; it does not switch storage mode. |
| Orphan cleanup | Recognizable spool files untouched for two hours, checked once at construction. Active runtimes refresh file modification times each cycle. |
| Final cycle | Drain error groups, flush batch processors, collect metrics, close all current files, and attempt delivery without inter-send pauses or the ten-file cap. Normal failure rules still apply. |

Resolve proxy environment settings once and bind them to the SDK's HTTP delivery. Run collection, flushing, and export POSTs under OTel instrumentation suppression. This is especially important with default `HttpClient` instrumentation and with a user's own exporter observing application activities.

**POC evidence:** four physical loopback POSTs replay identical persisted gzip bytes and preserve the required synthetic headers. Stock HTTP instrumentation exports the unsuppressed requests and none inside `SuppressInstrumentationScope`. Explicit proxy-object binding survives a later environment change, but physical proxying and full proxy-variable semantics are untested. Retry classification, spool durability/retention, scheduling and shutdown coordination are not implemented by this experiment.

**Proposed:** a host-owned worker drives metric collection and spool delivery. Use .NET background execution appropriate to the work: HTTP sends may be asynchronous, while CPU-bound body processing must remain outside request-serving execution. Keep concurrency close to the shared model rather than introducing a task or thread per request/span.

**Open:** exact batch processor settings, protobuf generation/distribution, metric snapshot lifetime, HTTP handler/proxy configuration, spool concurrency, and integration with the confirmed host-controlled shutdown budget. Preserve the shared limits while resolving their multi-host ownership explicitly.

## 11. Metrics

**Inherited:** record in the transport integration, independently of activities and trace sampling. Use a private meter/provider and the scope name `apitally`.

| Instrument | Aggregation | Unit |
| --- | --- | --- |
| `http.server.request.duration` | Delta exponential histogram | `s` |
| `http.server.request.body.size` | Delta exponential histogram | `By` |
| `http.server.response.body.size` | Delta exponential histogram | `By` |

Duration is the count anchor. Its attribute tuple is shared with size observations: request method, parameterized route, final status, and optional consumer identifier. Add `url.scheme` and the shared 5xx `error.type` convention. Skip `OPTIONS`, websockets, and unmatched routes; retain eligible excluded/sampled-out requests. Duration and response sizes reflect transport completion, not merely endpoint return.

**POC evidence:** histogram-specific `Base2ExponentialBucketHistogramConfiguration` views and a manually collected `BaseExportingMetricReader` produce independent delta intervals with matching request dimensions and units. Public `MaxScale = 3` works in the tested package; the native exponential-view defaults are maximum scale 20 and bucket size 160. Tested duration/byte ranges adapt within ingestion's accepted scale range. Do not change unrelated instrument aggregation. The experiment serializes metric data before exporter return and does not prove every possible floating-point range or concurrent collection pattern.

Observe normalized process CPU utilization, RSS-equivalent bytes, and uptime using direct .NET process/runtime APIs or suitable isolated instrumentation. CPU and memory need paired observation times within the server's one-second tolerance. Uptime keeps collections nonempty even without traffic or with CPU/memory disabled.

**POC evidence:** delta collection reclaims inactive dimension capacity after an idle collection. Before reclamation, new dimensions can overflow even while an existing dimension remains active. The overflow point has only `otel.metric.overflow=true`, losing the required request dimensions; it cannot preserve accepted endpoint/consumer counts. The deliberately low POC limit of two is a test setting, not a product limit. Idle collections also produce paired CPU/memory timestamps and uptime, including uptime alone with CPU/memory disabled.

**Research finding:** the tested OTel SDK defaults to 2,000 distinct attribute combinations per metric stream, with separate reserved slots for zero-attribute and overflow points. The public view's `CardinalityLimit` configures this at stream creation. Storage is partly allocated upfront and existing streams cannot be resized through public APIs. Delta collection resets measurements, not every dimension slot; active combinations retain slots until a later collection can reclaim them.

**Confirmed capacity policy:** use one generous, internally selected fixed limit for each of the three request histograms, configured through OTel's native views. Keep native aggregation and inactive-point reclamation. There is no user-facing capacity setting, runtime resizing, adaptive provider replacement or custom aggregation. Select a capacity intended to accommodate most applications by measuring startup/active memory and collection costs with representative consumer, route and status combinations across supported runtimes. Neither the SDK default of 2,000 nor the POC limit of two is an approved production value.

At capacity, retain native behavior for accepted combinations. Detect overflow during collection, omit the invalid overflow point from Apitally export and issue a deduplicated warning explaining that some request metrics are missing, with capacity documentation and support guidance. Preserve the required dimensions on valid points rather than reducing attribution to hide the limit. This remains a finite bound, not a promise of lossless metrics under arbitrary cardinality.

**Open:** the exact fixed capacity and its measured memory/collection costs. Process-gauge ownership across hosts remains unresolved.

## 12. Error handling and logging posture

**Inherited:** operational SDK failures must not break the application. This includes setup/activation, body observation, capture, processing, and export. Preserve application exceptions and stream behavior while containing SDK failures. Privacy failures never authorize exporting unredacted data.

Use SDK-namespaced .NET logging for diagnostics. Operational initialization failures are errors; actionable data loss is a deduplicated warning with a consequence and remedy; normal adaptation and best-effort enrichment failures are debug-level. Never interpolate a full write token.

A missing token disables telemetry rather than failing options validation during host startup. Documented API misuse may fail synchronously with an actionable message.

**Proposed:** diagnostics go through the application's logging infrastructure while the Apitally capture adapter excludes them. Verify export failures cannot re-enter the telemetry pipeline through `ILogger` or HTTP instrumentation.

## 13. Public API

### Setup

**Confirmed shape:** one builder-level call, an automatic `Apitally` configuration section, and optional typed code configuration. The exact overloads are still proposed.

Illustrative modern-host setup, with the token supplied through configuration or `APITALLY_WRITE_TOKEN`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddApitally();

var app = builder.Build();
app.MapGet("/orders/{id}", (string id, IApitally apitally) =>
{
    apitally.SetConsumer("example-consumer");
    apitally.SetRequestAttribute("order.id", id);
    using var activity = apitally.StartActivity("load-order");
    return Results.Ok(new { id });
});
app.Run();
```

Generic Host with `Startup` receives an appropriate builder registration entry point backed by the same implementation. The hosting POC demonstrates an `IHostBuilder` entry point alongside `WebApplicationBuilder`; final public overloads remain proposed.

**Confirmed external-provider setup:** register the existing instance with standard DI, preserving its original ownership:

```csharp
builder.Services.AddSingleton<TracerProvider>(existingProvider);
builder.AddApitally();
```

The provider's instrumentation and source subscriptions must be configured before it is built, as described in section 2. The exact combined integration remains to be verified.

### Span-based callbacks

**Confirmed:** `SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody` and `MaskResponseBody` use one complete, read-only span snapshot type, with `SpanSnapshot` as its working name. The body callbacks additionally receive the body to mask. The snapshot's shape is consistent across callbacks while its available data follows the stages described in section 6. Both sampling callbacks return `double?`, with probabilities and stage-specific abstention as described there. Both body callbacks use `Func<SpanSnapshot, byte[], byte[]?>`, with the snapshot first and decompressed body bytes second; a returned array replaces the body and `null` produces `[REDACTED]`. Final snapshot type/member names and value semantics remain open. Log masking uses the standard OTel `LogRecord` because it processes a different signal.

### Log masking

**Confirmed direction:** `MaskLogRecord` receives a private native `OpenTelemetry.Logs.LogRecord` synchronously and may return that record or drop it. The reference is valid only during the callback; callers must not retain it. Apitally isolates input values before the callback and copies accepted data afterward. This native-record path still requires the validation described in section 9.

### Request helpers

**Confirmed primary surface:** inject `IApitally` rather than require a static SDK singleton or public `HttpContext` extension methods.

| Operation | Required behavior |
| --- | --- |
| `SetConsumer(...)` | Retain normalized identity in request state and set the SERVER attributes when available. Metrics retain the consumer even without recorded trace detail. |
| `SetRequestAttribute(...)` | Target the request's SERVER span, including from inside a child activity; expose the value to response sampling. |
| `CaptureException(...)` | Retain the first eligible exception in request-local state and add its SERVER exception event when possible. |
| `StartActivity(...)` | Create an INTERNAL child activity under scope `apitally.otel`, usable with `using` across synchronous or asynchronous code. |

**Proposed:** `StartActivity` returns `Activity?`, matching native .NET behavior when instrumentation is inactive. Other request helpers are safe no-ops outside an active request or when disabled. Determine the service's DI lifetime and current-request resolution without retaining a completed `HttpContext` in a host singleton.

**Confirmed adaptation:** native activity scopes are the manual-tracing surface. The shared function-wrapper recommendation is satisfied differently for C#: additional `Trace`/`TraceAsync` delegate wrappers are not part of the initial API direction. Users can use native activity tags; a second SDK span abstraction is unnecessary.

**Confirmed:** database instrumentation and additional application activity sources are explicit opt-ins through standard OTel provider registration. Use `AddOpenTelemetry().WithTracing(...)` with the relevant instrumentation extensions and `AddSource(...)`. This selects application-owned tracing rather than customizing Apitally's private default provider. Document complete examples with deliberate sampler and instrumentation choices; keep the default experience to `AddApitally()`.

### Migration contract

Document the write-token replacement, enabled-by-default logging, capture option names/defaults, consumer helper, sampling callbacks' keep semantics, and the new setup path. Preserve familiar .NET concepts, not obsolete Hub payload types or configuration behavior that conflicts with the shared defaults.

## 14. Sentry integration

**Confirmed scope deviation:** Sentry integration is deferred beyond .NET SDK v1. Ordinary exception capture and error aggregation remain in scope independently of Sentry. Sentry-specific dependencies, companion packaging, activation hooks and event-ID correlation belong to future work rather than the v1 implementation or release criteria.

The shared target remains a reference for future integration: automatically detect a usable Sentry integration without an Apitally enable flag and attach its exception event ID to the SERVER export snapshot and eligible undrained server-error aggregate. This includes events processed after activity end; already-exported telemetry is not updated retroactively. Preserve the latest-nonempty enrichment rule if the integration is revisited.

**POC evidence:** Sentry.AspNetCore 6.11.1 supports typed `ISentryEventProcessor` registration through DI or through `PostConfigure<SentryAspNetCoreOptions>` with `AddEventProcessor`, in either builder registration order. Both hooks correlate the tested concurrent handled exceptions during request scope. The options hook also correlates an explicitly captured exception after SERVER activity end; the DI-only hook does not run for that out-of-request capture. Association uses the retained exception, not a process-wide last-event-ID lookup.

Event processors run before `BeforeSend`, so an observed event ID does not prove the event was ultimately sent. All tests use a fake transport. No package-neutral discovery/registration hook was found: a typed companion is feasible, but automatic activation merely because Sentry is installed is not demonstrated.

**Deferred research:** dependency/package strategy, activation, supported versions, simultaneous-host behavior and later Sentry initialization/replacement. Retain the POC findings without selecting reflection, a hard dependency or a companion package. Bounded association retention and SDK export enrichment also remain unimplemented future work.

## 15. Cross-language posture and explicit adaptations

| Shared design area | .NET treatment | Status |
| --- | --- | --- |
| Process-global configuration/runtime | Host-owned DI runtime with independently owned shutdown. | Confirmed adaptation. |
| Code options and environment fallbacks | Add the standard `Apitally` configuration section as setup options below explicit code values. | Confirmed adaptation. |
| Unified setup | Native builder registration for both supported hosting styles; automatic transport integration. | Confirmed API direction; focused hosting POC passed, combined pipeline remains open. |
| Provider activation/attachment | Standard DI provider registration; externally built providers use existing-instance registration and retain original ownership. | Confirmed API path; combined activation and lifetime validation remain open. |
| Tracing customization | Standard OTel provider registration selects application-owned tracing; configure-only hooks do not customize the private default provider. | Confirmed boundary. |
| Multi-host tracing | Single-host support baseline; document cross-provider sampling interference without prohibiting additional hosts or adding special coordination. | Confirmed support boundary; broader multi-host guarantees deferred. |
| Manual block and function forms | Native `Activity` scope via `IApitally.StartActivity`. | Confirmed adaptation of the shared SHOULD. |
| Activation failure scope | One attempt per host runtime. | Proposed consequence of host ownership. |
| Configuration timing and repeated calls | Populate options before applying host-local code callbacks in registration order. Defer callback execution until startup configuration resolution, register components once and freeze before activation. | Confirmed behavior; DI resolution integration remains to be validated. |
| Process identity, startup frequency, limits, process gauges | Must be reconciled with multiple host runtimes. | Open; existing process-wide requirements still apply until explicitly resolved. |
| Ordinary final drain | Share the host's remaining shutdown budget and honor host cancellation, without an additional SDK flush window. | Confirmed budget policy; exporter/spool and disposal coordination remain to be validated. |
| Unfinished-request detail | Discard requests still awaiting transport completion or SERVER activity end at the final SDK cutoff; retain normal flushing for finalized requests and independent recorded metrics/error aggregates. | Confirmed per-SDK policy permitted by the shared shutdown contract; integration remains to be validated. |
| SDK span/log representations | Owned export snapshots and generic stock batch processors. | Exercised in POCs; detailed ownership and production lifecycle integration remain open. |
| Span callback type | One complete, read-only span snapshot type for all sampling and body-masking callbacks, preserving stage-appropriate private data. | Confirmed .NET adaptation; detailed API and value semantics remain open. |
| Sampling result type | `double?` represents the keep probability or abstention for both callbacks; boolean choices use zero or one. | Confirmed typed C# adaptation; shared sampling semantics preserved. |
| Log callback type | Standard OTel `LogRecord` in a synchronous private-provider callback, with isolated inputs and copying before native record recycling. | Confirmed direction; native-callback isolation and masking require validation. |
| Encoding | Official OTLP schemas/protobuf encoding with SDK-owned mapping. | Proposed .NET mechanism; no change to HTTP/protobuf delivery. |
| Metric capacity | Internally selected fixed capacity through native OTel views and reclamation, with visible overflow degradation. | Confirmed policy; numeric capacity requires measurement. |
| Runtime-specific fork and signal mechanics | Use .NET host lifecycle instead. | Platform adaptation. |
| Sentry event-ID correlation | Defer the integration beyond v1 while retaining ordinary exception/error capture. | Confirmed v1 scope deviation; POC retained as future research. |
| Startup endpoint documentation | Populate native summaries/descriptions in `paths`; omit full OpenAPI JSON on all runtimes, including .NET 10. | Confirmed v1 scope deviation; native metadata probe passed, combined startup export remains to be validated. |

The wire attributes, scope names, default redaction/exclusion rules, sampling convention, complete-body/privacy guarantees, error identities, and transport behavior remain shared requirements. A proposed .NET mechanism does not override them by implication.

Native AOT support is a product scope decision, not a shared-contract deviation. The vendor comparison found no universal support expectation, while Sentry and upstream OTel provide relevant examples of in-process compatibility. Revisit demand before expanding the supported deployment matrix; do not use profiler-agent limitations as proof that AOT is inherently impractical for this SDK.

## 16. Code style and testing

Write small, idiomatic C# components following the shared naming and testing rules. Public entry points precede supporting helpers. Prefer .NET lifecycle and concurrency primitives over porting Python/JavaScript mechanics. Extract helpers only when they materially improve clarity.

### Test structure

**Proposed:** retain xUnit as the test framework, with focused shared-module tests and small real ASP.NET Core applications. Cover controller and Minimal API behavior, modern hosting, and Generic Host with `Startup` without multiplying identical business scenarios across every configuration.

Use in-memory OTel-side observation for SDK behavior, and a local HTTP endpoint when testing physical OTLP delivery. Permanent tests assert Apitally behavior, not upstream internals. POCs may investigate dependency behavior to choose the design; that does not require turning every probe into a permanent regression test.

Do not replace Apitally classes with mocks. Assert exact exported counts and attributes. Read responses to completion before asserting completed telemetry. Use real Kestrel coverage where test-server behavior cannot establish streaming, abort, or hosting correctness. Keep test state cleanup in shared fixtures; host ownership does not make process-wide activity listeners and environment variables disappear.

### Required behavioral coverage

- First request, remote unsampled parent, concurrent requests, and keep-alive reuse.
- Existing user providers, both DI registration orders, explicit external-provider setup, and preserved user exports.
- Correct request association and disposal of host-owned state without disposing user-owned tracing providers.
- Normal, unmatched, excluded, sampled-out, websocket, and `OPTIONS` requests.
- Streaming and aborted bodies, compression, size caps, body-reader/writer paths, and complete-body redaction.
- Consumer/custom attribute helpers inside nested activities and error capture without recorded spans.
- Validation/server aggregates independent of trace decisions and application-log capture.
- Log state isolation, masking, correlation, bounded buffering, and late telemetry.
- Delta exponential request metrics, matching sizes, process gauges, and idle liveness.
- Protobuf-decoded payloads, byte-identical retries, storage fallback/retention/rotation, headers, and export suppression.
- Host shutdown and once-only release/flush behavior.

Add a .NET application/language adapter to the sibling [SDK test harness](../../sdk-tests/README.md) so the shared end-to-end tests exercise real ingestion. The harness currently has Python and JavaScript language adapters; .NET integration is work to be done, not existing coverage. Keep ordinary app configuration in that harness rather than adding workarounds to make tests pass.

The runtime matrix is .NET 8/9/10. Native AOT publishing is not an initial release gate.

## 17. Rewrite approach and v0 reuse

**Preferred approach, not authorization to delete code:** preserve a frozen v0 reference checkout at a known commit, then build the v1 implementation on the `v1` branch. Keep useful repository infrastructure. Selectively bring proven logic and behavioral test scenarios into the new architecture rather than modifying every legacy component in place.

The current v0 reference is commit `65e25ed13e15c6d6b77125749eba5cece6aa008f`. A detached sibling worktree is a candidate reference location; none has been created as part of this design work.

| Existing area | Intended treatment |
| --- | --- |
| `ApitallyUtils.GetPaths` and route metadata | Reuse suitable endpoint-discovery logic after checking prefixes, route methods, error re-execution, and the v1 startup contract. |
| Recursive JSON field masking and pattern handling | Reuse proven algorithms with v1 patterns, sentinels, parse-based JSON detection, bounded decompression, and callback failure semantics. |
| `ResourceMonitor` | Reuse process measurement knowledge; adapt CPU to 0-1 utilization normalized across available CPUs and add uptime. |
| MVC validation filter | Reuse knowledge of model-validation ordering, with normalized opaque field strings and sampling-independent request state. |
| Test application and behavioral scenarios | Adapt real controller/route fixtures; replace legacy payload assertions with v1 contract assertions. |
| Hub client, payload models, retry policy, custom histogram bins | Replace with OTel signals and the prescribed delivery architecture. |
| Custom activity collector | Replace with provider integration and request-scoped OTel processing. |
| Whole-body request/response buffering | Replace with bounded transparent observation. |
| Persistent instance UUID/lock behavior | Replace with the v1 process-identity contract, subject to the explicit host/process resolution above. |
| Independent forwarding-header interpretation | Use ASP.NET Core's configured trust behavior. |

A stable implementation is a useful baseline, not evidence that every existing behavior is appropriate for v1. Review reused code and tests against the shared requirements before porting them.

## 18. POCs required before implementation choices are settled

The first feasibility round is complete and independently checked across the installed .NET 8/9/10 runtimes. Its six experiment groups cover the topics below, combining transport and host lifecycle. Each report distinguishes tested mechanisms, reproduced limitations and remaining gaps; this is not a complete integrated SDK or proof of every acceptance case. See [the POC index](../pocs/README.md) for results and reproducible checks.

| POC | Questions and acceptance evidence |
| --- | --- |
| Provider registration and host ownership | Both DI registration orders; explicit external provider; default HTTP instrumentation; existing instrumentation; user's sampler/exporters unchanged; first request captured; two active hosts correctly associated; one host can stop without disabling the other. |
| Middleware placement and completion | Modern and `Startup` hosting; controllers and Minimal APIs; exception-handler final responses and route re-execution; streamed responses, `BodyWriter`, and file sends; both transport/activity completion orders; abort/cancellation behavior. |
| Private export snapshots and batching | Preserve span identity/events/links/resource; late enrichment without original mutation; no captured payloads in user exports; maximum-size complete bodies; bounded release/drop and late descendants; public stock batching over the selected representation. |
| Private logging and internal events | Additive `ILogger` capture; category filtering, scopes, mutable state isolation, masking/drop; pooled-record lifetime; request linkage through child activities; startup JSON string versus structured error bodies; event names and context-free internal records. |
| Encoding, metrics, and delivery | Official protobuf round trips for all signals; binary bodies and exponential histograms; concatenated request decoding; actual encoded-byte rotation limits; delta collection/reclamation and capacity behavior; idle liveness; immutable retries, proxy binding, and instrumentation suppression. |
| Error and optional integration hooks | Conservative MVC/Minimal API validation; first exception and final-500 rule; .NET 10 handled-exception diagnostics; request cancellation; finalized routes and native summary/description metadata. Sentry and full-OpenAPI evidence is retained for future work outside v1. |
| Host shutdown | Server/request draining relative to SDK/provider disposal; ordinary final cycle; host cancellation budget; unfinished request policy; no duplicate release or retained host state after disposal. |

## 19. Next design decisions

The interview has settled support scope and the main user-facing direction. The next review should resolve:

1. Provider-selection/attachment timing, external-processor lifetime validation and a simple automatic test-activation guard for the confirmed standard DI integration paths.
2. Process identity, process-wide bounds, startup events and process measurements under host-owned state, plus measurement and selection of the fixed internal metric capacity.
3. Detailed span-snapshot members and value semantics, native log-mask callback isolation/normalization, remaining option types and validation of deferred configuration resolution.
4. Body completeness, implementation of the unfinished-request cutoff and exporter/spool completion within the host's shutdown budget.
5. Package target frameworks, C# language version and dependency floors.

After those decisions, focused integration probes should compose the verified mechanisms, especially early activation, final responses, private pipeline ownership and shutdown. Physical proxy/retry/storage-failure behavior and shared backend/harness acceptance also remain to be validated.

These remain open rather than being filled with assumptions from another SDK. Implementation approval follows review of this document and the relevant POC results.

## Research references

Local source snapshots used for the initial review:

- Shared SDK documents: cloud commit `f22ee6c0`, `docs/sdks/spec.md` and `docs/sdks/design.md`.
- Python reference: `ddf5127cd5e16fec6e89eed41b1965c202e03b73` (`v1.0.0b3`).
- JavaScript reference: `16ed4266a637a944b9962b00ddad2a626759eeb6` (following `v1.0.0-beta.2`).
- .NET v0 baseline: `65e25ed13e15c6d6b77125749eba5cece6aa008f`.

Upstream source research included OTel .NET 1.19.1, ASP.NET Core instrumentation 1.19.0 with an older-version comparison, and .NET runtime 8/10. These are research snapshots, not selected dependency floors.

All six POC groups were independently rerun on .NET 8.0.13, 9.0.2 and 10.0.9 using SDK 10.0.301. The provider, snapshot, logging and encoding/metrics experiments pin OTel 1.19.0. Encoding/metrics also passed independent builds/runs using SDKs 8.0.406 and 9.0.200. Stable 1.19.1 was unavailable from NuGet during the experiments. Their net8/net9 targets load the transitive DiagnosticSource 10.0.0 package, not their original in-box Activity implementation. The transport/lifecycle POC was rerun on the same runtimes without OTel/NuGet dependencies; its native listener evidence does not establish OTel processor ordering. Reports record exact dependencies, assertions and scope limits; these results do not select a production dependency floor.

- [Library tracing registration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry.Api.ProviderBuilderExtensions/Trace/OpenTelemetryDependencyInjectionTracingServiceCollectionExtensions.cs#L15-L88)
- [Public post-build processor attachment and its implementation restriction](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Trace/TracerProviderExtensions.cs#L14-L33)
- [ASP.NET instrumentation registration in 1.19.0](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/dda21df71c8ccd247ab1051760b13f1db9353cfa/src/OpenTelemetry.Instrumentation.AspNetCore/AspNetCoreInstrumentationTracerProviderBuilderExtensions.cs#L58-L84)
- [Activity sampling across listeners](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/ActivitySource.cs#L298-L328)
- [Native Activity identity properties](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/Activity.cs#L900-L938)
- [Generic batch processor](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/BatchExportProcessor.cs#L14-L64)
- [Activity-specific batch processor](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Trace/Processor/BatchActivityExportProcessor.cs#L12-L89)
- [Private OTel logger-provider construction](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLoggerProvider.cs#L32-L66)
- [Log-record pooling](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L102-L110)
- [Specialized log batch ownership](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Logs/Processor/BatchLogRecordExportProcessor.cs#L64-L93)
- [Internal OTLP serializer](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpTraceSerializer.cs#L9-L29)
- [Official OTLP protobuf definitions and generation guidance](https://github.com/open-telemetry/opentelemetry-proto/blob/790608c4d51e6ffc12210b541e8514cbed9e91a4/README.md#L51-L73)
- [OTel .NET metric cardinality behavior](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/docs/metrics/README.md#cardinality-limits)
- [OTel hosting registration and one provider per service collection](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Extensions.Hosting/README.md#L24-L47)
- [OTel guidance on separately constructed providers and the usual single-provider lifetime](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/docs/trace/customizing-the-sdk/README.md#L45-L59)
- [Azure Monitor report involving concurrent WebApplicationFactory tests](https://github.com/Azure/azure-sdk-for-net/issues/58951#issuecomment-4387389627)
- [Azure Monitor two-provider reproduction](https://github.com/Azure/azure-sdk-for-net/issues/58951#issuecomment-4445112258)
- [.NET DI ownership of externally created singleton instances](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines#services-not-created-by-the-service-container)
- [OTel 1.19.0 per-view cardinality limit and default](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/View/MetricStreamConfiguration.cs#L69-L95)
- [OTel fixed metric-capacity allocation](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/AggregatorStore.cs#L71-L179)
- [Python callback declarations](../../apitally-py/apitally/__init__.py) and [standard ReadableSpan copy construction](../../apitally-py/apitally/shared/span_processor.py)
- [JavaScript callback declarations](../../apitally-js/src/config.ts) and [structural ReadableSpan copies](../../apitally-js/src/spanProcessor.ts)
- [OTel private logger's synchronous processing and record recycling](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L44-L106)
- [.NET options configuration, post-configuration and deferred evaluation](https://learn.microsoft.com/en-us/dotnet/core/extensions/options)
- [Native route summary/description conventions](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Routing/src/Builder/OpenApiRouteHandlerBuilderExtensions.cs)
- [.NET 10 XML comments enrich OpenAPI operations during transformation](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/OpenApi/gen/XmlCommentGenerator.Emitter.cs#L361-L386)
- [Python activation guards](../../apitally-py/apitally/shared/activation.py) and [deliberate activation in SDK tests](../../apitally-py/tests/conftest.py)
