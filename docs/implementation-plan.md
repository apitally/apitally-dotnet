---
directory: /Users/simon.gurcke/Repos/apitally/apitally-dotnet
implemented_at: null
---

# Apitally .NET v1 implementation plan

Status: Proposed, 2026-09-28. This document plans implementation; it does not authorize production changes.

## 1. Scope and approach

Implement [the .NET design](design.md) as one ASP.NET Core distribution in the existing `Apitally` NuGet package. Use the official OpenTelemetry SDK for instrumentation, native metric aggregation and batch processing. Apitally owns request decisions, private snapshots, payload privacy, OTLP mapping, the spool and HTTP delivery.

The simplest appropriate architecture is:

- One host-owned runtime and one automatic transport middleware.
- One request state shared by middleware, activity processing, logging and request helpers.
- One tracing integration that joins an application provider or constructs an owned fallback.
- Two stock batch processors, for owned span and log records, plus one delivery worker. Metrics are collected by that worker.
- One spool implementation using either file streams or memory streams, selected at construction.
- Concrete internal classes with narrow responsibilities. Add interfaces only for the public `IApitally` service and framework/OTel extension points.

Preserve the confirmed boundaries: .NET 8/9/10, modern hosting and Generic Host with `Startup`, ordinary single-host operation, and managed deployment. Native file sends keep their native delivery path and omit body capture. Sentry, full OpenAPI capture, Native AOT qualification and special multi-host coordination remain outside v1. Keep the generic `ILoggerProvider` integration; the closed Serilog finding in [the design review](design-review.md) does not require another integration or release gate.

### Contract baseline

Use the shared [specification](../../cloud/docs/sdks/spec.md) for wire contracts and [design](../../cloud/docs/sdks/design.md) for shared behavior, with the explicit .NET adaptations taking precedence over shared mechanisms. This plan was prepared against .NET commit `23f4dd01855a817af076418bae64fce8c5663a98` and cloud checkout `9ff1af09`.

## 2. Project and dependency choices

- Keep one production project, `src/Apitally/Apitally.csproj`, targeting `net8.0` with C# 12, nullable reference types and the ASP.NET Core framework reference. A single net8 assembly can serve the supported runtimes; current production requirements do not justify three target frameworks.
- Retain the existing solution, xUnit, formatting, coverage and package-publishing infrastructure. Multi-target the test project and test application to `net8.0;net9.0;net10.0`. Use conditional compilation only in test fixtures for genuinely version-specific features, such as .NET 10 Minimal API validation.
- Start with OTel SDK and hosting packages at the tested 1.19.0 minimum, and the tested ASP.NET Core and HTTP instrumentation versions. Qualify the entire resolved graph, including `System.Diagnostics.DiagnosticSource`, rather than equating the target framework with the loaded dependency versions.
- Use `Google.Protobuf` and vendored official OTLP v1.11.0 schemas. Generate internal message classes at build time with build-only `Grpc.Tools`, with gRPC service generation disabled. Keep schema provenance, hashes and license in the repository; generated files belong in `obj/`, not the source tree or public API.
- Use framework `System.Text.Json`, compression, HTTP and process APIs. Remove legacy-only dependencies, including the Hub retry library. There is no stock OTLP network exporter in the production delivery path.
- Keep selected versions explicit and record the qualified dependency graph. Do not add older-OTel compatibility branches or a new dependency-management framework for one library.

## 3. Proposed folder, file and class structure

Files below are the intended end state, not an instruction to create empty scaffolding. Small private records, enums and framework wrapper types can stay beside their owner. All types are internal unless marked public.

Keep the public API and project file at the source root. Group internal implementation by the responsibility that owns the behavior, rather than by its caller or execution thread. Keep each implementation folder flat within the single library project.

| Location | Responsibility |
| --- | --- |
| Source root | Public setup, options, request helper interface and callback snapshot contract |
| `Hosting/` | Configuration, startup, activation and shutdown |
| `Requests/` | Request state, associations, sampling, helpers and request-derived aggregation |
| `AspNetCore/` | HTTP observation and ASP.NET Core-specific adapters |
| `Tracing/` | OTel tracing integration, activity processing and span snapshot construction |
| `Logging/` | Application log capture, log masking, internal event emission and SDK diagnostics |
| `Metrics/` | Native metric instruments, aggregation configuration and process measurements |
| `Export/` | Export-value normalization, payload privacy, batching, OTLP encoding, storage and delivery |
| `Protos/` | Vendored protocol definitions and provenance |

For example, `AspNetCore/ValidationCapture.cs` extracts framework validation details, `Requests/ErrorAggregates.cs` groups eligible request outcomes, and `Logging/InternalEvents.cs` emits the resulting internal events.

Use the `Apitally` prefix where library identity is useful: public setup/service API types and framework integration components, such as `ApitallyOptions`, `IApitally`, `ApitallyMiddleware` and `ApitallyLoggerProvider`. Name other implementation and data types by responsibility, such as `RuntimeConfiguration`, `RequestHelpers` and `SpanSnapshot`; their namespace establishes ownership. Public visibility alone does not require the prefix. Test class and file names follow the types they test.

