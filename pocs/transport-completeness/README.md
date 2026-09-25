# Body and transport completeness research

Isolated R3 follow-up, not production SDK code. The original `../transport-lifecycle/` experiment and its 271-assertion baseline remain separate. This experiment uses a `WebApplication`, real loopback Kestrel HTTP/1.1, synthetic files and bodies, and no telemetry packages or listeners.

## Run

```sh
cd pocs/transport-completeness
python3 run.py
```

Requires SDK 10.0.301, CSharpier 1.3.0, and both Microsoft.NETCore.App and Microsoft.AspNetCore.App at 8.0.13, 9.0.2 and 10.0.9. `global.json` disables SDK roll-forward. The runner generates runtime configurations pinning **both** shared frameworks with roll-forward disabled, checks their loaded paths, performs locked restore/build/format checks, strips `OTEL_*`, `APITALLY_*`, `ASPNETCORE_*` and `DOTNET_ENVIRONMENT`, and disables CLI telemetry. Process-group deadlines kill timed-out commands. Logs and runtime configurations are ignored.

Each run creates and deletes its own temporary fixture directory. HTTP operations, raw socket operations, gates, callbacks, host start/stop and child processes have deadlines. No correctness assertion depends on a sleep. Client automatic decompression is disabled. The runner executes the full matrix twice per runtime: fresh connections for isolated outcomes, then normal pooling. Actual Kestrel connection IDs verify reuse across the first 12 healthy requests and are logged for every request. Fresh connections are not required for a passing run. The earlier unexplained timeout and focused reuse investigation remain documented in RESULTS.

## Experimental modes

| Mode | File operation | What its evidence means |
| --- | --- | --- |
| `Control` | Original Kestrel body feature | No body/lifetime observer. Test routes and completion notification only. |
| `ExperimentalSideRead` (A) | Preserve `inner.SendFileAsync(path, offset, count, token)`, then bounded read of a successful eligible range | Limited candidate only. Extra file content I/O and latency. A second read cannot establish which bytes the first read sent. |
| `ExperimentalSinglePass` (B) | Public `SendFileFallback.SendFileAsync(observingStream, ...)` for eligible ranges | Tees the file-copy bytes in the tested Kestrel paths, but **bypasses the inner SendFile feature**. Comparison only, not a selected SDK architecture. |

Both observation modes delegate ordinary Stream/Writer/Start/Complete operations. Mode B uses the same public helper as the pinned Kestrel implementation, not reflection or copied framework internals. The helper's XML documentation explicitly calls it infrastructure that should not be used by application code. This is an architectural caveat, not permission to silently adopt it in the SDK.

Ineligible or known-overcap files use the native feature, with zero observation-side file-content reads. An eligible side-read failure makes the entire mixed body unavailable, including any buffered prefix and subsequent suffix. Counts for that branch are the **declared/metadata-derived file range after a successful native call**, not observed network bytes or proof of the actual number copied. Mode B counts actual successful observing-stream writes on its helper branch. File metadata lookups are additional I/O even on branches without a side read.

### Concrete side-read counterexample

A 120-byte file is changed, deleted or truncated in the real `Response.OnStarting` callback. Kestrel's file helper has already opened and read its first chunk before the callback runs during the destination write:

- The native client receives the original 120 bytes in all three variants.
- Mode A captures **replacement** bytes when the file is replaced with an equally sized payload. Its normal completion evidence still passes. The test asserts this mismatch as a limitation, not a successful complete capture.
- Mode A omits capture when the file is deleted or its second read is partial.
- Mode B captures the same original bytes as the client in these fixtures, without a second file-content read.

No filesystem locks prevent the application from making these changes. This is a real application callback, distinct from the explicitly named side-read-failure injection.

## Body decision under test

At `OnCompleted`, the observer publishes an owned, fixed result, including copied failure evidence and the body buffer transferred without a second retained body allocation. Tests inspect this result, not live cancellation flags after completion.

The conservative buffered-body rule is:

1. Eligible, nonempty body, every counted byte retained, at most 50,000 bytes, and no unavailable segment.
2. No synchronously intercepted app abort, escaped downstream exception, canceled original entry token or canceled current token at the decision point.
3. No observed Stream write/flush, Writer advance/write/flush/completion, or body-feature Start/Complete/SendFile failure. Canceled/terminal flush results and a non-null writer completion exception also suppress buffered export.
4. An applicable Content-Length matches the operation count. Transfer-Encoding makes that check inapplicable. HEAD/204/304 omit body payload and ignore representation Content-Length for completeness.

This means **no observed server-side incompleteness**, not universal success and not proof of client receipt. A healthy application replacement of `RequestAborted` is diagnosed but is not itself an omission condition. The original entry token remains registered until the completion decision. `TokenReplaced` records an observed setter; `TokenChangedAtCompletion` separately includes Kestrel's own token substitution.

`OriginalPredicate` in the log is the older no-abort/no-escaped-error/length-match predicate applied to this experiment's counts and applicable-length semantics. It is a diagnostic comparison, not a rerun of the original observer. Its cancellation race is not asserted to fail on every run.

Stream bytes are counted after successful writes. Writer memory is copied into bounded tentative storage **before** `Advance` returns its lease; the count and staged bytes are committed only if inner `Advance` succeeds. Direct Writer.WriteAsync delegates the native operation and preserves its FlushResult; canceled/terminal results are recorded rather than treated as normal acceptance. Request Reader segments are staged before `AdvanceTo` and committed afterward. Borrowed streams are not disposed. Public features are restored in `finally`. Unflushed accepted writer bytes are already observed; compression footer writes finish inside the observer before restoration.

