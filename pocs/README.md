# .NET v1 architectural POCs

POC execution was approved on 2026-09-23 after committing the initial [design draft](../docs/design.md) in `58dc004`. Production SDK implementation remains unapproved.

These standalone experiments investigate public API behavior and architectural feasibility. They are not part of `Apitally.sln`, the package, or the production test suite. Each directory contains its own reproduction instructions and records the exact dependencies and runtimes actually tested.

## Status

| Directory | Scope | Status |
| --- | --- | --- |
| [provider-registration](provider-registration/README.md) | DI registration order, tracing ownership, external providers, sampling and concurrent hosts | Reviewed; matrix passed and limitations reproduced |
| [transport-lifecycle](transport-lifecycle/README.md) | Automatic middleware, body features, streaming, error response ordering and host shutdown | Reviewed; 271 assertions passed per runtime |
| [activity-snapshots](activity-snapshots/README.md) | Private span snapshots, payload isolation, stock generic batching and request-buffer coordination | Reviewed; 109 assertions passed per runtime |
| [private-logging](private-logging/README.md) | Private OTel logging, capture isolation, record lifetime, callbacks and internal events | Reviewed; 229 assertions per runtime and expected API limitations reproduced |
| [encoding-metrics](encoding-metrics/README.md) | Official protobuf encoding, gzip/replay, delta exponential metrics and meter isolation | Reviewed; runtime matrix, native SDK 8/9 builds and independent gzip checks passed |
| [error-integrations](error-integrations/README.md) | Framework validation/exceptions, Sentry event hooks and route/OpenAPI discovery | Reviewed; all 12 project/runtime combinations passed |

All six experiment groups have been independently inspected and rerun. A successfully reproduced limitation is a valid POC outcome; passing checks do not establish untested behavior or a production-ready implementation.

## Verified findings

Every experiment group's .NET 8/9/10 matrix was independently rerun successfully. Encoding/metrics also passed builds/runs using SDKs 8 and 9. The OTel-based experiments pin 1.19.0; stable 1.19.1 was unavailable during restore, and their net8/net9 targets load DiagnosticSource 10.0.0 through NuGet. Transport uses only the shared frameworks and a native listener, without OTel or NuGet packages; it does not establish OTel processor ordering.

- Additive DI registration can preserve a user's sampling in either registration order. The tested private fallback does not consume tracing configuration that never enabled a host provider.
- Host export association works for the tested requests, but two providers with different samplers interfere through process-wide activity listeners. Filtering exports does not preserve independent sampling.
- Owned snapshots and generic stock batching preserve tested metadata and payload privacy. The request-coordination experiment is a sequential model, not a concurrent transport test.
- Generic batching needs explicit recorded-span and lifecycle handling. `ForceFlush` can report success before export work finishes; closing/sending spool files requires stronger completion evidence.
- Additive private logging preserves tested user output and structured-state ownership. Pooled records need synchronous owned copying before deferred processing. `EventId.Name` carries native event names, while structured internal bodies require an owned representation beyond the string-only `LogRecord.Body`.
- Automatic startup-filter placement works in both tested hosting styles and preserves client-visible streaming. Completion callbacks alone do not prove complete bodies. `StoppedAsync` is a candidate final-drain phase, but host cancellation can leave requests unfinished and actual export draining remains unproven.
- Official protobuf encoding, continuous gzip concatenation, exact-size rotation and byte-identical local replay work for the tested data. The full spool/retry service and backend acceptance remain untested.
- Private metric providers need explicit meter-scope filtering. Delta capacity is reclaimed, but overflow loses required request dimensions; production capacity behavior remains a design decision.
- Public exception features remain available despite net10 diagnostic suppression. Typed Sentry hooks work, but automatic dependency-free integration is unresolved. OpenAPI generation has public paths for Swashbuckle and built-in net10; the tested built-in net9 providers are internal.

Independent verification commands, from the repository root:

```sh
python3 pocs/provider-registration/run.py
DOTNET_CLI_TELEMETRY_OPTOUT=1 bash pocs/activity-snapshots/run.sh
python3 pocs/private-logging/run.py
python3 pocs/encoding-metrics/run-matrix.py
DOTNET_CLI_TELEMETRY_OPTOUT=1 bash pocs/error-integrations/run.sh
(
  set -e
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  dotnet build pocs/transport-lifecycle/TransportLifecycle.csproj
  for framework in net8.0 net9.0 net10.0; do
    dotnet run --project pocs/transport-lifecycle/TransportLifecycle.csproj --no-build -f "$framework"
  done
)
```

These constraints are recorded in the design without selecting new product behavior or implementing production fixes. The feasibility round is complete; cross-component integration, full transport reliability, backend acceptance and the open product decisions remain separate work.

## Execution boundaries

- Modify only experimental code and documentation. Preserve the existing SDK and tests.
- Use synthetic data, in-memory exporters and loopback endpoints. Do not use real credentials or send telemetry to external services.
- Prefer public supported APIs. Document constraints instead of hiding them behind private reflection.
- Pin tested dependencies. A tested version is not automatically the minimum supported SDK dependency.
- Run checks on .NET 8, 9 and 10 where applicable, recording runtime-specific differences and untested cases.
- Bound execution time and dispose servers, providers, listeners and workers.
- Keep unresolved product decisions explicit, especially host/process identity and limits, public callback APIs and optional integration packaging.

## Environment at launch

The machine is macOS on ARM64. Available SDKs include 8.0.406, 9.0.200 and 10.0.301, with 10.0.301 selected by default. Installed .NET and ASP.NET Core runtime versions are 8.0.13, 9.0.2 and 10.0.9. Individual reports distinguish runtime versions from the SDK used to build their projects.

## Review process

For each experiment, inspect the source and assertions, rerun meaningful checks, and record positive evidence, reproduced constraints and remaining gaps. Update the relevant design sections with verified outcomes. Bring decisions that change the shared contract or agreed product behavior back for review rather than silently making them in a POC.