```text
Apitally.sln
src/Apitally/
  Apitally.csproj
  ApitallyExtensions.cs                 public IServiceCollection AddApitally extension
  ApitallyOptions.cs                    public flat options and callback properties
  IApitally.cs                          public request helper interface
  SpanSnapshot.cs                       public span type passed to span callbacks
  LogRecordSnapshot.cs                  public mutable log record passed to MaskLogRecord
  Hosting/
    RuntimeConfiguration.cs            base options configuration and frozen runtime settings
    TelemetryRuntime.cs                 preparation, activation and final shutdown
    ApitallyStartupFilter.cs            automatic outer middleware registration
    ApitallyHostedService.cs            final lifecycle drain
  Requests/
    RequestHelpers.cs                  IApitally implementation
    RequestState.cs                     request data, completion flags and buffers
    RequestRegistry.cs                  feature access, associations and release
    RequestSampling.cs                  exclusions and deterministic sampling
    ConsumerUpdates.cs                  normalization and bounded hash cache
    ErrorAggregates.cs                  bounded validation/server group maps
  AspNetCore/
    ApitallyMiddleware.cs               HTTP observation and completion
    BodyCapture.cs                     bounded capture state and shared size result
    ObservedStream.cs                  transparent read/write observation
    ObservedBodyFeatures.cs            reader/writer, response and abort wrappers
    EndpointMetadata.cs                 route resolution and startup enumeration
    ValidationCapture.cs                MVC/problem-details hooks and known shapes
  Tracing/
    TracingIntegration.cs               DI contribution, attachment and fallback
    ApitallySpanProcessor.cs            activity start/end into RequestRegistry
    SpanSnapshots.cs                    owned copying and final HTTP enrichment
  Logging/
    ApitallyLoggerProvider.cs           additive ILogger adapter and scope merge
    LogMasking.cs                       mask invocation and owned log copying
    LogSnapshot.cs                      buffered application/internal log entry
    InternalEvents.cs                   startup and structured event emission
    SdkDiagnostics.cs                   SDK logging and warning deduplication
  Metrics/
    ApitallyMetrics.cs                  private meter, views, reader and histograms
    ProcessMetrics.cs                  paired process observations and uptime
  Export/
    AttributeValues.cs                 common normalization and value ownership
    SpanRedaction.cs                   query/header/body export privacy boundary
    BatchProcessor.cs                  stock generic batching
    OtlpEncoder.cs                     shared values, resources and bounded requests
    OtlpTraceMapper.cs                 span/event/link/scope mapping
    OtlpLogMapper.cs                   canonical bodies, event names and log truncation
    OtlpMetricMapper.cs                synchronous native metric mapping
    TelemetrySpool.cs                  file/memory storage and retention
    SpoolFile.cs                       one continuous gzip stream and file metadata
    ExportWorker.cs                    ordinary/final cycles and HTTP delivery
    ExportHttpClient.cs                frozen endpoint/proxy/headers and POST results
  Protos/
    README.md                          upstream revision and regeneration instructions
    LICENSE
    SHA256SUMS
    opentelemetry/proto/...             required common/resource/trace/log/metric schemas

tests/
  Apitally.TestApp/
    Apitally.TestApp.csproj
    Program.cs                         minimal and Startup host compositions
    Startup.cs                         Generic Host coverage
    Controller.cs                      controller and validation routes
    ApitallyTestExtensions.cs           shared small route/feature fixtures
  Apitally.Tests/
    Apitally.Tests.csproj
    Hosting/  Requests/  AspNetCore/  Tracing/  Logging/  Metrics/  Export/
    Integration/                        scenarios spanning request capture through OTLP delivery
    Support/                            real hosts, loopback OTLP receiver and common assertions

docs/
  design.md
  design-review.md
  implementation-plan.md
  migration.md                         v0 to v1 usage changes
README.md                              installation, setup and supported customization
pocs/                                  retained research; not production dependencies
.github/workflows/                     updated existing test/publish workflows
```

`SpanSnapshot` is the only public span type; events, links and resource use the existing `ActivityEvent`, `ActivityLink` and OTel `Resource` types. Request decisions, captured payloads, batch entries and aggregate keys stay with their owning implementation. Add test files when the corresponding module has meaningful observable behavior; do not create a test for every forwarding wrapper.

Module-focused tests mirror the production module folders. Organize by test scope: a real-server body observation test belongs in `AspNetCore/`, while a scenario spanning request capture through OTLP delivery belongs in `Integration/`. Keep shared test fixtures in `Support/` and module-specific helpers beside their tests.

The shared harness requires separately reviewed changes in `../sdk-tests`: a `DotNetLanguage` implementation in `harness/languages.py`, SDK path configuration, `dotnet/aspnetcore/` application and manifest, and .NET entries in `variants.toml`. That application implements the existing harness surface and flags with ordinary application configuration.

## 4. Public API and configuration

### Proposed API

Provide a single public entry point, `IServiceCollection AddApitally(this IServiceCollection services, Action<ApitallyOptions>? configure = null)`, returning the service collection. Document it as `builder.Services.AddApitally()` for modern hosting and `services.AddApitally()` in `Startup.ConfigureServices`. There are no host-builder overloads and no second middleware call. Everything it registers is an ordinary service registration: options, the logger provider, the startup filter, the hosted service, the tracing contribution and `IApitally`. Use `TryAdd*` and `TryAddEnumerable` so repeated calls register each SDK service once.

Keep `IApitally` small:

```csharp
void SetConsumer(
    string identifier,
    string? name = null,
    string? group = null,
    IReadOnlyDictionary<string, string?>? attributes = null);
void SetRequestAttribute(string name, object? value);
void CaptureException(Exception exception);
Activity? StartActivity(string name);
```

`StartActivity` creates an INTERNAL activity from `apitally.otel`; callers use native activity tags and `using`. The singleton implementation resolves the current request through `IHttpContextAccessor` and its private feature on each call. It never stores a `HttpContext` in the singleton. Helpers are safe no-ops without an active monitored request or when disabled; helper updates target the SERVER handle, not a current child activity.

