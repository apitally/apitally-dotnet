# Native log masking POC

Isolated follow-up to `../private-logging`, run on 2026-09-24. It validates a synchronous `Func<OpenTelemetry.Logs.LogRecord, LogRecord?>` over an actual native record created by a private OTel provider. It references no production project. Production implementation remains unapproved.

**Result:** the selected callback mechanism works through public APIs for this probe's explicitly limited value set on .NET 8/9/10. This establishes candidate input normalization and owned in-memory export, not complete production normalization or wire/backend conformance.

## Reproduce

Requirements: Python 3, SDK **10.0.301**, and installed runtimes **8.0.13**, **9.0.2**, **10.0.9**. NuGet access is needed for an uncached package/tool restore. All telemetry stays in memory; there are no endpoints or credentials.

```sh
cd /Users/simon.gurcke/Repos/apitally/apitally-dotnet/pocs/native-log-masking
python3 run.py
```

The local `global.json` pins the SDK. The local tool manifest pins **CSharpier 1.3.0**. The project pins **OpenTelemetry 1.19.0** exactly, with `Microsoft.Extensions.Logging` **8.0.0/9.0.0/10.0.0** per target. `packages.lock.json` locks transitive packages and content hashes; the runner restores in locked mode.

The runner checks formatting, requires an intentional constructor compilation failure on each target, builds all targets, and executes each exact runtime with roll-forward disabled. Commands have 120-second limits, version queries 30 seconds, and runtime processes 60 seconds. Each batch flush, export-completion wait, and batch shutdown has a 5-second limit; parallel logging has a 10-second limit. The process deadline also bounds provider/factory construction and disposal. No web server is started.

```sh
dotnet tool restore
dotnet csharpier check .
dotnet restore NativeLogMasking.csproj --locked-mode
dotnet build NativeLogMasking.csproj --no-restore --nologo
dotnet --fx-version 8.0.13 --roll-forward Disable bin/Debug/net8.0/NativeLogMasking.dll
dotnet --fx-version 9.0.2 --roll-forward Disable bin/Debug/net9.0/NativeLogMasking.dll
dotnet --fx-version 10.0.9 --roll-forward Disable bin/Debug/net10.0/NativeLogMasking.dll
```

Negative constructor command, repeated for `net8.0`, `net9.0`, `net10.0` before the normal build:

```sh
dotnet build NativeLogMasking.csproj --no-restore --nologo --framework net8.0 \
  -p:DefineConstants=UNSUPPORTED_PUBLIC_CONSTRUCTOR
```

It must fail with exactly **CS1729**, stating that `LogRecord` has no constructor taking zero arguments. The callback path does not need to construct or clone native records.

`results/` contains ignored raw command logs after a run; [RESULTS.md](RESULTS.md) preserves the observed results. `bin/` and `obj/` are ignored. To format edited sources, run `dotnet csharpier format .` in this directory.

## Actual results

`python3 run.py` exited 0. Normal build: zero warnings and zero errors. CSharpier checked four source/project files.

| Target | Actual runtime | Assertions | Constructor probe |
| --- | --- | --- | --- |
| net8.0 | 8.0.13 | 828 passed | Expected CS1729 |
| net9.0 | 9.0.2 | 828 passed | Expected CS1729 |
| net10.0 | 10.0.9 | 828 passed | Expected CS1729 |

`OpenTelemetry`, `OpenTelemetry.Api`, and `OpenTelemetry.Api.ProviderBuilderExtensions` resolve to **1.19.0** for every target. The `Microsoft.Extensions.*` packages resolve to the corresponding **8.0.0/9.0.0/10.0.0** line. `System.Diagnostics.DiagnosticSource` resolves to **10.0.0** on net8/net9; net10 uses its runtime assembly. The exact dependency graph is in the lock file.

## Supported path tested

```text
Application LoggerFactory
  + independent provider -> original state/scopes/exception/formatter
  + additive CaptureAdapter
      -> application formatter once for this forwarding call
      -> detached structured fields: outer scope, inner scope, explicit event
      -> exception strings; no original exception
      -> private public OpenTelemetryLoggerProvider, IncludeScopes=false
      -> native LogRecord / synchronous MaskingProcessor
          -> clear FormattedMessage before callback
          -> invoke Func<LogRecord, LogRecord?>
          -> accepted same record: clone values again into OwnedSnapshot
          -> fixture's deferred queue
          -> BatchExportProcessor<OwnedSnapshot>
          -> BaseExporter<OwnedSnapshot> in memory
```

### Before the callback

- `CaptureAdapter` uses the public `OpenTelemetryLoggerProvider(IOptionsMonitor<OpenTelemetryLoggerOptions>)` constructor and registers only itself with the application factory. It does not modify another provider.
- The adapter stores the application's external scope provider **only for synchronous enumeration**. It never forwards that provider to the private OTel provider. Private `IncludeScopes=false`; native `ForEachScope` yields no entries in the callback.
- Structured scope fields are copied and merged outer-first. Inner fields override outer fields, and explicit event fields override both. Unstructured labels are omitted. Formatted scopes contribute named fields, not `{OriginalFormat}` or their rendered labels. No `Scope` attribute is invented.
- The adapter calls the original application formatter once, with its original state and exception, then passes an immutable rendered string through a private formatter. The independent application sink can invoke the original formatter separately. Both ordering tests and an adapter-only test establish those counts.
- The private log has copied attributes with `{OriginalFormat}` removed. `Body` contains rendered text. `ParseStateValues=true` leaves obsolete `State` null. A narrowly scoped obsolete-warning suppression exists only for the synchronous assertion reading `State`.
- The adapter supplies a null native exception. `exception.type`, `exception.message`, and `exception.stacktrace` are private string attributes. The stack fixture comes from an actually thrown exception; the stack attribute uses `Exception.ToString()`, including type/message/frames, so it can contain sensitive text independently of `exception.message`. Original exception identity and mutable `Data` stay with the application provider.
- OTel fills `FormattedMessage` even with `IncludeFormattedMessage=false` when there is no `{OriginalFormat}`. The synchronous processor explicitly sets it to null before invoking the callback. A direct native-provider probe preserves this important negative finding.

