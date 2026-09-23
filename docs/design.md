# Apitally .NET v1 design

Status: Initial draft, 2026-09-23. Implementation and POCs have not been approved or started.

This document adapts the shared SDK design to ASP.NET Core and the .NET OpenTelemetry SDK. It is a living design for interviews, reviews, and focused POCs, not an implementation plan.

## Sources and decision status

The [shared specification](../../cloud/docs/sdks/spec.md) owns the ingestion contract. The [shared design](../../cloud/docs/sdks/design.md) owns the cross-SDK architecture and behavior. Sections 1-16 below follow the shared design's section numbering. These links assume the local sibling-repository layout.

Python and JavaScript are implementation references, not additional requirements. Preserve shared telemetry behavior while choosing idiomatic .NET APIs, configuration, and lifecycle mechanisms. Record architectural departures explicitly rather than either copying another runtime's mechanisms or silently changing the contract.

This draft distinguishes:

- **Confirmed:** agreed during the design interview.
- **Inherited:** a requirement from the shared documents, unless an adaptation is explicitly identified.
- **Proposed:** a .NET mechanism requiring review or validation.
- **Open:** a decision or technical question not yet resolved.

An approved product/API direction does not establish that its proposed implementation works. In particular, automatic middleware placement and additive provider registration still require POCs.

### Confirmed decisions

| Area | Decision |
| --- | --- |
| Runtime support | .NET 8 minimum; test .NET 8, 9, and 10. |
| Native AOT | Outside the initial support guarantee. Prefer compatibility-friendly choices when they add no complexity. |
| Hosting | Support modern `WebApplicationBuilder` hosting and Generic Host with `Startup`. Modern hosting is the primary documented path. |
| Setup | One builder-level call with automatic middleware registration, subject to the placement POC. |
| Existing tracing | Automatic integration with DI-registered tracing, plus an explicit path for separately constructed providers. |
| Configuration | Automatically read the `Apitally` configuration section, support typed code overrides, and retain shared environment-variable fallbacks. |
| Runtime ownership | The application host owns configuration, buffers, workers, and shutdown through DI. |
| Request helpers | An injectable `IApitally` service is the primary API. |
| Default instrumentation | When Apitally owns tracing, instrument ASP.NET Core and outgoing `HttpClient` calls automatically. Database instrumentation is opt-in. |
| Manual tracing | `IApitally.StartActivity(...)` returns the native .NET `Activity` type for a `using` scope. |
| Monitored scope | The whole HTTP application, subject to shared eligibility, sampling, and exclusion rules. |

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

**Confirmed:** use the host's normal tracing registration when available. Offer explicit integration for a provider constructed outside DI. Preserve user-owned providers, processors, exporters, resources, samplers, and instrumentation configuration.

**Proposed:** participate in provider construction through the supported .NET builder/DI APIs. Prefer one additive registration path over creating a competing provider. The OTel library registration API, `ConfigureOpenTelemetryTracerProvider`, can add configuration without independently creating a provider.

The registration POC must establish ownership in both registration orders. The presence of a `TracerProvider` service alone is not sufficient evidence that the user configured tracing: Apitally's own registration may have added it. In particular, do not apply an always-on sampler to an existing user pipeline simply because both configurations reach the same builder.

When Apitally owns tracing:

- Use an explicit sampler that records monitored requests and descendants regardless of upstream sampling. Apitally's own sampling remains a request/export decision.
- Enable suitable stock ASP.NET Core and `HttpClient` instrumentation.
- Subscribe to the manual tracing source `apitally.otel`.
- Select how additional application/library `ActivitySource` instances are enabled without assuming that every source emits automatically.

When the application owns tracing:

- Its sampler governs recorded request detail. Metrics and eligible error capture remain independent.
- Reuse its request activities and adapt to existing instrumentation without duplicate SERVER spans.
- Keep the default outbound-instrumentation decision scoped to Apitally-owned tracing.
- Inspect sampler and attribute-limit settings only through available supported APIs. A lack of introspection is not itself a warning condition.
- Do not dispose the user's provider during Apitally shutdown.

**Open:** the advanced integration API, exact ownership detection, minimum instrumentation versions for safe repeated registration, and the handling of a provider configured after the supported registration boundary.