Use these flat options: `WriteToken`, `Env`, `AppVersion`, `Disabled`, `CaptureLogs`, `CaptureRequestHeaders`, `CaptureRequestBody`, `CaptureResponseHeaders`, `CaptureResponseBody`, `SampleRate`, `SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody`, `MaskResponseBody`, `MaskLogRecord`, `MaskQueryParams`, `MaskHeaders`, `MaskBodyFields`, and `ExcludePaths`.

Sampling delegates are `Func<SpanSnapshot, double?>`; body delegates are `Func<SpanSnapshot, byte[], byte[]?>`; log masking is `Func<LogRecordSnapshot, LogRecordSnapshot?>`. Pattern properties are `List<string>`. Optional strings and delegates are nullable; ordinary booleans and numbers need no assignment-tracking wrappers.

### Resolution

Use the standard .NET Options pattern; there is no custom callback store or resolver.

1. The first `AddApitally` call registers one base `IConfigureOptions<ApitallyOptions>` with `TryAddEnumerable`. It applies the applicable `OTEL_*` fallbacks, then the `APITALLY_*` fallbacks, then binds the `Apitally` configuration section. Property initializers supply the defaults.
2. Each `AddApitally(configure)` call registers its callback with `PostConfigure`, so code callbacks run after all configuration steps, in registration order, each seeing earlier changes.
3. A direct `services.Configure<ApitallyOptions>(...)` runs in the ordinary configure phase with standard .NET ordering: after the base step if registered after `AddApitally`, before it otherwise. Document this; the `AddApitally` callback remains the recommended path because it always wins.
4. During startup preparation, read `IOptions<ApitallyOptions>.Value` once. Apply additive disable environment controls, validate credentials/settings and copy into the private immutable `RuntimeConfiguration`. Copy collections; never retain the mutable options object, use `IOptionsMonitor` or reread configuration during operation. Do not use `ValidateOnStart` or `IValidateOptions`, so invalid settings cannot fail host startup.
5. Default host builders read unprefixed environment variables into configuration, so keys such as `Apitally__SampleRate` populate the section without Apitally code. Only the shared `APITALLY_*` and `OTEL_*` fallback names need explicit mapping in the base step.
6. Invalid token disables telemetry with an error and no host-startup failure. Invalid static rate becomes `1.0`; reject invalid regexes individually. Compile/precompute patterns once, using case-insensitive search plus the caller's inline options. Built-in redaction and exclusion defaults use `[GeneratedRegex]`; user patterns use runtime `Regex` instances. Use a finite match timeout; privacy-boundary failures drop affected telemetry instead of exporting raw data.
7. Freeze the testing-only `APITALLY_OTLP_ENDPOINT` here, and capture `HttpClient.DefaultProxy` once as the delivery proxy. Generic OTel exporter variables never configure Apitally delivery.

Build the private OTel resource once, with Apitally-owned instance/environment/distro keys applied last. A process-local UUID value, regenerated on process restart, is the only process-wide identity state needed; do not add a global runtime or host coordinator. Apitally copies of user spans override only instance/environment while retaining other user resource attributes.

## 5. Hosting, tracing and request ownership

### Preparation and activation

Keep preparation separate from activation. `IStartupFilter` resolves the finalized configuration and actual `IServer`, installs the outer observer and prepares tracing before the server can accept its first request. Recognize TestServer only by the confirmed exact type and assembly names; disabled/suppressed hosts construct no private fallback providers, spool or workers.

Contribute tracing through `ConfigureOpenTelemetryTracerProvider`, which does not independently enable a host provider. Preserve user samplers, resources, exporters and configured instrumentation in both registration orders. Resolve the enabled DI provider during preparation; if none exists, construct the private fallback with an explicit always-on sampler, ASP.NET Core and outgoing HTTP instrumentation, and the manual source. Use the tested additive ASP.NET/manual-source registration where building a DI provider. Configure-only user callbacks do not customize the private fallback.

For an existing-instance DI registration, attach the processor through the public post-build API. Its owner retains disposal responsibility and must have configured instrumentation/source subscriptions before building it. Make the attached processor a small detachable forwarding object: after Apitally stops, its retained presence in an external provider must not retain request buffers, callbacks, spool resources or the host runtime. Providers created through application DI remain host-owned, not Apitally-owned.

Activate in the same startup filter, immediately after `next(app)` returns. `GenericWebHostService.StartAsync` builds the request pipeline before calling `Server.StartAsync`, so activation always completes before the server can accept a request, and routes registered by the application pipeline are final for the startup event. Hosts that are built but never started, such as design-time tools, never reach this point. There is no `ApplicationStarted` trigger, first-request fallback or per-request activation check; middleware is constructed against an already-activated or disabled runtime.

`TelemetryRuntime` has a small lifecycle: active, stopping and stopped, or disabled. Preparation or activation failure logs an error and leaves that runtime disabled; the application keeps serving without telemetry.

Register `TelemetryRuntime` as a DI singleton implementing `IAsyncDisposable`. If the host fails after activation, for example because Kestrel cannot bind, `StoppedAsync` may never run; container disposal then stops the workers and disposes owned resources without a final delivery. Disposal after the normal shutdown sequence is a no-op.

Construct workers with execution-context flow suppressed, so activation carries no ambient activity or execution context into background work. Avoid DI/logging cycles: the additive logging provider starts as an inert adapter; diagnostic logger resolution happens during preparation/activation, not while the application's logger factory is constructing its providers.

### One request state

Reuse the mechanism established by the [request-association POC](../pocs/request-association/README.md), not its prototype retention policy:

- Register `IHttpContextAccessor`. At SERVER activity start, HTTP tags may be absent; use the early context to create/adopt `RequestState` in a private feature and, only when `SampleOnRequest` is configured, populate the request-stage snapshot. Middleware later retrieves that same state.
- Retain a SERVER handle separately from `Activity.Current`. State exists for metrics, errors and consumers even when the user sampler provides no recorded SERVER span.
- Key associations by trace/span identity, never trace ID alone. A local-root SERVER activity, including one with a remote parent, starts a request association. Children inherit through their parent ID, including explicit parent contexts. Missing/unrelated associations drop locally.
- Require the request to be observed by this integration before releasing user-provider detail. Check `Activity.Recorded` explicitly; generic batch processing does not apply that policy.
- Apply exclusions before request sampling. Both sampling stages compare the same low 64 trace-ID bits with the rounded threshold, with explicit handling of zero/one. Request abstention uses the static rate; response abstention preserves the request decision. Throwing or invalid callbacks warn and fail open for that stage; they cannot restore previously dropped detail.
- Serialize request-local changes with a short lock. Under it, update flags and claim the once-only finalization/release. Run callbacks, snapshot processing and queue submission outside locks, using a claimed finalization state so late arrivals cannot bypass the initial release.

Transport completion commits eligible metrics/errors once. Trace/log release additionally requires SERVER end. Build response sampling input from the final route, status, sizes, consumer and custom attributes, even if those arrived after span end. Release retained descendants, then SERVER, then logs, once. Retain the earliest 1,000 spans and 1,000 application logs per request; reserve the request's SERVER export rather than allowing descendant saturation to remove the request boundary.

After release, discard raw payloads, full request state and the request's ID associations. Spans and logs that arrive after release find no association and drop locally, like any other unassociated telemetry. In ASP.NET Core this affects only work that outlives the response, such as fire-and-forget tasks: Kestrel finishes the response, runs `OnCompleted` callbacks and writes the "Request finished" log before it stops the SERVER activity, so streaming and end-of-request telemetry always arrive before release. There is no completed-request cache.

Bound captured payloads and queued detail as specified; do not claim an absolute process-memory bound independent of application request concurrency.

## 6. Owned values, snapshots and privacy

### Common value representation

`AttributeValues` implements the selected stock-exporter-aligned CLR conversion once for both signals. Normalize and detach before any callback. Scalars become the corresponding OTLP-compatible CLR scalar; bytes, arrays and maps are copied. Ordinary lists/objects use invariant string conversion, not reflection-based object traversal. Implement this type mapping; do not reproduce or test the stock converter's internal edge-case behavior, such as its failure omission rules or map depth, as an Apitally contract.

Propose these public snapshot representations:

- Native activity IDs, flags, kind and status types; start time and nullable end time; optional status description and trace state.
- Events as `IReadOnlyList<ActivityEvent>`, links as `IReadOnlyList<ActivityLink>`, resource as OTel `Resource`, and scope as `ScopeName` and `ScopeVersion` properties. No Apitally-specific event, link or scope types.
- Attributes as `IReadOnlyDictionary<string, object?>` over the owned dictionary. Values use the plain CLR types OTel .NET users see on activity tags: scalars, `string[]`, `long[]`, `double[]`, `bool[]`, `byte[]` and normalized maps.

Snapshots contain the available identity, parent, name, timing, status, attributes, events, links, resource and scope at that stage. Unknown data stays unset. A `SpanSnapshot` is the SDK-owned span record itself, not a per-callback copy. Copy from the `Activity` once for the request stage when `SampleOnRequest` is configured and once at final completion, then reuse the final record for response sampling, redaction, body masking and export. Read-only interfaces state intent only: the SDK does not defend against callbacks that cast and mutate values, or that retain a snapshot and observe later enrichment. Preserve available metadata without fabricating information that public OTel APIs do not expose.

For logs, normalize and detach values before the mask, so the mask sees the values that will be exported before truncation, then buffer the accepted `LogRecordSnapshot` without another copy. Convert any values the callback added, and truncate strings, when the batch worker encodes the record. Truncate to 2,048 UTF-16 code units (`string.Length`), matching the JavaScript SDK; a split surrogate pair is harmless because Google.Protobuf encodes with replacement. Apply this to scalar string bodies/attribute values, including values converted to strings; non-string array/map attributes pass through untruncated.

### Request-thread versus export-thread work

Request-serving work is limited to association, decisions, bounded observation/copying, synchronous log masking, framework validation recognition and native metric/error recording. Captured-body decompression, masking, JSON processing, query/header redaction and OTLP encoding run in the stock span batch worker. No task is created per request or span.

`SpanRedaction` processes every exported span, including descendants and user-produced attributes:

1. Normalize stable/legacy HTTP fields and redact all query-bearing URL forms and captured header attributes, including `Location` and `Content-Location` query strings.
2. Attach private redacted headers as lowercase, dash-preserving, list-valued attributes. A masked header has exactly one `[REDACTED]` value.
3. Pass the redacted owned record to the body masks at this point, before body attributes are attached.
4. Handle capture sentinels; otherwise bound decompression, call the body mask, parse JSON regardless of allowed content type, redact matching string-valued fields recursively, then serialize. Preserve non-JSON UTF-8 text or permitted body bytes.
5. Use callback output directly; it is consumed in this step and only the serialized result is retained. Dropped/throwing masks produce `[REDACTED]`; oversized input, decoded or replacement bodies produce `[BODY_TOO_LARGE]`. Unsupported/failed decompression never passes through original bytes.
6. A failure of the overall privacy boundary drops that span. No captured payload is ever attached to a live user activity.

Centralize shared names, patterns, content types and limits with their owning modules and verify them against the canonical spec. Do not port the older .NET allowlist or masking rules unchanged. Applicable owned span limits must permit 65,536 characters; inspect/warn about lower user limits only where public APIs expose them. Owned body attributes are added after activity copying and must not be clipped by generic attribute normalization.

## 7. Transport, routes and errors

