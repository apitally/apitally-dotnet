# Private logging POC

Isolated experiment, run on 2026-09-23. Production implementation and a public callback signature remain unapproved. This project references no repository SDK projects. All exports are in memory; there are no credentials, network exporters, reflection, or AOT builds.

## Run

Requirements: Python 3 (standard library only), SDK 10.0.301, and .NET 8/9/10 runtimes. NuGet access is needed for an uncached restore. The local `global.json` pins the SDK.

```sh
cd /Users/simon.gurcke/Repos/apitally/apitally-dotnet/pocs/private-logging
python3 run.py
```

The runner uses 120-second restore/build limits and 60-second runtime limits. It disables .NET CLI telemetry and writes command output under ignored `results/`. It first requires the negative API probes to fail with exactly CS0029 and CS1061, then builds the normal project and runs all three frameworks. A failed positive assertion exits nonzero. Batch flush, explicit exporter-completion waits, and shutdown each have a 5-second bound; all factories, providers, listeners, and batch workers are disposed.

Underlying positive commands:

```sh
dotnet restore PrivateLogging.csproj --locked-mode
dotnet build PrivateLogging.csproj --no-restore --nologo
dotnet run --no-build --no-restore --framework net8.0
dotnet run --no-build --no-restore --framework net9.0
dotnet run --no-build --no-restore --framework net10.0
```

Negative probe command, repeated for each target framework:

```sh
dotnet build PrivateLogging.csproj --no-restore --framework net8.0 \
  -p:DefineConstants=UNSUPPORTED_PUBLIC_API
```

This intentionally tries assigning a dictionary to `LogRecord.Body` and assigning `LogRecord.EventName`. A missing `EventName` property does NOT mean native event names are unsupported: use `EventId.Name`, as demonstrated below.

## Actual results and dependencies

`python3 run.py` exited 0. Normal build: zero warnings/errors.

| Target | Actual runtime | Microsoft.Extensions.Logging | Positive assertions | Negative API probe |
| --- | --- | --- | --- | --- |
| net8.0 | 8.0.13 | 8.0.0 | 229 passed | Expected CS0029 + CS1061 |
| net9.0 | 9.0.2 | 9.0.0 | 229 passed | Expected CS0029 + CS1061 |
| net10.0 | 10.0.9 | 10.0.0 | 229 passed | Expected CS0029 + CS1061 |

`OpenTelemetry`, `OpenTelemetry.Api`, and `OpenTelemetry.Api.ProviderBuilderExtensions` resolve to **1.19.0** on every target. `Microsoft.Extensions.*` dependencies resolve to the target's 8.0.0/9.0.0/10.0.0 line. `System.Diagnostics.DiagnosticSource` is **10.0.0** on net8/net9; net10 uses the runtime assembly. `packages.lock.json` records every resolved version and content hash.

The initial `OpenTelemetry` 1.19.1 restore actually failed with NU1102: NuGet reported nearest version `1.19.1-rc.1`. The NuGet flat-container index exposed 1.19.0 as the latest stable version at execution, although GitHub's latest release was `core-1.19.1`. We selected available stable 1.19.0, not a prerelease and not a production dependency floor. Its NuGet repository commit is `dac1573ece52e8c275c3db5282bc57e3d5eff5cf`. The GitHub comparison against researched 1.19.1 (`5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e`) contained no logging implementation changes.

## Verified construction and ownership

`CaptureBridge` constructs a private official `OpenTelemetryLoggerProvider` through its public `IOptionsMonitor<OpenTelemetryLoggerOptions>` constructor. `FixedOptions` supplies immutable POC options. It does not add processors to, replace, or resolve the application's OTel provider. `AddProvider(bridge)` adds only the bridge to the application's ordinary `LoggerFactory`.

The user pipeline independently registers `AddOpenTelemetry` with its own processor/resource, plus a separate `UserSink`. Both bridge registration orders pass. The tests check original state object identity, output text, nested values, scopes, resources, exact counts, and independent disposal. The bridge forwards the external scope provider through public `ISupportExternalScope`.

