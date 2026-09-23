# Private activity snapshots and stock generic batching POC

Experimental evidence only. All implementation and checks are confined to this directory. No production SDK API, logging implementation, protobuf mapping, host integration, or network exporter is introduced. Activity and provider APIs are public; assembly informational versions use public reflection only.

## Run

Requires SDK 10 and installed .NET 8, 9, and 10 runtimes. From this directory:

```sh
bash run.sh
```

The script performs a locked restore, Release build, transitive package listing, and these three executions:

```sh
dotnet run --project ActivitySnapshots.csproj --no-build --no-restore -c Release -f net8.0
dotnet run --project ActivitySnapshots.csproj --no-build --no-restore -c Release -f net9.0
dotnet run --project ActivitySnapshots.csproj --no-build --no-restore -c Release -f net10.0
```

Exact evidence-capture command executed:

```sh
cd /Users/simon.gurcke/Repos/apitally/apitally-dotnet/pocs/activity-snapshots
set -o pipefail
bash run.sh 2>&1 | tee results.txt
```

Exit status: 0. Full output, including package versions, is in [results.txt](results.txt). Build: zero warnings and errors. Assertions fail the process with exit status 1. Tests use synthetic values and in-memory exporters. Restore/research require package/source access; running the built experiment sends no telemetry. All test gates have finite waits; workers are shut down and joined, including a deliberately unsafe Dispose-only probe. No server or external collector is started.

## Exact tested versions

Executed on macOS Arm64 with SDK **10.0.301**, compiling all three targets with that SDK, not three separate compilers.

| Target | Actual runtime | OpenTelemetry / Api / ProviderBuilderExtensions | Microsoft.Extensions packages | Loaded DiagnosticSource | Result |
| --- | --- | --- | --- | --- | --- |
| net8.0 | 8.0.13 | 1.19.0 | 8.0.0 | 10.0.0 from NuGet | 109 assertions pass |
| net9.0 | 9.0.2 | 1.19.0 | 9.0.0 | 10.0.0 from NuGet | 109 assertions pass |
| net10.0 | 10.0.9 | 1.19.0 | 10.0.0 | 10.0.9 from runtime | 109 assertions pass |

`packages.lock.json` pins all transitive dependencies and hashes. OpenTelemetry is an exact `[1.19.0]` reference. Its loaded informational version is `1.19.0+dac1573ece52e8c275c3db5282bc57e3d5eff5cf`. These are experiment versions, **not a selected production dependency floor**. In particular, net8/net9 are exercising DiagnosticSource 10 rather than their original in-box Activity implementation.

The first restore of `[1.19.1]` failed with NU1102: nearest version `1.19.1-rc.1`. The NuGet flat-container index confirmed stable 1.19.0 was available despite GitHub's `core-1.19.1` release/tag. The experiment therefore uses stable 1.19.0 and source from its actual assembly commit. No claim of testing 1.19.1 is made.

## Verified results

### Activity/provider evidence

`SnapshotProcessor.OnEnd` copies the following from an actual provider-observed SERVER Activity using a remote parent:

- Trace, span, and parent IDs; display name and kind; source name/version.
- Start time and 125 ms duration; trace state/flags; status and description.
- Activity tags and resource attributes.
- Event name, timestamp, and tags; link context including remote flag, trace state/flags, IDs, and tags.

`SpanSnapshot` is internal experimental export data. It retains no Activity, ActivitySource, provider, or HttpContext reference. It copies dictionaries and supported mutable arrays. The checks mutate string[], long[], byte[], bool[], and double[] values in snapshot tags, event/link tags, and resources. A simultaneously active stock user Activity exporter then reads the originals and confirms they are unchanged. Both processor registration orders pass; exact counts are one private and one user export per run.

The private snapshot receives a complete **50,000-byte** synthetic JSON body, private authorization header, late route, and late Sentry-like ID. None appears on the user's Activity. Private resource instance/environment overrides leave the user resource intact. On the batch worker, the experiment applies synthetic query/header redaction, verifies the callback view before attaching a body attribute, hashes/parses/redacts/serializes the body, and releases raw bytes. All 49,963 padding characters survive. This is ownership and threading evidence, not a complete redaction implementation or a complete-body transport test.

Worker creation occurs while the synthetic request AsyncLocal is set, with `ExecutionContext.SuppressFlow()` around construction. CPU/body work runs on the stock worker, not the request thread, with no request AsyncLocal or Activity.Current. This explicit construction boundary matters for first-request activation. The test models request-serving execution with a synchronous thread/AsyncLocal, not ASP.NET middleware. Copying at OnEnd still runs synchronously on the activity-ending thread; the deferred work is body processing/redaction.

### Sampling evidence

An actual provider sampler produces RecordAndSample, RecordOnly, and Drop decisions:

| Path | Observed exports |
| --- | --- |
| Provider OnEnd callbacks | Recorded and RecordOnly: 2; Drop: 0 |
| Stock BatchActivityExportProcessor | Recorded only: 1 |
| Stock generic batch with an unfiltered bridge | Recorded and RecordOnly: 2 |
| Stock generic batch with explicit Activity.Recorded guard | Recorded only: 1 |

The RecordOnly snapshot retains flags=None. Generic batching has no span sampling semantics. An adapter must explicitly preserve the desired recorded-span coverage instead of assuming stock specialized behavior carries over.

### Batch lifecycle evidence and negative results

All parameters are explicit: normally queue=32, batch=8, delay=1000 ms, exporter timeout=1000 ms. Privacy tests use batch=1. A blocked-export probe uses queue=2 and batch=1.

