# Recorded execution

Date: 2026-09-24. Platform: macOS 26.6 / osx-arm64. Command: `python3 run.py`, executed in this directory in the foreground.

- SDK: `10.0.301` (local SDK pin, checked by runner).
- CSharpier: `1.3.0`; four files passed formatting check.
- Locked NuGet restore: exit 0.
- Expected constructor builds: exit 1 for each target, exactly CS1729.
- Normal multi-target build: exit 0, zero warnings, zero errors.
- Runtime executions: exit 0 for each exact version below.
- Overall runner: exit 0.

Initial project restore and positive build also succeeded. No unexpected compile/runtime failure was encountered. Intentional negative findings were retained rather than removed to make the experiment pass.

## Expected constructor failure

Each of `net8.0`, `net9.0`, and `net10.0`, using `-p:DefineConstants=UNSUPPORTED_PUBLIC_CONSTRUCTOR`, emitted this diagnostic:

```text
UnsupportedApi.cs(6,48): error CS1729: 'LogRecord' does not contain a constructor that takes 0 arguments
Build FAILED.
    0 Warning(s)
    1 Error(s)
PASS expected negative API probe: no public parameterless LogRecord constructor
```

Paths and target suffixes are omitted above; raw output is regenerated under ignored `results/unsupported-<framework>.log`.

## net8.0

```text
Runtime: .NET 8.0.13; SDK: 10.0.301; OpenTelemetry: 1.19.0
PASS negative findings: native FormattedMessage populated despite false option; OnEnd return does not drop downstream
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=True
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=False
PASS exact same-record acceptance; null/throw/replacement drops using public native records
PASS negative native lifetime probe: actual pool reuse and late mutation cannot change owned export
PASS experimental finite value set fails closed at both ownership boundaries (not production normalization policy)
PASS formatter invoked exactly once for private forwarding
PASS 12 parallel async scopes / 36 logs, detached arrays and coherent SERVER/child fixture identities
PASS: 828 assertions; all providers and batch workers disposed; no network exporters.
```

## net9.0

```text
Runtime: .NET 9.0.2; SDK: 10.0.301; OpenTelemetry: 1.19.0
PASS negative findings: native FormattedMessage populated despite false option; OnEnd return does not drop downstream
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=True
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=False
PASS exact same-record acceptance; null/throw/replacement drops using public native records
PASS negative native lifetime probe: actual pool reuse and late mutation cannot change owned export
PASS experimental finite value set fails closed at both ownership boundaries (not production normalization policy)
PASS formatter invoked exactly once for private forwarding
PASS 12 parallel async scopes / 36 logs, detached arrays and coherent SERVER/child fixture identities
PASS: 828 assertions; all providers and batch workers disposed; no network exporters.
```

## net10.0

```text
Runtime: .NET 10.0.9; SDK: 10.0.301; OpenTelemetry: 1.19.0
PASS negative findings: native FormattedMessage populated despite false option; OnEnd return does not drop downstream
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=True
PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst=False
PASS exact same-record acceptance; null/throw/replacement drops using public native records
PASS negative native lifetime probe: actual pool reuse and late mutation cannot change owned export
PASS experimental finite value set fails closed at both ownership boundaries (not production normalization policy)
PASS formatter invoked exactly once for private forwarding
PASS 12 parallel async scopes / 36 logs, detached arrays and coherent SERVER/child fixture identities
PASS: 828 assertions; all providers and batch workers disposed; no network exporters.
```

Runner final line:

```text
PASS all three exact runtimes, format check, locked restore, and expected constructor limitations
```

## Scope of the result

The observed negative runtime findings are: the false formatted-message option alone leaves a rendered `FormattedMessage`; `OnEnd` return does not cancel later processors; native records actually reuse the same object and cannot be retained safely. The selected adapter/processor path accounts for all three through pre-isolated input, synchronous normalization, and explicit forwarding of accepted owned snapshots.

The export target was exclusively in-memory `BaseExporter<OwnedSnapshot>`. No wire format or backend was exercised. Normalization is limited to the experimental set listed in README; production decisions and integration gaps remain open.