Adapt only the ordinary observation paths from the transport POCs:

- Wrap request reads and response writes through stream/body features, covering `BodyReader` and `BodyWriter` without double-counting shared stream paths. Preserve sync/async behavior, cancellation, flushes, backpressure and application exceptions.
- Observe only request bytes the application consumes. Check headers before allocating capture storage or doing body I/O. Copy leased pipe memory before returning it and commit capture/counts only for successfully accepted operations.
- Keep independent byte counts and capture state. A disallowed or sampled-out body can still have a known size; discarded oversized bytes do not erase size observations.
- At transport completion, freeze the applicable content length, observed counts, EOF and simple incomplete flag. Mark directly observed read/write/advance/flush/completion failures, escaped errors, explicit aborts and already-visible cancellation. Omit known incomplete bytes; complete handled error responses remain eligible. Do not add transport-success certification or cancellation settling.
- Delegate `SendFileAsync` unchanged and invalidate the entire response capture, including mixed output. Do not reread files or replace native dispatch. Ordinary observed stream output remains eligible even if the application generated it from a file.
- Resolve one request-size and one response-size value and reuse them in the span and histograms. Do not trust conflicting chunked/length headers or label a partial count as a complete body size. Unknown remains absent.

Use `OnCompleted` for final transport observation, not as proof of client receipt. Keep the observer outside application exception handling/compression so final synthesized responses and compressed trailers are observed. Validate its position in both hosting styles, including Development exception responses and common earlier short-circuits; do not promise ordering against every third-party startup filter.

`EndpointMetadata` resolves parameterized routes, group/path prefixes and original endpoints exposed by exception re-execution. Unmatched requests keep route unset. Read scheme/client address from the framework's trusted-forwarding result at the appropriate final observation point; do not parse forwarding headers independently. At startup, enumerate finalized route/method pairs and native summary/description metadata without an OpenAPI dependency.

`ValidationCapture` wraps existing MVC invalid-model-state and problem-details callbacks without changing their output or installing MVC in Minimal-only applications. Prefer typed details; otherwise recognize only the tested standard complete 400/422 response shapes, allowing localized/custom message strings. Share a bounded response observation buffer where useful, but keep validation eligibility separate from trace/body-capture flags. Validation-only bytes never become exported body attributes. Skip unknown formats and preserve opaque fields; use empty unavailable source/field values.

The request state preserves the first eligible exception from helpers, escaped errors or exception-handler features. Exclude request cancellation and unwrap a single-leaf aggregate. Add at most the first SDK exception event to the SERVER representation without duplicating an already-observed equivalent instrumentation event. Later transport enrichment uses the private copy if the activity has ended.

At completion, aggregate validation details and captured exceptions only for eligible routed requests. A server error requires final status exactly 500 plus a captured exception. Pattern exclusions, sampling and application-log settings do not change this path; OPTIONS, websockets and unmatched routes contribute neither error category.

## 8. Logs and internal events

The additive logger provider carries `[ProviderAlias("Apitally")]`, so it obeys ordinary provider-independent category filters and users can narrow capture with standard `Logging:Apitally:LogLevel` configuration. It builds Apitally's log records directly; there is no private OTel logger provider. `ILogger` has no ecosystem record type, and an OTel `LogRecord` produced this way would carry different `Body`, `Exception` and `FormattedMessage` semantics from normal OTel .NET use, so the mask receives an Apitally-owned type. The provider leaves all application sinks, exception objects and scopes unchanged. `CaptureLogs = false` bypasses application capture, not internal events. Exclude Apitally/OTel diagnostic categories before capture.

For each associated application record:

1. Resolve SERVER identity through the activity association while preserving the emitting child span ID.
2. Render the message for Apitally once, copy supported structured values and flatten scopes. Explicit entry fields win over inner scopes, then outer scopes. Omit unstructured scope labels and `{OriginalFormat}`.
3. Add copied `exception.type`, `exception.message` and `exception.stacktrace` attributes; the original exception never reaches the callback.
4. Build a `LogRecordSnapshot` with read-only `Timestamp`, `CategoryName`, `LogLevel` and `EventId`, and mutable `string? Body` (the canonical rendered message) and `Dictionary<string, object?> Attributes` (keys are unique after scope precedence). Its constructor is internal. Invoke `MaskLogRecord` synchronously on the logging thread. Null, exceptions, a different instance or a null/empty `Body` after the callback drop the log; the server drops empty-body records at ingest.
5. Buffer the accepted instance itself in a `LogSnapshot` that references it; do not copy it again. `OtlpLogMapper` converts callback-added values and truncates application strings during encoding. Only accepted output supplies message/exception content; never restore removed values from original input. The SDK does not defend against callbacks that retain and later mutate the record.
6. Add trace context and `apitally.request.server_span_id` to the `LogSnapshot` after masking. The callback never sees or edits linkage, so masking cannot unlink or reassign a record; the SDK-set linkage overwrites a same-named callback attribute.

`InternalEvents` builds `LogSnapshot` entries directly, with the event name, scope `apitally`, empty trace/span context and a string or structured body, and submits them to the log batch processor. Internal events never pass through the logger provider, so they bypass application masking by construction, and the mapper applies application truncation only to application records.

- Startup: one event at serving activation, `framework = aspnetcore`, runtime/framework/app versions, resolved settings and finalized routes. The body is a JSON string; exclude credentials, endpoint, disabled, environment and app version from `config`; serialize configured callbacks as `true` and patterns with effective flags. Omit `openapi`.
- Errors: two short-lock dictionaries with at most 100 validation and 100 server groups. Normalize/truncate before keying; increment existing groups at capacity, silently ignore new groups, and keep positive counts within UInt32. Atomically swap maps immediately before ordinary/final log flush, then emit structured object bodies outside the lock.
- Consumers: normalize each submitted identifier/name/group/attribute patch per current spec before change detection. The string-or-null dictionary API avoids coercion rules. Retain the first ten valid attributes, including deletions; reject invalid/NUL-containing entries without discarding other valid fields. Emit only metadata-bearing updates from monitored request state, independently of recording, trace decisions and log capture, including unmatched requests but not websockets.

