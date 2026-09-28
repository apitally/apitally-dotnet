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
| OpenTelemetry compatibility | Use the tested OTel SDK 1.19.0 baseline as the minimum for v1, subject to integrated qualification. Applications using older OTel dependencies may need to upgrade. |
| Native AOT | Outside the initial support guarantee. Prefer compatibility-friendly choices when they add no complexity. |
| Hosting | Support modern `WebApplicationBuilder` hosting and Generic Host with `Startup`. Modern hosting is the primary documented path. |
| Setup | One `IServiceCollection.AddApitally` registration call with automatic middleware registration, subject to integrated-pipeline validation. |
| Existing tracing | Automatic integration with DI-registered tracing; register separately constructed providers as existing `TracerProvider` instances in DI. |
| Hosting support boundary | Normal single-host ASP.NET Core integration is the v1 baseline. Additional hosts are not prohibited; special multi-host coordination is outside v1 implementation and release requirements. Independent sampling across overlapping providers is not guaranteed. |
| Configuration | Use the standard .NET Options pattern: one base step applies environment fallbacks and binds the `Apitally` section, and `AddApitally` callbacks run as `PostConfigure`. Read `IOptions<ApitallyOptions>` once at startup preparation, then validate and freeze before activation. |
| Repeated setup | Within one host, compose code callbacks in registration order; later explicit assignments win. Register SDK components once with `TryAdd*`. Direct `Configure<ApitallyOptions>` follows standard .NET ordering. |
| Runtime ownership | The application host owns configuration, buffers, workers, and shutdown through DI. |
| Options layout | Use flat properties on `ApitallyOptions` and directly under the `Apitally` configuration section. |
| Unfinished requests at shutdown | At the final SDK cutoff, discard detail for requests still awaiting transport completion or SERVER activity end. Flush finalized requests normally; recorded metrics and eligible error aggregates remain independent. |
| Shutdown budget | Use the host's remaining shutdown budget and honor its cancellation. Add no separate Apitally flush window; final delivery may remain incomplete when the budget expires. |
| Test-host activation | Automatically suppress Apitally for the standard in-memory TestServer by recognizing the resolved server's exact type and assembly. Real Kestrel tests use the existing disable configuration. The candidate runtime passed .NET 8/9/10 validation; full SDK integration remains open. |
| Request helpers | An injectable `IApitally` service is the primary API. |
| Default instrumentation | When Apitally owns tracing, instrument ASP.NET Core and outgoing `HttpClient` calls automatically. Database instrumentation is opt-in. |
| Tracing customization | Use standard OTel provider registration for database instrumentation and additional activity sources. Apitally-specific tracing-configuration callbacks are outside the initial API. |
| Metric capacity | Use a generous, internally selected fixed capacity through native OTel views and reclamation. Select the number after memory and collection-cost measurements; no public capacity setting or runtime resizing. |
| Individually oversized records | Split ordinary batches to fit the spool cap. Drop an indivisible encoded record that still cannot fit, with a deduplicated actionable warning, and continue with other records. |
| Span-based callbacks | All request/response sampling and body-masking callbacks receive the same complete span snapshot type, populated for the callback's stage. It exposes read-only interfaces and native .NET/OTel types but is the SDK-owned record itself, with no isolation guarantee. |
| Sampling callback result | Both sampling callbacks return `double?`: a keep probability in `[0, 1]`, or `null` to abstain. |
| Late request telemetry | Drop spans and logs that arrive after a request is released. There is no completed-request cache. |
| Body-mask callbacks | Both use `Func<SpanSnapshot, byte[], byte[]?>`: snapshot first, decompressed body bytes second, replacement bytes returned. `null` produces `[REDACTED]`. |
| Body completeness | Finalize bounded ordinary capture using directly observed failures, visible cancellation and applicable length checks. Omit known incomplete bytes; do not build a transport-success certification system. |
| File response bodies | Delegate native file sends unchanged and omit their entire body capture, including mixed stream/file output. Eligible content already passing through ordinary observed streams may be captured incidentally. |
| Custom pattern inputs | Use `List<string>` in code and configuration files. Custom redaction and path-exclusion patterns are case-insensitive by default and respect explicit .NET inline options. |
| Log-mask callback | Use an Apitally-owned mutable `LogRecordSnapshot`, built directly by the logger adapter and masked synchronously. There is no private OTel logger provider. Explicit deviation from the shared "ecosystem log-record type" rule, recorded in section 9. |
| Log exception metadata | Present copied `exception.type`, `exception.message` and `exception.stacktrace` string attributes. The callback type has no exception object. |
| Log callback message | Present rendered text in `Body` and omit the original message template from callback input. Structured attributes remain separately maskable. |
| Structured log scopes | Flatten private scope fields into `Attributes` before masking. Explicit log fields override inner scopes, which override outer scopes. Expose no separate scope chain and restore no removed values after masking. |
| Plain log scope labels | Omit unstructured scope labels, matching the official .NET OTLP exporter. Log messages and structured scope fields remain captured. |
| Manual tracing | `IApitally.StartActivity(...)` returns the native .NET `Activity` type for a `using` scope. |
| Monitored scope | The whole HTTP application, subject to shared eligibility, sampling, and exclusion rules. |
| Validation capture | Use framework-provided details and known standard response shapes for MVC and Minimal APIs. Preserve opaque field/message strings and available metadata; leave unknown source/field empty. |
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

**Confirmed dependency policy:** use OTel SDK 1.19.0, the tested baseline, as the minimum for v1 and qualify it through integrated testing. Document that applications with older OpenTelemetry dependencies may need to upgrade them. The initial release will not introduce compatibility paths for older OTel SDK releases. This leaves the .NET 8/9/10 runtime support unchanged; a selected dependency baseline is not evidence that the complete SDK integration already works.

**Open:** exact NuGet target frameworks, C# language version, the complete instrumentation/dependency graph and its integrated qualification. Testing runtimes 8/9/10 does not by itself require three target frameworks in the package.

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

**Confirmed:** normal single-host ASP.NET Core integration is the v1 baseline. Additional hosts are not prohibited. Coordinating startup events, process gauges, aggregate/spool budgets or tracing across multiple hosts is outside v1 implementation and release requirements. Independent sampling across providers listening to the same sources is not guaranteed, even within a single host. Retain host-owned configuration and lifecycle without a global host coordinator or process-global Apitally configuration singleton.

This boundary follows the distinction in official OTel guidance: repeated hosting registration creates one provider per service collection, while separately constructed providers are supported without establishing host or sampling isolation. Real Azure Monitor reports describe overlapping test hosts and multiple providers; they establish actual usage, not its production prevalence. See the research references below.

**Open:** the tested middleware association does not establish ownership of descendants ending before middleware entry or children with only an explicit parent context. These request-association questions also matter in a single host. Do not treat the POC's association filter as a complete request algorithm.

If the public APIs cannot preserve the agreed ownership and tracing behavior within the supported single-host integration, document the limitation and bring the decision back for review. Do not silently substitute a process-global configuration singleton.

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

**Inherited:** use one process instance identity across all signals, regenerated on process restart. Host-owned state does not change the shared process-wide event, gauge or aggregate/spool requirements into per-host requirements. Implement these contracts for the single-host baseline and preserve private-pipeline isolation; special multi-host coordination is outside v1 scope as described above.

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

**Confirmed mechanism:** use the standard .NET Options pattern rather than a custom callback store. The first `AddApitally` call registers one base `IConfigureOptions<ApitallyOptions>` that applies the fallbacks and binds the section; each `AddApitally` callback is registered as `PostConfigure`, so the options factory runs callbacks after all configuration steps and in registration order. A direct `services.Configure<ApitallyOptions>(...)` follows standard .NET ordering relative to the base step and is documented as such. Read `IOptions<ApitallyOptions>.Value` once at startup preparation; it is computed once and never reloaded. Apply the additive environment disable controls, validate the resulting settings and copy them into immutable runtime configuration before activation. Later mutations to the options object or configuration sources must not alter the running SDK.

**Confirmed layout:** keep the configuration surface as flat properties on `ApitallyOptions`, such as `SampleRate` and `CaptureRequestBody`. The same keys appear directly under the `Apitally` configuration section. Sampling, capture and redaction do not introduce nested options groups.

**Proposed names:** use PascalCase names corresponding to the shared settings: `WriteToken`, `Env`, `AppVersion`, `Disabled`, `CaptureLogs`, the four directional capture toggles, `SampleRate`, the sampling and masking callbacks, and the redaction/exclusion pattern collections.

