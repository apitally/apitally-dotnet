# Implementation plan review, round 2

Date: 2026-09-28. Status: Resolved. All findings are decided and folded into the implementation plan and the design. Production code is unchanged.

## Assessment

The plan is still correct and feasible. Round 1 fixed the lifecycle ownership gaps, and this round found no new blocking correctness defect in the request, spool or shutdown model.

This round looks at a different risk: concepts carried over from the Python and JavaScript SDKs where .NET already has a standard mechanism. It found four places where the plan builds its own machinery next to a .NET or OTel feature that already does the job:

1. Configuration resolution and freezing duplicate the .NET Options pattern (F1).
2. Registration on `WebApplicationBuilder` and `IHostBuilder` is not how .NET libraries register themselves (F2).
3. Application log masking keeps a whole private OTel logger pipeline just to create `LogRecord` instances. Internal events then need a body-carrier workaround to get back out of it (F3, F4).
4. Activation copies the shared "startup completion plus first-request fallback" state machine. ASP.NET Core already gives a signal that fires before the first request can arrive (F5).

It also found two small delivery-client idioms (F6). Everything else is a small idiom improvement or a candidate that did not hold up.

If F1-F5 are adopted, the plan loses a custom resolver, a lifecycle state machine, the private OTel logger provider with its options shim and record-pool handling, and the internal-event carrier. Nothing new is added in their place. Adopting these would reduce the stage 1, 2 and 5 work.

## Baseline and method

| Source | Reviewed revision |
| --- | --- |
| .NET repository | `364764f0ad3e1a064d7b43a205ee9d33926bf53c`, branch `v1` |
| Design | `docs/design.md`, SHA-256 `8884c1701d926c8532dc9f7559bb7acff902c9ce3dea54f1922a2a54cd75b037` |
| Implementation plan | `docs/implementation-plan.md`, SHA-256 `4f602adb2a37fba37265245da81f5320ba63aa8e27b4b68c54acfd6c067162c9` |
| Shared specification/design | Cloud `6e480ace5da22fae426baddffaa84435ce007e46` |
| OpenTelemetry .NET | 1.19.0, source commit `dac1573ece52e8c275c3db5282bc57e3d5eff5cf` |
| .NET runtime / ASP.NET Core | `release/8.0`, `release/9.0` and `release/10.0` branch heads at review time (not pinned commits) |

The parent read both documents in full, together with the round-1 review and the design review. The v0 setup code and the POC sources were also inspected. Every retained claim about dependency behavior was checked against upstream source. No builds, tests or experiments were run. Anything that needs a probe is marked that way. Confirmed design decisions are treated as decisions: where a finding questions one, it is presented as a decision for you, not as a defect.

## Findings

| ID | Priority | Finding | Kind | Needs decision |
| --- | --- | --- | --- | --- |
| F1 | High | A custom configuration resolver duplicates the Options pattern | Idiom, simplification | No, if you accept that ordinary `Configure<ApitallyOptions>` works |
| F2 | High | Setup should be registered on `IServiceCollection` | Idiom, simplification | Yes, the public entry-point shape |
| F3 | High | The private OTel logger pipeline exists only to produce `LogRecord` | Simplification | Yes, it reopens a confirmed callback type |
| F4 | Medium | Internal events should skip the OTel logger provider | Simplification | No |
| F5 | Medium | Activation can happen when the request pipeline is built, removing the first-request gate | Simplification | Yes, the activation trigger |
| F6 | Low | Construct the delivery client explicitly and use the built-in default proxy (metrics concern withdrawn) | Idiom | No |
| F7 | Medium | The public span snapshot should reuse native `System.Diagnostics` and OTel types | Idiom, public API | No |
| F8 | Low | Smaller idiom improvements | Idiom | No |

### F1. Use the Options pattern for configuration resolution and freezing

