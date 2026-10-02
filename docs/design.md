# Apitally .NET v1 design

Status: Decided, 2026-09-28. The [implementation plan](implementation-plan.md) describes how to build it.

This document adapts the shared SDK design to ASP.NET Core and the .NET OpenTelemetry SDK. It records the .NET decisions and the facts behind them; it is not an implementation plan.

## Sources and decision status

The [shared specification](../../cloud/docs/sdks/spec.md) owns the ingestion contract. The [shared design](../../cloud/docs/sdks/design.md) owns the cross-SDK architecture and behavior. Sections 1-16 below follow the shared design's section numbering. These links assume the local sibling-repository layout.

Python and JavaScript are implementation references, not additional requirements. Preserve shared telemetry behavior while choosing idiomatic .NET APIs, configuration, and lifecycle mechanisms. Record architectural departures explicitly rather than either copying another runtime's mechanisms or silently changing the contract.

This document distinguishes:

- **Confirmed:** a decided .NET choice.
- **Inherited:** a requirement from the shared documents, unless an adaptation is explicitly identified.

### Confirmed decisions

| Area | Decision |
| --- | --- |
| Runtime support | .NET 8 minimum; test .NET 8, 9, and 10. |
| OpenTelemetry compatibility | Use the tested OTel SDK 1.19.0 baseline as the minimum for v1, subject to integrated qualification. Applications using older OTel dependencies may need to upgrade. |
| Native AOT | Outside the initial support guarantee. Prefer compatibility-friendly choices when they add no complexity. |
| Hosting | Support modern `WebApplicationBuilder` hosting and Generic Host with `Startup`. Modern hosting is the primary documented path. |
| Setup | One `IServiceCollection.AddApitally` registration call with automatic middleware registration. |
| Existing tracing | Automatic integration with DI-registered tracing; register separately constructed providers as existing `TracerProvider` instances in DI. |
| Hosting support boundary | Normal single-host ASP.NET Core integration is the v1 baseline. Additional hosts are not prohibited; special multi-host coordination is outside v1 implementation and release requirements. Independent sampling across overlapping providers is not guaranteed. |
| Configuration | Use the standard .NET Options pattern: one base step binds the `Apitally` section and then applies the `APITALLY_*` environment variables, and `AddApitally` callbacks run as `PostConfigure`. Read `IOptions<ApitallyOptions>` once at startup preparation, then validate and freeze before activation. |
| Repeated setup | Within one host, compose code callbacks in registration order; later explicit assignments win. Register SDK components once with `TryAdd*`. Direct `Configure<ApitallyOptions>` follows standard .NET ordering. |
| Runtime ownership | The application host owns configuration, buffers, workers, and shutdown through DI. |
| Options layout | Use flat properties on `ApitallyOptions` and directly under the `Apitally` configuration section. |
| Unfinished requests at shutdown | At the final SDK cutoff, discard detail for requests still awaiting transport completion or SERVER activity end. Flush finalized requests normally; recorded metrics and eligible error aggregates remain independent. |
| Shutdown budget | Use the host's remaining shutdown budget and honor its cancellation. Add no separate Apitally flush window; final delivery may remain incomplete when the budget expires. |
| Test-host activation | Automatically suppress Apitally for the standard in-memory TestServer by recognizing the resolved server's exact type and assembly. Real Kestrel tests use the existing disable configuration. |
| Request helpers | An injectable `IApitally` service is the primary API. |
| Default instrumentation | When Apitally owns tracing, instrument ASP.NET Core and outgoing `HttpClient` calls automatically, and capture activities from every `ActivitySource` within monitored requests, as v0 did. Instrumentation packages such as EF Core are opt-in. |
| Tracing customization | Use standard OTel provider registration for instrumentation packages. Apitally-specific tracing-configuration callbacks are outside the initial API. |
| Metric capacity | A fixed capacity of 50,000 attribute combinations per collection interval in Apitally's own request-metric aggregation; no public capacity setting or runtime resizing. |
| Individually oversized records | Split ordinary batches to fit the spool cap. Drop an indivisible encoded record that still cannot fit, with a deduplicated actionable warning, and continue with other records. Request metrics are split by attribute combination, keeping each combination's three histograms in one request, because the server joins them within a request. |
| Span-based callbacks | All request/response sampling and body-masking callbacks receive the same complete span snapshot type, populated for the callback's stage. It exposes read-only interfaces and native .NET/OTel types but is the SDK-owned record itself, with no isolation guarantee. |
| Sampling callback result | Both sampling callbacks return `double?`: a keep probability in `[0, 1]`, or `null` to abstain. |
| Late request telemetry | Drop spans and logs that arrive after a request is released. There is no completed-request cache. |
| Body-mask callbacks | Both use `Func<SpanSnapshot, byte[], byte[]?>`: snapshot first, decompressed body bytes second, replacement bytes returned. `null` produces `[REDACTED]`. |
| Body completeness | Finalize bounded ordinary capture using directly observed failures, visible cancellation and applicable length checks. Omit known incomplete bytes; do not build a transport-success certification system. |
| File response bodies | Delegate native file sends unchanged and omit their entire body capture, including mixed stream/file output. Eligible content already passing through ordinary observed streams may be captured incidentally. |
| Custom pattern inputs | Use `List<string>` in code and configuration files. Custom redaction and path-exclusion patterns are case-insensitive by default and respect explicit .NET inline options. |
| Log-mask callback | Use an Apitally-owned mutable `LogRecordSnapshot`, built directly by the logger adapter and masked synchronously. There is no private OTel logger provider. Explicit deviation from the shared "ecosystem log-record type" rule, recorded in section 9. |
| Log exception metadata | Log records carry no exception attributes; the server does not store them. Exceptions are captured on the SERVER span and in error aggregates. |
| Log callback message | Present rendered text in `Body` and omit the original message template from callback input. |
| Structured log values and scopes | Not captured; the server stores only the rendered message, level, logger and code location. |
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
| Environment | Lowercased host environment name, with `Production` as `prod` and `Development` as `dev` |
| Built-in redaction and trace exclusion patterns | Enabled |

Log capture becoming enabled by default must be called out in the migration guide. Application log content is unchanged except for the shared truncation rules unless the user supplies a masking callback.

**Confirmed:** Native AOT is not a v1 release requirement. This is a support boundary, not a claim that an unsupported application will publish successfully or merely lose telemetry. An incompatible dependency can fail during publishing or execution. Conventional managed deployment is the initial supported mode.

**Confirmed dependency policy:** use OTel SDK 1.19.0, the tested baseline, as the minimum for v1 and qualify it through integrated testing. Document that applications with older OpenTelemetry dependencies may need to upgrade them. The initial release will not introduce compatibility paths for older OTel SDK releases. This leaves the .NET 8/9/10 runtime support unchanged; a selected dependency baseline is not evidence that the complete SDK integration already works.

**Confirmed:** one `net8.0` package assembly built with C# 12 serves all supported runtimes; tests run on .NET 8/9/10.

## 2. Integration with existing OpenTelemetry setups

### Tracing registration

**Confirmed:** use the host's normal tracing registration when available. Register a separately constructed provider as an existing `TracerProvider` instance in the host's DI container. Preserve user-owned providers, processors, exporters, resources, samplers, and instrumentation configuration.

**Confirmed:** participate in provider construction through the supported .NET builder/DI APIs. Prefer one additive registration path over creating a competing provider. The OTel library registration API, `ConfigureOpenTelemetryTracerProvider`, can add configuration without independently creating a provider.

The presence of a `TracerProvider` service alone is not sufficient evidence that the user configured tracing: Apitally's own registration may have added it. In particular, do not apply Apitally's fallback sampler to an existing user pipeline simply because both configurations reach the same builder.

When Apitally owns tracing:

- Use an explicit sampler that records monitored requests and their descendants, and nothing else. It applies `SampleRate` to requests without a sampled upstream parent.
- Enable suitable stock ASP.NET Core and `HttpClient` instrumentation.
- Subscribe to all sources with `AddSource("*")`, including the manual tracing source `apitally.otel`, so activities from application sources and natively instrumented libraries appear in request traces without OTel configuration.

**Confirmed fallback sampler:** record an activity only when its operation name is `Microsoft.AspNetCore.Hosting.HttpRequestIn` (the ASP.NET Core hosting activity) and either the configured `SampleRate` keeps its trace ID or its remote parent is sampled, or its parent is local and recorded; drop everything else. The rate check is skipped when a `SampleOnRequest` callback is set, because the callback can raise the rate. OTel sampling parameters carry the operation name but not the source name. This mirrors v0's `ActivityListener`: all sources are observed, but background work, other roots such as .NET 9+ SignalR hub invocations, and remote-parented non-request activities are never created as recorded activities.

**POC evidence:** the [HttpClient source-subscription experiment](../pocs/http-client-source-subscription/README.md) passed on .NET 8.0.13, 9.0.2 and 10.0.9 with OTel 1.19.0. `AddSource("*")` alongside stock `HttpClient` instrumentation yields exactly one CLIENT span per outgoing call, as without the wildcard. The sampler sees the hosting activity's operation name, records it under an unsampled remote parent, records app-source and `apitally.otel` children, and records nothing outside requests. On .NET 9+, the only additional in-request span is one `Experimental.System.Net.Http.Connections.WaitForConnection` span when a call opens a new connection; connection-setup, DNS and socket activities start their own trace and are dropped by the sampler. Tested with HTTP/1.1 over loopback and one shared client; `IHttpClientFactory`, HTTP/2 and failures were not tested.