`ConsumerUpdates` uses a dictionary plus linked list for the specified 10,000-identifier LRU cache. Hash canonical normalized submitted payloads with stable key ordering and equivalent null/empty deletions; store hashes, not merged consumer state. Update the cache when handing the structured event to the log batch processor. Do not build a generic cache framework or a separate consumer delivery queue.

## 9. Native metrics

`ApitallyMetrics` owns a scoped `Meter` and private provider named `apitally`. Apply the POC's scope filter so this provider drops foreign meter instances. Do not pass it to ASP.NET instrumentation or replace application metrics registration; document that another unfiltered user meter provider can still observe instruments process-wide.

- Record the three request histograms at final transport completion, independent of activities, passing dimensions as a `TagList`. Skip `OPTIONS`, websockets and unmatched routes; excluded and sampled-out requests are still recorded. Use the same method, parameterized route, status, optional consumer, scheme and 5xx `error.type` tuple for duration and known sizes.
- Use histogram-specific `Base2ExponentialBucketHistogramConfiguration` views, delta temporality, `MaxScale = 3` and native inactive-series reclamation. Preserve the accepted wire scale range; validate measured value ranges without implementing custom aggregation/downscaling.
- Use a non-periodic reader. The export worker performs ordinary collections; terminal reader `Shutdown(Timeout.Infinite)` performs the final collection in the cleanup task before spool sealing. Its one-shot shutdown guard prevents another collection during provider disposal. Map/encode metric points synchronously inside the exporter before reusable native storage is released.
- Measure startup allocation, active memory and collection time for representative route/status/consumer cardinalities, comparing candidate fixed limits such as 10,000, 20,000 and 50,000. Select and document one internal limit before release; the POC's two-point limit and OTel's default are not product decisions.
- Omit native overflow points, preserve valid dimensional points and warn once with the consequence and capacity/support guidance. There is no public capacity setting or adaptive provider replacement.
- Read CPU, working set and uptime through direct process APIs. Normalize CPU by elapsed time and available CPUs; observe CPU/memory together. Uptime is always available so idle collections remain nonempty if CPU/memory observation is unavailable.

## 10. Batching, spool and delivery

### Stock intake and safe spool closure

Use one generic `ApitallyBatchProcessor<T>` subclass of `BatchExportProcessor<T>`, instantiated once for owned span entries and once for owned log entries, initially with explicit queue size 2,048, batch size 512 and 1,000 ms delay. Pass all remaining constructor settings explicitly and qualify memory/throughput before fixing release values. User OTel batch environment variables do not tune these processors.