**Plan:** [resolution](implementation-plan.md#resolution), lines 206-213; `Hosting/RuntimeConfiguration.cs`. **Design:** section 3, line 187.

The plan stores setup callbacks itself, runs them once in its own resolver, and states that an ordinary `Configure<ApitallyOptions>` registration will not affect the result (line 211). This is Python's `apitally.configure(...)` model rebuilt by hand. The standard .NET Options pattern already provides every required property:

| Requirement | Options pattern behavior |
| --- | --- |
| Deferred until startup configuration is resolved | The options factory runs on the first `IOptions<T>.Value` access, not at registration. |
| Code callbacks composed in registration order, each seeing earlier changes | `Configure` actions run in registration order on the same instance. `PostConfigure` actions run after all of them. |
| Code values above the `Apitally` section, which is above environment fallbacks | One `IConfigureOptions<ApitallyOptions>` registered once: apply `OTEL_*` fallbacks, then `APITALLY_*` fallbacks, then bind the section. The `AddApitally` callback is registered as `PostConfigure`. This is what v0 already does ([ApitallyExtensions.cs](../src/Apitally/ApitallyExtensions.cs#L22-L36)). |
| Frozen, no reload | `IOptions<T>` is a singleton, computed once and never reloaded. Only `IOptionsMonitor<T>` reloads, and Apitally does not use it. |
| Invalid token disables rather than failing startup | Leave out `ValidateOnStart` and `IValidateOptions`. Keep the plain validation step that copies into `RuntimeConfiguration`. |

**Recommendation:** register the base configurator with `TryAddEnumerable`, register each `AddApitally` callback with `PostConfigure`, and read `IOptions<ApitallyOptions>.Value` once during preparation. Then apply the additive disable variables, validate, compile patterns and copy into the immutable `RuntimeConfiguration`, as the plan already describes. Delete plan steps 1 and 4 and the callback store. Document that `services.Configure<ApitallyOptions>(...)` works: it runs after the section and before `AddApitally` callbacks. .NET developers expect that, and the plan currently has to warn them that it does not work.

One side effect comes free: host builders already add environment variables without a prefix, so `Apitally__SampleRate` and similar keys populate the section with no Apitally code. The only custom code left is the short list of shared `APITALLY_*` and `OTEL_*` fallback names.

**Acceptance:** the existing precedence, repeated-registration and mutation-isolation tests from stage 1, plus one test showing that `services.Configure<ApitallyOptions>` sits between the section and the `AddApitally` callbacks.

**Decision (2026-09-28):** adopt the Options pattern as recommended, documenting direct `services.Configure<ApitallyOptions>` with standard .NET ordering. Folded into [plan section 4](implementation-plan.md#resolution).

### F2. Register on `IServiceCollection`, not on two host builder types

**Plan:** [proposed API](implementation-plan.md#proposed-api), line 185. **Design:** confirmed "one builder-level call", line 30.

The plan adds `AddApitally` to both `WebApplicationBuilder` and `IHostBuilder`. Nothing in the registration needs a host builder:

- Configuration comes through `IConfiguration` from DI (F1).
- The logger provider is a `TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ApitallyLoggerProvider>())`.
- The startup filter, hosted service, tracing contribution (`ConfigureOpenTelemetryTracerProvider`) and `IApitally` are all service registrations.

The established .NET convention is an `IServiceCollection` extension: OTel's `AddOpenTelemetry()`, Application Insights' `AddApplicationInsightsTelemetry()`, and v0's own `AddApitally`. It covers `WebApplicationBuilder`, `HostApplicationBuilder`, `CreateSlimBuilder` and `Startup.ConfigureServices` with one overload. An extension method on `IHostBuilder` is unusual for a library. With it, `Startup`-based applications would register Apitally somewhere other than where they register every other service.

**Recommendation:** make `IServiceCollection AddApitally(this IServiceCollection services, Action<ApitallyOptions>? configure = null)` the only entry point, documented as `builder.Services.AddApitally()`. For v0 users, migration becomes "delete `app.UseApitally()`". Use `TryAdd*`/`TryAddEnumerable` for the once-only registration the design requires, rather than tracking it separately. If you specifically want `builder.AddApitally()`, add a thin `IHostApplicationBuilder` overload that forwards to it. `WebApplicationBuilder` implements that interface, so no `WebApplicationBuilder` or `IHostBuilder` overloads are needed. The design's "one builder-level call" wording would become "one registration call".

**Decision (2026-09-28):** `IServiceCollection.AddApitally(Action<ApitallyOptions>? configure = null)` is the only entry point; no host-builder overloads. Folded into [plan section 4](implementation-plan.md#proposed-api).

### F3. The private OTel logger pipeline exists only to produce `LogRecord`

**Plan:** [logs](implementation-plan.md#8-logs-and-internal-events), lines 302-311. **Design:** section 9, lines 384 and 396, and the confirmed log callback rows.

The plan's `ApitallyLoggerProvider` already does all the real work: category filtering, rendering the message once, copying structured state, flattening scopes and adding `exception.*` attributes (plan step 2-3). It then forwards the result into a privately constructed `OpenTelemetryLoggerProvider`. That provider's only contribution is a pooled `LogRecord` instance with timestamp, category, level, event ID and trace context filled in. A synchronous processor then:

- overwrites `Body`,
- clears `FormattedMessage` and `Exception`,
- calls the mask,
- copies the result again into `LogSnapshot` before the pool reuses the record.

The cost of that detour:

- A second provider lifecycle and an `IOptionsMonitor<OpenTelemetryLoggerOptions>` shim (the POCs' `FixedOptions` class).
- Pool-reuse hazards that need their own tests.
- Two copies of every record.
- Rules for ignoring callback-assigned `FormattedMessage` and `Exception`.
- The internal-event body carrier (F4).
- A dependency on a constructor that upstream plans to deprecate. In pinned 1.19.0 it carries `// todo: [Obsolete("Use the Sdk.CreateLoggerProviderBuilder method instead this ctor will be removed in a future version.")]` ([source](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLoggerProvider.cs#L36-L37)), and the replacement `Sdk.CreateLoggerProviderBuilder` is still `[Experimental]` in 1.19.0.

The familiarity benefit is also weaker than it looks. The callback receives a `LogRecord` whose semantics differ from normal OTel .NET: `Body` is rendered text rather than the template, `Exception` is always null, `FormattedMessage` is ignored, and scopes are flattened. Someone who knows OTel .NET would be misled by the familiar type. In Python and JavaScript the logging bridge naturally produces the OTel record. In .NET, `ILogger` has no record type, and Apitally already builds the record itself.

**Recommendation (decision required):** replace the callback input with a small public mutable class owned by Apitally, for example `LogRecordSnapshot`: `Timestamp`, `CategoryName`, `LogLevel`, `EventId`, `string? Body`, and `IList<KeyValuePair<string, object?>> Attributes`. Keep the same supplied-record-or-drop contract. The adapter builds it directly, the mask runs synchronously, and the accepted instance is itself the owned record, so there is one copy and no pool. This deletes the private OTel logger provider, the options shim, the pooling rules, the `FormattedMessage`/`Exception` normalization rules and F4's carrier. It also matches the snapshot approach the design already chose for spans.

This departs from the shared design's "use the ecosystem's mutable log-record type" (shared design section 9, line 240). Record it as an explicit .NET adaptation. The existing POC evidence for isolation, scope precedence and exception attributes still applies, because those steps happen in the adapter.

**Decision (2026-09-28):** adopt an Apitally-owned `LogRecordSnapshot`; no private OTel logger provider. Folded into [plan section 8](implementation-plan.md#8-logs-and-internal-events). Two refinements while folding: accepted output is still normalized into the owned `LogSnapshot`, because a callback can retain the record and add arbitrary values, so the saving is one copy rather than all copies; and trace context and request linkage are added after masking, which removes the rule for dropping records whose linkage the callback damaged.

**Follow-up decision (2026-09-28):** the same no-guarantee rule applies to logs. Values are still normalized before the mask, so the mask sees exactly what is exported, but the accepted `LogRecordSnapshot` is buffered without a second copy. Conversion of callback-added values and string truncation move to encoding in the batch worker.

### F4. Internal events should skip the OTel logger provider

**Plan:** line 313. **Design:** line 473.

This holds whichever way F3 is decided. Internal events are made by the SDK, bypass masking and truncation, carry no trace context, and need an object body that `LogRecord.Body` (string only) cannot hold. So the plan sends them through the private OTel logger with a hidden carrier attribute, then extracts and removes that attribute in the mapper. The pipeline behind the batch processor already accepts owned `LogSnapshot` entries. `InternalEvents` should build `LogSnapshot` instances directly, with the event name, scope `apitally`, empty context and a string or structured body, and submit them to the log batch processor. That removes the carrier and its extraction step, and the bypass rules follow from how the code is built.

**Decision (2026-09-28):** follows from F3, since no private OTel logger provider remains. Folded into [plan section 8](implementation-plan.md#8-logs-and-internal-events).

### F5. Activate when the request pipeline is built

**Plan:** [preparation and activation](implementation-plan.md#preparation-and-activation), line 227. **Design:** section 4, lines 215-221 (marked Proposed).

The plan brings over the shared trigger: startup completion (`ApplicationStarted`), plus a first-request fallback, a six-state lifecycle, and request handling that waits for concurrent activation. The fallback exists because Generic Host can serve before `ApplicationStarted`, which the POC confirmed.

ASP.NET Core has a signal that always comes first. `GenericWebHostService.StartAsync` runs every `IStartupFilter` to build the request pipeline and then calls `Server.StartAsync` ([source](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Hosting/Hosting/src/GenericHost/GenericWebHostService.cs#L122-L161)). The plan already resolves configuration, the actual `IServer` and the tracer provider in that startup filter. Tools such as `dotnet ef`, and hosts that are built but never run, never reach this point, and TestServer is already detected there. The shared design allows this: "frameworks with a pre-request signal use that" (shared design line 106).

**Recommendation (decision required):** activate in the startup filter, right after preparation. Workers start there and the startup event is queued, so no request can arrive first. This deletes the first-request fallback, the concurrent waiter and the per-request activation check. The lifecycle becomes "active, stopping, stopped" plus disabled/failed. It also lets middleware construction assume an active runtime.

The trade-off: if Kestrel then fails to bind, a startup event may be delivered during that failed host's final cycle for a process that never served. That affects only a crash-on-start process, and nothing in the spec depends on it.

If you keep the current trigger, implement the once-only, concurrent-wait and permanent-failure behavior with `Lazy<Task>` (`LazyThreadSafetyMode.ExecutionAndPublication`, which caches the faulted task) rather than an explicit state machine.

**Decision (2026-09-28):** activate in the startup filter immediately after `next(app)` returns, with the simplified lifecycle and no first-request gate. `TelemetryRuntime` is disposable through DI so a failed server bind still cleans up workers. Folded into [plan section 4](implementation-plan.md#preparation-and-activation).

### F6. Construct the delivery client explicitly and use the built-in default proxy

**Plan:** lines 370-372. **Design:** line 513.

Two idiom points about how `ExportHttpClient` is built:

- **Client factory.** If delivery went through `IHttpClientFactory`, as v0 does, .NET 8 `ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler())` would add retries underneath the worker. That breaks the shared rule of at most one immediate retry and can multiply upload traffic during an outage.
- **Proxy.** `HttpClient.DefaultProxy` is initialized once and already implements `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` on Unix, and environment-then-system proxy settings on Windows ([source](https://github.com/dotnet/runtime/blob/release/8.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/SystemProxyInfo.Windows.cs)). Parsing those variables in Apitally code duplicates it.

**Recommendation:** build one private `HttpClient` over a `SocketsHttpHandler`, not through `IHttpClientFactory`. Capture `HttpClient.DefaultProxy` once at configuration and assign it to the handler. Test proxy binding with an injected loopback proxy rather than testing .NET's environment parsing.

**Withdrawn from the original F6:** the claim that export POSTs appearing in the application's `System.Net.Http` metrics is a defect, along with the proposed private `IMeterFactory` probe and `ActivityHeadersPropagator = null`. The metrics are accurate: the process does make those calls. The stock OTLP exporter behaves the same way; in pinned 1.19.0 its default client is a plain `new HttpClient` ([source](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/OtlpExporterOptions.cs#L107-L129)). Suppression exists to prevent tracing and self-logging feedback loops, which the plan already handles; a metric measurement per POST creates no loop. Hiding the calls would also hide slow or failing uploads from the user's own tooling.

**Decision (2026-09-28):** adopt the two remaining points. Folded into [plan section 10](implementation-plan.md#10-batching-spool-and-delivery) and resolution step 7.

### F7. Build `SpanSnapshot` from native types

**Plan:** lines 175 and 254-258.

The plan adds public `SpanEventSnapshot`, `SpanLinkSnapshot` and `InstrumentationScopeSnapshot` records, and exposes array attributes as read-only collections and bytes as `ReadOnlyMemory<byte>`. .NET already has immutable public types for these shapes:

| Snapshot member | Native type |
| --- | --- |
| Identity and parent | `ActivityContext`, `ActivitySpanId` (the plan already chooses the native ID types) |
| Events | `ActivityEvent` (readonly struct: name, timestamp, tags) |
| Links | `ActivityLink` (readonly struct: context, tags) |
| Kind and status | `ActivityKind`, `ActivityStatusCode` |
| Resource | `OpenTelemetry.Resources.Resource` (public, immutable) |
| Scope | Two properties, `ScopeName` and `ScopeVersion`. No wrapper type. |

Also give attribute values the CLR types that OTel .NET users see on `Activity` tags: `string[]`, `long[]`, `double[]`, `bool[]` and `byte[]`, copied for each invocation. Wrapping in read-only views costs the same copy anyway. With native types, users can pattern-match as they would on activities, and the snapshot stays independent because each call gets its own copy. Keep the top-level `Attributes` as `IReadOnlyDictionary<string, object?>`.

This shrinks the public surface to one type and avoids an API break later. It is the one remaining public API choice, so decide it before stage 1 is merged.

**Decision (2026-09-28):** use the native types, and drop the read-only and per-callback isolation guarantees where they cost code or copies. A `SpanSnapshot` is the SDK-owned span record itself, reused from response sampling through export; attribute values are plain CLR arrays. Read-only interfaces state intent only. Folded into [plan section 6](implementation-plan.md#6-owned-values-snapshots-and-privacy).

### F8. Smaller idiom improvements

| Area | Recommendation |
| --- | --- |
| Time and randomness in tests (plan line 428) | Inject .NET 8's `TimeProvider` into spool retention, scheduling and uptime, and use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) in tests. Use `Task.Delay(delay, timeProvider, token)` and `Random.Shared` for jitter. Do not write a clock abstraction. |
| SDK diagnostics assertions | Use `FakeLogger`/`FakeLogCollector` and `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing` instead of custom collectors. |
| Logger provider | Mark `ApitallyLoggerProvider` with `[ProviderAlias("Apitally")]` and implement `ISupportExternalScope`. Users can then limit captured categories through standard `Logging:Apitally:LogLevel` configuration. This matters because capture is on by default, and it costs no Apitally code. |
| SDK diagnostic messages | Use `[LoggerMessage]` source-generated methods in `SdkDiagnostics`. |
| Built-in patterns | Use `[GeneratedRegex]` for the built-in redaction and exclusion defaults, with `CultureInvariant`. Keep runtime `Regex` with a match timeout for user patterns. |
| Process metrics | Use `Environment.WorkingSet` and `Environment.ProcessorCount`, and keep one cached `Process` only for `TotalProcessorTime`. This avoids repeated `Process` refresh cost. |
| Request metrics | Record with `TagList` to avoid a tag allocation per request. |
| Public API guard (stage 1 acceptance) | Use `Microsoft.CodeAnalysis.PublicApiAnalyzers` to catch generated protobuf or snapshot types leaking into the public API, rather than a custom test. |
| AOT-friendly analyzers | Consider enabling `IsAotCompatible` analyzers and the configuration binding source generator. This follows the design's "compatibility-friendly when it adds no complexity" rule. It is optional and does not make AOT a release gate. |
| Value conversion (plan line 252) | Implement the type-mapping table from design section 9. Do not also copy stock `TagWriter`'s internal failure quirks, such as a failed element dropping the whole array or three-level map depth, as a behavior contract with dedicated tests. They are internal details, not shared requirements. |

**Decision (2026-09-28):** fold in time and randomness, test helpers, `ProviderAlias`, `[LoggerMessage]`, `[GeneratedRegex]`, `TagList` and value conversion. Not adopted: PublicApiAnalyzers. Withdrawn after re-checking: `ISupportExternalScope` (the design already forwards an external scope provider), AOT analyzers (they add binder warnings and workarounds for a deferred goal), and `Environment.WorkingSet` (not verified as cheaper than the planned process API).

## Keep these choices

| Choice | Why it is idiomatic or necessary |
| --- | --- |
| Single `net8.0` assembly with C# 12 | Microsoft advises against a language version newer than the target framework's default. One assembly serves 8/9/10 through the shared framework. |
| `IStartupFilter` for automatic middleware | The standard hook for library middleware placement in both hosting styles. |
| `IHostedLifecycleService.StoppedAsync` for the final drain, with `Task.WaitAsync(token)` around the cleanup task | Standard .NET 8 lifecycle and cancellation. The OTel batch threads are background threads, so a stuck cleanup cannot keep the process alive. |
| Request state as an `HttpContext` feature, early access through `IHttpContextAccessor`, `IApitally` as a singleton over the accessor | The standard ASP.NET Core way to reach request state. Verified by the request-association POC. |
| Span-ID map for associations | `Activity.Parent` is null for explicit-parent children, and late telemetry needs lookups after release, so a map is needed. `SetCustomProperty` would add a second mechanism without removing the map. |
| Stock `BatchExportProcessor<T>` subclasses, OTel metric views and exponential histograms | Reuses the OTel SDK instead of custom queues and aggregation. |
| `Google.Protobuf` with build-time `Grpc.Tools` generation | The standard .NET protobuf toolchain. The OTLP serializers are internal and cannot be reused. |
| `ExecutionContext.SuppressFlow` when starting workers | Required: `Thread.Start` and `Task.Run` capture the execution context, including the request's `Activity.Current` and `HttpContext`. |
| Explicit single retry on a connection error | Needed. `SocketsHttpHandler`'s built-in stale-connection retry only covers requests without a body ([source](https://github.com/dotnet/runtime/blob/release/8.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs#L613-L623)), so export POSTs are not retried by the handler. |
| Hand-written FIFO and LRU caches | .NET has no bounded LRU. `MemoryCache` compaction is priority- and expiry-based and heavier than the roughly 30 lines needed. |
| `double?` sampling, `Func<SpanSnapshot, byte[], byte[]?>` body masking, `IApitally.StartActivity` returning `Activity?` | Typed C# versions of the shared API that stay close to native activity usage. |

## Candidates not retained

| Candidate | Disposition |
| --- | --- |
| Use `LangVersion latest`, as v0 does | Rejected. Pinning C# 12 for a net8 target follows Microsoft's guidance, and newer language features give nothing the plan needs. |
| Replace the spool admission lock with a volatile flag | Not retained. The lock also makes in-flight submissions finish before the terminal drain target is set. The generic `BatchExportProcessor<T>` path does not use the stock `TryEnterOnEnd` fence, which is only used by the activity and log processors. The lock is short and uncontended in normal operation. |
| Rely on the handler's stale-connection retry and drop the explicit one | Rejected. See the keep table: POSTs with a body are not retried automatically. |
| Use ASP.NET Core's native `http.server.request.duration` meter with `IHttpMetricsTagsFeature` | Rejected. It cannot give scope `apitally`, the body-size histograms or the OPTIONS/unmatched exclusions, and the private provider would observe the meter process-wide. |
| Use `Microsoft.Extensions.Compliance.Redaction` for log masking | Rejected. It is based on data classification, requires annotating application types, and does not meet the shared masking contract. |
| Treat `IApitally.StartActivity` as redundant with `ActivitySource` | Not retained. Apitally's fallback provider subscribes only to `apitally.otel`, so an application `ActivitySource` would not be captured without application-owned tracing. The helper is the one-line path. |

## Decisions

All findings were decided on 2026-09-28 and folded into the implementation plan:

| Finding | Decision |
| --- | --- |
| F1 | Options pattern; direct `services.Configure<ApitallyOptions>` documented with standard ordering |
| F2 | `IServiceCollection.AddApitally` is the only entry point |
| F3 | Apitally-owned `LogRecordSnapshot`; no private OTel logger provider; accepted records buffered without a second copy |
| F4 | Internal events submit `LogSnapshot` entries directly |
| F5 | Activate in the startup filter; simplified lifecycle; `TelemetryRuntime` disposable through DI |
| F6 | Private `HttpClient` and built-in `DefaultProxy`; metrics concern withdrawn |
| F7 | Native event, link and resource types; `SpanSnapshot` is the owned record, with no read-only or isolation guarantees |
| F8 | Items adopted as listed in F8; PublicApiAnalyzers not adopted |

The design document was updated to match, recording F3 as an explicit .NET deviation from the shared design's ecosystem log-record rule. The shared cloud design is unchanged. Production implementation still requires explicit approval.
