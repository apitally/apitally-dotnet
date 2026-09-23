# Provider registration and host ownership POC

Executed 2026-09-23. This is isolated experimental evidence, not an approved SDK implementation or a complete multi-host solution. It uses real Kestrel servers on loopback ephemeral ports, native activities, the official OTel SDK/instrumentation, and in-memory collectors/exporters. No telemetry leaves the process. NuGet restore uses the public package feed.

## Run

Requires Python 3, .NET SDK 10, and the three target runtimes:

```sh
cd pocs/provider-registration
python3 run.py
```

The runner restores in locked mode, builds all targets, and runs each DLL in a separate process. It removes inherited `OTEL_*` settings and disables .NET CLI telemetry for repeatability. Restore/build have 180-second limits; each executable has a 120-second limit. A timeout kills the process group. HTTP requests have 5-second limits, host startup waits 10 seconds, and shutdown/disposal is bounded. Hosts/providers are disposed, including failure paths. No background servers are launched.

Equivalent individual commands (the Python runner supplies the outer process deadlines):

```sh
dotnet restore ProviderRegistration.csproj --locked-mode --disable-parallel
dotnet build ProviderRegistration.csproj -c Release --no-restore --disable-build-servers
dotnet run --project ProviderRegistration.csproj -c Release -f net8.0 --no-build --no-restore
dotnet run --project ProviderRegistration.csproj -c Release -f net9.0 --no-build --no-restore
dotnet run --project ProviderRegistration.csproj -c Release -f net10.0 --no-build --no-restore
```

Any failed assertion exits nonzero. `EXPECTED FAILURE` means the executable successfully reproduced a failed architectural hypothesis; it does not mean that the requirement was weakened or that the architecture is acceptable. The final successful matrix output is retained in `results/`.

## Exact tested dependencies and runtime matrix

The first restore requested **OpenTelemetry 1.19.1** and **OpenTelemetry.Extensions.Hosting 1.19.1**, plus instrumentation 1.19.0. NuGet returned NU1102 for the first two packages (nearest version `1.19.1-rc.1`). The public flat-container indexes confirmed that stable 1.19.0 was available. Rather than test a prerelease or build GitHub source, this POC pins stable 1.19.0. These are tested versions, not recommended release floors.

| Dependency | Version |
| --- | --- |
| OpenTelemetry | 1.19.0 |
| OpenTelemetry.Extensions.Hosting | 1.19.0 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.19.0 |
| OpenTelemetry.Instrumentation.Http | 1.19.0 |
| OpenTelemetry.Api (transitive) | 1.19.0 |
| OpenTelemetry.Api.ProviderBuilderExtensions (transitive) | 1.19.0 |
| System.Diagnostics.DiagnosticSource (net8/net9 transitive) | 10.0.0 |

Exact direct versions use NuGet equality ranges; `packages.lock.json` pins all transitive versions and hashes. The `Microsoft.AspNetCore.App` framework reference comes from the Web SDK. No exporter package or test framework is required.

Build SDK: **10.0.301**, all three targets, zero warnings/errors. SDKs 8.0.406 and 9.0.200 were installed but were not used to build this multi-target project.

| Target | .NET runtime | ASP.NET runtime | Actual DiagnosticSource | Result |
| --- | --- | --- | --- | --- |
| net8.0 | 8.0.13 | 8.0.13 | NuGet 10.0.0 | All assertions pass; negative hypotheses reproduced |
| net9.0 | 9.0.2 | 9.0.2 | NuGet 10.0.0 | Same |
| net10.0 | 10.0.9 | 10.0.9 | Framework 10.0.9 | Same |

The net8/net9 results therefore do **not** characterize their original in-box DiagnosticSource assemblies. The executable prints informational versions and commits, not misleading assembly versions such as OTel's `1.0.0.0`.

## Public registration evidence versus ownership inference

`RegistrationEvidence` establishes through public `IServiceCollection`, `ServiceDescriptor`, and provider resolution:

- Both `ConfigureOpenTelemetryTracerProvider` overloads add configuration without registering a `TracerProvider`. The deferred callback does not run without an enabled provider.
- `AddOpenTelemetry()` alone does not enable tracing.
- `AddOpenTelemetry().WithTracing()` registers one singleton provider. Another call keeps the **same descriptor object**, while its configuration still applies.
- That descriptor does not identify the caller or establish whether the application, Apitally, or another library chose the sampler. Counting providers after Apitally has enabled tracing is not ownership detection.

`NaiveOwnershipFailure` executes the tempting check-for-provider-then-enable approach in both orders. The app uses OTel's implicit parent-based default. With a remote unsampled parent, app-first exports zero SERVER spans; Apitally-first exports one because its earlier explicit always-on sampler still wins over the user's implicit default. Repeated `WithTracing` is additive, but that does not make unconditional sampler configuration safe.

### Candidate actually exercised

`Candidate.Register` contributes builder configuration only, never a host `TracerProvider` descriptor. A DI-created `IStartupFilter` resolves the fully registered provider before Kestrel accepts requests:

1. If normal DI tracing is enabled, use that provider; its construction-time callback adds the processor. No candidate sampler is installed.
2. If an external provider is explicitly supplied, or a resolved provider bypassed the builder callback, use public post-build `TracerProvider.AddProcessor`.
3. If no provider is available, construct a separate, host-owned provider with explicit always-on sampling, ASP.NET/HttpClient instrumentation, and `apitally.otel`.

This can say "registered outside this candidate" because this candidate never occupies the tracing slot. It cannot say who originally registered a service. It does not inspect internal OTel configuration descriptors, builder state, or provider sampler fields. The counting samplers are test fixtures, not an introspection mechanism.

**Important limitation:** a user's `ConfigureOpenTelemetryTracerProvider(SetSampler(AlwaysOff))` without `WithTracing` does not enable a provider. `ConfigureOnlyLimitation` proves that the private fallback records while those callbacks never run. The fallback does not replay host library/source/resource configuration into its private container. Intent in unenabled configuration cannot be inferred from a missing provider. This trade-off needs a design decision; this POC does not call it automatic compatibility with every possible registration pattern.

The candidate selects its provider once during startup-filter construction. Tracing enabled or replaced after that boundary is not detected. The startup filter is only a provider-readiness and association mechanism here. No private telemetry workers, activation state machine, middleware-placement guarantee, or production lifecycle API is implemented.

## Positive checks

| Check | Observed assertions |
| --- | --- |
| Both registration orders | One DI provider; explicit counting drop and record samplers still run. Drop gives zero exports; record gives one SERVER plus one manual child. User resource, enrichment, exporter IDs, and background exports survive. |
| Implicit user sampler | Both orders retain the default: unsampled remote parent exports zero; new root exports one. |
| Repeated instrumentation | Three same-name ASP.NET registrations in the same DI container produce one SERVER and one user enrichment callback per recorded request. These tests do not establish behavior for different named registrations or older versions. |
| First request | An `IHostedLifecycleService.StartedAsync` client issues the first request before `ApplicationStarted`. The owned provider observes its SERVER `OnStart` and exports SERVER plus child. Waiting until `ApplicationStarted` alone would be too late for this request. |
| Owned sampling | Explicit always-on records a remote `traceparent` ending in `-00`, preserves its trace/parent IDs, and records the child even with `OTEL_TRACES_SAMPLER=always_off`. The next keep-alive request has a different trace ID. |
| External provider | Public `AddProcessor` attaches after `Build`; first request and child reach both paths. Host disposal leaves the external user's sampler/exporter usable. The candidate disables its processor rather than disposing the provider. |
| Outbound defaults | Owned fallback captures one real outgoing HTTP CLIENT span. User-owned tracing without HttpClient instrumentation captures none. The harness client is suppressed and does not contaminate counts. |
| Association | An unmonitored local receiver is observed by the process-wide listener but excluded from the candidate's accepted SERVER spans. |
| Two active hosts | Two requests overlap using a shared async gate. Each provider observes four activities (both SERVERs and children), but each host accepts only its own two. Stopping/disposing A removes its listener; B serves another request and reaches three accepted spans while A's counts stay unchanged. |

## Failed hypotheses and limits