The outer factory preserves provider-independent minimum/category rules. The bridge's own minimum is Trace. Provider-specific user OTel filtering still applies only to that provider: it does not implicitly become the bridge's filter. The test deliberately shows an Information record reaching the independent sink and private capture but not the user's Error-filtered OTel provider. This is ordinary Microsoft logging behavior, not universal equivalence to every provider's filter settings.

Apitally/OpenTelemetry diagnostic categories reach user sinks but are excluded from capture. Disabling capture does not disable private internal events. Disposing the private provider leaves the user's processor alive and receiving records.

## Safe processing and exact drops

The private official provider has one capture processor. Within `OnEnd` it synchronously copies record fields, structured attributes, and ordered scopes before invoking the POC-only masking delegate. Known mutable dictionaries, arrays, and nested values are recursively copied. `FormattedMessage` supplies the rendered body while `{OriginalFormat}` remains available in the private attributes. Code-location attributes are preserved. Exceptions become strings, not retained exception objects.

Masking can mutate the supplied private object and return that same object. Null, exceptions, and replacement objects each drop exactly one record. A second owned copy detaches buffering from a callback that retains its input. The tests mutate application state, disposed scope values, and the callback-retained object after logging; delayed output remains unchanged. Fifty subsequent logs exercise SDK record reuse before request release.

Truncation follows masking: bodies and direct string attribute/scope values are hard-cut at 2,048 Unicode scalar values. Tests cover 2,048/2,050/3,000-character values and an emoji at the boundary without splitting its surrogate pair. Non-string values, including string arrays, are copied but not truncated. Scalar-count semantics are an explicit POC choice for review, not a finalized cross-SDK Unicode policy.

Returning from a processor does **not** cancel following processors: a negative test confirms both records still reach the next processor. Exact drops therefore use explicit forwarding: only accepted owned copies enter `RequestBuffer`, then `BatchExportProcessor<OwnedLog>`. No exporter sits after the private gate in the official `LogRecord` processor chain.

A separate deliberately unsafe negative probe retains the first `LogRecord` only to prove the next log reuses that same object and changes its body to `second`. The synchronous owned copy still contains `first`. The functional pipeline never retains pooled records. The specialized SDK log batch processor has internal lifetime management; generic delayed retention is not equivalent, and the SDK's internal copies are not recursive application-object copies.