**Accepted consequence:** as with any OTel .NET sampler, `Drop` still creates propagation-only activities. Outside requests, `ActivitySource.StartActivity` returns a non-null activity that is not recorded, and outgoing `HttpClient` calls send `traceparent` with the sampled flag unset. Downstream services with parent-based samplers therefore do not trace requests that originate from the application's background work. Outgoing calls within recorded requests propagate as sampled. Requests that `SampleRate` drops and that have no sampled upstream parent are not recorded, so their outgoing calls propagate as unsampled. A sampled upstream parent keeps the request recorded, so upstream traces continue downstream; Apitally still drops the request's detail.

When the application owns tracing:

- Its sampler governs recorded request detail. Metrics and eligible error capture remain independent.
- Reuse its request activities and adapt to existing instrumentation without duplicate SERVER spans.
- Keep the default outbound-instrumentation decision scoped to Apitally-owned tracing.
- Do not dispose the user's provider during Apitally shutdown.

**POC evidence:** the [provider experiment](../pocs/provider-registration/README.md) preserves explicit and implicit user sampling in both DI registration orders by contributing configuration without enabling a host provider itself. At startup-filter construction it resolves an enabled user provider or constructs a private owned fallback. This also captures a request issued before `ApplicationStarted`. Repeated same-name ASP.NET instrumentation registration is deduplicated in the tested version. The fallback does not apply host tracing callbacks that were registered without enabling a provider.

**Confirmed customization boundary:** `AddApitally()` provides default tracing when the application has not enabled its own provider. To add instrumentation packages such as EF Core or other tracing customization, enable and configure the application's provider through standard `AddOpenTelemetry().WithTracing(...)` registration. Apitally joins that provider and preserves its sampler and instrumentation configuration. Configure-only callbacks without an enabled provider do not customize Apitally's private default pipeline. Document complete customization examples, including the application's responsibility for its sampler and optional instrumentation. An Apitally-specific tracing-configuration callback is outside the initial API.

**Confirmed external-provider path:** use `builder.Services.AddSingleton<TracerProvider>(existingProvider)` before building the host, followed by the normal `AddApitally()` setup. Existing-instance registration leaves disposal with the original owner; Apitally does not take ownership. The supplied official SDK provider must already have the required instrumentation and source subscriptions configured before it is built. This uses standard DI discovery rather than a dedicated Apitally provider parameter or attachment API.

Public post-build `AddProcessor` works for an official SDK provider that already subscribes to the required sources. It cannot add missing source subscriptions, and there is no public processor-detachment counterpart. Disabling the attached path preserves the external provider's lifetime, but does not remove its retained processor.

**Confirmed:** ASP.NET Core and HTTP instrumentation 1.19.0 are the minimum. The provider is selected during startup-filter preparation, and an existing instance receives a detachable forwarding processor (plan section 5).

### Multiple hosts

**Confirmed:** Apitally runtime ownership is per host. This is not permission to create one unrestricted tracing provider per host.

.NET `ActivityListener` subscriptions operate process-wide. The provider POC reproduces sampling interference in both host startup orders: a user sampler still returns `Drop`, but another host's always-on listener causes that user's exporter to receive recorded SERVER activities. One owned host can stop while another keeps serving, but export filtering does not undo sampling promotion or its effect on user exporters. Independent multi-host sampling is not established by host-owned runtime state. Because `IHttpContextAccessor` is also process-wide, one host's Apitally runtime may claim another host's requests and process them with its own configuration.

**Confirmed:** normal single-host ASP.NET Core integration is the v1 baseline. Additional hosts are not prohibited. Coordinating startup events, process gauges, aggregate/spool budgets or tracing across multiple hosts is outside v1 implementation and release requirements. Independent sampling across providers listening to the same sources is not guaranteed, even within a single host. Retain host-owned configuration and lifecycle without a global host coordinator or process-global Apitally configuration singleton.

This boundary follows the distinction in official OTel guidance: repeated hosting registration creates one provider per service collection, while separately constructed providers are supported without establishing host or sampling isolation.

**Confirmed:** request state is created at SERVER activity start, so descendants that end before middleware entry are associated, and children with only an explicit parent context inherit through their parent span ID (plan section 5).

Do not substitute a process-global configuration singleton.

### Private providers and resources

**Inherited:** Apitally owns private meter and logger pipelines. Do not register them as replacements for application-owned pipelines or pass the private meter provider to framework instrumentation.

**Confirmed .NET adaptation:** request and process metrics are aggregated by Apitally without an OTel meter (section 11), so no other meter or provider observes them.

Build an Apitally resource using standard OTel resource configuration, then override:

- `service.instance.id`
- `deployment.environment.name`
- `telemetry.distro.name = apitally-dotnet`
- `telemetry.distro.version`

The resolved Apitally environment must also be used for the `Apitally-Env` HTTP header. Generic resource configuration never overrides it.

On the Apitally-only export of user-owned spans, override the instance ID and environment while preserving other resource attributes and the original user's telemetry.

**Inherited:** use one process instance identity across all signals, regenerated on process restart. Host-owned state does not change the shared process-wide event, gauge or aggregate/spool requirements into per-host requirements. Implement these contracts for the single-host baseline and preserve private-pipeline isolation; special multi-host coordination is outside v1 scope as described above.

**Inherited:** captured bodies must not be silently truncated by OTel limits. In OTel .NET 1.19.0, attribute limits (`SdkLimitOptions`) exist only inside the OTLP exporter package and are applied during its serialization; `Activity` tags are never clipped. Apitally copies activities and encodes them itself, so no OTel limit applies to its export and there is nothing to pin or inspect.

## 3. Configuration

### .NET configuration sources

**Confirmed:** automatically bind the `Apitally` section from the application's `IConfiguration`. This naturally includes the application's chosen JSON files, environment-specific configuration, environment variables, and other providers.

Precedence, highest first:

1. Values explicitly assigned through code options.
2. `APITALLY_WRITE_TOKEN` and `APITALLY_ENV` environment variables.
3. Values present in the `Apitally` configuration section.
4. The host environment name (`IHostEnvironment.EnvironmentName`), for `Env` only.
5. Apitally defaults.

No `OTEL_*` variable maps to an option, matching the Python and JavaScript SDKs. `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` apply through the standard resource builder.

Layering satisfies the shared absent-versus-default rule: an unassigned value keeps the value of the layer below. The application configuration providers resolve precedence within the section; Apitally resolves precedence between the layers above.

The SDK is disabled when the resolved `Disabled` option is true, or either `APITALLY_DISABLED` or `OTEL_SDK_DISABLED` is truthy. A code-level `false` cannot override those environment variables. Shared truthy values are `1`, `true`, and `yes`, ignoring case and surrounding whitespace.

`APITALLY_OTLP_ENDPOINT` remains a testing-only environment override, not a public options property. User `OTEL_EXPORTER_OTLP_*` endpoint, protocol, and header settings do not redirect or authenticate Apitally's delivery pipeline.

The write token must match `apt_` followed by 24 alphanumeric characters. Missing or invalid credentials log an error with at most a masked short prefix and disable telemetry without preventing the application from starting.

### Options and immutability

**Confirmed:** code configuration callbacks receive populated options rather than an override-only object. Apply defaults, the `Apitally` section and the `APITALLY_*` environment variables in increasing precedence, then run the collected code callbacks in registration order. Each callback sees the populated values and earlier callbacks' changes. Unassigned settings retain their existing values; ordinary boolean and numeric settings do not require nullable properties or assignment tracking to distinguish omission from an explicit value.

**Confirmed mechanism:** use the standard .NET Options pattern rather than a custom callback store. The first `AddApitally` call registers one base `IConfigureOptions<ApitallyOptions>` that binds the section and then applies the `APITALLY_*` environment variables; each `AddApitally` callback is registered as `PostConfigure`, so the options factory runs callbacks after all configuration steps and in registration order. A direct `services.Configure<ApitallyOptions>(...)` follows standard .NET ordering relative to the base step and is documented as such. Read `IOptions<ApitallyOptions>.Value` once at startup preparation; it is computed once and never reloaded. Apply the additive environment disable controls, validate the resulting settings and copy them into immutable runtime configuration before activation. Later mutations to the options object or configuration sources must not alter the running SDK.

**Confirmed layout:** keep the configuration surface as flat properties on `ApitallyOptions`, such as `SampleRate` and `CaptureRequestBody`. The same keys appear directly under the `Apitally` configuration section. Sampling, capture and redaction do not introduce nested options groups.

**Confirmed names:** use PascalCase names corresponding to the shared settings: `WriteToken`, `Env`, `AppVersion`, `Disabled`, `CaptureLogs`, the four directional capture toggles, `SampleRate`, the sampling and masking callbacks, and the redaction/exclusion pattern collections.

Callbacks are configured in code. The startup event serializes their presence as `true`, not their implementation. Pattern serialization includes their effective flags where relevant.

**Inherited:** invalid static sampling rates resolve to full capture; invalid patterns are individually rejected with an error while valid patterns remain active. Default patterns remain case-insensitive and user patterns extend them.

**Confirmed regex inputs:** expose each custom query-param, header, body-field and path-exclusion pattern collection as `List<string>`. Both code options and the `Apitally` configuration section supply .NET regex pattern strings. Validate and prepare patterns during startup configuration resolution, preserving the immutable-runtime rule above. Native `Regex` objects are not an additional public input form.