A capture buffer allocates at most 50,000 bytes per direction. `PeakRetained` is the highest number of staged response bytes, including tentative writer bytes; allocated array capacity can be 50,000 even for a small body. Kestrel's buffers and the harness's client/decompression buffers are not observer capture storage. An established oversized sentinel survives abort; a bodyless response still omits payload.

### Capture completeness versus delivery

The requirement is to avoid an incomplete **captured body**. A hidden network flush failure does not, by itself, prove that the capture is partial when all application bytes were observed. Several conservative failure cases here omit a complete seven-byte application prefix even though the client also receives those seven bytes. These demonstrate evidence handling, not an invented missing-byte counterexample.

Material counterexamples are concrete: the side-read replacement mismatch, an unobserved file segment, or a promised Content-Length not supplied. Public Kestrel finalization does not expose every internal failure as a positive/negative completion result; that source constraint is not independently a new release blocker based on missing TCP success certification.

## Coverage and scope

`Program.cs` defines the exact 68-case table, each run under all three capture modes with fresh and pooled connections. `RESULTS.md` records runtime outcomes and failed development hypotheses.

- Stream sync/async writes, Writer memory/Advance and WriteAsync, unflushed writer, normal writer/feature Complete and Start.
- Real prefix-before-handler-release tests, both ordinary and gzip-decoded; encoded gzip capture includes the final trailer.
- Empty/ineligible bodies, 49,999/50,000/50,001 boundaries, complete handled 500, healthy replacement token and bodyless responses.
- App abort, oversized abort, client reset after prefix, short Content-Length, failed oversized Stream write/Writer Advance, swallowed write error, canceled Stream write/flush, canceled Writer flush and native canceled FlushResult, explicit completion errors and replacement token.
- Direct full/offset/count/remainder/mixed files, cap-crossing mixed body, Minimal and MVC physical files/ranges, 416, HEAD, one stream file result, gzip file and gzip-suppressed range.
- Missing file, invalid offset/count, canceled native file operation, injected side-read error, mixed-body side-read error, and actual OnStarting replacement/deletion/truncation.
- Request Content-Length completion without an extra EOF read, chunked Stream EOF and BodyReader consumption, partial/unread bodies and reset during upload. Observer adds no reads of unconsumed request data.

Comparisons include body bytes, status, Content-Type/Length/Encoding/Range, Accept-Ranges, Vary, Content-Disposition, Transfer-Encoding, and application exception types. Dynamic dates are ignored. Deterministic client outcomes compare exactly; synchronous writer aborts compare transport-failure category because FIN and RST produce different IOException subclasses. App abort races header/body receipt, so only its failure outcome is required. Gates ensure the writer-error prefix reaches the client before completion is invoked.

The decoded-size check runs **in the harness after actual completion** using bounded decompression. It is not server-thread work or a complete SDK decompression/redaction pipeline. Only synthetic text/plain and application/json eligibility is modeled, not a production media-type policy.

HTTPS, HTTP/2/3, other servers, arbitrary custom/replaced features, middleware permutations, concurrency stress and general file mutation during multi-chunk transfer remain unqualified. The rule implements a Transfer-Encoding exemption, but conflicting explicit Content-Length plus Transfer-Encoding headers are not a runtime fixture. IsCompleted terminal-flush handling is implemented; the real terminal-result fixture exercises IsCanceled. A native internally swallowed network-flush exception is source-backed, not deterministically injected into real Kestrel here. No OTel, lifecycle redesign, SDK callbacks, privacy pipeline, OTLP or production architecture decision is included.

## Pinned source constraints

Source identities supplied for this investigation:

| ASP.NET runtime | Repository commit |
| --- | --- |
| 8.0.13 | `009e1ccafde4086ea52999e878f6e7aa5a7c4ccf` |
| 9.0.2 | `704f7cb1d2cea33afb00c2097731216f121c2c73` |
| 10.0.9 | `d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2` |

Representative exact source links:

- [HttpProtocol cancellation, request completion, Advance and Complete](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs): OnCompleted runs after success and error finalization paths; cancellation scheduling and token overrides are distinct from synchronous app Abort.
- [Native feature SendFile](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.FeatureCollection.cs#L337-L340) and [public SendFileFallback](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Http/src/SendFileFallback.cs): native destination is Kestrel's ResponseBody, not the wrapping feature's Stream; helper uses one file open and a 16 KiB copy buffer.
- [StreamResponseBodyFeature](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Http/src/StreamResponseBodyFeature.cs#L86-L111): SendFile starts/flushes before file validation/open. Replacing the feature with this adapter is not automatically transparent for failures and is not the selected comparison.
- [TimingPipeFlusher](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Infrastructure/PipeWriterHelpers/TimingPipeFlusher.cs#L77-L113): internal flush errors can be caught without becoming an observer-visible thrown exception. This establishes an observability limit, not missing captured bytes.
- [SendFileResponseExtensions](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Http.Extensions/src/SendFileResponseExtensions.cs#L112-L123): extension-level cancellation suppression differs from direct feature calls; file-error fixtures deliberately call the feature directly.
- [HttpResponsePipeWriter](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Servers/Kestrel/Core/src/Internal/Http/HttpResponsePipeWriter.cs#L40-L44): CompleteAsync(exception) calls Complete() without forwarding the argument in all three pinned revisions. The observer records the argument without changing native behavior.