Resolve [review finding R4](design-review.md#r4-spool-closure-needs-completed-export-not-only-a-successful-flush) with spool synchronization and terminal worker completion, rather than another queue or acknowledgement protocol:

- The cutoff's adapter detachment is the admission boundary; there is no admission lock or closed flag. A record submitted concurrently with terminal shutdown is either drained or left unsent, which the best-effort shutdown contract permits. Submitting after stock `Shutdown` only buffers the record. Keep native bounded-queue behavior.
- Encode outside the spool lock. Inside that lock, select the current file, append, or rotate/seal it. Exporters must not retain a current-file reference across this boundary. Only a fully finalized gzip file becomes sendable, and HTTP runs outside the lock.
- In ordinary cycles, `ForceFlush` prompts stock processing; it is not proof that every batch finished. Rotate under the append lock so all bytes in the closed file are complete. A batch still encoding appends to the next current file when it acquires the lock and remains eligible for a later cycle. This preserves safe best-effort delivery without making request intake wait for a flush barrier.
- In the final cycle, stop producers, emit final internal events while budget remains and call stock terminal `Shutdown(Timeout.Infinite)` in the cleanup task before sealing remaining files. This joins the batch worker; `Shutdown(0)` can return true without joining, and a finite timeout cannot be retried to obtain a later join. Do not use standalone `Dispose` as a drain.
- Establish the cleanup task before any cancellable wait, even if the host token is already canceled. The host token cancels awaiting that task and further delivery, not its scheduling or terminal shutdown. The task retains ownership of resource disposal until existing synchronous work returns, as described below.

Prove these rules with production exporters in integrated tests: encoding overlapping rotation and a blocked append still produce complete, decodable files, and a write failure discards only the current file. Do not test stock queue overflow.

### Encoding and storage

Map complete official OTLP fields, including resources/scopes, span IDs/parents/events/links/status, log event names/string-or-object bodies and native metric points. Encode only owned records; metrics are the synchronous native-storage exception described above.

Build bounded protobuf requests, starting with at most 32 records per chunk and measuring exact serialized size including repeated resource/scope overhead. Split before appending if necessary. If one record cannot fit in 4,000,000 uncompressed bytes, drop it with a deduplicated actionable warning and continue. Record counts alone are not a byte bound.

`SpoolFile` owns one continuous `GZipStream`, raw-byte count, compressed length, creation/order and first-attempt metadata. Append concatenated same-signal protobuf requests; finalize the gzip trailer before a file becomes sendable. Stored bytes are immutable after closure and reused on every retry.

`TelemetrySpool` implements the shared policies directly:

| Concern | Implementation rule |
| --- | --- |
| Storage selection | Probe temp storage at construction; choose file streams or memory streams once. Warn once on fallback. |
| File permissions | On non-Windows, create probe and spool files with `FileMode.CreateNew` and `UnixCreateMode = UserRead \| UserWrite` (`0600`), matching the Python and JavaScript SDKs; the .NET default is `0666` before umask. Windows `%TEMP%` is already per-user. |
| File rotation | Check 4,000,000 raw-byte limit before append. At send time rotate a signal's current file only if that signal has no closed backlog. |
| Storage bounds | Account compressed current and closed storage: 50 MB disk or 10 MB memory. Evict oldest closed non-metrics first, then metrics. If open storage alone needs reclamation, close a current file under the same lock so the absolute bound remains enforceable. |
| Write failure | Discard the affected current file, deduplicate warnings until recovery; keep the selected storage mode. |
| Retention | Expire 59 minutes after first attempt; no age expiry before the first attempt. |
| Orphans | Recognizable filenames; delete only files untouched for two hours at construction and refresh owned file times each cycle. No restart replay protocol. |
| Concurrency | Serialize short metadata/append/close operations; pin a selected closed file while sending so storage reclamation cannot invalidate the active read. |

### Worker and HTTP

One asynchronous delivery worker runs ordinary cycles: drain error aggregates before log flush, request stock span/log flushing, collect metrics, perform retention/rotation under the spool lock and send eligible closed files oldest first. The single cleanup task serializes the final cycle after any in-progress ordinary cycle and requires terminal completion of the batch workers and metric reader before sealing remaining files. Run collection, flushing and sends under instrumentation suppression; SDK diagnostics are also excluded by the logger adapter. SDK diagnostic messages use `[LoggerMessage]` source-generated methods in `SdkDiagnostics`.

Resolve a `TimeProvider` from DI when registered, otherwise use `TimeProvider.System`, and pass it to scheduling, spool retention and uptime. Wait with `Task.Delay(delay, timeProvider, token)` and draw jitter from `Random.Shared`.

Freeze endpoint, headers and proxy configuration in `ExportHttpClient`. Use HTTP/protobuf with gzip, bearer token and matching `Apitally-Env` on every POST. Construct one private `HttpClient` over a `SocketsHttpHandler`; do not use `IHttpClientFactory`, because application-wide defaults such as `ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler())` would add retries beneath the worker's own retry rules. Assign the captured `HttpClient.DefaultProxy` to the handler rather than parsing proxy environment variables: .NET already implements `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`, plus the system proxy on Windows. Verify proxy binding with an injected loopback proxy; do not test .NET's environment parsing or change process-global proxy state. Keep credentials out of diagnostics and do not install a retry handler.

First attempt is about two seconds after activation. Subsequent cycles wait 15 seconds with +/-10% jitter after the preceding cycle finishes; accept integer server interval adjustments clamped to 5-60 seconds. Ordinary cycles send at most ten files with 0.1-0.5 seconds between sends. Each POST has a ten-second timeout bounded by shutdown cancellation when applicable.

Connection failures, timeouts, 408, 429 and 5xx retain the file and stop that send sequence. Allow exactly one immediate retry for a connection error. Other 4xx discard and warn once per status; trace quota rejection does not disable collection of other signals. Never re-encode retries or layer retry/backoff policies beneath the worker.

### Shutdown

Use `IHostedLifecycleService.StoppedAsync` as the proposed final phase after server/service stop calls, validated in both hosting compositions. Keep SDK intake active while the host drains requests; stopping the server is not itself the final detail cutoff.

1. Stop ordinary scheduling and perform the short atomic request cutoff even if the host token is already canceled. Preserve already-finalized releases; discard detail still missing transport completion or SERVER end, clear raw capture, and detach the tracing/logging adapters. Completed metrics/errors remain eligible. Do not invoke application callbacks while holding the cutoff lock.
2. Always establish one shutdown/cleanup task before any cancellable wait, including when the host token is already canceled. Do not pass that token as cancellation of task scheduling. This task owns the remaining sequence and resource disposal; the host awaits it with its existing token, not a new timeout per phase.
3. In that task, wait for any in-progress ordinary cycle to finish before the final cycle. Host cancellation ends the host's wait and prevents new POSTs, but does not cancel this serialization or terminal cleanup.
4. While budget remains, drain error aggregates into the log batch processor. Run both batch processors' terminal `Shutdown(Timeout.Infinite)` calls. Use the owned metric reader's `Shutdown(Timeout.Infinite)` as the final metric collection, rather than a separate `Collect`. Complete these terminal operations before sealing current files.
5. While budget remains, seal and deliver remaining files without pauses or the ordinary ten-file cap, using the same failure rules. Cancellation initiates no further delivery.
6. Dispose only owned providers/resources after the ordinary worker and terminal operations finish. The metric reader's one-shot shutdown guard prevents another collection during provider disposal. Application tracing remains application-owned.

Host cancellation, including cancellation already present on entry, only ends the host's wait and prevents new POSTs; exporters and the spool have no separate abandoned state. Drain, sealing and disposal finish in the cleanup task in the background, or end with the process. Orphaned spool files are not sent by later processes, so nothing is gained by discarding local work early. Cancellation cannot interrupt an already-running synchronous user mask callback or filesystem call, so resources stay alive until that work returns rather than being disposed underneath it.

The host's shutdown budget takes precedence over completing cleanup before returning. Cleanup is best effort while the process remains alive: a callback that never returns can retain its resources until process exit. The one-off cleanup task adds neither a delivery worker nor an extra export window. Validate through integrated tests that shutdown with an already-canceled token returns promptly without throwing, and that an idle host still delivers the uptime gauge in the final cycle.

## 11. Implementation sequence and acceptance gates

Implement small vertical slices on `v1`, keeping each stage buildable. Tests use the production classes rather than reimplementing their algorithms.

| Stage | Work |
| --- | --- |
| 1. Establish the v1 foundation | Push a `v0` branch at `65e25ed13e15c6d6b77125749eba5cece6aa008f` before `v1` replaces `main`. Update project targets/dependencies and build/test CI together, explicitly installing the supported SDKs/runtimes and configuring the 8/9/10 matrix. Implement public options/helpers, frozen configuration, common value ownership and internal protobuf generation; generated protobuf types stay internal. Replace legacy components as their v1 replacements land. |
| 2. Prove the integrated request path | Implement preparation/activation, the `IServiceCollection` entry point in both hosting styles, provider ownership, suppression, request associations, snapshots, minimal log capture and helpers. Add two-completion release. |
| 3. Complete delivery and lifetime ownership | Implement owned batching, synchronized file closure, unconditional cleanup ownership and terminal batch/metric-reader shutdown, OTLP mapping, file/memory spool, worker/HTTP and final cutoff. Begin with small real span/log payloads and native metric collection. |
| 4. Complete transport and privacy | Adapt bounded stream/pipe observation, final route/status/size metadata, headers, body processing, native file omission and exception/validation adapters. Add sampling-independent error aggregation. |
| 5. Complete logs and internal events | Finish log-mask normalization, scopes, canonical messages, exception attributes, startup metadata and consumer update cache/events. |
| 6. Qualify metrics and resource bounds | Finish native delta histograms/process gauges; measure fixed cardinality and queue settings. Exercise sustained consumer churn and spool pressure. |
| 7. Validate the package and publish readiness | Add the sibling harness adapter/app/variants; run real ingestion and assert it on the backend. Package-consumer validation belongs to the harness; this repository has no packed-package tests. Update README, migration guide and publishing workflows; remove remaining legacy-only code/tests/dependencies. |

A stage is complete when it builds on the 8/9/10 matrix and the section 12 tests for its modules pass.

Port knowledge, not old architecture: retain useful route enumeration, recursive masking, process measurement and test scenarios after contract review. Replace the Hub client/models, custom counters/histograms/activity collector, whole-response buffering, persistent UUID/lock and independent forwarding-header interpretation. Do not delete v0 source before preserving its reference or treat this plan as deletion approval.

## 12. Validation and performance criteria

### Automated behavior

Use xUnit module tests for algorithms and small real Kestrel applications for lifecycle, body features, compression, aborts and provider interaction. Reserve TestServer tests for automatic suppression. Use in-memory exporters for owned-data assertions and a physical loopback OTLP receiver for gzip/protobuf/HTTP behavior. Read responses fully and wait for observed export completion; neither a delay nor stock `ForceFlush` alone proves it.

Cover these contract groups without multiplying every case across every hosting arrangement:

- Setup precedence, repeated registration, callback order, invalid credentials, both disable variables, no activation for a built but unstarted host, activation before the first request, and worker cleanup through container disposal after a failed server bind.
- Early/ordinary requests, unsampled remote parents, user sampling/RecordOnly, external provider lifetime, suppressed TestServer and one request SERVER despite instrumentation reuse.
- Nested/explicit-parent associations, helpers under children, concurrent/keep-alive requests, shared trace IDs, both completion orders, response sampling, per-request caps and dropping telemetry that arrives after release.
- Matched/unmatched, OPTIONS, websocket, excluded and sampled-out requests with exact independent metric/error/consumer eligibility.
- Request stream/reader and response stream/writer paths, app-consumed-only request capture, allowed content types, size boundaries, bounded compression, known incomplete bodies, handled errors, unchanged native file delivery/mixed omission and incidental streamed files.
- Value mapping for scalars, arrays, dictionaries and string fallback; detachment of arrays before callbacks; conversion of callback-added log values; stage-appropriate callback snapshot contents, other logging providers, scopes and exception objects left unchanged, user-export privacy, callback-proof linkage, scope precedence, 2,048-unit truncation and canonical Body/exception output.
- Validation supported shapes/localized messages, unknown-shape omission, first exception and final-500 rules, aggregate identities/limits/atomic drains, native event names, startup string body with config exclusions, and structured aggregate and consumer bodies.
- Consumer partial patches/deletions, order-independent hashing, per-update entry limits, bounded LRU and sampling/log-capture independence.
- Delta exponential histograms, shared size tuple, scope isolation, overflow omission, reclamation, paired gauges and idle uptime.
- Exact wire size splitting and oversized-record loss, continuous gzip merging for each signal, byte-identical retries, headers/intervals, proxy binding, suppression, file/memory retention/eviction/write failures, complete files under overlapping rotation and blocked appends, completed-write shutdown, and prompt return with an already-canceled shutdown token.

Keep global environment/listener cleanup in shared fixtures, and serialize or isolate tests that alter process-global state. Use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` for retention/scheduling tests, and `FakeLogger`/`FakeLogCollector` and `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing` for SDK diagnostics and metric assertions; do not write custom clocks or collectors or mock Apitally's classes. Ordinary CI must not send to the production endpoint. Permanent tests assert SDK behavior, not every upstream fact investigated in the POCs.

### Measured performance

Measure only to choose a constant or to catch unbounded growth; there is no standalone benchmark framework and no generic latency/GC/CPU/RSS reporting gate.

- Select the fixed metric capacity from startup allocation, active memory and collection time, as described in section 9.
- Select the batch queue size, batch size and delay from memory and throughput under concurrent requests.
- Run one bounded soak with sustained traffic, high consumer churn and an export outage, and confirm retained memory is stable after traffic stops.

Streaming/backpressure, requests not waiting on body processing, and bounded buffers/spool are verified by the integration tests, not by measurements.