- A sub-batch exports automatically after approximately one second on all three runtimes; exact observed delays are in `results.txt`. ForceFlush triggers queued export.
- With an exporter held at a deterministic gate, two queued items survive and a third overflow item drops. Earliest queued items are retained.
- **ForceFlush is not an export-completion barrier in this version.** After the exporter dequeues its final item but remains blocked, ForceFlush(100) returns true while the exporter has produced no output. With more items still queued behind it, ForceFlush(100) returns false. Source confirms the wait compares queue removed/added counts, and batch enumeration increments removed count before processing. Privacy assertions therefore wait on explicit exporter completion signals, not just ForceFlush.
- **The synchronous exporter timeout does not cancel Export.** A configured 1000 ms timeout leaves the gated exporter blocked for more than 1100 ms; releasing the gate permits completion.
- Successful Shutdown(5000) drains and joins the worker, calls exporter shutdown once, and a second Shutdown returns false. Dispose afterwards disposes the exporter once.
- **Bare generic intake remains callable after Shutdown.** OnEnd queues a new snapshot, no export occurs, and subsequent ForceFlush returns false. The inherited generic OnEnd does not invoke the internal shutdown-entry guard used by specialized processors. This is not a ready-made production lifecycle adapter.
- **Dispose alone does not call Shutdown, drain, or join.** The probe disposes during a blocked export with a second item queued. Dispose returns while the worker is alive and disposes the exporter; after releasing the gate, only the first item has exported. The test manually joins that worker. Provider disposal does invoke processor Shutdown first; a standalone generic processor owner must do so explicitly.

Consequently, stock public generic batching is feasible intake, but private pipeline lifecycle ownership and completed-spool-write coordination remain necessary. A successful ForceFlush alone is insufficient evidence that it is safe to close/send a spool file. This POC does not design those production mechanisms.

### Coordination model evidence only

`CoordinationModel.cs` is a small sequential model separate from the Activity/provider experiment. Logs and spans are names, not OTel log records. It tests both SERVER-first and transport-first completion, crossed with keep/drop:

- Hold detail until both completions; retain earliest two descendants and two logs (small stand-ins for the shared bounds, with SERVER stored separately).
- Release descendants, SERVER, then logs once; repeated completion signals cannot release again or rerun the response decision.
- After release, late descendants/logs remain eligible.
- Drop clears buffers/raw bytes and rejects subsequent descendants/logs.

This does not prove concurrent coordination, host/request association, streaming completeness, actual ASP.NET callback ordering, response-sampling callback content, global limits, or unfinished-request shutdown behavior. The two completion order tests are model evidence only.

## Snapshot and callback constraints

The public Activity API is not a faithful detached clone facility: creating another Activity generates another identity; ID/source/kind are not freely assignable into a clone. This POC does not fabricate a replacement Activity. The specialized processor requires Activity; the public generic processor accepts the owned record directly through a trivial derived class.

OnEnd is an appropriate copy boundary for data available then. Later request measurements must update only held owned data, before enqueue/ownership transfer. Another user processor that enriches the Activity after this processor's OnEnd can supply information too late for this copy. Arbitrary concurrent application mutation during copying is not solved here.

The copy policy here accepts null, immutable scalar strings/bool/numbers, and one-dimensional arrays of those scalar types. Unsupported mutable values such as StringBuilder are deliberately omitted rather than retained or stringified. It is not an arbitrary deep-clone facility or final attribute normalization policy. Scope tags/schema URLs, hierarchical IDs, dropped counts, and broader value mappings are not proven.

A later body-mask callback can receive data representing the ended export span, including redacted headers/query, final custom attributes, and no body attributes yet. It cannot rely on a live HttpContext, Activity identity as a clone, request thread, or ambient request context. A read-only dictionary alone would not make mutable array values read-only; a future public view must address that ownership boundary. The inline callback-shaped block here is a test, not a proposed public signature. Request/response sampling and body masking callback types remain open.

Remaining decisions include value normalization and callback immutability, copy cost/bounds, concurrent request completion, integration ordering, activation/lifecycle guarding, export-completion barriers, actual shutdown budget behavior for slow callbacks, and production package floors. Logging ownership and OTLP conversion remain separate POCs.

## Sources inspected

Requirements: local `docs/design.md` sections 5-7, 10, 13, 18 and sibling `cloud/docs/sdks/design.md` request buffering, capture, sampling, export, callback, and resource rules. Context7 library `/open-telemetry/opentelemetry-dotnet` supplied current processor extension guidance. GitHub source was fetched with `gh api`; actual 1.19.0 source:

- [BatchExportProcessor: public generic queue, worker, lifecycle](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/BatchExportProcessor.cs)
- [BaseExportProcessor: inherited unguarded OnEnd](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/BaseExportProcessor.cs)
- [BatchActivityExportProcessor: Recorded and shutdown guards](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/Processor/BatchActivityExportProcessor.cs)
- [Batch enumeration removes before exporter processing](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Batch.cs)
- [Thread worker: flush counts, shutdown join, disposal](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Internal/BatchExportThreadWorker.cs)
- [Worker: synchronous Export and stored timeout](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Internal/BatchExportWorker.cs)
- [Provider: recording callback gate and shutdown before disposal](https://github.com/open-telemetry/opentelemetry-dotnet/blob/dac1573ece52e8c275c3db5282bc57e3d5eff5cf/src/OpenTelemetry/Trace/TracerProviderSdk.cs)
