# Recorded results

Executed locally on macOS arm64, 2026-09-25. Experimental R2 evidence only; production design documents were not changed.

## Commands

| Command | Result |
| --- | --- |
| `dotnet --version` | Exactly 10.0.301 |
| `dotnet tool restore`; `dotnet csharpier --version` | Exactly 1.3.0 |
| `dotnet csharpier check .` | Passed, 6 files |
| `dotnet restore RequestAssociation.csproj --locked-mode` | Passed |
| `dotnet build RequestAssociation.csproj --no-restore --nologo` | All targets passed, zero warnings/errors |
| `python3 run.py` | Full bounded matrix passed; both shared frameworks pinned separately in runtimeconfig |

| Target | Microsoft.NETCore.App | Microsoft.AspNetCore.App | Result |
| --- | --- | --- | --- |
| net8.0 | 8.0.13 | 8.0.13 | PASS, 1533 assertions |
| net9.0 | 9.0.2 | 9.0.2 | PASS, 1533 assertions |
| net10.0 | 10.0.9 | 10.0.9 | PASS, 1533 assertions |

Each runtime ran all nine rows below. The full interaction suite is included in the candidate-first/native/sampled row.

| Registration | User sampling | Processing | Result on all runtimes |
| --- | --- | --- | --- |
| Fallback | Candidate AlwaysOn | Native | PASS |
| User builder first | Custom RecordAndSample | Native | PASS |
| Candidate first | Custom RecordAndSample | Native, full suite | PASS |
| Existing instance in DI | Custom RecordAndSample | Native | PASS |
| Candidate first | Custom RecordAndSample | TEST-ONLY reverse | PASS |
| Candidate first | Custom RecordAndSample | TEST-ONLY race | PASS |
| Candidate first | AlwaysOff | Native | PASS |
| Candidate first | Custom RecordOnly | Native | PASS |
| Candidate first | ParentBased, remote unsampled | Native | PASS |

## Observations

- All sampled first requests completed before ApplicationStarted. Native callback order was transport then SERVER on all three runtimes. Reverse handoff processed SERVER then transport. Race processing varied between both orders across executions. Every retained release observed both processed completions, exactly one response decision and exactly one initial release.
- Normal retained requests exported six spans (five descendants and SERVER) and five native logs. Descendants were the before-middleware child, manual child, nested child, explicit-ID-parent child and instrumented HttpClient span. Exact recorded IDs and structural parentage matched. Actual exporter order was ended descendants in arrival order, SERVER, then buffered logs in arrival order. All exported logs followed SERVER; only genuine fixture late spans were excluded from the initial-descendant-before-SERVER check and were separately required to follow SERVER. The same-host sink's separate SERVER was deliberately detail-dropped privately and remained in the application output.
- Private method/path/user-agent state existed before ASP.NET HTTP Activity tags. Enrichment-time child/log association succeeded before middleware. Explicit ActivityContext children had null Activity.Parent and still associated. Unrelated work sharing the trace ID did not associate.
- Concurrent, same-connection HTTP/1.1 and shared-remote-trace requests retained distinct SERVER/helper/log identities. No duplicate/missing accepted spans or logs were observed. Candidate payload buffers/fields were empty after final release/drop; test-only observer/exporter outputs intentionally remained available for assertions.
- Request-stage drop survived a response keep. Response keep/abstain retained detail and response drop retained none. The decision's owned SERVER and actual exported SERVER contained final method GET, status 202, route and response body size 2, with unknown request size absent. Decision observations also contained consumer/custom values and first-exception fields. The independent application SERVER exporter had no transport-injected body-size attributes. Duration and final content-length inputs were available even for AlwaysOff, RecordOnly and remote-unsampled requests, which exported no candidate detail.
- A child actually stayed alive past both native completions. Its later log/end followed cached keep/drop without rerunning the decision. Cumulative caps rejected the later span/log after five descendants and six logs had used their slots; SERVER retained its separate slot.
- The explicit POC-only cleanup sweep kept metadata before 60 seconds and removed all completed-request IDs at 60 seconds, including a still-running child. Later child/log detail was ignored privately but remained available to the app exporter. This duration and eviction behavior are not a selected production contract.
- Explicit final cutoff while a real request was gated released none of its buffered/later detail and cleared associations. Transport and helper/first-exception inputs still completed independently. This is not a spool/delivery/shutdown-budget result.
- Actual native masking changed the private Body and secret attribute. The independent application log provider was registered after the candidate provider and still received original messages, attributes and IDs without private SERVER linkage. Application Activities retained original data and user enrichment, not private helper/exception attributes.
- Existing-instance host disposal disabled candidate capture without disposing the external provider. A later manual span was still sampled and exported by the application's provider. Batch exporter completion was observed separately from ForceFlush, followed by bounded worker shutdown/join before disposal.

## Development failures and limits

The initial build failed with CS1061 on `TracerProvider.GetResource`: the extension's `OpenTelemetry` namespace import was missing. The import was corrected; no assertion was removed to resolve it. Initial complete runtime matrices passed 1248 and then 1349 assertions. Those checks missed two ordinary-request contract violations identified by parent review:

1. The mixed pending list emitted buffered logs before SERVER. New actual-export ordering assertions were added before changing candidate code. With both shared frameworks pinned to 8.0.13, `python3 run.py` failed on the first normal request with `ASSERT: actual exported logs follow SERVER: first`. The raw failure is preserved in `results/negative-order-net8.0.log`.
2. Transport response size was present only in the observation, not merged into the owned SERVER. New decision/exported SERVER field checks were then added while candidate code was still unchanged. The pinned 8.0.13 run failed with `ASSERT: decision SERVER includes final transport response size: first`. This second raw failure is preserved in `results/negative-transport-net8.0.log`.

The candidate was then corrected to submit descendants, SERVER and logs separately, and to merge final stable transport fields into the owned SERVER before deciding/releasing. Unknown sizes and absent route are removed from that copy. Late direct submission and cumulative caps are unchanged. All existing and new checks passed the complete corrected matrix at 1533 assertions per runtime, including native/reverse/race and application-output isolation.

The parent independently inspected the corrected implementation and assertions, checked both preserved negative logs, and reran `python3 pocs/request-association/run.py` successfully on 2026-09-25 (`bg_3529314875767961`): 1533 assertions on each exact runtime, locked restore, CSharpier 1.3.0 and zero build warnings/errors. The earlier independent run `bg_586e48a9af4fde5e` passed the pre-correction 1349-assertion matrix and is not evidence for the fixes.

A separate read-only reviewer found no additional material integration issue. Its narrow observation that map/payload counters do not prove garbage collection is retained as an evidence boundary: cleanup removes the candidate registry entries, while test-only output observers deliberately retain copies. No GC or memory-use guarantee is inferred.

Fixture limits and unresolved production choices are detailed in README.md: named-category primitive-only logging, content-length rather than body/wire capture, two-field exception representation, limited primitive snapshot shape rather than full public SpanSnapshot validation, path-selecting response policy, test-only completion handoff and output ledgers, manually swept POC-only retention, and unresolved production cleanup policy. No independent metrics/error exporter, abort matrix, spool, delivery or R4 shutdown-budget behavior is claimed. No reflection, additional sampling listener, supplied span/request map, or manufactured SERVER completion was used.

Reproducible raw command output is in ignored `results/`; `run.py` regenerates it. No generated binaries or runtimeconfig outputs are tracked.