### Acceptance and ownership

The callback receives an actual `OpenTelemetry.Logs.LogRecord`, on the logging call's thread. Returning that same record accepts it. Returning null, throwing, or returning a different native record drops it. Only accepted **owned** snapshots leave the synchronous processor. There is no downstream native batch processor that could inadvertently export rejected records.

`OwnedSnapshot.Copy` takes `Body` directly, never `FormattedMessage` or a template fallback. Callback edits/removals of ordinary, scope, and exception attributes reach completed exports. Setting `Body=null` exports null without restoring the rendered input. Removing all exception attributes restores none of them.

Mutable accepted values are copied **again before deferred buffering**. Tests mutate original arrays/lists and state/scope collections after logging, replace native `Attributes` with a callback-owned mutable list containing arrays/lists, then mutate/clear those callback-owned values after logging. Completed owned exports preserve the accepted content.

The normal path never retains native records. Two deliberate negative-test misuses are confined to the harness:

1. A second public provider emits while the current native record is active, yielding a distinct native record reference for the replacement-return test. No private constructor or reflection is used.
2. A callback retains a native reference to demonstrate actual pool reuse on the next call. It observes the second call's body through that first reference, and later mutates the reference again. The first accepted snapshot remains unchanged through completed export.

These tests **do not permit caller retention**. User callbacks must finish synchronously and never retain or asynchronously use the native record. Retaining caller-owned arrays for the output-detachment test is separate from retaining a native record.

A direct two-processor negative test proves that returning from `OnEnd` does not cancel the next processor. Explicit forwarding is therefore part of the selected mechanism. `ForceFlush` is followed by an explicit exporter completion count; it is not treated alone as an export-completion barrier. Submitted counts and accepted queue counts are checked too.

### Context and concurrency

The fixture uses real SERVER and child `Activity` instances. Trace/span IDs come from the emitting child; a manually populated activity-ID-to-SERVER-ID map adds a separate `apitally.request.server_span_id` before masking. No value is reattached after the callback.

Twelve bounded parallel async workers emit three logs each with independent scopes. Callback assertions and completed exports check detached scope arrays and coherent worker/child/SERVER identities. This tests concurrent scope capture, not a performance or stress guarantee.

## Experimental normalization and remaining gaps

`ProbeValues` intentionally supports only **null, string, bool, int, long, double, int[], string[], List<int>**. Arrays and lists are cloned; string/scalar values are immutable. This is enough to prove both ownership boundaries without a general object copier. Unsupported attribute/scope values drop the private probe log before callback; unsupported callback output drops before buffering. The independent sink continues receiving all logs.

Those fail-closed probe constraints are **not a selected production unknown-object policy**. Remaining work includes:

- General CLR normalization, unknown values, recursive/nested maps, object arrays, cycles, and custom state/scope implementations. No reflection-based deep copying or arbitrary `ToString` normalization is attempted for attribute values.
- Duplicate keys **within one source collection**. Fixtures use unique keys; the local `ToDictionary` limitation is not a new public duplicate policy. The selected cross-scope/event precedence is tested separately. Collisions with generated exception/linkage keys are also not decided here.
- Production validation of the selected canonical message/exception mapping. Accepted `Body` and `Attributes` remain the output sources even if a callback assigns `FormattedMessage` or `Exception`; those alternate representations are ignored. These reassignment cases are not fixtures in this probe, and the decision adds no general compatibility layer for every writable native member.
- Unicode truncation semantics. Only ASCII body/direct string-attribute fixtures exercise 2,048-character truncation after masking. The simple substring helper is not a Unicode product decision. Array element truncation is not selected by this probe.
- Production request association, transport completion, sampling, queue/capacity policy, factory replacements, concurrent input mutation while `Log` is running, options reload, filter/lifecycle integration, diagnostics exclusion, capture toggles, and performance/AOT. The deferred queue and association dictionary are explicit fixtures with bounded test workloads, not production request buffers.
- Full SDK logging-factory/transport integration, internal event encoding, OTLP serialization/delivery, and backend acceptance. This probe validates only the selected mapping into an in-memory `BaseExporter<OwnedSnapshot>`.

The original private-logging experiment separately covers other logging integration questions but invokes an owned-object callback. This probe supplies the missing native-callback mechanism; it does not claim to complete those experiments' combined production implementation.

## Source checks

Context7 `/open-telemetry/opentelemetry-dotnet` supplied general processor/message guidance. Version-sensitive behavior was checked with `gh api` against package-source commit `dac1573ece52e8c275c3db5282bc57e3d5eff5cf`, and then exercised above:

- [Provider processing, formatter selection, scopes, pool return](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L40-L106)
- [Structured state sharing and ParseStateValues behavior](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/ILogger/OpenTelemetryLogger.cs#L130-L213)
- [Internal constructors](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L35-L51)
- [Public Body, FormattedMessage, State, Attributes, Exception members](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L239-L378)
- [Public synchronous scope enumeration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Logs/LogRecord.cs#L457-L485)