### Multiple hosts

**Confirmed:** Apitally runtime ownership is per host. This is not permission to create one unrestricted tracing provider per host.

.NET `ActivityListener` subscriptions operate process-wide. Multiple listeners can affect the sampling result for the same activity, so separate provider objects do not establish isolation. The POC must cover concurrent hosts, host-to-request association, and one host shutting down while another continues serving. Do not export another host's request simply because a processor observed its SERVER activity.

If the public APIs cannot preserve the agreed ownership and tracing behavior in a particular composition, document the limitation and bring the decision back for review. Do not silently substitute a process-global configuration singleton.

### Private providers and resources

**Inherited:** Apitally owns private meter and logger pipelines. Do not register them as replacements for application-owned pipelines or pass the private meter provider to framework instrumentation.

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

**Proposed:** use one typed options surface with PascalCase names corresponding to the shared settings: `WriteToken`, `Env`, `AppVersion`, `Disabled`, `CaptureLogs`, the four directional capture toggles, `SampleRate`, the sampling and masking callbacks, and the redaction/exclusion pattern collections. Resolve these into immutable runtime configuration before activation.

Callbacks are configured in code. The startup event serializes their presence as `true`, not their implementation. Pattern serialization includes their effective flags where relevant.

**Inherited:** invalid static sampling rates resolve to full capture; invalid patterns are individually rejected with an error while valid patterns remain active. Default patterns remain case-insensitive and user patterns extend them.

**Open:** exact option property names and layout, unset-value representation, regex input types/flag semantics, and when resolution occurs relative to standard options registration and host construction. Configuration is immutable once resolved; dynamic reload is not introduced by using `IConfiguration`.

**Open:** repeated registration within one host. The host-owned decision replaces process-global first-call-wins behavior, but does not settle whether repeated code configuration follows normal options composition or a host-local first-call rule. Registration must not duplicate middleware, processors, workers, or logging providers.

## 4. Lifecycle: configure, activate, shut down

**Confirmed:** DI and the application host own the runtime. A host's shutdown drains and disposes its Apitally components without stopping another host's components.

**Inherited:** configuration and serving activation are separate. Registration must not start Apitally export workers, send telemetry, or report the process online. Route/schema preparation may happen once the framework has finalized that information.

**Proposed lifecycle:**

1. Builder registration wires options, services, tracing registration, logging capture, and middleware/lifecycle hooks.
2. Resolve and validate configuration through the supported host construction path, then hold it fixed.
3. Activate on completed web-server startup, with first-request fallback. Early tracing registration must ensure the first SERVER activity is observed even if it starts before middleware executes.
4. Serialize concurrent activation attempts. Request handling must not proceed through a partially initialized Apitally pipeline.
5. On ordinary host shutdown, finish eligible request state and run the shared final export cycle before disposing owned providers and transport resources.

`ApplicationStarted` and the host lifecycle are candidates, not a proven ordering solution. Test early requests and user startup registrations rather than assuming the callback alone meets the first-request guarantee.

**Proposed adaptation:** one activation attempt per host runtime. An activation failure logs an error and leaves that host serving without telemetry. This follows host ownership rather than Python's process-global activation state.

**Open:** shutdown ordering relative to the HTTP server and OTel hosted services, propagation of the host's shutdown cancellation budget, and the policy for unfinished requests. Respecting the host's budget is the proposed .NET approach; no separate arbitrary SDK-wide shutdown deadline is selected yet.

**Open:** test-host activation policy. .NET integration tests deliberately start hosts, and ordinary application construction is not equivalent to serving. Choose a reliable rule rather than assuming Python-style test-runner environment markers exist or scanning arbitrary loaded assemblies. The SDK's own tests must be able to exercise real activation.

Host lifetime integration is the default direction. Python fork handling and JavaScript signal re-delivery are not mechanisms to port into this SDK.

## 5. Request model: span filtering and exclusion

**Inherited:** Apitally exports request-rooted telemetry only. The SERVER span is the request boundary even when an upstream service supplies a remote parent.

At activity start, classify recording activities by their local parent relationship. A local-root SERVER activity is a candidate request; descendants inherit its request identity; unrelated roots and missing-parent associations are dropped from Apitally's path. A user-provider SERVER activity must be associated with a request observed by this integration before export.