**Confirmed custom-pattern matching:** use case-insensitive regex search by default for all four collections, consistently across code and configuration files. Respect standard .NET inline options: for example, `secret` matches `Secret` and `SECRET`, while `(?-i:secret)` makes that expression case-sensitive. Inline options override conflicting constructor options for their applicable scope. User patterns still extend the built-in defaults rather than changing their flags or removing them. Startup pattern serialization must retain the effective default and explicit inline options.

Invalid settings never fail startup: a missing or invalid token disables telemetry and other invalid values degrade as described above, so the SDK uses neither `ValidateOnStart` nor `IValidateOptions`. Default host builders also read unprefixed environment variables such as `Apitally__SampleRate` into the section without Apitally code.

Exact public names and types are listed in plan section 4.

**Confirmed:** repeated registration within one host composes code configuration callbacks in registration order. Later explicit assignments override earlier assignments; a later callback or setup call leaves settings it does not assign unchanged. Resolve the composed code overrides using the source precedence and additive disable rules above, then freeze runtime configuration before activation. Repeated setup must not duplicate middleware, processors, workers or logging providers.

## 4. Lifecycle: configure, activate, shut down

**Confirmed:** DI and the application host own the runtime. A host's shutdown drains and disposes only its owned Apitally components.

**Inherited:** configuration and serving activation are separate. Registration must not start Apitally export workers, send telemetry, or report the process online. Route metadata preparation may happen once the framework has finalized that information.

**Lifecycle:**

1. `AddApitally` on `IServiceCollection` wires options, services, tracing registration, logging capture, and middleware/lifecycle hooks.
2. In the startup filter, read the resolved options once, apply additive disable controls, validate and freeze the resulting settings, and prepare tracing.
3. Activate in the same startup filter, immediately after the application pipeline is built. `GenericWebHostService.StartAsync` builds the pipeline before calling `Server.StartAsync`, so activation completes before the server can accept a request, and hosts that are built but never started, such as design-time tools, never activate. This is the shared design's pre-request signal. Early tracing registration must still ensure the first SERVER activity is observed even if it starts before middleware executes.
4. There is no first-request fallback or concurrent activation gate. The runtime is active, stopping, stopped, or disabled.
5. On ordinary host shutdown, flush finalized requests and run the shared final export cycle before disposing owned providers and transport resources. At the final SDK cutoff, discard request detail that still lacks transport completion or SERVER activity end, as described in section 6.

**POC evidence:** the [transport/lifecycle experiment](../pocs/transport-lifecycle/README.md) observes an early Generic Host request before `ApplicationStarted`, which is why activation does not wait for that event. The separate provider experiment establishes tracing readiness for an early request.

**Confirmed adaptation:** one activation attempt per host runtime. A preparation or activation failure logs an error and leaves that host serving without telemetry. This follows host ownership rather than Python's process-global activation state. `TelemetryRuntime` is a DI singleton implementing `IAsyncDisposable`: if the host fails after activation, for example because the server cannot bind, container disposal stops workers and releases owned resources without final delivery.

**POC evidence:** ordinary hosted-service `StopAsync` ordering relative to Kestrel differs between the two tested hosting compositions. `IHostedLifecycleService.StoppedAsync` follows all service stop calls and is the final-drain phase. A request ignoring cancellation can remain unfinished after host stop returns; a 300 ms host budget took about 1.3 seconds in the tested Kestrel abort path. The final phase receives the already-canceled token in that case. Host cancellation is not an exact wall-clock termination guarantee.

**Confirmed shutdown budget:** the application host controls the available shutdown time. Apitally uses the remaining host budget and honors its cancellation rather than starting an additional SDK flush window. Drain, flush and delivery share that budget; each phase does not receive a fresh allowance. If request draining exhausts it, final telemetry delivery may remain incomplete. This is cooperative cancellation, not a guarantee that framework or blocking synchronous operations terminate at an exact wall-clock deadline.

Plan section 10 specifies the shutdown sequence, including disposal ordering, completed spool writes and the unfinished-request cutoff.

**Confirmed test-suppression direction:** automatically suppress telemetry activation for application integration tests through the TestServer guard below, without broad test-framework detection. The SDK's own tests must remain able to exercise real activation deliberately.

**Research finding:** no documented marker that is generally set across ordinary VSTest and Microsoft.Testing.Platform runs was established. Debug/runtime-selection settings and mode-specific controller variables do not establish a general test environment. This is not proof that every runner-specific marker is absent.

**Confirmed .NET guard:** inspect the host's resolved `IServer` and recognize the exact runtime type `Microsoft.AspNetCore.TestHost.TestServer` from assembly `Microsoft.AspNetCore.TestHost`. Reading that existing object's type and assembly names requires no TestHost dependency or loaded-assembly scan. `UseTestServer` registers this singleton, and default `WebApplicationFactory` uses it. Resolve the actual server after host registrations are complete, not during `AddApitally`.

The guard covers standard in-memory TestServer tests; it does not identify real Kestrel tests, including the explicit Kestrel mode in .NET 10 `WebApplicationFactory`. Such tests use the existing disable configuration. Do not add wrapper introspection or infer testing from the Development environment. No new public testing override is selected; SDK telemetry tests can use loopback Kestrel, with TestServer tests verifying suppression.

**POC evidence:** the independently inspected and rerun [test-host-suppression probe](../pocs/test-host-suppression/README.md) passed 11 cases on .NET 8.0.13, 11 on 9.0.2 and 13 on 10.0.9, using SDK 10.0.301 and OTel 1.19.0. Its startup filter resolves the actual server during pipeline construction, before private fallback-provider creation. Default `WebApplicationFactory`, direct `WebApplicationBuilder` and Generic Host/`Startup` TestServer paths stay inactive for startup and request activation signals. The factory replaces the registration-time Kestrel server before the guard runs; no DI cycle occurred in the probe. The guard assembly references neither TestHost nor Mvc.Testing.

Application-owned tracing continues exporting real completed SERVER spans in suppressed TestServer cases, including after probe disposal. Loopback Kestrel in Development activates and exports through the private fallback even with TestHost loaded; .NET 10 factory Kestrel mode also passes. Export and disposal assertions await observed completion rather than assuming `ForceFlush` establishes it.

Host lifetime integration is the default direction. Python fork handling and JavaScript signal re-delivery are not mechanisms to port into this SDK.

## 5. Request model: span filtering and exclusion

**Inherited:** Apitally exports request-rooted telemetry only. The SERVER span is the request boundary even when an upstream service supplies a remote parent.

At activity start, classify recording activities by their local parent relationship. Only the ASP.NET Core hosting activity (source `Microsoft.AspNetCore`, operation `Microsoft.AspNetCore.Hosting.HttpRequestIn`) is a candidate request; descendants inherit its request identity; unrelated roots and missing-parent associations are dropped from Apitally's path. A user-provider SERVER activity must be associated with a request observed by this integration before export.

Apply shared exclusions before request sampling: `OPTIONS`, websocket requests, excluded paths, and excluded user agents.

**.NET adaptation:** there is no stable/legacy HTTP attribute normalization. Exclusions and final SERVER enrichment read `HttpContext`, and the ASP.NET Core and HttpClient instrumentations at the 1.19.0 minimum emit stable names only. Query redaction still covers stable and legacy query-bearing keys.

**Confirmed:** maintain host-owned request state associated with `HttpContext` through an internal feature, plus an activity-to-request association for processors and log linkage. The state exists even when no recording SERVER activity exists. Its responsibilities are:

- The request's SERVER activity handle and identity, independent of `Activity.Current` becoming a child.
- Consumer identity and request attributes.
- First captured exception and validation details.
- Sampling/exclusion decisions and bounded trace/log buffers.
- Transport completion, body-capture state, and final response measurements.

Consumer identity must survive sampling and be adoptable when set before the SERVER handle is available. Request helpers resolve this state rather than writing indiscriminately to `Activity.Current`.

**.NET adaptation:** there is no per-message span filter. Stock ASP.NET Core instrumentation emits no per-message websocket spans. SignalR on .NET 9+ starts a parentless SERVER activity per hub invocation when its `Microsoft.AspNetCore.SignalR.Server` source is enabled; the hosting-activity rule above classifies it as an unrelated root, so it drops locally.

Plan section 5 specifies the association mechanics. Isolate concurrent and keep-alive requests without a blanket context reset that destroys legitimate upstream propagation.

## 6. Sampling and per-request buffering

**Inherited:** request and response sampling refine the static rate. Both stages compare the low 64 bits of the trace ID against the shared rounded probability threshold, so their effective combined rate is the minimum rather than the product. An invalid or throwing sampling callback warns and fails open.

Request-stage drops skip trace-detail capture work. Excluded requests never invoke sampling callbacks. Metrics and eligible error aggregates remain independent of all trace-detail decisions.

Response sampling runs once with final route, status, sizes, consumer, and custom request attributes. Abstention preserves the request-stage decision rather than sampling again at the static rate.

**Confirmed .NET result type:** both sampling callbacks return `double?`. Zero means drop, one means keep, and a value between them is the keep probability. `null` at request stage falls back to the configured static rate; `null` at response stage preserves the earlier decision. Express boolean conditions as numeric probabilities, such as `condition ? 1.0 : 0.0`, rather than introducing a custom result type or a weakly typed boolean/numeric union. This adapts the shared API's return shape to C# without changing probability, abstention or invalid-result behavior. A response-stage keep cannot recover detail already dropped at request stage.