### Host filtering cannot preserve independent sampling

`ConflictingHostSamplers` creates a user host whose actual sampler always returns `Drop`, plus a second host with the owned always-on provider. It tests both host startup orders:

- With the user host alone, its exporter receives nothing.
- While both providers listen, the user sampler still returns `Drop` for both SERVERs, yet the user's stock `SimpleActivityExportProcessor` exports **both** SERVERs as recorded.
- The candidate's host filter correctly accepts only its own request, but accepts **one request that the user's sampler dropped**. Filtering export association cannot undo another listener's sampling promotion, downstream propagation, or user exporter behavior.
- Dispose the always-on host and request the user host again: it still serves successfully, and its exporter returns to exporting nothing.

OTel providers install process-wide `ActivityListener`s. Activity sampling uses the maximum listener result; the activity's recording flags are shared. OTel's callbacks then use those shared flags, not a provider-private copy of the sampling decision. Separate DI containers and providers do not isolate sampling. The candidate deliberately leaves this failure visible. Re-running the sampler at export, replacing the user sampler, or mutating shared activity flags is not offered as a fix.

### Association filter scope

The startup-filter middleware records its `IHttpActivityFeature.Activity` in a host-local weak table. At activity end the processor accepts that activity and descendants whose native `Activity.Parent` chain reaches it. It never writes host tags or changes user activities. SERVER `OnStart` precedes middleware, so start callbacks are observed but not classified there.

This demonstrates export association for ordinary middleware requests and ambient-parent descendants. It is not a complete request association algorithm: it does not capture descendants that finish before middleware observes the SERVER, or reconstruct an explicit-parent-context child whose `Activity.Parent` object chain is absent. It does not settle late-child ownership or cleanup policy. The raw/start collections are unbounded test diagnostics, not production buffers.

### External providers and shutdown

`ExternalMissingSource` supplies a built provider that never subscribed to ASP.NET. Public `AddProcessor` succeeds, but the request produces **zero processor observations**. It cannot add instrumentation or sources to an already-built provider. The explicit path must establish those prerequisites before serving.

Source inspection also shows that this public extension only attaches to the official SDK implementation and returns the supplied provider even for other implementations. Return identity is not proof of successful attachment. This POC does not claim support for arbitrary custom `TracerProvider` subclasses.

There is no public counterpart to remove an attached processor. The external-provider experiment disables the candidate path at host disposal; the external provider retains the processor until its own disposal. Retention/cleanup requires review for a long-lived external provider serving many short-lived hosts. Ordinary DI provider lifetime remains the application's container's responsibility.

## Research and remaining decisions

Read local design sections 2, 4, 13, 18 and the shared provider/lifecycle/request/sampling/resource requirements. Queried Context7 `/open-telemetry/opentelemetry-dotnet` for supported registration APIs, then verified tagged source with `gh api`. Core 1.19.0's informational commit matches the tested package. The inspected registration, processor attachment, and sampler files are unchanged between core 1.19.0 and the initially researched core 1.19.1.

- [Library configuration APIs](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Api.ProviderBuilderExtensions/Trace/OpenTelemetryDependencyInjectionTracingServiceCollectionExtensions.cs#L15-L88)
- [Hosting registration guidance](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Extensions.Hosting/README.md)
- [Public post-build processor attachment](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderExtensions.cs#L14-L33)
- [OTel listener sampling and shared recording flags](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L193-L271)
- [ASP.NET 1.19.0 same-name instrumentation deduplication](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/dda21df71c8ccd247ab1051760b13f1db9353cfa/src/OpenTelemetry.Instrumentation.AspNetCore/AspNetCoreInstrumentationTracerProviderBuilderExtensions.cs#L58-L94)
- [Runtime ActivitySource maximum listener sampling result](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/ActivitySource.cs#L298-L328)

Before a production choice: resolve the multi-host sampling compatibility boundary, whether the private fallback's unenabled-configuration limitation is acceptable, external source/processor lifetime requirements, and association before middleware. No process/host identity policy, release floor, or new public setup API is decided here. Native AOT, payload privacy, worker activation, and final export draining are outside this experiment.
