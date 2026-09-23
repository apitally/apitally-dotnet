# Experimental OTLP encoding and metrics POC

Approved POC only. No production SDK integration or dependency-floor decision.
All application HTTP traffic is synthetic and goes to an ephemeral loopback TCP
listener. The program never reads `APITALLY_WRITE_TOKEN` and uses a fixed fake
credential. Providers, meters, service containers, HTTP clients and listeners
are disposed. The runner disables shared compilation and MSBuild node reuse.

## Reproduce

Requirements: Python 3, installed .NET SDKs/runtimes below, and NuGet access for
the first restore. `gh` authentication is needed only to refresh vendored schemas.

```sh
cd /Users/simon.gurcke/Repos/apitally/apitally-dotnet/pocs/encoding-metrics
python3 run-matrix.py
# Optional: refresh exactly the same eight official schemas and license.
python3 fetch-proto.py
```

The executed runner verifies schema checksums, performs locked restore, builds
all target frameworks with default SDK 10.0.301, runs each target, then rebuilds
and runs net8.0/net9.0 with their own installed SDKs. It uses direct SDK DLL
invocation for those extra builds, without modifying global.json or branches.
Every subprocess has a 180-second timeout. The HTTP probe has a 20-second
lifetime bound and 5-second per-request timeout.

For one target after restore:

```sh
export DOTNET_CLI_HOME="$PWD/.local/dotnet"
export NUGET_PACKAGES="$PWD/.local/packages"
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project EncodingMetrics.csproj -f net8.0 --no-restore
```

Build products, generated code, NuGet/CLI caches and compressed payloads stay
under this directory (`bin`, `obj`, `.local`, `artifacts`). Small execution logs
are retained in `results`; the other generated directories are ignored.

## Exact versions and execution

Executed on macOS Arm64:

| Build SDK | Target | Actual runtime | Result |
| --- | --- | --- | --- |
| 10.0.301 | net8.0 | 8.0.13 | PASS |
| 10.0.301 | net9.0 | 9.0.2 | PASS |
| 10.0.301 | net10.0 | 10.0.9 | PASS |
| 8.0.406 | net8.0 | 8.0.13 | PASS |
| 9.0.200 | net9.0 | 9.0.2 | PASS |

All final matrix builds: zero warnings and zero errors. Evidence includes
`results/default-build.log`, `results/default-net*.log`,
`results/sdk-*-build.log`, `results/sdk-*-run.log`, and
`results/gzip-verification.log`.

- OpenTelemetry core/API/provider builder: **1.19.0**
- OpenTelemetry.Instrumentation.Http: **1.19.0**
- Google.Protobuf: **3.33.5**
- Grpc.Tools: **2.76.0**, bundled protoc reports **31.1**
- OTLP schema: **v1.11.0**, commit
  `790608c4d51e6ffc12210b541e8514cbed9e91a4`
- DiagnosticSource: transitive **10.0.0** on net8/net9; framework on net10
- Full dependency resolution and package hashes: `packages.lock.json`

Initial `dotnet restore` with stable OpenTelemetry 1.19.1 failed with NU1102:
"Nearest version: 1.19.1-rc.1". The successful experiments use 1.19.0; findings
must not be described as 1.19.1 package validation. GitHub has a 1.19.1 tag,
which was useful for API research but does not establish NuGet availability.

## Verified encoding and file behavior

`EncodingProbe.cs` builds official generated requests for all three signals.
`MetricEncoding.cs` maps real SDK metric points synchronously inside
`BaseExporter<Metric>.Export`, then serializes before returning. It retains
neither the SDK batch nor reusable metric-point/bucket storage. A saved first
collection remains unchanged after later collection.

- Trace IDs, parent IDs, times, flags, status, events, links, header arrays,
  complete 50,000-byte non-UTF8 binary body and 50,000-character string body
  survive generated-parser round trips.
- Logs use native `event_name`. Startup body is a JSON-string AnyValue;
  server-error body is a structured key/value object, including UInt32 maximum
  count encoded as int64. Internal events have empty trace/span context.
  Recursive AnyValue strings, booleans, integers, doubles, arrays, objects and
  unset values also round-trip. Fixtures are encoder samples, not a full
  startup-event implementation or an ILogger adapter.
- Exponential histogram temporality, count, sum, scale, offset, bucket counts,
  zero count/threshold, min/max presence, attributes and timestamps round-trip.
  Request sizes use `By`; duration uses `s`; attribute tuples match exactly.
- For each signal, two unframed requests written to **one continuously open
  GZipStream** decode into one merged request with two resource entries.
  Python zlib independently verifies one gzip member and no trailing members
  for all 21 final files.
- A 32-record trace chunk is **5,312,342 encoded bytes**, exceeding the
  4,000,000-byte file cap. Exact `CalculateSize()` checks recursively split
  that chunk before append; actual serialized length is also checked. Sixty-five
  records survive across four files; the largest is 2,822,514 uncompressed
  bytes. Every tested stored file is also below the 4 MiB wire cap.
- A single indivisible request larger than the cap is rejected explicitly.
  This proves the guard, not a selected production data-loss policy. The splitter
  materializes candidate protobuf requests and is not a memory benchmark.
- Four physical local POSTs read and replay the same persisted gzip file.
  All received bodies exactly match disk, including both simulated replay sends.
  Headers use fake authorization, `Apitally-Env: poc`, protobuf and gzip.
  Byte identity is established for replaying the persisted file, not regenerating
  it. Inputs include per-run resource IDs and timestamps, so the POC does not
  isolate runtime-dependent compression differences. Retries reuse stored bytes.