Hold ended descendants and application logs until both transport observation and the SERVER activity complete. Associate at most the first 1,000 recorded descendants started per request, in addition to the SERVER span. Retain those associations until release; later descendants and logs emitted under them drop locally. Keep at most 1,000 application log records from retained associations, retaining the earliest log arrivals; the SERVER span is always retained. Release descendants, then the SERVER span, then the request's logs once. A drop discards buffered detail and raw payloads. Release and drop both remove the request's associations, so telemetry arriving afterwards drops locally.

### Late telemetry

**Confirmed:** drop spans and logs that arrive after the request has been released. Release removes the request's associations; there is no completed-request cache, retained decision or cross-release counter.

In ASP.NET Core, Kestrel awaits the application pipeline, finishes the response, runs `OnCompleted` callbacks and then calls `HostingApplication.DisposeContext`, which writes the "Request finished" log before stopping the SERVER activity ([HttpProtocol.cs](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs#L673-L772), [HostingApplication.cs](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Hosting/Hosting/src/Internal/HostingApplication.cs#L99-L104)). Because release waits for both transport completion and SERVER end, telemetry from streamed responses, `OnCompleted` callbacks and automatic Sentry.AspNetCore captures always arrives before release. Only work that outlives the response, such as fire-and-forget tasks or queue hand-offs that keep `Activity.Current`, is lost; the trace is shorter, not misattributed.

**Shared contract:** shared spec section 6.5 and the shared design's per-request buffering section make telemetry arriving after release best effort: an SDK may export or drop it. The JavaScript SDK keeps a 10,000-ID completed-request cache; Python drops children started after release. A future Sentry integration that correlates explicit captures made after the request would need its own small pending-event-ID map, as in the Python SDK, not a general late-telemetry cache.

### Export snapshots

**Research finding:** .NET `Activity` is not a detached immutable span representation, and public APIs do not provide a faithful clone with the same IDs, source, and kind. A retained, stopped activity remains usable; the problem is shared mutable state and the inability to represent a privately enriched export view without changing the original. The tested .NET SDK has no equivalent public `ReadableSpan` abstraction. The specialized `BatchActivityExportProcessor` accepts `Activity` objects, not an arbitrary export snapshot.

**Confirmed:** copy the data required for Apitally export into an SDK-owned snapshot. Preserve IDs, parents, times, status, events, links, scope, and resource. Apply late enrichment and privacy processing to that owned representation. Do not fabricate a second live activity to represent the original request or modify user-owned activities to finish export.

**POC evidence:** the [snapshot experiment](../pocs/activity-snapshots/README.md) demonstrates public generic `BatchExportProcessor<T>` intake of owned records, tested metadata/value copying, and private 50,000-byte body processing without changing a simultaneous user export. Mutable array values are copied rather than shared. Worker construction suppresses execution-context flow so activation does not carry ambient context into body processing.

Generic batching does not enforce span sampling semantics: an explicit `Activity.Recorded` check is needed to match the specialized activity processor's treatment of `RecordOnly`. The request-buffer checks are a sequential model, not proof of concurrent framework completion.

**Confirmed callback shape:** all four span-based callbacks (`SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody`, `MaskResponseBody`) receive the same complete span snapshot type, `SpanSnapshot`. Expose available identity/parent, name, kind, timestamps, status, attributes, events, links, resource and instrumentation-scope metadata. Events and links use `ActivityEvent` and `ActivityLink`, the resource uses OTel `Resource`, and the scope is flat `ScopeName`/`ScopeVersion` properties; there are no Apitally-specific event, link or scope types. This is an inspection type, not another span-creation or mutation API.

Each invocation receives SDK-owned data appropriate to its stage, not a shared live `Activity` or `HttpContext`. Request sampling sees currently available information; response sampling includes final transport and custom attributes, including values learned after span end; body masking sees query/header redaction and captured headers before body attributes are attached. Information not yet available is represented as unset. The snapshot is the SDK-owned span record itself, reused from response sampling through export, not a per-callback copy. Read-only interfaces state intent only; the SDK does not defend against callbacks that cast and mutate values or that retain a snapshot and observe later enrichment. User telemetry is unaffected either way because the record is private. The same public type is used throughout rather than mixing `Activity`, request-specific contexts and snapshots.

Python constructs a new instance of its standard OTel `ReadableSpan` class; JavaScript constructs a plain object implementing the standard `ReadableSpan` interface. Both preserve full span metadata while supplying private attributes. The .NET-owned snapshot is an explicit adaptation to preserve that behavior and consistency across .NET callbacks when the standard SDK lacks an equivalent abstraction.

Attributes are an `IReadOnlyDictionary<string, object?>`. Values follow the exporter-aligned normalization selected in section 9 and use the plain CLR types OTel .NET users see on activity tags, including `string[]`, `long[]`, `double[]`, `bool[]`, `byte[]` and one-level `Dictionary<string, object?>` maps.

Exact members are listed in plan section 4. Log masking operates on a different signal and uses the separate `LogRecordSnapshot` type described in section 9.

**Confirmed unfinished-request shutdown policy:** at the final SDK cutoff, discard trace and application-log detail for requests still awaiting either transport completion or SERVER activity end. Discard their buffered descendants/logs together, release captured payloads unprocessed and ensure later telemetry cannot revive those requests. Do not create partial SERVER exports or synthetic end times, and leave application-owned activities untouched.

Requests finalized before the cutoff follow the normal response-sampling, complete-body and once-only release rules and remain eligible for the final export cycle. Already-recorded metrics and eligible error aggregates remain independent of the request-detail discard policy. This is a permitted per-SDK choice under the shared best-effort shutdown contract.

## 7. Capture pipeline: bodies, headers, sizes, redaction

**Inherited privacy boundary:** captured headers and body payloads remain private to Apitally. Request-serving code collects bounded data; decompression, masking, JSON processing, and redaction execute outside request handling before attributes are attached to an export snapshot. User exporters must never see Apitally-captured payloads.

For in-scope ordinary body capture, apply the canonical content-type allowlist and 50,000-byte limit. Check headers before body I/O. A known oversized body yields `[BODY_TOO_LARGE]` without reading it; crossing the cap discards buffered bytes. Empty bodies and known incomplete captures are omitted, while an already-established oversized sentinel can still be exported for an aborted ordinary stream. Native file sends have the explicit scope exclusion below. Behind `UseRequestDecompression`, a chunked request body compressed with `br` or `deflate` is omitted with its size: those decoders stop at the end of the compressed data without the final zero-byte read that proves a chunked body complete.

Process bodies in the shared order: bounded decompression, mask callback, parse, field redaction, and serialization. Parse to identify JSON regardless of content type once capture is allowed. Unsupported/failed decompression must not export the original bytes. A failed or dropping mask callback yields `[REDACTED]`; an oversized masked result yields `[BODY_TOO_LARGE]`. The oversized sentinel bypasses body processing.

**Confirmed .NET body-mask signature:** `MaskRequestBody` and `MaskResponseBody` both use `Func<SpanSnapshot, byte[], byte[]?>`. The first argument is the complete span snapshot; the second is the decompressed body bytes before JSON parsing. The returned array replaces those bytes, and `null` produces `[REDACTED]`. Ordinary byte arrays keep the two callbacks consistent without another buffer abstraction. The returned array is consumed immediately and not copied; only the serialized result is retained.

The body-mask callback sees the export snapshot after query/header redaction and captured-header attachment, but before body attributes are attached. Document that execution may happen later on another thread. A failure in the export redaction boundary drops the affected span rather than sending raw sensitive data.

Header attributes are list-valued, lowercase, and retain dashes. A masked header exports one `[REDACTED]` value. Redact query strings in request URLs and captured `Location`/`Content-Location` values, including stable/legacy query-bearing attributes on descendant spans and attributes supplied by user instrumentation. Defaults and allowlists come from the shared specification, not a separately maintained .NET variant.

**Confirmed transport direction:** transparently observe ordinary request reads and response writes through ASP.NET Core stream/body features, including `BodyReader` and `BodyWriter`. The request side replaces only `Request.Body`: Kestrel's `IRequestBodyPipeFeature` and the default feature used by IIS and HttpSys create `BodyReader` over the current `Request.Body`, so `BodyReader` consumers such as gRPC read through a stream adapter instead of the native pipe. Preserve streaming and backpressure; read only request bytes the application consumes. Copy written response pipe memory into capture at `Advance`, and commit capture counts only after successful acceptance. `Advance` can precede the response start and its `OnStarting` callbacks, so response eligibility stays undecided until the response starts; bytes are copied provisionally within the size limit while capture could still apply, then kept or dropped under the final headers. Whole-response buffering is not the implementation.

**Confirmed completeness boundary:** finalize ordinary capture at transport completion using the observed bytes, an applicable declared length and a simple incomplete flag. Set that flag for directly observed read/write/advance/flush failures and escaped request errors, and for explicit abort or writer-completion errors through existing wrappers or a lightweight hook. Honor cancellation already visible at completion. A fully consumed request is established by applicable length or observed EOF; retain an ordinary response only when all counted bytes are available and no known incompleteness remains. Handled error status codes do not themselves make their complete response bodies ineligible.

`OnCompleted` is a finalization point, not a success certificate. Use these local checks rather than token-replacement tracking, cancellation-settling delays, separate failure-coordination machinery or attempts to discover every internal server failure. Replacing an application's cancellation token is not itself an omission condition. The boundary is known capture completeness, not verified client receipt: a network failure after all application bytes were observed does not by itself make the capture partial. Unknown-length responses with no observed failure carry no universal transport-success guarantee.

**Confirmed v1 scope deviation:** omit response-body capture when the observer's native `SendFileAsync` path is used, even for an otherwise eligible small text/JSON file. Delegate file delivery unchanged and invalidate the entire capture, including any mixed stream prefix or suffix. Do not reread files or substitute an SDK file-copy path. If the application or compression middleware already writes eligible file content through the ordinary observed stream, capture it incidentally under the normal allowlist, completeness and size rules; add no file/download detection or special handling. This narrows body-capture coverage, not request monitoring, response headers or independent size observations.

Body size observations are independent of content capture. Use trustworthy declared lengths or complete observed byte counts as the shared design allows; do not read a body or inspect a file merely to determine its size. Unknown size remains unknown. The same resolved sizes feed spans and histograms, including when captured bytes have been discarded after crossing the cap.

**POC evidence:** the original bounded stream/feature wrappers and the [transport-completeness follow-up](../pocs/transport-completeness/README.md) observe tested request `Body`/`BodyReader` and response `Body`/`BodyWriter` paths without whole-response buffering. The follow-up passed 3564 assertions per exact .NET/ASP.NET 8.0.13, 9.0.2 and 10.0.9 pair across fresh and pooled connections. Clients receive ordinary and gzip-decoded prefixes while endpoints remain gated, and outer observation includes the gzip trailer. The original native file omission also clears mixed output. The follow-up's second file read can capture different bytes from those served; its single-pass comparison bypasses native dispatch.

Native file payload capture and exhaustive server-internal failure detection are outside the v1 implementation and release criteria.

## 8. Transport observation, routes, frameworks

**Confirmed:** automatically register the transport integration for modern and `Startup`-based hosting. The user should not need a second middleware call or knowledge of OTel ordering.

**POC evidence:** public `IStartupFilter` registration inserts the observer before the application pipeline in both modern and Generic Host/`Startup` hosting. It observes the tested final exception-handler response, unmatched route and streams. `IExceptionHandlerPathFeature.Endpoint` retains the original parameterized route after re-execution selects the error endpoint. Earlier short-circuits, third-party startup filters and Development exception-page placement remain untested.

Prefer stock ASP.NET Core request instrumentation when it satisfies one SERVER activity per request. Apitally-specific capture, metrics, consumer attribution, and errors remain in SDK-owned paths, so reusing user instrumentation does not remove those features.

Route resolution must produce parameterized endpoint templates with applicable path/group prefixes. Preserve the original matched route through exception-handler re-execution where the framework exposes it. Unmatched requests export trace detail without a route and contribute no request histograms or error aggregates. Client address and scheme attribution follow ASP.NET Core's configured forwarding/trust behavior; Apitally does not add a second forwarding-header trust policy.

### Validation and server errors

**Inherited:** automatic framework recognition is the validation API. No public validation-capture method or response-parser callback is added.

**POC evidence:** the [error/integration experiments](../pocs/error-integrations/README.md) observe automatic MVC validation by wrapping the existing `ApiBehaviorOptions.InvalidModelStateResponseFactory`, preserving its behavior. `ProblemDetailsOptions.CustomizeProblemDetails` exposes typed validation objects when the registered problem-details service runs. Registering these options callbacks does not itself install MVC; Minimal-only hosts remain without MVC services.

Known response shapes provide a conservative fallback. MVC's `DefaultProblemDetailsFactory` applies `CustomizeProblemDetails` to every MVC problem response, but Minimal API `ProblemHttpResult` does so only through `IProblemDetailsService`, which only `AddProblemDetails` registers. Without it, `Results.ValidationProblem` writes JSON directly, so the fallback is the only Minimal API capture path on every runtime. `TypedResults.ValidationProblem` bypasses the problem-details service on net8 but uses it on net9/net10. Built-in Minimal API parameter validation appears with `AddValidation` on net10; without problem-details services its tested response is compact 400 JSON containing title/errors. The probe recognizes tested 400/422 defaults, preserves opaque field strings and skips ordinary 400s. Recognizing bytes does not prove their transport capture is bounded or complete.

**Confirmed v1 scope:** capture MVC and Minimal API validation from framework-provided details and known standard response shapes. Preserve available metadata and opaque field/message strings, including localized or customized message text within a supported shape. Do not infer arbitrary custom/localized schemas or guess unavailable binding sources or fields. An unfamiliar response format without framework-provided validation details skips dedicated validation aggregation; ordinary request monitoring and existing error eligibility remain unchanged.

Validation response observation is independent of trace sampling and response-body logging. Parsing requires a complete eligible response and retains at most 50,000 bytes in the response body-capture buffer, which is retained for 400/422 JSON responses even when body capture is off. Retaining bytes for validation never enables exporting those bytes as a captured response body.

Normalize `source`, `field`, `message`, and `type` at the adapter boundary. Preserve useful field strings; do not split and reconstruct dotted model keys. Use empty values when source/field information is genuinely unavailable.

Request-local error state retains the first captured exception, independent of a recording span. Automatic hooks and `IApitally.CaptureException(...)` update the same state and record at most the first SDK exception event on the SERVER span, skipping it if the span already has an `exception` event. Exclude request cancellation and unwrap a single-leaf aggregate where appropriate. Commit error data once at transport completion:

- Validation details contribute their normalized groups for eligible routed requests.
- A captured exception contributes a server error only when final status is exactly 500.
- An intentional 500 without a captured exception contributes no server-error group.
- `OPTIONS`, websockets, and unmatched routes contribute neither category.

**POC evidence:** `IExceptionHandlerFeature.Error` remains available after handled responses even when net10 suppresses handled-exception diagnostics. A non-handling `IExceptionHandler` observer depends on registration order, and an outer catch sees no escaping exception for these handled requests. Feature observation therefore avoids relying solely on either mechanism. Tests preserve the first exception, distinguish request cancellation from an unrelated `OperationCanceledException`, and apply the exact final-500 eligibility predicate.

## 9. Logs

### Application logs

**Inherited:** capture through the standard `ILogger` abstraction into Apitally's private logging pipeline. Preserve the user's logging output, providers, and applicable category thresholds. `CaptureLogs = false` disables application-log capture, not the private pipeline or internal events.

**Confirmed:** an additive `ILoggerProvider` capture adapter, marked `[ProviderAlias("Apitally")]`, that builds Apitally's log records directly. There is no private OTel logger provider. The [logging POC](../pocs/private-logging/README.md) forwarded an external scope provider through the adapter and showed that both registration orders preserve independent user sinks, resources and output; that adapter evidence still applies. Do not call the application's OTel logging registration.

Provider-independent minimum/category rules apply to the adapter, and the alias lets users narrow capture with standard `Logging:Apitally:LogLevel` configuration. Filters targeting the user's OTel provider remain specific to that provider. Third-party logging-factory replacements remain untested.

Resolve `apitally.request.server_span_id` through the activity-to-request association, preserving the emitting child span ID separately. Application logs without a request association are dropped. Exclude Apitally's and the OTel SDK's own diagnostic logs from capture to prevent feedback loops.

**Confirmed:** also exclude `Microsoft.AspNetCore.*` categories from capture, as v0 did. Framework request logs repeat what the request log already shows and would crowd out application logs. Exclude `System.Net.Http.HttpClient.*` and `Yarp.ReverseProxy.Forwarder.*` categories too: outgoing and proxied calls are captured as redacted CLIENT spans, while `HttpClient` logs on .NET 8 and YARP forwarder logs contain unredacted query strings. Other logging providers still receive them.

Run `MaskLogRecord` synchronously on the captured record before buffering. It may return the supplied record or drop it; exceptions, a different instance or a null/empty `Body` drop the record. Truncate the body to 2,048 characters after masking, when the batch worker encodes the record.

**Confirmed .NET deviation from the shared design:** the shared design asks for the ecosystem's mutable log-record type. `ILogger` has no record type, and an OTel `LogRecord` produced for this path would differ from ordinary OTel .NET use: `Body` would hold rendered text rather than the template, `Exception` would always be null, `FormattedMessage` would be ignored and scopes would be flattened. Producing it would also require a second provider lifecycle, pooled-record lifetime rules, a second copy of every record, and the `OpenTelemetryLoggerProvider(IOptionsMonitor<...>)` constructor that pinned 1.19.0 source marks for deprecation in favor of the still-experimental `Sdk.CreateLoggerProviderBuilder`. `MaskLogRecord` therefore receives an Apitally-owned `LogRecordSnapshot`.

**Confirmed callback type:** `LogRecordSnapshot` has read-only `Timestamp`, `CategoryName`, `LogLevel` and `EventId`, and a mutable `string? Body`. Its constructor is internal. Trace context and request linkage are added after masking, so the callback cannot unlink or reassign a record. The accepted instance is buffered without another copy; the SDK does not defend against callbacks that retain and later mutate it.

**Confirmed callback message:** `Body` contains rendered text, and the original message-template attribute `{OriginalFormat}` is omitted. `Body` is the message field for masking and export. A record whose `Body` is null or empty after masking is dropped locally, because the server drops empty-body records at ingest. Do not restore an unmasked rendered message or template after the callback. `Body` is the only exported content, so masking it covers everything the record sends. Other logging providers retain their normal message representations.

**Confirmed structured values and scopes:** structured log values and scopes are not captured, because the server's log ingestion stores only the rendered message, level, logger and code location. Values that appear in the message template are part of the rendered `Body`.

**Confirmed exception representation:** log records carry no exception attributes, because the server's log ingestion does not store them. The logged exception is passed only to the application's formatter. Request-level exception capture, error aggregates and other logging providers are unaffected.

**POC evidence:** the [native-log-masking follow-up](../pocs/native-log-masking/README.md) passes 828 assertions on each of .NET 8.0.13, 9.0.2 and 10.0.9, using SDK 10.0.301 and OTel 1.19.0. Its adapter-side results: both provider orders preserve the independent sink's original state, scopes, formatter output and exception, and body/attribute edits and removals, supplied-record acceptance, null/throw/replacement drops and 12 concurrent scope contexts pass. The tested value set is deliberately finite: selected scalar values, `int[]`, `string[]` and `List<int>`.

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

**Confirmed span value policy:** use the type mapping of the inspected standard OTel .NET export conversions for span attribute values, including event, link and custom request attributes. Normalize when the span snapshot is taken, before callbacks, and detach arrays/maps from application-owned data. Maps convert one level deep, with nested dictionaries using the string fallback; the stock three-level recursion is not adopted. Ordinary lists and opaque objects use the standard string fallback rather than expanding their contents or cloning object properties. Applications can supply arrays explicitly when they want element values captured. Conversion failures omit the value, never passing through raw mutable values as a fallback; the stock converter's internal edge-case behavior is not an Apitally contract. This selects value conversion, not every stock serializer limit; shared Apitally payload requirements still apply.

Log bodies are rendered strings, truncated at encoding to 2,048 characters, including a body replaced by the log-mask callback. The limit counts UTF-16 code units, matching the JavaScript SDK; a split surrogate pair is harmless because Google.Protobuf encodes strings with replacement. Span attributes follow their own spec-defined limits.

**Confirmed duplicate-key rule:** within one attribute or key/value collection, the last occurrence of a key wins.

### Startup event

Emit `apitally.app.startup` through the private logs pipeline with scope `apitally` and no trace/span context. The body is a JSON string containing:

- `framework = aspnetcore`
- Runtime/framework versions and `versions["app"]` when configured.
- Resolved SDK settings, including defaults, with the shared secret/metadata exclusions and callback/pattern representations.
- Registered route templates and method metadata, with optional `summary` and `description` strings from native endpoint metadata.

**Confirmed v1 scope deviation:** omit the startup event's `openapi` field. Full OpenAPI document capture is deferred on all supported runtimes, including .NET 10. Endpoint registration and native summary/description enrichment remain in scope.

**POC evidence:** finalized `EndpointDataSource` entries retain tested nested group prefixes, route constraints and method metadata.

**Additional POC evidence:** the [native endpoint-metadata probe](../pocs/endpoint-metadata/README.md) passed eight path/method cases on .NET 8/9/10 without OpenAPI package references or loaded generator assemblies. `IEndpointSummaryMetadata` and `IEndpointDescriptionMetadata` supply strings during existing route enumeration, including Minimal API group inheritance/endpoint overrides and MVC action annotations. Unannotated endpoints have no values. The two attributes are method-only; controller-class placement failed compilation on all three targets. This is metadata-read evidence, not integrated startup-event export validation. XML-only comments and OpenAPI-only transformer changes do not populate these route metadata interfaces: .NET 10's generated XML-comment transformer writes the OpenAPI operation during document generation.

**Confirmed implementation direction:** read `IEndpointSummaryMetadata.Summary` and `IEndpointDescriptionMetadata.Description` during existing startup route enumeration and copy available values into each corresponding method/path entry. Leave unavailable metadata absent. This uses the ASP.NET Core shared framework on .NET 8/9/10, with no additional generator dependencies or per-request documentation work. Documentation supplied only through XML comments or OpenAPI transformers is outside v1 capture.

Emit once per serving process under the shared contract. Cross-host startup coordination is outside the v1 scope defined in section 2.

### Error aggregates

Use the shared validation/server aggregation identities, truncation rules and positive `UInt32` count range. Sentry event-ID enrichment is deferred from v1 as described in section 14. Limits remain 100 validation and 100 server errors per process between drains. Each error counts occurrences per consumer, with a separate count for requests without a consumer; per-consumer counts are not limited.

Drain atomically, then emit outside the synchronization boundary immediately before the logs pipeline flushes in ordinary and final cycles. Each aggregate has the native event name and a structured OTLP object body, not the startup event's JSON-string body. It carries no request trace context and bypasses application-log masking/truncation.

### Consumer updates

Emit `apitally.consumer.update` events under spec section 9.3. `SetConsumer` accepts optional `name`, `group` and an `IReadOnlyDictionary<string, string?>` attribute patch; string-or-null values avoid coercion rules, and a null value deletes an attribute. Normalize each patch per the spec, keeping the first ten valid attribute entries including deletions, before change detection. Emit only patches that carry metadata, from monitored requests, independently of span recording, trace sampling, exclusion and application-log capture, including unmatched routes but not websockets.

Detect changes with a 10,000-identifier LRU cache of hashes of the canonical normalized patch, not merged consumer state, following the shared design. Update the cache when the event is handed to the log batch processor. Consumer names, groups and attributes are not put on spans.

**Confirmed:** internal events do not pass through the logger adapter. `InternalEvents` builds owned log entries directly, with the event name, scope `apitally`, empty trace/span context and a string or structured body, and submits them to the log batch processor. They bypass application masking and truncation by construction, even with application capture disabled.

**POC evidence:** the encoding POC round-trips native event names, startup string bodies and structured error bodies through official protobuf messages.

## 10. Export pipeline

**Inherited:** stock batching machinery feeds SDK-owned OTLP encoding, a write-through spool, and one export worker per runtime. Delivery is HTTP/protobuf; there is no stock OTLP network exporter and no additional retry policy layered underneath.

### Encoding and batching

**Research finding:** the .NET OTLP exporter's protobuf serializers are internal. The package does not expose a public encode-only API. Its network exporter is not a drop-in spool encoder.

**POC evidence:** generated official OTLP v1.11.0 message classes and `Google.Protobuf` round-trip the tested trace, log and real SDK metric data, including binary bodies and structured internal events. Two unframed requests per signal merge correctly from a single continuous gzip stream, independently checked with Python zlib.

A 32-record trace chunk exceeds the 4,000,000-byte cap in the experiment; exact encoded-size checks and splitting preserve the tested records within it. The POC also rejects an indivisible oversized encoded request.

**Confirmed oversized-record policy:** after ordinary exact-size batch splitting, drop an indivisible encoded telemetry record that still exceeds the 4,000,000-byte spool cap, issue a deduplicated actionable warning and continue with the other records. Do not invent fragments or rewrite the record to force it to fit. The warning explains the lost item and how to reduce its size without logging its contents. This is separate from the existing 50,000-byte body-capture limit and `[BODY_TOO_LARGE]` behavior; ordinary records are not discarded with the oversized item. Request metrics are split by attribute combination, at most 1,000 combinations per request, and each combination's duration and body-size histograms stay in the same request because the server joins them within a request. Process gauges are appended as their own request. The measured worst case is about 1.4 KB uncompressed per combination, so a request of 1,000 combinations stays well below the cap.

Use stock batch queue/worker machinery with explicit settings and approximately one-second intake delay. The snapshot POC demonstrates `BatchExportProcessor<T>` intake without private reflection. Bound encoded appends by actual size; a record-count chunk limit alone is not proof that a file stays below the cap.

**POC evidence:** in the tested stock batch processor, `ForceFlush` can return true after dequeue but before synchronous `Export` finishes. Its export timeout does not cancel a blocked synchronous exporter. Successful `Shutdown` drains and joins; standalone `Dispose` alone does not. Generic intake does not reject calls after shutdown; such records are only buffered, and the SDK accepts that a record racing shutdown may be left unsent rather than adding an admission lock. Plan section 10 specifies the spool closure and terminal shutdown that account for this. Do not treat a successful flush as proof a file is ready to close and send.

### Delivery contract to preserve

| Area | Shared behavior |
| --- | --- |
| Endpoint | `/v1/traces`, `/v1/metrics`, `/v1/logs` at `https://otlp.apitally.io`, subject to the testing override. |
| Authentication | `Authorization: Bearer <write token>` and the resolved `Apitally-Env` on every POST. |
| Encoding | Protobuf (`Content-Type: application/x-protobuf`), stored and sent gzip-compressed (`Content-Encoding: gzip`). The server caps wire payloads at 4 MiB and drops decompressed payloads above 16 MiB. |
| Spool format | One continuous gzip stream per file containing concatenated same-signal protobuf requests. Retries replay identical stored bytes. |
| Rotation | At most 4 MB uncompressed per file, checked before append. Rotate a signal's current file at send time only when no closed files are already waiting. |
| Schedule | First attempt about two seconds after activation; subsequent cycles wait 15 seconds by default, with +/-10% jitter, after the preceding cycle completes. |
| Server adjustment | Read integer `Apitally-Export-Interval` and clamp it to 5-60 seconds. |
| Send bounds | Each ordinary cycle sends, oldest first, the files closed since the previous cycle plus at most ten files from earlier cycles, with 0.1-0.5 seconds between sends; ten-second timeout per POST. The ten-file bound spreads backlog delivery after an outage over several cycles and keeps cycles short, without limiting delivery of current traffic. |
| Retryable failures | Connection errors, timeouts, 408, 429, and 5xx leave the file queued and stop that cycle's send sequence. One immediate retry on connection error covers a stale connection. |
| Permanent rejection | Other 4xx discard the file and warn once per status under the shared warning policy. Trace quota rejection does not stop metrics or eligible error capture. |
| Retention | Expire files 59 minutes after first send attempt. Never-attempted files have no age expiry. |
| Storage bounds | 50 MB disk or 10 MB memory, measured as compressed bytes. Evict oldest closed non-metrics files first, then metrics if necessary to enforce the bound. |
| File permissions | Create spool files owner-only (`0600`) on non-Windows, as the Python and JavaScript SDKs do; they contain masked but potentially sensitive payloads. |
| Filesystem fallback | Probe at spool construction; a failed probe selects memory with one warning. Later write failure discards the current affected file with deduplicated warning; it does not switch storage mode. |
| Orphan cleanup | Recognizable spool files untouched for two hours, checked once at construction. Active runtimes refresh file modification times each cycle. |
| Final cycle | Drain error groups, flush batch processors, collect metrics, close all current files, and attempt delivery without inter-send pauses or the backlog bound. Normal failure rules still apply. |

Send through one private `HttpClient` over a `SocketsHttpHandler`, not `IHttpClientFactory`, so application-wide client defaults such as resilience handlers cannot add retries beneath the worker. Capture `HttpClient.DefaultProxy` once and assign it to the handler; .NET already implements `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`, plus the system proxy on Windows. Run collection, flushing, and export POSTs under OTel instrumentation suppression. This is especially important with default `HttpClient` instrumentation and with a user's own exporter observing application activities.

**POC evidence:** four physical loopback POSTs replay identical persisted gzip bytes and preserve the required synthetic headers. Stock HTTP instrumentation exports the unsuppressed requests and none inside `SuppressInstrumentationScope`. Explicit proxy-object binding survives a later environment change, but physical proxying and full proxy-variable semantics are untested.

**Confirmed:** a host-owned worker drives metric collection and spool delivery. Use .NET background execution appropriate to the work: HTTP sends may be asynchronous, while CPU-bound body processing must remain outside request-serving execution. Keep concurrency close to the shared model rather than introducing a task or thread per request/span.

## 11. Metrics

**Inherited:** record in the transport integration, independently of activities and trace sampling. Use a private meter/provider and the scope name `apitally`.

**Confirmed .NET adaptation:** Apitally aggregates the request histograms itself and does not use the OTel metrics SDK. The exported OTLP data is the same.

| Instrument | Aggregation | Unit |
| --- | --- | --- |
| `http.server.request.duration` | Delta exponential histogram | `s` |
| `http.server.request.body.size` | Delta exponential histogram | `By` |
| `http.server.response.body.size` | Delta exponential histogram | `By` |

Duration is the count anchor. Its attribute tuple is shared with size observations: request method, parameterized route, final status, and optional consumer identifier. Add `url.scheme` and the shared 5xx `error.type` convention. Skip `OPTIONS`, websockets, and unmatched routes; retain eligible excluded/sampled-out requests. Duration and response sizes reflect transport completion, not merely endpoint return.

**Confirmed:** each attribute combination holds three delta exponential histograms at a fixed scale of 3, with bucket counts covering only the recorded index range. Bucket indexes use the OTel .NET mapping, so boundaries agree with other SDKs. On 307 test series, including exact powers of two, bucket boundaries and wide ranges, the output matched OTel .NET 1.19.0 exactly; where OTel reduced the scale to fit 160 buckets, the output matched after merging buckets to that scale. Realistic durations and sizes span at most about 320 buckets at scale 3, so the scale is never reduced.

Record a request's duration and body-size measurements under one lock. Collection swaps in an empty set of combinations under the same lock and encodes the previous set outside it, so each collection contains exactly the combinations recorded since the previous one, and recording is blocked only for the swap. Per request, recording takes about 40 ns, compared with about 250 ns through the OTel metrics SDK.

Observe normalized process CPU utilization, RSS-equivalent bytes, and uptime using direct .NET process/runtime APIs. CPU and memory need paired observation times within the server's one-second tolerance. Uptime keeps collections nonempty even without traffic or with CPU/memory disabled.

**Research finding:** the OTel .NET metrics SDK keeps an attribute combination's slot until a collection finds it idle, so slots cover the combinations of two collection intervals, and it reserves about 12.5 KB per combination for 160 buckets. A soak test with many short-lived consumers needed about 13,300 slots at a 60-second interval, more than a fixed capacity of 10,000.

**Confirmed capacity policy:** accept at most 50,000 distinct attribute combinations per collection interval, with no user-facing capacity setting. Nothing is allocated upfront, and each combination uses about 0.7 KB, so the limit bounds this storage at about 35 MB; typical applications use a few MB at most. The limit guards against misuse such as a request ID used as the consumer identifier, which would otherwise grow memory and metrics exports with traffic. New combinations beyond the limit are dropped and a deduplicated warning explains that some request metrics are missing, with capacity documentation and support guidance. Accepted combinations keep all their dimensions. This remains a finite bound, not a promise of lossless metrics under arbitrary cardinality.

## 12. Error handling and logging posture

**Inherited:** operational SDK failures must not break the application. This includes setup/activation, body observation, capture, processing, and export. Preserve application exceptions and stream behavior while containing SDK failures. Privacy failures never authorize exporting unredacted data.

Use SDK-namespaced .NET logging for diagnostics. Operational initialization failures are errors; actionable data loss is a deduplicated warning with a consequence and remedy; normal adaptation and best-effort enrichment failures are debug-level. Never interpolate a full write token.

A missing token disables telemetry rather than failing options validation during host startup. Documented API misuse may fail synchronously with an actionable message.

**Confirmed:** diagnostics go through the application's logging infrastructure while the Apitally capture adapter excludes them. Verify export failures cannot re-enter the telemetry pipeline through `ILogger` or HTTP instrumentation.

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

The provider's instrumentation and source subscriptions must be configured before it is built, as described in section 2.

### Span-based callbacks

**Confirmed:** `SampleOnRequest`, `SampleOnResponse`, `MaskRequestBody` and `MaskResponseBody` use one complete span snapshot type, `SpanSnapshot`. The body callbacks additionally receive the body to mask. The snapshot's shape is consistent across callbacks while its available data follows the stages described in section 6. Both sampling callbacks return `double?`, with probabilities and stage-specific abstention as described there. Both body callbacks use `Func<SpanSnapshot, byte[], byte[]?>`, with the snapshot first and decompressed body bytes second; a returned array replaces the body and `null` produces `[REDACTED]`. Attribute values use the exporter-aligned normalization in section 9. Events, links and resource use native `ActivityEvent`, `ActivityLink` and OTel `Resource` types; plan section 4 lists the exact members. Log masking uses the separate `LogRecordSnapshot` type because it processes a different signal.

### Log masking

**Confirmed direction:** `MaskLogRecord` is a `Func<LogRecordSnapshot, LogRecordSnapshot?>` invoked synchronously; it may return the supplied record or drop it. `LogRecordSnapshot` exposes read-only `Timestamp`, `CategoryName`, `LogLevel` and `EventId`, and a mutable `Body`. The callback receives rendered message text in `Body` with the original message template omitted. Message content is exported from the accepted `Body` only. Trace context and request linkage are SDK-owned and added after masking.

### Request helpers

**Confirmed primary surface:** inject `IApitally` rather than require a static SDK singleton or public `HttpContext` extension methods.

| Operation | Required behavior |
| --- | --- |
| `SetConsumer(identifier, name, group, attributes)` | Retain normalized identity in request state and set the SERVER attributes when available. Metrics retain the consumer even without recorded trace detail. Name, group and attributes produce consumer-update events as described in section 9. |
| `SetRequestAttribute(...)` | Target the request's SERVER span, including from inside a child activity; expose the value to response sampling. |
| `CaptureException(...)` | Retain the first eligible exception in request-local state and add its SERVER exception event when possible. |
| `StartActivity(...)` | Create an INTERNAL child activity under scope `apitally.otel`, usable with `using` across synchronous or asynchronous code. |

**Confirmed:** `StartActivity` returns `Activity?`, matching native .NET behavior when instrumentation is inactive. Outside a monitored request, the default setup returns an unrecorded propagation-only activity (section 2); tags set on it are discarded. Other request helpers are safe no-ops outside an active request or when disabled. The service is a singleton that resolves the current request through `IHttpContextAccessor` and never retains an `HttpContext`.

**Confirmed adaptation:** native activity scopes are the manual-tracing surface. The shared function-wrapper recommendation is satisfied differently for C#: additional `Trace`/`TraceAsync` delegate wrappers are not part of the initial API direction. Users can use native activity tags; a second SDK span abstraction is unnecessary.

**Confirmed:** in the default setup, application `ActivitySource` instances and natively instrumented libraries need no configuration (section 2). Instrumentation packages, such as EF Core or SqlClient, are explicit opt-ins through standard OTel provider registration. Use `AddOpenTelemetry().WithTracing(...)` with the relevant instrumentation extensions and `AddSource(...)`. This selects application-owned tracing rather than customizing Apitally's private default provider, and only the application's registered sources are then captured. Document complete examples with deliberate sampler and instrumentation choices; keep the default experience to `AddApitally()`.

### Migration contract

Document the write-token replacement, enabled-by-default logging, capture option names/defaults, consumer helper, sampling callbacks' keep semantics, and the new setup path. Preserve familiar .NET concepts, not obsolete Hub payload types or configuration behavior that conflicts with the shared defaults.

## 14. Sentry integration

**Confirmed scope deviation:** Sentry integration is deferred beyond .NET SDK v1. Ordinary exception capture and error aggregation remain in scope independently of Sentry. Sentry-specific dependencies, companion packaging, activation hooks and event-ID correlation belong to future work rather than the v1 implementation or release criteria.

The shared target remains a reference for future integration: automatically detect a usable Sentry integration without an Apitally enable flag and attach its exception event ID to the SERVER export snapshot and eligible undrained server-error aggregate. This includes events processed after activity end; already-exported telemetry is not updated retroactively. Preserve the latest-nonempty enrichment rule if the integration is revisited.

The [error-integrations POC](../pocs/error-integrations/README.md) retains the Sentry event-processor findings for future work.

## 15. Cross-language posture and explicit adaptations

| Shared design area | .NET treatment | Status |
| --- | --- | --- |
| Process-global configuration/runtime | Host-owned DI runtime with independently owned shutdown. | Confirmed adaptation. |
| Code options and environment fallbacks | Add the standard `Apitally` configuration section as setup options below explicit code values. | Confirmed adaptation. |
| Unified setup | One `IServiceCollection.AddApitally` registration for both supported hosting styles; automatic transport integration. | Confirmed API direction. |
| Provider activation/attachment | Standard DI provider registration; externally built providers use existing-instance registration and retain original ownership. | Confirmed API path. |
| Tracing customization | The private default provider captures all sources within requests; standard OTel provider registration selects application-owned tracing for instrumentation packages; configure-only hooks do not customize the private default provider. | Confirmed boundary. |
| Multi-host integration | Single-host support baseline; additional hosts are not prohibited. Preserve the documented cross-provider sampling limitation. | Confirmed support boundary; special cross-host coordination is outside v1 implementation and release requirements. |
| Manual block and function forms | Native `Activity` scope via `IApitally.StartActivity`. | Confirmed adaptation of the shared SHOULD. |
| Activation trigger and failure scope | Activate in the startup filter after the application pipeline is built, before the server starts; one attempt per host runtime, with no first-request fallback. | Confirmed consequence of host ownership and the shared pre-request signal. |
| Test-host suppression | Recognize the resolved server's exact TestServer type/assembly without a test-framework dependency; real Kestrel tests use explicit disabling. | Confirmed scope. |
| Options layout | Flat `ApitallyOptions` properties, matching keys directly under the `Apitally` configuration section. | Confirmed .NET API layout. |
| Configuration timing and repeated calls | Standard Options pattern: a base configure step, then `AddApitally` callbacks as `PostConfigure` in registration order. Read once at startup preparation, register components once and freeze before activation. | Confirmed behavior. |
| Process identity, startup frequency, limits, process gauges | Preserve shared process-wide contracts and one process identity across all signals under host-owned state. | Inherited requirements for the single-host baseline; no global host coordinator. |
| Ordinary final drain | Share the host's remaining shutdown budget and honor host cancellation, without an additional SDK flush window. | Confirmed budget policy. |
| Unfinished-request detail | Discard requests still awaiting transport completion or SERVER activity end at the final SDK cutoff; retain normal flushing for finalized requests and independent recorded metrics/error aggregates. | Confirmed per-SDK policy permitted by the shared shutdown contract. |
| Late request telemetry | Drop spans and logs arriving after release; no completed-request cache. | Confirmed .NET choice, permitted by shared spec section 6.5. |
| SDK span/log representations | Owned export snapshots and generic stock batch processors. | Confirmed .NET mechanism. |
| Span callback type | One complete span snapshot type for all sampling and body-masking callbacks, using native event, link and resource types. It is the SDK-owned record, with read-only interfaces but no isolation guarantee. | Confirmed .NET adaptation. |
| Sampling result type | `double?` represents the keep probability or abstention for both callbacks; boolean choices use zero or one. | Confirmed typed C# adaptation; shared sampling semantics preserved. |
| Custom pattern inputs | `List<string>` of .NET regex patterns for both code options and configuration files, case-insensitive by default with explicit inline options respected. | Confirmed input type and matching convention; user patterns extend built-in defaults. |
| Body completeness | Finalize ordinary bounded capture with directly observed failure, visible cancellation and applicable length checks; omit known incomplete bytes without certifying transport success. | Confirmed scope and mechanism boundary. |
| Native file response bodies | Delegate native file sends and omit their entire body capture, including mixed output; retain incidental eligible capture through ordinary observed streams. | Confirmed v1 scope deviation; no SDK file rereading or replacement copy path. |
| Validation recognition | Framework-provided details and known standard response shapes; opaque field/message strings and available metadata, with unknown source/field empty. | Confirmed v1 boundary; arbitrary schema and binding-source inference are outside scope. |
| Log callback type | Apitally-owned mutable `LogRecordSnapshot` instead of the ecosystem log-record type, built by the logger adapter and masked synchronously; no private OTel logger provider. `ILogger` has no record type, and an OTel `LogRecord` would carry misleading semantics on this path. | Confirmed .NET deviation from the shared ecosystem log-record rule. |
| Callback attribute values | Standard .NET OTLP value type mapping before span callbacks. | Confirmed .NET adaptation. |
| Log exception representation | No exception attributes on log records; the server does not store them. | Confirmed; exceptions are captured on the SERVER span and in error aggregates. |
| Log message representation | Rendered text in `Body`, with the original message template omitted from callback input. | Confirmed .NET adaptation. |
| Structured log values and scopes | Not captured; the server does not store them. | Confirmed; values in the message template remain part of `Body`. |
| Encoding | Official OTLP schemas/protobuf encoding with SDK-owned mapping. | Confirmed .NET mechanism; no change to HTTP/protobuf delivery. |
| Metric capacity | Fixed capacity of 50,000 attribute combinations per collection interval in Apitally's own request-metric aggregation, with visible overflow degradation. | Confirmed. |
| Runtime-specific fork and signal mechanics | Use .NET host lifecycle instead. | Platform adaptation. |
| Sentry event-ID correlation | Defer the integration beyond v1 while retaining ordinary exception/error capture. | Confirmed v1 scope deviation. |
| Startup endpoint documentation | Populate native summaries/descriptions in `paths`; omit full OpenAPI JSON on all runtimes, including .NET 10. | Confirmed v1 scope deviation. |

The wire attributes, scope names, default redaction/exclusion rules, sampling convention, intact-or-omit body/privacy rules, error identities, and transport behavior remain shared requirements subject to the explicit adaptations above. The native file-send exclusion narrows capture coverage; it does not permit exporting a partial captured prefix. A .NET mechanism does not override other shared requirements by implication.

Native AOT support is a product scope decision, not a shared-contract deviation. Revisit demand before expanding the supported deployment matrix.

## 16. Code style and testing

Write small, idiomatic C# components following the shared naming and testing rules. Public entry points precede supporting helpers. Prefer .NET lifecycle and concurrency primitives over porting Python/JavaScript mechanics. Extract helpers only when they materially improve clarity.

### Test structure

**Confirmed:** retain xUnit as the test framework, with focused shared-module tests and small real ASP.NET Core applications. Cover controller and Minimal API behavior, modern hosting, and Generic Host with `Startup` without multiplying identical business scenarios across every configuration.

Use in-memory OTel-side observation for SDK behavior, and a local HTTP endpoint when testing physical OTLP delivery. Permanent tests assert Apitally behavior, not upstream internals. POCs may investigate dependency behavior to choose the design; that does not require turning every probe into a permanent regression test.

Do not replace Apitally classes with mocks. Assert exact exported counts and attributes. Read responses to completion before asserting completed telemetry. Use real Kestrel for telemetry tests; TestServer tests verify automatic suppression. Keep test state cleanup in shared fixtures; host ownership does not make process-wide activity listeners and environment variables disappear.

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

## 18. POCs

The completed experiments are listed in the [POC index](../pocs/README.md). They are research code outside the solution and package. The [request-association](../pocs/request-association/README.md) and [transport-completeness](../pocs/transport-completeness/README.md) POCs contain the mechanisms plan sections 5 and 7 adapt.

## 19. Remaining decisions

None. Remaining work is implementation and validation, sequenced in plan section 11.

## Research references

Local source snapshots used for the initial review. Decisions were last checked against cloud commit `f98007ae`.

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
- [OTel hosting registration and one provider per service collection](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Extensions.Hosting/README.md#L24-L47)
- [OTel guidance on separately constructed providers and the usual single-provider lifetime](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/docs/trace/customizing-the-sdk/README.md#L45-L59)
- [.NET DI ownership of externally created singleton instances](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines#services-not-created-by-the-service-container)
- [OTel 1.19.0 metric slot reclamation](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Metrics/AggregatorStore.cs)
- [Python callback declarations](../../apitally-py/apitally/__init__.py) and [standard ReadableSpan copy construction](../../apitally-py/apitally/shared/span_processor.py)
- [JavaScript callback declarations](../../apitally-js/src/config.ts) and [structural ReadableSpan copies](../../apitally-js/src/spanProcessor.ts)
- [OTel private logger's synchronous processing and record recycling](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L44-L106)
- [.NET options configuration, post-configuration and deferred evaluation](https://learn.microsoft.com/en-us/dotnet/core/extensions/options)
- [Native route summary/description conventions](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Routing/src/Builder/OpenApiRouteHandlerBuilderExtensions.cs)
- [.NET 10 XML comments enrich OpenAPI operations during transformation](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/OpenApi/gen/XmlCommentGenerator.Emitter.cs#L361-L386)
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