Apply shared exclusions before request sampling: `OPTIONS`, websocket requests, excluded paths, and excluded user agents. Normalize stable and legacy HTTP attribute forms, deriving missing path/query fields from full URLs where necessary. Keep stable HTTP semantic conventions on Apitally's exported representation without changing the user's original representation.

**Proposed:** maintain host-owned request state associated with `HttpContext` through an internal feature, plus an activity-to-request association for processors and log linkage. The state exists even when no recording SERVER activity exists. Its responsibilities are:

- The request's SERVER activity handle and identity, independent of `Activity.Current` becoming a child.
- Consumer identity and request attributes.
- First captured exception, validation details, and Sentry event ID.
- Sampling/exclusion decisions and bounded trace/log buffers.
- Transport completion, body-capture state, and final response measurements.

Consumer identity must survive sampling and be adoptable when set before the SERVER handle is available. Request helpers resolve this state rather than writing indiscriminately to `Activity.Current`.

Suppress framework per-message spans at the source where supported and filter their known kind/name/scope combinations in Apitally's processor. Do not treat websocket messages as HTTP requests.

**Open:** exact association mechanics, context behavior before middleware entry, and cleanup for late-ending descendants and background work retaining request context. Verify isolation across concurrent and keep-alive requests without adding a blanket context reset that destroys legitimate upstream propagation.

## 6. Sampling and per-request buffering

**Inherited:** request and response sampling refine the static rate. Both stages compare the low 64 bits of the trace ID against the shared rounded probability threshold, so their effective combined rate is the minimum rather than the product. An invalid or throwing sampling callback warns and fails open.

Request-stage drops skip trace-detail capture work. Excluded requests never invoke sampling callbacks. Metrics and eligible error aggregates remain independent of all trace-detail decisions.

Response sampling runs once with final route, status, sizes, consumer, and custom request attributes. Abstention preserves the request-stage decision rather than sampling again at the static rate.

Hold ended descendants and application logs until both transport observation and the SERVER activity complete. Keep at most 1,000 spans and 1,000 application log records per request, retaining the earliest arrivals. Release descendants, then the SERVER span, then the request's logs once. A drop discards buffered detail and raw payloads and ensures late detail also drops. Late descendants/logs for a released request remain eligible for export.

### Export snapshots

**Research finding:** .NET `Activity` is not a detached immutable span representation, and public APIs do not provide a faithful clone with the same IDs, source, and kind. The specialized `BatchActivityExportProcessor` accepts `Activity` objects, not an arbitrary export snapshot.

**Proposed:** copy the data required for Apitally export into an SDK-owned snapshot. Preserve IDs, parents, times, status, events, links, scope, and resource. Apply late enrichment and privacy processing to that owned representation. Do not fabricate a second live activity to represent the original request or modify user-owned activities to finish export.

The public generic `BatchExportProcessor<T>` is a candidate for stock queue/worker behavior over owned snapshots. Its suitability, including sampling semantics and log-record ownership, needs a POC before selecting this pipeline.

**Open:** snapshot layout and public callback types. Sampling and body-mask callbacks need request/span data, including custom attributes. A callback that can run after completion cannot safely depend on a retained live `HttpContext` or a fabricated `Activity` clone. Prefer a minimal read-only snapshot if public .NET types cannot represent the required data safely; do not finalize that API before the POC.

**Open:** unfinished-request shutdown policy. It must preserve complete-body guarantees, apply response sampling before any release, and leave user-owned activities untouched. The different Python and JavaScript shutdown policies are examples, not defaults to copy.

## 7. Capture pipeline: bodies, headers, sizes, redaction

**Inherited privacy boundary:** captured headers and body payloads remain private to Apitally. Request-serving code collects bounded data; decompression, masking, JSON processing, and redaction execute outside request handling before attributes are attached to an export snapshot. User exporters must never see Apitally-captured payloads.

Apply the canonical content-type allowlist and 50,000-byte limit. Check headers before body I/O. A known oversized body yields `[BODY_TOO_LARGE]` without reading it; crossing the cap discards buffered bytes. Empty bodies are omitted. Partial bytes from an aborted stream are omitted, while an already-established oversized sentinel can still be exported.