The file test directly finishes each synchronous append before closing its
stream and opens the stored file for HTTP only afterward. It does not build a
spool or use batch `ForceFlush` as exporter-completion evidence. The sibling
snapshot POC's finding that ForceFlush may return before a dequeued export
finishes is an integration constraint: batch intake will need explicit export
completion/serialization with close/send. This POC does not establish that
coordination for production batching.

## Verified metrics and ownership

`MetricsProbe.cs` constructs official private MeterProviders directly, using
`BaseExportingMetricReader` with Delta temporality. There is no periodic reader
or global/application DI registration. A public histogram view selects
`Base2ExponentialBucketHistogramConfiguration`; other instrument types retain
their semantics. Exemplars are explicitly disabled for these probes.

- Independent duration deltas are count/sum **3/0.625**, then **1/0.25**, with
  contiguous interval timestamps; an inactive collection repeats no histogram
  values. Exact size counts/sums and shared dimensions are asserted.
- Public `MaxScale=3` works; untouched exponential-view defaults are
  `MaxScale=20`, `MaxSize=160`. Duration values from 1 ns to one year and byte
  values from 1 to Int64.MaxValue adapt to scale 1. There is no minimum-scale
  clamp in this experiment; these ranges do not prove every arbitrary double
  will meet ingestion's [-2,20] bound.
- A deliberately low **test-only CardinalityLimit=2** retains a and b; c goes
  to a separate `otel.metric.overflow=true` point. The overflow point has no
  method/route/status/consumer dimensions and is unsuitable for accepted
  request-histogram ingestion. It is not equivalent to preserved request counts.
- Late a is retained in the next delta; new d still overflows before inactive
  capacity is reclaimed. After an idle collection, e and f occupy reclaimed
  capacity with only their new values. This verifies functional capacity reuse,
  not a concurrency stress or heap-retention bound. No product limit is chosen.
- **Separate same-name Meter/Provider objects do not isolate measurements.**
  Two `apitally` meters record 1 and 10; both unrestricted private providers and
  a name-subscribed user provider observe 11. Their collections remain separate.
- A public `MeterOptions.Scope` owner token plus `AddView` returning
  `MetricStreamConfiguration.Drop` for foreign scopes isolates private outputs
  to 1 and 10. The unfiltered user provider still observes 11.
- Two DI containers' `IMeterFactory` instances become their meters' public
  scopes. Applying the same explicit view filter works. Disposing A's provider
  and container leaves B active: B's next delta is 20, while the unaffected
  user's cumulative result is 31. **Factory creation alone is insufficient**
  for an OTel provider subscribing by name.
- These are meter association/filtering results, not Activity sampling results
  or full web-host lifecycle tests. They do not contradict the provider POC's
  process-wide ActivityListener sampling constraint. A user's unrestricted
  meter subscription can still observe Apitally instruments; scope filtering
  is not process-level confidentiality.
- Two traffic-free collections report normalized CPU, RSS-equivalent memory
  and uptime. CPU/memory observations are sampled together before collection;
  wire timestamp skew was tens of microseconds, below one second. Disabling CPU
  and memory still emits uptime alone. Process-gauge ownership across hosts and
  process-wide resource/limit policies remain design decisions.

## Suppression and explicit remaining scope

`HttpProbe.cs` installs stock HttpClient instrumentation and a user-style
exporter. Local POSTs before/during/during/after
`SuppressInstrumentationScope.Begin()` produce **1/0/0/1** exported client
activities. Collection is also explicitly invoked under suppression.

A synthetic HTTP_PROXY URI is read once into an explicit `WebProxy` bound to a
SocketsHttpHandler. Changing the variable leaves that object bound to the old
URI; the original variable is restored. This is a configuration probe only.
Actual loopback sends use `UseProxy=false`. Physical proxying, HTTPS/CONNECT,
NO_PROXY matching, case precedence, proxy credentials and platform-specific
proxy behavior remain follow-up work.

Also untested: retry/error classification, stale connections, disk durability
and failures, retention/eviction/orphan cleanup, send scheduling, async spool
concurrency, shutdown-drain coordination, real backend acceptance, Windows/Linux,
AOT, production allocation bounds, concurrent metric reclamation, and broader
metric types/exemplars. The manual reader timeout is not proof of cancellation
of arbitrary slow exporter code. There is no complete spool/retry service,
background export worker, production snapshot mapper or SDK integration here.

## Research and provenance

Contracts read before implementation: `../../docs/design.md` sections 2, 10,
11 and 18; sibling `cloud/docs/sdks/spec.md` and `design.md`, especially resource,
body, metric, internal-log, transport and private-provider requirements.

Context7 library `/open-telemetry/opentelemetry-dotnet` supplied current public
view, cardinality, exporter and suppression guidance. Pinned GitHub source was
retrieved with `gh api` to check API visibility; there is no private reflection.

- [Executed core source](https://github.com/open-telemetry/opentelemetry-dotnet/tree/dac1573ece52e8c275c3db5282bc57e3d5eff5cf)
- [Executed Http instrumentation source](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/295327c4352523fbc4f4c8b182f09ae8794cc65e)
- [Researched BaseExportingMetricReader](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Metrics/Reader/BaseExportingMetricReader.cs)
- [Researched exponential configuration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/5fbeba3a3d8bbd4f4235170ddeb6329fe0b8b86e/src/OpenTelemetry/Metrics/View/Base2ExponentialBucketHistogramConfiguration.cs)
- [Official OTLP schemas](https://github.com/open-telemetry/opentelemetry-proto/tree/790608c4d51e6ffc12210b541e8514cbed9e91a4)

`vendor/README.md`, `vendor/LICENSE` and `vendor/SHA256SUMS` preserve source and
license provenance, including the reproducibly generated message classes.
