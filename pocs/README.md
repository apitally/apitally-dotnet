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
| [endpoint-metadata](endpoint-metadata/README.md) | Follow-up: native route summaries/descriptions without generator dependencies | Eight path/method cases passed on .NET 8/9/10 |
| [test-host-suppression](test-host-suppression/README.md) | Follow-up: resolved TestServer guard, activation signals and preserved user tracing | Reviewed and independently rerun; 11/11/13 cases passed on .NET 8/9/10 |
| [native-log-masking](native-log-masking/README.md) | Follow-up: actual native callback, isolated inputs, normalized fields and owned output | Reviewed and independently rerun; 828 assertions per runtime and expected constructor failures on .NET 8/9/10 |
| [request-association](request-association/README.md) | Follow-up: real request/span/log/helper association, completion coordination and tracing coexistence | Reviewed and independently rerun; 1533 assertions per exact .NET 8/9/10 runtime |
| [transport-completeness](transport-completeness/README.md) | Follow-up: completion-time body decisions and native versus single-pass file capture | Reviewed and independently rerun; 3564 assertions per exact .NET 8/9/10 runtime; simple completeness checks and native file omission selected |

All six initial experiment groups have been independently inspected and rerun. The endpoint-metadata, test-host-suppression, native-log-masking, request-association and transport-completeness follow-ups were also executed across the three supported runtimes. A successfully reproduced limitation is a valid POC outcome; passing checks do not establish untested behavior or a production-ready implementation.

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
- Public exception features remain available despite net10 diagnostic suppression. Typed Sentry hooks work, but automatic dependency-free integration was not established; [Sentry integration is deferred beyond v1](../docs/design.md#14-sentry-integration), with the POC retained as future research. OpenAPI generation has public paths for Swashbuckle and built-in net10; the tested built-in net9 providers are internal.

The follow-up endpoint-metadata probe confirms direct summary/description reads for Minimal API endpoints, groups and MVC actions on all three runtimes. It adds no OpenAPI package reference and makes no schema-generation calls. This is the selected v1 documentation boundary; full OpenAPI capture, including native .NET 10, is deferred. See its README for reproduction and limits.

The test-host-suppression follow-up verifies the selected exact type/assembly guard during pipeline construction, before fallback provider creation. Standard TestServer hosts stay inactive while application-owned tracing continues exporting. Real loopback Kestrel remains active in Development with TestHost loaded. The request-only mode tests that trigger after host startup, not a concurrent early-request race. This is a candidate runtime, not complete SDK logging/metrics/worker integration. Reproduce with `python3 pocs/test-host-suppression/run.py`; see its README for the complete hosting matrix and limits.

The native-log-masking follow-up verifies synchronous native `LogRecord` callbacks with rendered `Body`, copied exception attributes and flattened structured scopes. Input/output detachment, callback edits/removals, drop behavior, actual pooling and independent-provider isolation pass for the probe's finite value set. Exporter completion is observed, not inferred from `ForceFlush`. General CLR-value normalization, auxiliary-member reassignment, production request association and OTLP wire mapping remain open. Reproduce with `python3 pocs/native-log-masking/run.py`; see its README for the experimental value policy and negative findings.

The request-association follow-up was approved on 2026-09-25 for review finding R2. It derives request/span/log linkage from real Kestrel and OTel callbacks, including before middleware, rather than supplied association maps. The matrix covers first requests, nested and explicit-parent activities, outgoing HTTP, helpers without recorded spans, concurrent/shared-trace/keep-alive isolation, late detail, both tracing registration orders and an existing-instance DI provider. Actual owned exporter output proves descendants/SERVER/log release ordering, final private transport enrichment and preserved application output. Native completion order was transport then SERVER; reverse/racing processing uses explicitly test-only handoffs of real events. Its 60-second manually swept metadata retention is experimental, not a production policy. Full normalization, body capture, metric/error export and spool/shutdown-budget behavior remain separate. Reproduce with `python3 pocs/request-association/run.py`; see its README and RESULTS for limits and two corrected prototype gaps.

The transport-completeness follow-up was approved on 2026-09-25 for review finding R3. It compares uninstrumented Kestrel with two file-capture experiments and publishes owned completion-time decisions. Streaming, gzip, writer acceptance, request consumption, errors and bounded file/range capture are exercised. A real OnStarting file replacement proves that native sending followed by a second read can capture different bytes from those served. Single-pass capture matches the tested bytes but bypasses the native feature using an infrastructure helper. The approved v1 design instead delegates native file sending unchanged and omits its whole body capture, including mixed output; ordinary observed streams may capture eligible file content incidentally. Completion uses simple direct failure/cancellation/length checks rather than the richer diagnostic tracker. Both file-capture alternatives remain research, not production requirements. Capture completeness is distinct from client receipt. The complete 68-case matrix passes with both fresh and pooled connections, with actual reuse verified. A historical development timeout remains unexplained; the successful reuse investigation does not establish its cause or a fix. Reproduce with `python3 pocs/transport-completeness/run.py`; see its README and RESULTS for scope, failures and remaining choices.

Independent verification commands for the initial round, from the repository root:

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