Delayed-export assertions use an explicit cumulative completion count signaled by the exporter after all batch output is stored. `ForceFlush` only triggers processing; it is not treated as an output-completion barrier. Every flush assertion also verifies the synchronous submitted count, so zero-output and dropped-record checks cannot pass while an unexpected record is still queued. The separate [activity-snapshots lifecycle evidence](../activity-snapshots/README.md#batch-lifecycle-evidence-and-negative-results) demonstrates the OTel 1.19.0 flush limitation; this POC does not duplicate that probe.

Request buffers hold the earliest 1,000 records. Both SERVER/transport completion orders, duplicate completion, late kept logs, response-stage drops, and late dropped logs are asserted. The emitted child span ID and trace ID stay separate from the explicit lowercase SERVER association attribute. The POC manually supplies the activity-to-request map; it does not claim middleware association has been solved.

## Internal events and the representation boundary

Internal emission uses the same private official provider directly, category/scope `apitally`, and public `EventId(0, eventName)`. `Activity.Current` is cleared synchronously and restored in `finally`. Assertions check that even the official incoming record has empty trace/span/flags, and that owned output has no SERVER linkage or ambient request scopes. Masking/truncation is bypassed even with capture disabled inside a child activity.

**Native event names are supported:** the pinned stock OTLP serializer writes `LogRecord.EventId.Name` to native `event_name`. `OwnedLog.EventName` copies that value for the eventual owned encoder. An `event.name` fallback is unnecessary for this tested version. Numeric EventId is copied separately.

**Structured bodies are not representable in stable `LogRecord.Body`:** it is `string?`, and the pinned stock serializer writes its body as `AnyValue.string_value`. Passing structured state creates attributes, not an OTLP object body. Serializing an error dictionary to JSON would violate the error-event contract.

The minimal demonstrated path is:

```text
Internal EventId.Name + private body-carrier attribute
    -> official private ILogger / LogRecord / synchronous processor
    -> OwnedLog { EventName, Body: string OR Dictionary<string, object?> }
    -> ordinary owned batch worker / in-memory exporter
```

`poc.internal.body` is a private carrier, removed before the owned record is queued. This is an explicit adapter, not a claim that the official record had an object body. Internal error/validation objects are copied before return from emission. Assertions preserve a UInt32 maximum count and 65,536-character stacktrace; startup stays a full JSON string longer than 2,048 characters.

Mapping responsibilities for the separate encoder POC:

| Owned field | Required OTLP mapping |
| --- | --- |
| `EventName` (from `EventId.Name`) | Native `event_name` |
| Startup string `Body` | `AnyValue.string_value`, containing JSON |
| Error dictionary `Body` | `AnyValue.kvlist_value`, preserving typed values |
| `Scope` | `InstrumentationScope.name` |
| Timestamp/observed timestamp | Corresponding Unix-nanosecond fields |
| Application trace/span IDs and flags | Original emitting context, not SERVER replacement |
| SERVER attribute | Explicit request linkage |
| `Level` from ILogger | Corresponding OTel severity number/text |

No OTLP encoding, decoding, network delivery, or wire conformance is claimed here. The encoder must use the explicit owned body, not re-select an unmasked `{OriginalFormat}`. Scope flattening/collision policy and mapping of every CLR value type remain separate design work.

## Limits that remain open

- This is a console assertion harness using Microsoft's `LoggerFactory`, not a web-host integration. Third-party logging-factory replacements, reload behavior, concurrent request integration, mapping cleanup, and multi-host lifecycle are not established.
- Copying covers the tested finite acyclic structured shapes. Arbitrary CLR objects are normalized synchronously with `ToString`; the explicit opaque-object test proves this is lossy but detached. Cycles, arbitrary typed object graphs, duplicate attribute keys, and faithfully preserving original `TState` type are not solved by these public APIs. The owned delegate is an experiment, not the callback contract.
- A bridge invokes the original formatter for its additional provider. Stateful/side-effectful formatters and concurrent mutation of objects during `Log` are not shown to be transparent.
- No live `Activity`, `HttpContext`, exception, scope object, or original structured collection is kept in the functional request buffer. It keeps only the supported normalized owned value shapes. The request-map fixture itself lasts until fixture disposal.
- Internal event examples exercise representation, not full startup discovery, config redaction, aggregate normalization, process-wide limits, or once-per-process policy. The internal bypass requires exclusive ownership of the private provider; applications do not obtain its `apitally` logger.
- The stable provider constructor is public and works without warnings, but upstream contains a future-obsolescence TODO. `Sdk.CreateLoggerProviderBuilder` is internal in the stable build and exposed only behind the experimental build flag. Normal SDK initialization also has process-level defaults; this POC's isolation claim concerns logging providers/resources/output, not absence of every SDK static side effect.

## Source evidence

Context7 lookup used `/open-telemetry/opentelemetry-dotnet` for current logging/scopes/processor guidance. Version-sensitive conclusions were then checked with `gh api` against the actual package source. Evidence permalinks:

- [Public private-provider constructor and ownership](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLoggerProvider.cs#L32-L66)
- [Stable versus experimental logger builder](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Sdk.cs#L76-L103)
- [OnEnd followed by return to pool](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L100-L106)
- [Structured state values are originally shared](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L164-L194)
- [Public EventId and string Body](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L235-L272)
- [Internal, shallow record copying and scope buffering](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L490-L579)
- [Specialized log batch reference/copy management](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/Processor/BatchLogRecordExportProcessor.cs#L64-L93)
- [EventId.Name becomes native event_name](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs#L325-L328)
- [Stock body writer only emits string AnyValue](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs#L376-L385)

Inputs reviewed: local design sections 2, 6, 9, 10, 18; shared SDK design sections 2, 6, 9, 10; shared specification sections 8 and 9. Shared documents were not edited.