Callbacks are configured in code. The startup event serializes their presence as `true`, not their implementation. Pattern serialization includes their effective flags where relevant.

**Inherited:** invalid static sampling rates resolve to full capture; invalid patterns are individually rejected with an error while valid patterns remain active. Default patterns remain case-insensitive and user patterns extend them.

**Confirmed regex inputs:** expose each custom query-param, header, body-field and path-exclusion pattern collection as `List<string>`. Both code options and the `Apitally` configuration section supply .NET regex pattern strings. Validate and prepare patterns during startup configuration resolution, preserving the immutable-runtime rule above. Native `Regex` objects are not an additional public input form.

**Confirmed custom-pattern matching:** use case-insensitive regex search by default for all four collections, consistently across code and configuration files. Respect standard .NET inline options: for example, `secret` matches `Secret` and `SECRET`, while `(?-i:secret)` makes that expression case-sensitive. Inline options override conflicting constructor options for their applicable scope. User patterns still extend the built-in defaults rather than changing their flags or removing them. Startup pattern serialization must retain the effective default and explicit inline options.

Invalid settings disable telemetry rather than failing startup, so the SDK uses neither `ValidateOnStart` nor `IValidateOptions`. Default host builders also read unprefixed environment variables such as `Apitally__SampleRate` into the section without Apitally code.

**Open:** exact option property names and genuinely optional values. The deferred resolution and freezing behavior still requires integrated validation.

**Confirmed:** repeated registration within one host composes code configuration callbacks in registration order. Later explicit assignments override earlier assignments; a later callback or setup call leaves settings it does not assign unchanged. Resolve the composed code overrides using the source precedence and additive disable rules above, then freeze runtime configuration before activation. Repeated setup must not duplicate middleware, processors, workers or logging providers.

## 4. Lifecycle: configure, activate, shut down

**Confirmed:** DI and the application host own the runtime. A host's shutdown drains and disposes only its owned Apitally components.

**Inherited:** configuration and serving activation are separate. Registration must not start Apitally export workers, send telemetry, or report the process online. Route metadata preparation may happen once the framework has finalized that information.

**Proposed lifecycle:**

1. `AddApitally` on `IServiceCollection` wires options, services, tracing registration, logging capture, and middleware/lifecycle hooks.
2. In the startup filter, read the resolved options once, apply additive disable controls, validate and freeze the resulting settings, and prepare tracing.
3. Activate in the same startup filter, immediately after the application pipeline is built. `GenericWebHostService.StartAsync` builds the pipeline before calling `Server.StartAsync`, so activation completes before the server can accept a request, and hosts that are built but never started, such as design-time tools, never activate. This is the shared design's pre-request signal. Early tracing registration must still ensure the first SERVER activity is observed even if it starts before middleware executes.
4. There is no first-request fallback or concurrent activation gate. The runtime is active, stopping, stopped, or disabled.
5. On ordinary host shutdown, flush finalized requests and run the shared final export cycle before disposing owned providers and transport resources. At the final SDK cutoff, discard request detail that still lacks transport completion or SERVER activity end, as described in section 6.

**POC evidence:** the [transport/lifecycle experiment](../pocs/transport-lifecycle/README.md) observes an early Generic Host request before `ApplicationStarted`, which is why activation does not wait for that event. The separate provider experiment establishes tracing readiness for an early request. Real worker activation is not yet proven.

**Confirmed adaptation:** one activation attempt per host runtime. A preparation or activation failure logs an error and leaves that host serving without telemetry. This follows host ownership rather than Python's process-global activation state. `TelemetryRuntime` is a DI singleton implementing `IAsyncDisposable`: if the host fails after activation, for example because the server cannot bind, container disposal stops workers and releases owned resources without final delivery.

**POC evidence:** ordinary hosted-service `StopAsync` ordering relative to Kestrel differs between the two tested hosting compositions. `IHostedLifecycleService.StoppedAsync` follows all service stop calls and is a candidate final-drain phase. A request ignoring cancellation can remain unfinished after host stop returns; a 300 ms host budget took about 1.3 seconds in the tested Kestrel abort path. The final phase receives the already-canceled token in that case. Host cancellation is not an exact wall-clock termination guarantee.

**Confirmed shutdown budget:** the application host controls the available shutdown time. Apitally uses the remaining host budget and honors its cancellation rather than starting an additional SDK flush window. Drain, flush and delivery share that budget; each phase does not receive a fresh allowance. If request draining exhausts it, final telemetry delivery may remain incomplete. This is cooperative cancellation, not a guarantee that framework or blocking synchronous operations terminate at an exact wall-clock deadline.

**Open:** actual OTel provider/worker disposal ordering, completed-spool-write coordination within the host budget, and implementation of the confirmed unfinished-request cutoff. The lifecycle POC's drain is a counter/cancellable delay, not export evidence. Validate that blocking processor/export work and cleanup do not introduce deliberate extra waiting beyond host cancellation.

**Confirmed test-suppression direction:** automatically suppress telemetry activation for application integration tests when a reliable, straightforward detector is available. Requiring users to disable every test host explicitly is not the preferred default. Bring any complex mechanism back for review rather than adding broad test-framework detection. The SDK's own tests must remain able to exercise real activation deliberately.

**Reference behavior:** Python checks `PYTEST_CURRENT_TEST` and the Django `manage.py test` argument shape at activation. Its telemetry tests deliberately clear the pytest marker. This is targeted coverage, not a universal detector for every test runner.

**Research finding:** no documented marker that is generally set across ordinary VSTest and Microsoft.Testing.Platform runs was established. Debug/runtime-selection settings and mode-specific controller variables do not establish a general test environment. This is not proof that every runner-specific marker is absent.

**Confirmed .NET guard:** inspect the host's resolved `IServer` and recognize the exact runtime type `Microsoft.AspNetCore.TestHost.TestServer` from assembly `Microsoft.AspNetCore.TestHost`. Reading that existing object's type and assembly names requires no TestHost dependency or loaded-assembly scan. `UseTestServer` registers this singleton, and default `WebApplicationFactory` uses it. Resolve the actual server after host registrations are complete, not during `AddApitally`.

The guard covers standard in-memory TestServer tests; it does not identify real Kestrel tests, including the explicit Kestrel mode in .NET 10 `WebApplicationFactory`. Such tests use the existing disable configuration. Do not add wrapper introspection or infer testing from the Development environment. No new public testing override is selected; SDK telemetry tests can use loopback Kestrel, with TestServer tests verifying suppression.

**POC evidence:** the independently inspected and rerun [test-host-suppression probe](../pocs/test-host-suppression/README.md) passed 11 cases on .NET 8.0.13, 11 on 9.0.2 and 13 on 10.0.9, using SDK 10.0.301 and OTel 1.19.0. Its startup filter resolves the actual server during pipeline construction, before private fallback-provider creation. Default `WebApplicationFactory`, direct `WebApplicationBuilder` and Generic Host/`Startup` TestServer paths stay inactive for startup and request activation signals. The factory replaces the registration-time Kestrel server before the guard runs; no DI cycle occurred in the candidate. The guard assembly references neither TestHost nor Mvc.Testing.

Application-owned tracing continues exporting real completed SERVER spans in suppressed TestServer cases, including after candidate disposal. Loopback Kestrel in Development activates and exports through the private fallback even with TestHost loaded; .NET 10 factory Kestrel mode also passes. Export and disposal assertions await observed completion rather than assuming `ForceFlush` establishes it.

**Still unproven:** full SDK configuration/logging/metrics/worker integration, other tracing-registration modes and concurrent early-request activation. The request-only probe omits the startup activation hook but sends requests after the host has started. Active Kestrel controls use private tracing; application-owned tracing is exercised in the suppressed path. The selected guard remains limited to the exact TestServer identity.

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

**Open:** full SDK integration of association mechanics and early request context, plus concurrency and lifecycle validation. Verify isolation across concurrent and keep-alive requests without adding a blanket context reset that destroys legitimate upstream propagation.

## 6. Sampling and per-request buffering

**Inherited:** request and response sampling refine the static rate. Both stages compare the low 64 bits of the trace ID against the shared rounded probability threshold, so their effective combined rate is the minimum rather than the product. An invalid or throwing sampling callback warns and fails open.

Request-stage drops skip trace-detail capture work. Excluded requests never invoke sampling callbacks. Metrics and eligible error aggregates remain independent of all trace-detail decisions.