Process bodies in the shared order: bounded decompression, mask callback, parse, field redaction, and serialization. Parse to identify JSON regardless of content type once capture is allowed. Unsupported/failed decompression must not export the original bytes. A failed or dropping mask callback yields `[REDACTED]`; an oversized masked result yields `[BODY_TOO_LARGE]`. The oversized sentinel bypasses body processing.

The body-mask callback sees the export snapshot after query/header redaction and captured-header attachment, but before body attributes are attached. Document that execution may happen later on another thread. A failure in the export redaction boundary drops the affected span rather than sending raw sensitive data.

Header attributes are list-valued, lowercase, and retain dashes. A masked header exports one `[REDACTED]` value. Redact query strings in request URLs and captured `Location`/`Content-Location` values, including stable/legacy query-bearing attributes on descendant spans and attributes supplied by user instrumentation. Defaults and allowlists come from the shared specification, not a separately maintained .NET variant.

**Proposed transport mechanism:** transparent bounded observation of request reads and response writes. Investigate ASP.NET Core body features, including `IHttpResponseBodyFeature`, rather than assuming replacing `Response.Body` covers `BodyWriter`, `SendFileAsync`, flushing, and completion. Preserve the application's streaming and backpressure behavior.

Body size observations are independent of content capture. Use trustworthy declared lengths or complete observed byte counts as the shared design allows; do not read a body merely to determine its size. Unknown size remains unknown. The same resolved sizes feed spans and histograms, including when captured bytes have been discarded after crossing the cap.

**Open:** request `Body`/`BodyReader` coverage, response feature wrapping, compression ordering, completeness detection, and size semantics on cancellation/abort. Whole-response buffering is not the fallback implementation.

## 8. Transport observation, routes, frameworks

**Confirmed:** automatically register the transport integration for modern and `Startup`-based hosting. The user should not need a second middleware call or knowledge of OTel ordering.

**Proposed:** evaluate `IStartupFilter` and supported hosting hooks for automatic placement. This must observe final responses from exception-handling middleware, unmatched routes, and streams. Hooks may be registered during builder configuration, but must not depend on undocumented pipeline ordering.

Prefer stock ASP.NET Core request instrumentation when it satisfies one SERVER activity per request. Apitally-specific capture, metrics, consumer attribution, and errors remain in SDK-owned paths, so reusing user instrumentation does not remove those features.

Route resolution must produce parameterized endpoint templates with applicable path/group prefixes. Preserve the original matched route through exception-handler re-execution where the framework exposes it. Unmatched requests export trace detail without a route and contribute no request histograms or error aggregates. Client address and scheme attribution follow ASP.NET Core's configured forwarding/trust behavior; Apitally does not add a second forwarding-header trust policy.

### Validation and server errors

**Inherited:** automatic framework recognition is the validation API. No public validation-capture method or response-parser callback is added.

**Proposed recognition targets:** MVC model validation and framework-produced validation problem responses, including applicable Minimal API behavior across the supported versions. Confirm the exact supported structures and hooks. Do not infer validation from every 400 response or install MVC services into an application that does not use MVC.

Validation response observation is independent of trace sampling and response-body logging. Parsing requires a complete eligible response and retains at most 50,000 bytes. Retaining bytes for validation never enables exporting those bytes as a captured response body.

Normalize `source`, `field`, `message`, and `type` at the adapter boundary. Preserve useful field strings; do not split and reconstruct dotted model keys. Use empty values when source/field information is genuinely unavailable.

Request-local error state retains the first captured exception, independent of a recording span. Automatic hooks and `IApitally.CaptureException(...)` update the same state and record at most the first SDK exception event on the SERVER span. Exclude request cancellation and unwrap a single-leaf aggregate where appropriate. Commit error data once at transport completion:

- Validation details contribute their normalized groups for eligible routed requests.
- A captured exception contributes a server error only when final status is exactly 500.
- An intentional 500 without a captured exception contributes no server-error group.
- `OPTIONS`, websockets, and unmatched routes contribute neither category.

**Open:** framework exception hooks across .NET 8/9/10, especially handled exceptions and diagnostics suppression, route re-execution, cancellation identification, and duplicate events from user instrumentation. Catching only exceptions that escape the middleware is not a complete design.

## 9. Logs

### Application logs

**Inherited:** capture through the standard `ILogger` abstraction into Apitally's private logging pipeline. Preserve the user's logging output, providers, and applicable category thresholds. `CaptureLogs = false` disables application-log capture, not the private pipeline or internal events.

**Proposed:** an additive `ILoggerProvider` capture adapter forwarding to a separately owned OTel logger provider. Do not call the application's OTel logging registration as a shortcut to creating a private provider. Prove behavior with common logging-factory replacements before claiming universal coverage.

Resolve `apitally.request.server_span_id` through the activity-to-request association, preserving the emitting child span ID separately. Application logs without a request association are dropped. Exclude Apitally's and the OTel SDK's own diagnostic logs from capture to prevent feedback loops. Capture code-location attributes when supplied by the logging interface; do not invent stack inspection solely to manufacture them.

Run `MaskLogRecord` synchronously on the private captured record before buffering. It may return the supplied record or drop it; exceptions or a replacement record drop the record. Isolate mutable state so the callback cannot change what other application logging providers receive. Truncate string bodies and string attributes to 2,048 characters after masking and before buffering/export.

**Research finding:** OTel .NET log records can be pooled and returned after synchronous processing. Their public mutability does not make arbitrary delayed retention safe. Original structured state can also be shared with other providers.

**Open:** safe copying/ownership, callback type, scopes and structured state, category-to-scope mapping, and private provider construction. An owned log snapshot plus stock generic batching is a candidate, not an approved replacement for the official log processing model. Returning from a processor does not inherently stop later processors; drop behavior needs explicit forwarding control.

### Startup event

Emit `apitally.app.startup` through the private logs pipeline with scope `apitally` and no trace/span context. The body is a JSON string containing:

- `framework = aspnetcore`
- Runtime/framework versions and `versions["app"]` when configured.
- Resolved SDK settings, including defaults, with the shared secret/metadata exclusions and callback/pattern representations.
- Registered route templates and method metadata.
- Available OpenAPI JSON, omitted above 4,000,000 bytes while retaining paths.

**Proposed:** obtain finalized endpoints from ASP.NET Core endpoint data sources. Exact support for built-in OpenAPI, Swashbuckle, NSwag, and multiple documents is open. Do not require an HTTP request to the application's own schema endpoint as an assumed discovery mechanism.

The shared contract says once per serving process. Emitting once per serving host is the natural host-owned adaptation, but its interaction with process identity and multiple hosts must be reviewed explicitly under section 2.

### Error aggregates

Use the shared validation/server aggregation identities, truncation rules, positive `UInt32` count range, and latest-nonempty Sentry enrichment rule. Limits are 100 validation and 100 server groups between drains in the shared process model; their multi-host scope remains open as noted in section 2.

Drain atomically, then emit outside the synchronization boundary immediately before the logs pipeline flushes in ordinary and final cycles. Each aggregate has the native event name and a structured OTLP object body, not the startup event's JSON-string body. It carries no request trace context and bypasses application-log masking/truncation.

**Open:** represent structured internal-event bodies through the .NET private logging/export path. The public `LogRecord.Body` string property alone does not express this wire contract; verify an explicit internal-event representation and mapping rather than serializing every event as a string.

## 10. Export pipeline

**Inherited:** stock batching machinery feeds SDK-owned OTLP encoding, a write-through spool, and one export worker per runtime. Delivery is HTTP/protobuf; there is no stock OTLP network exporter and no additional retry policy layered underneath.

### Encoding and batching

**Research finding:** the .NET OTLP exporter's protobuf serializers are internal. The package does not expose a public encode-only API. Its network exporter is not a drop-in spool encoder.

**Proposed:** generate message types from pinned official OTLP protobuf definitions and serialize with the official protobuf implementation. This reuses the protocol and encoder while leaving the conversion from owned span/log snapshots and OTel metric data as SDK-owned code. Prove field/value mappings and concatenated-request decoding before committing to the dependency/layout choice.

Use stock batch queue/worker machinery with explicit settings and approximately one-second intake delay. Investigate `BatchExportProcessor<T>` for snapshot intake rather than private reflection into OTel objects. Bound encoded appends by actual size; a record-count chunk limit alone is not proof that a file stays below the cap.

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