Response sampling runs once with final route, status, sizes, consumer, and custom request attributes. Abstention preserves the request-stage decision rather than sampling again at the static rate.

**Confirmed .NET result type:** both sampling callbacks return `double?`. Zero means drop, one means keep, and a value between them is the keep probability. `null` at request stage falls back to the configured static rate; `null` at response stage preserves the earlier decision. Express boolean conditions as numeric probabilities, such as `condition ? 1.0 : 0.0`, rather than introducing a custom result type or a weakly typed boolean/numeric union. This adapts the shared API's return shape to C# without changing probability, abstention or invalid-result behavior. A response-stage keep cannot recover detail already dropped at request stage.

Hold ended descendants and application logs until both transport observation and the SERVER activity complete. Keep at most 1,000 spans and 1,000 application log records per request, retaining the earliest arrivals. Release descendants, then the SERVER span, then the request's logs once. A drop discards buffered detail and raw payloads. Release and drop both remove the request's associations, so telemetry arriving afterwards drops locally.

### Late telemetry

**Confirmed:** drop spans and logs that arrive after the request has been released. Release removes the request's associations; there is no completed-request cache, retained decision or cross-release counter.

In ASP.NET Core, Kestrel awaits the application pipeline, finishes the response, runs `OnCompleted` callbacks and then calls `HostingApplication.DisposeContext`, which writes the "Request finished" log before stopping the SERVER activity ([HttpProtocol.cs](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs#L673-L772), [HostingApplication.cs](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/Hosting/src/Internal/HostingApplication.cs#L99-L104)). Because release waits for both transport completion and SERVER end, telemetry from streamed responses, `OnCompleted` callbacks, end-of-request framework logs and automatic Sentry.AspNetCore captures always arrives before release. Only work that outlives the response, such as fire-and-forget tasks or queue hand-offs that keep `Activity.Current`, is lost; the trace is shorter, not misattributed.

**Shared contract:** shared spec section 6.5 and the shared design's per-request buffering section make telemetry arriving after release best effort: an SDK may export or drop it. The JavaScript SDK keeps a 10,000-ID completed-request cache; Python drops children started after release. A future Sentry integration that correlates explicit captures made after the request would need its own small pending-event-ID map, as in the Python SDK, not a general late-telemetry cache.

### Export snapshots

**Research finding:** .NET `Activity` is not a detached immutable span representation, and public APIs do not provide a faithful clone with the same IDs, source, and kind. A retained, stopped activity remains usable; the problem is shared mutable state and the inability to represent a privately enriched export view without changing the original. The tested .NET SDK has no equivalent public `ReadableSpan` abstraction. The specialized `BatchActivityExportProcessor` accepts `Activity` objects, not an arbitrary export snapshot.

**Proposed:** copy the data required for Apitally export into an SDK-owned snapshot. Preserve IDs, parents, times, status, events, links, scope, and resource. Apply late enrichment and privacy processing to that owned representation. Do not fabricate a second live activity to represent the original request or modify user-owned activities to finish export.

**POC evidence:** the [snapshot experiment](../pocs/activity-snapshots/README.md) demonstrates public generic `BatchExportProcessor<T>` intake of owned records, tested metadata/value copying, and private 50,000-byte body processing without changing a simultaneous user export. Mutable array values are copied rather than shared. Worker construction suppresses execution-context flow so activation does not carry ambient context into body processing. Arbitrary value types and full scope metadata remain unproven.

Generic batching does not enforce span sampling semantics: an explicit `Activity.Recorded` check is needed to match the specialized activity processor's treatment of `RecordOnly`. The request-buffer checks are a sequential model, not proof of concurrent framework completion. Log ownership, production lifecycle guards, and detailed snapshot ownership/value semantics remain open.

**Confirmed callback shape:** all four span-based callbacks (`SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody`, `MaskResponseBody`) receive the same complete span snapshot type. `SpanSnapshot` is the working name. Expose available identity/parent, name, kind, timestamps, status, attributes, events, links, resource and instrumentation-scope metadata. Events and links use `ActivityEvent` and `ActivityLink`, the resource uses OTel `Resource`, and the scope is flat `ScopeName`/`ScopeVersion` properties; there are no Apitally-specific event, link or scope types. This is an inspection type, not another span-creation or mutation API.

Each invocation receives SDK-owned data appropriate to its stage, not a shared live `Activity` or `HttpContext`. Request sampling sees currently available information; response sampling includes final transport and custom attributes, including values learned after span end; body masking sees query/header redaction and captured headers before body attributes are attached. Information not yet available is represented as unset. The snapshot is the SDK-owned span record itself, reused from response sampling through export, not a per-callback copy. Read-only interfaces state intent only; the SDK does not defend against callbacks that cast and mutate values or that retain a snapshot and observe later enrichment. User telemetry is unaffected either way because the record is private. The same public type is used throughout rather than mixing `Activity`, request-specific contexts and snapshots.

Python constructs a new instance of its standard OTel `ReadableSpan` class; JavaScript constructs a plain object implementing the standard `ReadableSpan` interface. Both preserve full span metadata while supplying private attributes. The .NET-owned snapshot is an explicit adaptation to preserve that behavior and consistency across .NET callbacks when the standard SDK lacks an equivalent abstraction.

Attributes are an `IReadOnlyDictionary<string, object?>`. Values follow the exporter-aligned normalization selected in section 9 and use the plain CLR types OTel .NET users see on activity tags, including `string[]`, `long[]`, `double[]`, `bool[]` and `byte[]`.

**Open:** final member names, full normalization validation, and complete scope/resource copying. The existing POC does not establish the complete public callback implementation. Log masking operates on a different signal and uses the separate `LogRecordSnapshot` type described in section 9.

**Confirmed unfinished-request shutdown policy:** at the final SDK cutoff, discard trace and application-log detail for requests still awaiting either transport completion or SERVER activity end. Discard their buffered descendants/logs together, release captured payloads unprocessed and ensure later telemetry cannot revive those requests. Do not create partial SERVER exports or synthetic end times, and leave application-owned activities untouched.

Requests finalized before the cutoff follow the normal response-sampling, complete-body and once-only release rules and remain eligible for the final export cycle. Already-recorded metrics and eligible error aggregates remain independent of the request-detail discard policy. The cutoff's coordination with completion callbacks, adapter detachment and exporter/spool shutdown still requires integrated validation. This is a permitted per-SDK choice under the shared best-effort shutdown contract.

## 7. Capture pipeline: bodies, headers, sizes, redaction

**Inherited privacy boundary:** captured headers and body payloads remain private to Apitally. Request-serving code collects bounded data; decompression, masking, JSON processing, and redaction execute outside request handling before attributes are attached to an export snapshot. User exporters must never see Apitally-captured payloads.

For in-scope ordinary body capture, apply the canonical content-type allowlist and 50,000-byte limit. Check headers before body I/O. A known oversized body yields `[BODY_TOO_LARGE]` without reading it; crossing the cap discards buffered bytes. Empty bodies and known incomplete captures are omitted, while an already-established oversized sentinel can still be exported for an aborted ordinary stream. Native file sends have the explicit scope exclusion below.

Process bodies in the shared order: bounded decompression, mask callback, parse, field redaction, and serialization. Parse to identify JSON regardless of content type once capture is allowed. Unsupported/failed decompression must not export the original bytes. A failed or dropping mask callback yields `[REDACTED]`; an oversized masked result yields `[BODY_TOO_LARGE]`. The oversized sentinel bypasses body processing.

**Confirmed .NET body-mask signature:** `MaskRequestBody` and `MaskResponseBody` both use `Func<SpanSnapshot, byte[], byte[]?>`, with `SpanSnapshot` as the working name. The first argument is the complete span snapshot; the second is the decompressed body bytes before JSON parsing. The returned array replaces those bytes, and `null` produces `[REDACTED]`. Ordinary byte arrays keep the two callbacks consistent without another buffer abstraction. The returned array is consumed immediately and not copied; only the serialized result is retained.

The body-mask callback sees the export snapshot after query/header redaction and captured-header attachment, but before body attributes are attached. Document that execution may happen later on another thread. A failure in the export redaction boundary drops the affected span rather than sending raw sensitive data.

Header attributes are list-valued, lowercase, and retain dashes. A masked header exports one `[REDACTED]` value. Redact query strings in request URLs and captured `Location`/`Content-Location` values, including stable/legacy query-bearing attributes on descendant spans and attributes supplied by user instrumentation. Defaults and allowlists come from the shared specification, not a separately maintained .NET variant.

**Confirmed transport direction:** transparently observe ordinary request reads and response writes through ASP.NET Core stream/body features, including `BodyReader` and `BodyWriter`. Preserve streaming and backpressure; read only request bytes the application consumes. Copy leased pipe memory before returning it, and commit capture counts only after successful acceptance. Whole-response buffering is not the implementation.

**Confirmed completeness boundary:** finalize ordinary capture at transport completion using the observed bytes, an applicable declared length and a simple incomplete flag. Set that flag for directly observed read/write/advance/flush failures and escaped request errors, and for explicit abort or writer-completion errors through existing wrappers or a lightweight hook. Honor cancellation already visible at completion. A fully consumed request is established by applicable length or observed EOF; retain an ordinary response only when all counted bytes are available and no known incompleteness remains. Handled error status codes do not themselves make their complete response bodies ineligible.

`OnCompleted` is a finalization point, not a success certificate. Use these local checks rather than token-replacement tracking, cancellation-settling delays, separate failure-coordination machinery or attempts to discover every internal server failure. Replacing an application's cancellation token is not itself an omission condition. The boundary is known capture completeness, not verified client receipt: a network failure after all application bytes were observed does not by itself make the capture partial. Unknown-length responses with no observed failure carry no universal transport-success guarantee.

**Confirmed v1 scope deviation:** omit response-body capture when the observer's native `SendFileAsync` path is used, even for an otherwise eligible small text/JSON file. Delegate file delivery unchanged and invalidate the entire capture, including any mixed stream prefix or suffix. Do not reread files or substitute an SDK file-copy path. If the application or compression middleware already writes eligible file content through the ordinary observed stream, capture it incidentally under the normal allowlist, completeness and size rules; add no file/download detection or special handling. This narrows body-capture coverage, not request monitoring, response headers or independent size observations.

Body size observations are independent of content capture. Use trustworthy declared lengths or complete observed byte counts as the shared design allows; do not read a body or inspect a file merely to determine its size. Unknown size remains unknown. The same resolved sizes feed spans and histograms, including when captured bytes have been discarded after crossing the cap.

**POC evidence:** the original bounded stream/feature wrappers and the [transport-completeness follow-up](../pocs/transport-completeness/README.md) observe tested request `Body`/`BodyReader` and response `Body`/`BodyWriter` paths without whole-response buffering. The follow-up passed 3564 assertions per exact .NET/ASP.NET 8.0.13, 9.0.2 and 10.0.9 pair across fresh and pooled connections. Clients receive ordinary and gzip-decoded prefixes while endpoints remain gated, and outer observation includes the gzip trailer. The original native file omission also clears mixed output. The follow-up's second file read can capture different bytes from those served; its single-pass comparison bypasses native dispatch. Both file-capture alternatives and the richer diagnostic failure tracker remain research, not production requirements.

**Open:** integration of the selected simple checks with capture/privacy processing, supported body-feature compositions/protocols, and size semantics for incomplete observations. The probes do not establish the full shared capture/privacy pipeline or resolve an earlier unexplained development timeout. Native file payload capture and exhaustive server-internal failure detection are outside the v1 implementation and release criteria.

## 8. Transport observation, routes, frameworks

**Confirmed:** automatically register the transport integration for modern and `Startup`-based hosting. The user should not need a second middleware call or knowledge of OTel ordering.

**POC evidence:** public `IStartupFilter` registration inserts the observer before the application pipeline in both modern and Generic Host/`Startup` hosting. It observes the tested final exception-handler response, unmatched route and streams. `IExceptionHandlerPathFeature.Endpoint` retains the original parameterized route after re-execution selects the error endpoint. Earlier short-circuits, third-party startup filters and Development exception-page placement remain untested.

Prefer stock ASP.NET Core request instrumentation when it satisfies one SERVER activity per request. Apitally-specific capture, metrics, consumer attribution, and errors remain in SDK-owned paths, so reusing user instrumentation does not remove those features.

Route resolution must produce parameterized endpoint templates with applicable path/group prefixes. Preserve the original matched route through exception-handler re-execution where the framework exposes it. Unmatched requests export trace detail without a route and contribute no request histograms or error aggregates. Client address and scheme attribution follow ASP.NET Core's configured forwarding/trust behavior; Apitally does not add a second forwarding-header trust policy.

### Validation and server errors

**Inherited:** automatic framework recognition is the validation API. No public validation-capture method or response-parser callback is added.

**POC evidence:** the [error/integration experiments](../pocs/error-integrations/README.md) observe automatic MVC validation by wrapping the existing `ApiBehaviorOptions.InvalidModelStateResponseFactory`, preserving its behavior. `ProblemDetailsOptions.CustomizeProblemDetails` exposes typed validation objects when the registered problem-details service runs. Registering these options callbacks does not itself install MVC; Minimal-only hosts remain without MVC services.

Known response shapes provide a conservative fallback. `TypedResults.ValidationProblem` bypasses the problem-details service on net8 but uses it on net9/net10. Built-in Minimal API parameter validation appears with `AddValidation` on net10; without problem-details services its tested response is compact 400 JSON containing title/errors. The probe recognizes tested 400/422 defaults, preserves opaque field strings and skips ordinary 400s. Recognizing bytes does not prove their transport capture is bounded or complete.

**Confirmed v1 scope:** capture MVC and Minimal API validation from framework-provided details and known standard response shapes. Preserve available metadata and opaque field/message strings, including localized or customized message text within a supported shape. Do not infer arbitrary custom/localized schemas or guess unavailable binding sources or fields. An unfamiliar response format without framework-provided validation details skips dedicated validation aggregation; ordinary request monitoring and existing error eligibility remain unchanged.

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

**Confirmed:** an additive `ILoggerProvider` capture adapter, marked `[ProviderAlias("Apitally")]`, that builds Apitally's log records directly. There is no private OTel logger provider. The [logging POC](../pocs/private-logging/README.md) forwarded an external scope provider through the adapter and showed that both registration orders preserve independent user sinks, resources and output; that adapter evidence still applies. Do not call the application's OTel logging registration.

Provider-independent minimum/category rules apply to the adapter, and the alias lets users narrow capture with standard `Logging:Apitally:LogLevel` configuration. Filters targeting the user's OTel provider remain specific to that provider. Third-party logging-factory replacements remain untested.

Resolve `apitally.request.server_span_id` through the activity-to-request association, preserving the emitting child span ID separately. Application logs without a request association are dropped. Exclude Apitally's and the OTel SDK's own diagnostic logs from capture to prevent feedback loops. Capture code-location attributes when supplied by the logging interface; do not invent stack inspection solely to manufacture them.

Run `MaskLogRecord` synchronously on the captured record before buffering. It may return the supplied record or drop it; exceptions, a different instance or a null/empty `Body` drop the record. Normalize values before the callback so it cannot change what other application logging providers receive, and so it sees the values that will be exported before truncation. Truncate string bodies and string attributes to 2,048 characters after masking, when the batch worker encodes the record.

**Confirmed .NET deviation from the shared design:** the shared design asks for the ecosystem's mutable log-record type. `ILogger` has no record type, and an OTel `LogRecord` produced for this path would differ from ordinary OTel .NET use: `Body` would hold rendered text rather than the template, `Exception` would always be null, `FormattedMessage` would be ignored and scopes would be flattened. Producing it would also require a second provider lifecycle, pooled-record lifetime rules, a second copy of every record, and the `OpenTelemetryLoggerProvider(IOptionsMonitor<...>)` constructor that pinned 1.19.0 source marks for deprecation in favor of the still-experimental `Sdk.CreateLoggerProviderBuilder`. `MaskLogRecord` therefore receives an Apitally-owned `LogRecordSnapshot`.

**Confirmed callback type:** `LogRecordSnapshot` has read-only `Timestamp`, `CategoryName`, `LogLevel` and `EventId`, and mutable `string? Body` and `Dictionary<string, object?> Attributes`. Its constructor is internal. Trace context and request linkage are added after masking, so the callback cannot unlink or reassign a record. The accepted instance is buffered without another copy; the SDK does not defend against callbacks that retain and later mutate it.

**Confirmed callback message:** `Body` contains rendered text, and the original message-template attribute `{OriginalFormat}` is omitted. `Body` is the message field for masking and export. A record whose `Body` is null or empty after masking is dropped locally, because the server drops empty-body records at ingest. Do not restore an unmasked rendered message or template after the callback. Structured values remain separate attributes and need separate masking when sensitive; changing `Body` does not implicitly redact those values. Other logging providers retain their normal message representations.

**Confirmed exception representation:** present captured `exception.type`, `exception.message` and `exception.stacktrace` strings as attributes. Capture these values before the callback so it can mask or remove them as ordinary attributes. Export the accepted attribute values after normal log truncation, and do not restore original exception details after masking. The application's exception object never reaches the callback. Separate request-level exception capture, error aggregates and other logging providers are unchanged.

**Confirmed structured-scope representation:** copy and flatten structured scope fields into `Attributes` before invoking `MaskLogRecord`. For overlapping keys, explicit log-entry fields take precedence over the innermost scope, with inner scopes taking precedence over outer scopes. Expose these values once, as attributes rather than a separate scope chain. Export the callback's accepted values; do not re-enumerate or reattach scope values after masking. This is Apitally's explicit precedence rule, not a universal OTel convention. Preserve application scope objects and other logging providers unchanged. The native-log-masking follow-up verified this precedence and private scope-value mutation in both application-provider orders; the adapter-side mechanism carries over unchanged.

**Confirmed plain-scope handling:** omit unstructured scope labels such as `BeginScope("Importing orders")` from both callback input and exported application logs. Do not synthesize a `Scope` attribute or append labels to `Body`. For formatted scopes, retain their structured fields while omitting the template and rendered label. This matches the official .NET OTLP exporter's treatment of empty-key scope values and `{OriginalFormat}`. Actual log messages and structured scope fields remain captured, and other logging providers are unchanged.

**POC evidence:** the [native-log-masking follow-up](../pocs/native-log-masking/README.md) passes 828 assertions on each of .NET 8.0.13, 9.0.2 and 10.0.9, using SDK 10.0.301 and OTel 1.19.0. Its adapter-side results still apply: both provider orders preserve the independent sink's original state, scopes, formatter output and exception, and body/attribute edits and removals, supplied-record acceptance, null/throw/replacement drops and 12 concurrent scope contexts pass. Its native-record findings, such as pool reuse and `IncludeFormattedMessage` behavior, no longer affect the design. The tested value set is deliberately finite: selected scalar values, `int[]`, `string[]` and `List<int>`.

**Scope comparison:** the official OTel .NET 1.19.0 provider keeps scopes separate in `LogRecord` when `IncludeScopes` is enabled; it is off by default. Its OTLP exporter flattens structured scope fields into log attributes, skips empty keys and `{OriginalFormat}`, and deliberately preserves duplicate keys. It does not establish a general event-over-scope precedence rule.

Serilog.Extensions.Logging 10.0.0 merges structured scope fields into Serilog event properties before Serilog.Sinks.OpenTelemetry 4.2.0 maps properties to OTLP attributes. The bridge's own scopes use inner-to-outer traversal and add-if-absent, so existing event properties win, then inner scopes. Its external-scope path instead adds or updates properties and can overwrite event fields. Plain/formatted scope text can also appear in a `Scope` array. These are Serilog events, not native OTel SDK `LogRecord` objects.

The third-party NLog.Targets.OpenTelemetryProtocol 1.2.9 target merges captured `ScopeContext` properties into attributes before its OTel `EmitLog` call when `IncludeScopeProperties` is enabled. With the investigated NLog 6.2.1 merge, event/scope collisions retain the event key and rename the conflicting scope key. Outer-versus-inner collision precedence was not established by this review. Plain nested scope states are not automatically exported by this path. These comparisons are source-based; no Serilog/NLog runtime comparison was performed.

**CLR-value source findings:** the pinned OTel 1.19.0 log and span exporters use the same internal attribute writer. Native records/activities retain CLR values until export; their values are not automatically owned or normalized before an Apitally callback. The writer and protobuf serializers are internal, not a supported public conversion service.

| Attribute value | Stock OTLP conversion in the inspected source |
| --- | --- |
| `null` | Empty `AnyValue`, when the source collection retains the key. |
| String, bool, signed/smaller unsigned integers, float/double | Corresponding string, bool, int64 or double value. |
| `char`, `ulong`, `decimal`, dates, time spans and GUIDs | Strings, using invariant formatting where applicable. |
| Enum, ordinary custom object, `List<int>` | Invariant `Convert.ToString` fallback; an ordinary list usually yields its type name, not its elements. |
| `byte[]` | Bytes. Shared spec section 6.3 still limits accepted bytes-valued attributes to captured request/response bodies. |
| Other arrays | Array elements are converted individually; nested arrays/maps and other fallback elements become strings rather than recursive structures. |
| Supported key/value sequences and `IDictionary` | Structured key/value lists, recursively converted through three map levels; deeper maps use string conversion. |

A failing ordinary fallback conversion omits that attribute; a failing array-element conversion omits the whole array attribute. Key/value conversion can omit individual entries, while enumeration failures can reject the containing value. These are source findings, not runtime validation of an Apitally normalizer. They do not adopt every stock serializer limit or override shared payload requirements.

**Confirmed callback value policy:** use the type mapping of the inspected standard OTel .NET export conversions for span and log callback attribute values. Normalize before callbacks and detach arrays/maps from application-owned data. Ordinary lists and opaque objects use the standard string fallback rather than expanding their contents or cloning object properties. Applications can supply arrays explicitly when they want element values captured. Convert values a log callback added when the batch worker encodes the record. Conversion failures omit the value, never passing through raw mutable values as a fallback; the stock converter's internal edge-case behavior is not an Apitally contract. This selects value conversion, not every stock serializer limit; shared Apitally payload requirements still apply.

This is an explicit .NET qualification of the shared design's non-string pass-through wording: a CLR value converted to a log string before masking is subject to the existing 2,048-character string limit after masking. The same applies to a string produced while encoding accepted callback output. Span attributes retain their separate limits. The limit counts UTF-16 code units, matching the JavaScript SDK; a split surrogate pair is harmless because Google.Protobuf encodes strings with replacement. Array and map values pass through untruncated, as the shared design specifies for non-string values. The selected normalizer requires runtime validation beyond the finite POC value set.

**Confirmed duplicate-key rule:** within one attribute or key/value collection, the last occurrence of a key wins, including accepted callback output. The selected log-entry-over-inner-scope-over-outer-scope precedence remains unchanged.

**Open:** full normalizer validation, production request association and integration with other logging factories.

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

Emit once per serving process under the shared contract. Cross-host startup coordination is outside the v1 scope defined in section 2.

### Error aggregates

Use the shared validation/server aggregation identities, truncation rules and positive `UInt32` count range. Sentry event-ID enrichment is deferred from v1 as described in section 14. Limits remain 100 validation and 100 server groups per process between drains.

Drain atomically, then emit outside the synchronization boundary immediately before the logs pipeline flushes in ordinary and final cycles. Each aggregate has the native event name and a structured OTLP object body, not the startup event's JSON-string body. It carries no request trace context and bypasses application-log masking/truncation.

### Consumer updates

Emit `apitally.consumer.update` events under spec section 9.3. `SetConsumer` accepts optional `name`, `group` and an `IReadOnlyDictionary<string, string?>` attribute patch; string-or-null values avoid coercion rules, and a null value deletes an attribute. Normalize each patch per the spec, keeping the first ten valid attribute entries including deletions, before change detection. Emit only patches that carry metadata, from monitored requests, independently of span recording, trace sampling, exclusion and application-log capture, including unmatched routes but not websockets.

Detect changes with a 10,000-identifier LRU cache of hashes of the canonical normalized patch, not merged consumer state, following the shared design. Update the cache when the event is handed to the log batch processor. Consumer names, groups and attributes are not put on spans.

**Confirmed:** internal events do not pass through the logger adapter. `InternalEvents` builds owned log entries directly, with the event name, scope `apitally`, empty trace/span context and a string or structured body, and submits them to the log batch processor. They bypass application masking and truncation by construction, even with application capture disabled.

**POC evidence:** the encoding POC round-trips native event names, startup string bodies and structured error bodies through official protobuf messages. Implementing full aggregate/startup behavior remains unproven.

## 10. Export pipeline

**Inherited:** stock batching machinery feeds SDK-owned OTLP encoding, a write-through spool, and one export worker per runtime. Delivery is HTTP/protobuf; there is no stock OTLP network exporter and no additional retry policy layered underneath.

### Encoding and batching

**Research finding:** the .NET OTLP exporter's protobuf serializers are internal. The package does not expose a public encode-only API. Its network exporter is not a drop-in spool encoder.

**POC evidence:** generated official OTLP v1.11.0 message classes and `Google.Protobuf` round-trip the tested trace, log and real SDK metric data, including binary bodies and structured internal events. Metric points are mapped and serialized synchronously before exporter return, avoiding retention of reusable SDK storage. Two unframed requests per signal merge correctly from a single continuous gzip stream, independently checked with Python zlib.

A 32-record trace chunk exceeds the 4,000,000-byte cap in the experiment; exact encoded-size checks and splitting preserve the tested records within it. The POC also rejects an indivisible oversized encoded request. Generated messages are a demonstrated encoding mechanism; the full mapper, package layout, memory bounds and backend acceptance remain open.

**Confirmed oversized-record policy:** after ordinary exact-size batch splitting, drop an indivisible encoded telemetry record that still exceeds the 4,000,000-byte spool cap, issue a deduplicated actionable warning and continue with the other records. Do not invent fragments or rewrite the record to force it to fit. The warning explains the lost item and how to reduce its size without logging its contents. This is separate from the existing 50,000-byte body-capture limit and `[BODY_TOO_LARGE]` behavior; ordinary records are not discarded with the oversized item.

Use stock batch queue/worker machinery with explicit settings and approximately one-second intake delay. The snapshot POC demonstrates `BatchExportProcessor<T>` intake without private reflection. Bound encoded appends by actual size; a record-count chunk limit alone is not proof that a file stays below the cap.

**POC evidence:** in the tested stock batch processor, `ForceFlush` can return true after dequeue but before synchronous `Export` finishes. Its export timeout does not cancel a blocked synchronous exporter. Successful `Shutdown` drains and joins; standalone `Dispose` alone does not. Generic intake does not reject calls after shutdown; such records are only buffered, and the SDK accepts that a record racing shutdown may be left unsent rather than adding an admission lock. The production design still needs completed-spool-write coordination and lifecycle handling. Do not treat a successful flush as proof a file is ready to close and send.

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
| File permissions | Create spool files owner-only (`0600`) on non-Windows, as the Python and JavaScript SDKs do; they contain masked but potentially sensitive payloads. |
| Filesystem fallback | Probe at spool construction; a failed probe selects memory with one warning. Later write failure discards the current affected file with deduplicated warning; it does not switch storage mode. |
| Orphan cleanup | Recognizable spool files untouched for two hours, checked once at construction. Active runtimes refresh file modification times each cycle. |
| Final cycle | Drain error groups, flush batch processors, collect metrics, close all current files, and attempt delivery without inter-send pauses or the ten-file cap. Normal failure rules still apply. |

Send through one private `HttpClient` over a `SocketsHttpHandler`, not `IHttpClientFactory`, so application-wide client defaults such as resilience handlers cannot add retries beneath the worker. Capture `HttpClient.DefaultProxy` once and assign it to the handler; .NET already implements `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`, plus the system proxy on Windows. Run collection, flushing, and export POSTs under OTel instrumentation suppression. This is especially important with default `HttpClient` instrumentation and with a user's own exporter observing application activities.

**POC evidence:** four physical loopback POSTs replay identical persisted gzip bytes and preserve the required synthetic headers. Stock HTTP instrumentation exports the unsuppressed requests and none inside `SuppressInstrumentationScope`. Explicit proxy-object binding survives a later environment change, but physical proxying and full proxy-variable semantics are untested. Retry classification, spool durability/retention, scheduling and shutdown coordination are not implemented by this experiment.

**Proposed:** a host-owned worker drives metric collection and spool delivery. Use .NET background execution appropriate to the work: HTTP sends may be asynchronous, while CPU-bound body processing must remain outside request-serving execution. Keep concurrency close to the shared model rather than introducing a task or thread per request/span.

**Open:** exact batch processor settings, protobuf generation/distribution, metric snapshot lifetime, spool concurrency, and integration with the confirmed host-controlled shutdown budget. Preserve the shared process-wide limits for the single-host baseline.

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

**Open:** the exact fixed capacity and its measured memory/collection costs. Ordinary process-gauge implementation remains in scope; cross-host coordination does not gate v1.

## 12. Error handling and logging posture

**Inherited:** operational SDK failures must not break the application. This includes setup/activation, body observation, capture, processing, and export. Preserve application exceptions and stream behavior while containing SDK failures. Privacy failures never authorize exporting unredacted data.

Use SDK-namespaced .NET logging for diagnostics. Operational initialization failures are errors; actionable data loss is a deduplicated warning with a consequence and remedy; normal adaptation and best-effort enrichment failures are debug-level. Never interpolate a full write token.

A missing token disables telemetry rather than failing options validation during host startup. Documented API misuse may fail synchronously with an actionable message.

**Proposed:** diagnostics go through the application's logging infrastructure while the Apitally capture adapter excludes them. Verify export failures cannot re-enter the telemetry pipeline through `ILogger` or HTTP instrumentation.

## 13. Public API

### Setup

**Confirmed shape:** one `IServiceCollection AddApitally(this IServiceCollection services, Action<ApitallyOptions>? configure = null)` registration call, an automatic `Apitally` configuration section, and optional typed code configuration. There are no host-builder overloads; `TryAdd*` registration makes repeated calls safe.

Illustrative modern-host setup, with the token supplied through configuration or `APITALLY_WRITE_TOKEN`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApitally();

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

Generic Host with `Startup` calls the same `services.AddApitally()` in `Startup.ConfigureServices`, following the .NET convention used by OTel's `AddOpenTelemetry()` and v0's `AddApitally`.

**Confirmed external-provider setup:** register the existing instance with standard DI, preserving its original ownership:

```csharp
builder.Services.AddSingleton<TracerProvider>(existingProvider);
builder.Services.AddApitally();
```

The provider's instrumentation and source subscriptions must be configured before it is built, as described in section 2. The exact combined integration remains to be verified.

### Span-based callbacks

**Confirmed:** `SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody` and `MaskResponseBody` use one complete span snapshot type, with `SpanSnapshot` as its working name. The body callbacks additionally receive the body to mask. The snapshot's shape is consistent across callbacks while its available data follows the stages described in section 6. Both sampling callbacks return `double?`, with probabilities and stage-specific abstention as described there. Both body callbacks use `Func<SpanSnapshot, byte[], byte[]?>`, with the snapshot first and decompressed body bytes second; a returned array replaces the body and `null` produces `[REDACTED]`. Attribute values use the exporter-aligned normalization in section 9. Events, links and resource use native `ActivityEvent`, `ActivityLink` and OTel `Resource` types; final member names remain open. Log masking uses the separate `LogRecordSnapshot` type because it processes a different signal.

### Log masking

**Confirmed direction:** `MaskLogRecord` is a `Func<LogRecordSnapshot, LogRecordSnapshot?>` invoked synchronously; it may return the supplied record or drop it. `LogRecordSnapshot` exposes read-only `Timestamp`, `CategoryName`, `LogLevel` and `EventId`, and mutable `Body` and `Attributes`. The callback receives rendered message text in `Body` with the original message template omitted, and exception metadata as string attributes that masking can change or remove. Message and exception content are exported from accepted `Body` and `Attributes` only. Structured scope fields are merged into `Attributes`, with explicit log-entry fields taking precedence over inner scopes, then outer scopes. No separate scope chain is exposed, and removed values are not restored after masking. Unstructured scope labels are omitted; actual log messages and structured scope fields remain captured. Trace context and request linkage are SDK-owned and added after masking. Section 9 records the remaining normalization and production-integration work.

### Request helpers

**Confirmed primary surface:** inject `IApitally` rather than require a static SDK singleton or public `HttpContext` extension methods.

| Operation | Required behavior |
| --- | --- |
| `SetConsumer(identifier, name, group, attributes)` | Retain normalized identity in request state and set the SERVER attributes when available. Metrics retain the consumer even without recorded trace detail. Name, group and attributes produce consumer-update events as described in section 9. |
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
| Unified setup | One `IServiceCollection.AddApitally` registration for both supported hosting styles; automatic transport integration. | Confirmed API direction; combined pipeline remains open. |
| Provider activation/attachment | Standard DI provider registration; externally built providers use existing-instance registration and retain original ownership. | Confirmed API path; combined activation and lifetime validation remain open. |
| Tracing customization | Standard OTel provider registration selects application-owned tracing; configure-only hooks do not customize the private default provider. | Confirmed boundary. |
| Multi-host integration | Single-host support baseline; additional hosts are not prohibited. Preserve the documented cross-provider sampling limitation. | Confirmed support boundary; special cross-host coordination is outside v1 implementation and release requirements. |
| Manual block and function forms | Native `Activity` scope via `IApitally.StartActivity`. | Confirmed adaptation of the shared SHOULD. |
| Activation trigger and failure scope | Activate in the startup filter after the application pipeline is built, before the server starts; one attempt per host runtime, with no first-request fallback. | Confirmed consequence of host ownership and the shared pre-request signal. |
| Test-host suppression | Recognize the resolved server's exact TestServer type/assembly without a test-framework dependency; real Kestrel tests use explicit disabling. | Confirmed scope; candidate runtime passed .NET 8/9/10 checks, full SDK integration remains open. |
| Options layout | Flat `ApitallyOptions` properties, matching keys directly under the `Apitally` configuration section. | Confirmed .NET API layout. |
| Configuration timing and repeated calls | Standard Options pattern: a base configure step, then `AddApitally` callbacks as `PostConfigure` in registration order. Read once at startup preparation, register components once and freeze before activation. | Confirmed behavior; integration remains to be validated. |
| Process identity, startup frequency, limits, process gauges | Preserve shared process-wide contracts and one process identity across all signals under host-owned state. | Inherited requirements for the single-host baseline; no global host coordinator. |
| Ordinary final drain | Share the host's remaining shutdown budget and honor host cancellation, without an additional SDK flush window. | Confirmed budget policy; exporter/spool and disposal coordination remain to be validated. |
| Unfinished-request detail | Discard requests still awaiting transport completion or SERVER activity end at the final SDK cutoff; retain normal flushing for finalized requests and independent recorded metrics/error aggregates. | Confirmed per-SDK policy permitted by the shared shutdown contract; integration remains to be validated. |
| Late request telemetry | Drop spans and logs arriving after release; no completed-request cache. | Confirmed .NET choice, permitted by shared spec section 6.5. |
| SDK span/log representations | Owned export snapshots and generic stock batch processors. | Exercised in POCs; detailed ownership and production lifecycle integration remain open. |
| Span callback type | One complete span snapshot type for all sampling and body-masking callbacks, using native event, link and resource types. It is the SDK-owned record, with read-only interfaces but no isolation guarantee. | Confirmed .NET adaptation; final member names remain open. |
| Sampling result type | `double?` represents the keep probability or abstention for both callbacks; boolean choices use zero or one. | Confirmed typed C# adaptation; shared sampling semantics preserved. |
| Custom pattern inputs | `List<string>` of .NET regex patterns for both code options and configuration files, case-insensitive by default with explicit inline options respected. | Confirmed input type and matching convention; user patterns extend built-in defaults. |
| Body completeness | Finalize ordinary bounded capture with directly observed failure, visible cancellation and applicable length checks; omit known incomplete bytes without certifying transport success. | Confirmed scope and mechanism boundary; production integration remains to be validated. |
| Native file response bodies | Delegate native file sends and omit their entire body capture, including mixed output; retain incidental eligible capture through ordinary observed streams. | Confirmed v1 scope deviation; no SDK file rereading or replacement copy path. |
| Validation recognition | Framework-provided details and known standard response shapes; opaque field/message strings and available metadata, with unknown source/field empty. | Confirmed v1 boundary; arbitrary schema and binding-source inference are outside scope. |
| Log callback type | Apitally-owned mutable `LogRecordSnapshot`, built by the logger adapter and masked synchronously; no private OTel logger provider. | Confirmed .NET deviation from the shared ecosystem log-record rule; full normalizer/integration validation open. |
| Callback attribute values | Standard .NET OTLP value type mapping before span/log callbacks; accepted log output is converted and truncated at encoding. | Confirmed .NET adaptation; full normalizer validation remains open. |
| Log exception representation | Private exception type/message/stacktrace string attributes available to masking; the callback type has no exception object. | Confirmed .NET adaptation; preserves maskable exception metadata without sharing the application object. |
| Log message representation | Rendered text in `Body`, with the original message template omitted from callback input. | Confirmed .NET adaptation; structured attributes remain separately maskable. |
| Log-mask record type | Apitally-owned `LogRecordSnapshot` instead of the ecosystem log-record type, because `ILogger` has no record type and an OTel `LogRecord` would carry misleading semantics on this path. | Confirmed .NET deviation. |
| Structured log scopes | Flatten private scope fields into attributes before masking; explicit log fields override inner scopes, then outer scopes. | Confirmed .NET adaptation; no separate scope chain or post-mask restoration. |
| Plain log scope labels | Omit unstructured labels while preserving log messages and structured scope fields. | Confirmed boundary, matching the official .NET OTLP exporter. |
| Encoding | Official OTLP schemas/protobuf encoding with SDK-owned mapping. | Proposed .NET mechanism; no change to HTTP/protobuf delivery. |
| Metric capacity | Internally selected fixed capacity through native OTel views and reclamation, with visible overflow degradation. | Confirmed policy; numeric capacity requires measurement. |
| Runtime-specific fork and signal mechanics | Use .NET host lifecycle instead. | Platform adaptation. |
| Sentry event-ID correlation | Defer the integration beyond v1 while retaining ordinary exception/error capture. | Confirmed v1 scope deviation; POC retained as future research. |
| Startup endpoint documentation | Populate native summaries/descriptions in `paths`; omit full OpenAPI JSON on all runtimes, including .NET 10. | Confirmed v1 scope deviation; native metadata probe passed, combined startup export remains to be validated. |

The wire attributes, scope names, default redaction/exclusion rules, sampling convention, intact-or-omit body/privacy rules, error identities, and transport behavior remain shared requirements subject to the explicit adaptations above. The native file-send exclusion narrows capture coverage; it does not permit exporting a partial captured prefix. A proposed .NET mechanism does not override other shared requirements by implication.

Native AOT support is a product scope decision, not a shared-contract deviation. The vendor comparison found no universal support expectation, while Sentry and upstream OTel provide relevant examples of in-process compatibility. Revisit demand before expanding the supported deployment matrix; do not use profiler-agent limitations as proof that AOT is inherently impractical for this SDK.

## 16. Code style and testing

Write small, idiomatic C# components following the shared naming and testing rules. Public entry points precede supporting helpers. Prefer .NET lifecycle and concurrency primitives over porting Python/JavaScript mechanics. Extract helpers only when they materially improve clarity.

### Test structure

**Proposed:** retain xUnit as the test framework, with focused shared-module tests and small real ASP.NET Core applications. Cover controller and Minimal API behavior, modern hosting, and Generic Host with `Startup` without multiplying identical business scenarios across every configuration.

Use in-memory OTel-side observation for SDK behavior, and a local HTTP endpoint when testing physical OTLP delivery. Permanent tests assert Apitally behavior, not upstream internals. POCs may investigate dependency behavior to choose the design; that does not require turning every probe into a permanent regression test.

Do not replace Apitally classes with mocks. Assert exact exported counts and attributes. Read responses to completion before asserting completed telemetry. Use real Kestrel coverage where test-server behavior cannot establish streaming, abort, or hosting correctness. Keep test state cleanup in shared fixtures; host ownership does not make process-wide activity listeners and environment variables disappear.

### Required behavioral coverage

The implementation plan's section 12 is the single list of required test coverage.

Add a .NET application/language adapter to the sibling [SDK test harness](../../sdk-tests/README.md) so the shared end-to-end tests exercise real ingestion. The harness currently has Python and JavaScript language adapters; .NET integration is work to be done, not existing coverage. Keep ordinary app configuration in that harness rather than adding workarounds to make tests pass.

The runtime matrix is .NET 8/9/10. Native AOT publishing is not an initial release gate.

## 17. Rewrite approach and v0 reuse

**Confirmed approach:** preserve v0 as a branch, remove the v0 code from `v1` and build v1 fresh. Keep useful repository infrastructure. Port proven logic and behavioral test scenarios from the branch into the new architecture rather than modifying legacy components in place.

The current v0 reference is commit `65e25ed13e15c6d6b77125749eba5cece6aa008f`. Preserve it as a `v0` branch before removing v0 code from `v1`; none has been created as part of this design work.

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
| Persistent instance UUID/lock behavior | Replace with the v1 process-identity contract: one identity across all signals, regenerated on process restart. |
| Independent forwarding-header interpretation | Use ASP.NET Core's configured trust behavior. |

A stable implementation is a useful baseline, not evidence that every existing behavior is appropriate for v1. Review reused code and tests against the shared requirements before porting them.

## 18. POCs required before implementation choices are settled

The first feasibility round is complete and independently checked across the installed .NET 8/9/10 runtimes. Its six experiment groups cover the topics below, combining transport and host lifecycle. Each report distinguishes tested mechanisms, reproduced limitations and remaining gaps; this is not a complete integrated SDK or proof of every acceptance case. See [the POC index](../pocs/README.md) for results and reproducible checks.

| POC | Questions and acceptance evidence |
| --- | --- |
| Provider registration and host ownership | Both DI registration orders; explicit external provider; default HTTP instrumentation; existing instrumentation; user's sampler/exporters unchanged; first request captured. Two-host association, shutdown and sampling-interference experiments remain evidence, not additional v1 release requirements. |
| Middleware placement and completion | Modern and `Startup` hosting; controllers and Minimal APIs; exception-handler final responses and route re-execution; streamed responses, `BodyWriter`, and native file delegation with capture omission; both transport/activity completion orders; directly observed abort/cancellation behavior. |
| Private export snapshots and batching | Preserve span identity/events/links/resource; late enrichment without original mutation; no captured payloads in user exports; maximum-size complete bodies; bounded release/drop and late descendants; public stock batching over the selected representation. |
| Private logging and internal events | Additive `ILogger` capture; category filtering, scopes, mutable state isolation, masking/drop; request linkage through child activities; startup JSON string versus structured error bodies; event names and context-free internal records. |
| Encoding, metrics, and delivery | Official protobuf round trips for all signals; binary bodies and exponential histograms; concatenated request decoding; actual encoded-byte rotation limits; delta collection/reclamation and capacity behavior; idle liveness; immutable retries, proxy binding, and instrumentation suppression. |
| Error and optional integration hooks | Conservative MVC/Minimal API validation; first exception and final-500 rule; .NET 10 handled-exception diagnostics; request cancellation; finalized routes and native summary/description metadata. Sentry and full-OpenAPI evidence is retained for future work outside v1. |
| Host shutdown | Server/request draining relative to SDK/provider disposal; ordinary final cycle; host cancellation budget; unfinished request policy; no duplicate release or retained host state after disposal. |

Follow-up probes independently validate [native endpoint metadata](../pocs/endpoint-metadata/README.md), [TestServer activation suppression](../pocs/test-host-suppression/README.md) and [native log masking](../pocs/native-log-masking/README.md) on .NET 8/9/10. Their reports distinguish candidate-runtime evidence and limited value sets from full SDK integration. The [transport-completeness follow-up](../pocs/transport-completeness/README.md) informs the simple completeness checks and native file exclusion in section 7; its experimental file-capture alternatives are not v1 release requirements.

## 19. Next design decisions

The interview has settled support scope and the main user-facing direction. Further design discussion should focus on material architecture, application impact and release-support decisions. Routine implementation details should follow the selected contracts and established .NET conventions rather than becoming individual interview questions. Most remaining items below are engineering validation work:

1. Provider-selection/attachment timing, external-processor lifetime and full SDK integration of the verified TestServer guard for the confirmed standard DI integration paths.
2. Implement and validate process identity, process-wide bounds, startup events and process measurements for the single-host baseline, plus measurement and selection of the fixed internal metric capacity. Special multi-host coordination is outside v1 scope.
3. Detailed span-snapshot members and collection/value types, full normalizer validation and integration of the selected log-mask representation, remaining option types and validation of Options-based configuration resolution.
4. Integration of the simple body-completeness checks and native file omission, implementation of the unfinished-request cutoff and exporter/spool completion within the host's shutdown budget.
5. Package target frameworks, C# language version and integrated qualification of the selected OTel 1.19.0 baseline and instrumentation/dependency graph.

After those decisions, focused integration probes should compose the verified mechanisms, especially startup-filter activation, final responses, private pipeline ownership and shutdown. Physical proxy/retry/storage-failure behavior and shared backend/harness acceptance also remain to be validated.

These remain open rather than being filled with assumptions from another SDK. Implementation approval follows review of this document and the relevant POC results.

## Research references

Local source snapshots used for the initial review:

- Shared SDK documents: cloud commit `f22ee6c0`, `docs/sdks/spec.md` and `docs/sdks/design.md`.
- Python reference: `ddf5127cd5e16fec6e89eed41b1965c202e03b73` (`v1.0.0b3`).
- JavaScript reference: `16ed4266a637a944b9962b00ddad2a626759eeb6` (following `v1.0.0-beta.2`).
- .NET v0 baseline: `65e25ed13e15c6d6b77125749eba5cece6aa008f`.

Upstream source research included OTel .NET 1.19.1, ASP.NET Core instrumentation 1.19.0 with an older-version comparison, and .NET runtime 8/10. These are research snapshots, not selected dependency floors.

All six POC groups were independently rerun on .NET 8.0.13, 9.0.2 and 10.0.9 using SDK 10.0.301. The provider, snapshot, logging and encoding/metrics experiments pin OTel 1.19.0. Encoding/metrics also passed independent builds/runs using SDKs 8.0.406 and 9.0.200. Stable 1.19.1 was unavailable from NuGet during the experiments. Their net8/net9 targets load the transitive DiagnosticSource 10.0.0 package, not their original in-box Activity implementation. The transport/lifecycle POC was rerun on the same runtimes without OTel/NuGet dependencies; its native listener evidence does not establish OTel processor ordering. Reports record exact dependencies, assertions and scope limits. OTel SDK 1.19.0 is now the selected v1 minimum baseline, subject to integrated qualification; other dependency versions are not automatically production floors.

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
- [TestServer identity and features](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/TestHost/src/TestServer.cs#L13-L31) and [server registration](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/TestHost/src/WebHostBuilderExtensions.cs#L27-L50)
- [WebApplicationFactory server selection](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs#L341-L368)
- [Request pipeline construction with the resolved server](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/Hosting/src/GenericHost/GenericWebHostService.cs#L122-L143)
- [Native log-record writable members and scope enumeration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L228-L457)
- [Native log input processing, message fields and synchronous pooling](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L44-L208)
- [Stock OTLP log exception and message serialization](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs#L258-L309)
- [OTel attribute type dispatch and invariant fallback](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/Shared/TagWriter/TagWriter.cs#L8-L181), [dictionary depth handling](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/Shared/TagWriter/TagWriter.cs#L214-L289) and [array element conversion](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/Shared/TagWriter/TagWriter.cs#L438-L515)
- [OTLP empty values, bytes and structured key/value writing](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpTagWriter.cs#L153-L232)
- [Span attributes use the shared writer](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpTraceSerializer.cs#L327-L395) and [log attributes use the same writer](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs#L387-L412)
- [Official .NET OTLP scope export and duplicate keys](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md#L65-L72)
- [Serilog scope conversion and collision handling](https://github.com/serilog/serilog-extensions-logging/blob/d220ae75c7f1150d58f228f210f2ff8f6a36bcde/src/Serilog.Extensions.Logging/Extensions/Logging/SerilogLoggerScope.cs#L50-L129) and [own/external scope traversal](https://github.com/serilog/serilog-extensions-logging/blob/d220ae75c7f1150d58f228f210f2ff8f6a36bcde/src/Serilog.Extensions.Logging/Extensions/Logging/SerilogLoggerProvider.cs#L92-L126)
- [Serilog OTLP property mapping](https://github.com/serilog/serilog-sinks-opentelemetry/blob/9c39ab59a1e4595dbbe20baffe72f5da994331e8/src/Serilog.Sinks.OpenTelemetry/Sinks/OpenTelemetry/OtlpEventBuilder.cs#L101-L125)
- [NLog OTLP target scope-property mapping](https://github.com/juliuskoval/NLog.Targets.OpenTelemetryProtocol/blob/8d6da4b587c933ab5b5af9929691fafc2ab02c5f/NLog.Targets.OpenTelemetryProtocol/OtlpTarget.cs#L340-L435) and [NLog property collision handling](https://github.com/NLog/NLog/blob/b058028290f245d12c30cb2910c2a574558ed9c6/src/NLog/Targets/TargetWithContext.cs#L279-L357)