**Proposed:** a host-owned worker drives metric collection and spool delivery. Use .NET background execution appropriate to the work: HTTP sends may be asynchronous, while CPU-bound body processing must remain outside request-serving execution. Keep concurrency close to the shared model rather than introducing a task or thread per request/span.

**Open:** exact batch processor settings, protobuf generation/distribution, metric snapshot lifetime, HTTP handler/proxy configuration, spool concurrency, and the host-shutdown budget. Preserve the shared limits while resolving their multi-host ownership explicitly.

## 11. Metrics

**Inherited:** record in the transport integration, independently of activities and trace sampling. Use a private meter/provider and the scope name `apitally`.

| Instrument | Aggregation | Unit |
| --- | --- | --- |
| `http.server.request.duration` | Delta exponential histogram | `s` |
| `http.server.request.body.size` | Delta exponential histogram | `By` |
| `http.server.response.body.size` | Delta exponential histogram | `By` |

Duration is the count anchor. Its attribute tuple is shared with size observations: request method, parameterized route, final status, and optional consumer identifier. Add `url.scheme` and the shared 5xx `error.type` convention. Skip `OPTIONS`, websockets, and unmatched routes; retain eligible excluded/sampled-out requests. Duration and response sizes reflect transport completion, not merely endpoint return.

**Proposed:** configure histogram-specific views with `Base2ExponentialBucketHistogramConfiguration` and a non-periodic metric reader collected by the export worker. Prefer maximum scale 3 if publicly configurable; otherwise preserve accepted native scales in [-2, +20]. Do not change unrelated instrument aggregation.

Observe normalized process CPU utilization, RSS-equivalent bytes, and uptime using direct .NET process/runtime APIs or suitable isolated instrumentation. CPU and memory need paired observation times within the server's one-second tolerance. Uptime keeps collections nonempty even without traffic or with CPU/memory disabled.

.NET OTel has delta metric-point reclamation and configurable cardinality limits. These must be evaluated together: an overflow point without the required endpoint/consumer dimensions is not equivalent to an accepted request histogram. **Open:** choose supported aggregation/reader settings that avoid indefinite accumulation of inactive consumers and characterize actual capacity behavior without inventing a new product-level consumer limit.

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

Generic Host with `Startup` receives an appropriate builder registration entry point backed by the same implementation. Its precise receiver/signature awaits the hosting POC. The explicit user-provider integration also remains open; do not publish a signature before validating its registration and ownership semantics.

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

Database instrumentation is explicit opt-in. **Proposed:** use normal OTel registration rather than invent per-database wrapper APIs where the ecosystem already provides a one-line registration method. The supported customization path and handling of app-defined activity sources still need design review.

### Migration contract

Document the write-token replacement, enabled-by-default logging, capture option names/defaults, consumer helper, sampling callbacks' keep semantics, and the new setup path. Preserve familiar .NET concepts, not obsolete Hub payload types or configuration behavior that conflicts with the shared defaults.

## 14. Sentry integration

**Inherited target:** automatically detect a usable Sentry integration and attach an event-pipeline hook without an Apitally enable flag. Only the Sentry exception event ID crosses the integration boundary.

The ID must be attachable to the SERVER export snapshot and eligible undrained server-error aggregate, including when Sentry processes the exception after activity end. Already-exported telemetry is not updated retroactively.

**Open:** supported Sentry versions, dependency/package strategy, public event-processor registration, host scoping, and exception-processing order. Research these before choosing reflection, a hard dependency, or a separate integration package. A process-wide "last event ID" lookup is not a substitute for request-correlated capture.

## 15. Cross-language posture and explicit adaptations

| Shared design area | .NET treatment | Status |
| --- | --- | --- |
| Process-global configuration/runtime | Host-owned DI runtime with independently owned shutdown. | Confirmed adaptation. |
| Code options and environment fallbacks | Add the standard `Apitally` configuration section as setup options below explicit code values. | Confirmed adaptation. |
| Unified setup | Native builder registration for both supported hosting styles; automatic transport integration. | Confirmed API direction; mechanism needs POC. |
| Provider activation/attachment | Prefer supported DI construction-time registration; explicit advanced path for separately built providers. | Confirmed direction; exact mechanism open. |
| Manual block and function forms | Native `Activity` scope via `IApitally.StartActivity`. | Confirmed adaptation of the shared SHOULD. |
| Activation failure scope | One attempt per host runtime. | Proposed consequence of host ownership. |
| Configuration timing and repeated calls | Resolve through host construction; host-local registration semantics. | Open; do not silently impose process-global first-call behavior. |
| Process identity, startup frequency, limits, process gauges | Must be reconciled with multiple host runtimes. | Open; existing process-wide requirements still apply until explicitly resolved. |
| Ordinary final drain | Host lifetime and cancellation-budget integration. | Proposed; exact policy open. |
| SDK span/log representations | Owned export snapshots and potentially generic stock batch processors. | Proposed; public API and ownership POCs required. |
| Encoding | Official OTLP schemas/protobuf encoding with SDK-owned mapping. | Proposed .NET mechanism; no change to HTTP/protobuf delivery. |
| Runtime-specific fork and signal mechanics | Use .NET host lifecycle instead. | Platform adaptation. |

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
- Multiple host lifetimes and correct request association without cross-host exports.
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

These are proposed investigations, not approved execution steps. Keep them isolated from the production SDK and record outcomes back into the relevant sections.

| POC | Questions and acceptance evidence |
| --- | --- |
| Provider registration and host ownership | Both DI registration orders; explicit external provider; default HTTP instrumentation; existing instrumentation; user's sampler/exporters unchanged; first request captured; two active hosts correctly associated; one host can stop without disabling the other. |
| Middleware placement and completion | Modern and `Startup` hosting; controllers and Minimal APIs; exception-handler final responses and route re-execution; streamed responses, `BodyWriter`, and file sends; both transport/activity completion orders; abort/cancellation behavior. |
| Private export snapshots and batching | Preserve span identity/events/links/resource; late enrichment without original mutation; no captured payloads in user exports; maximum-size complete bodies; bounded release/drop and late descendants; public stock batching over the selected representation. |
| Private logging and internal events | Additive `ILogger` capture; category filtering, scopes, mutable state isolation, masking/drop; pooled-record lifetime; request linkage through child activities; startup JSON string versus structured error bodies; event names and context-free internal records. |
| Encoding, metrics, and delivery | Official protobuf round trips for all signals; binary bodies and exponential histograms; concatenated request decoding; actual encoded-byte rotation limits; delta collection/reclamation and capacity behavior; idle liveness; immutable retries, proxy binding, and instrumentation suppression. |
| Error and optional integration hooks | Conservative MVC/Minimal API validation; first exception and final-500 rule; .NET 10 handled-exception diagnostics; request cancellation; Sentry ordering and late enrichment; finalized route/OpenAPI discovery. |
| Host shutdown | Server/request draining relative to SDK/provider disposal; ordinary final cycle; host cancellation budget; unfinished request policy; no duplicate release or retained host state after disposal. |

## 19. Next design decisions

The interview has settled support scope and the main user-facing direction. The next review should resolve:

1. Process identity, process-wide bounds, startup events, and process measurements under host-owned runtime state.
2. Configuration resolution/re-registration semantics and exact builder/provider integration APIs.
3. Public callback snapshots and log ownership, informed by the .NET API constraints.
4. The transport completion model and unfinished-request shutdown policy.
5. Validation, Sentry, OpenAPI, package boundaries, and dependency floors based on actual supported hooks.

These remain open rather than being filled with assumptions from another SDK. Implementation approval follows review of this document and the relevant POC results.

## Research references

Local source snapshots used for the initial review:

- Shared SDK documents: cloud commit `f22ee6c0`, `docs/sdks/spec.md` and `docs/sdks/design.md`.
- Python reference: `ddf5127cd5e16fec6e89eed41b1965c202e03b73` (`v1.0.0b3`).
- JavaScript reference: `16ed4266a637a944b9962b00ddad2a626759eeb6` (following `v1.0.0-beta.2`).
- .NET v0 baseline: `65e25ed13e15c6d6b77125749eba5cece6aa008f`.

Upstream source research included OTel .NET 1.19.1, ASP.NET Core instrumentation 1.19.0 with an older-version comparison, and .NET runtime 8/10. These are research snapshots, not selected dependency floors.

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
