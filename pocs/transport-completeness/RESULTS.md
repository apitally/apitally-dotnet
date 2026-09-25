# Results

## Environment and completed checks

Observed locally on 2026-09-25, macOS 26.6.2 arm64. The implementation agent and independent parent initially passed 1,771 assertions per runtime with fresh connections. Following the connection-reuse investigation, the parent added both fresh and pooled execution plus actual connection-ID assertions to the checked-in runner. The final independently executed matrix passed:

| Target | NETCore.App | AspNetCore.App | Cases x capture modes x connection modes | Assertions per connection mode |
| --- | --- | --- | --- | --- |
| net8.0 | 8.0.13 | 8.0.13 | 68 x 3 x 2 | 1,782 |
| net9.0 | 9.0.2 | 9.0.2 | 68 x 3 x 2 | 1,782 |
| net10.0 | 10.0.9 | 10.0.9 | 68 x 3 x 2 | 1,782 |

SDK 10.0.301, CSharpier 1.3.0 check, locked restore and all-target build passed. There are no application PackageReferences. The final table totals 1,224 real requests across all three runtimes. Each runtime executes 3,564 assertions across its two connection modes. Hosts stopped and synthetic temporary files were removed. A separate read-only source/assertion reviewer found no additional material issue within the documented fixture scope. Assertion counts establish only these fixtures, not broader compatibility.

The runner stores detailed ignored logs in `results/net8.0-fresh.log`, `results/net8.0-pooled.log` and matching net9/net10 files, plus separate restore/build/format logs. Each case prints the actual connection ID, client outcome, capture/client comparison, completion-time evidence, native/helper file branch, operation counts, retained-byte peak and side-read byte count. `captureClient=MISMATCH` is an expected **limitation**, not capture success.

## Observed runtime results

All three pinned runtime combinations produced the following substantive outcomes:

| Fixture group | Client/control comparison | Capture result |
| --- | --- | --- |
| Stream sync/async, Writer memory/Advance and WriteAsync | Same bytes/status/selected headers | Full bounded capture |
| Unflushed Writer and normal explicit Writer/feature completion | Native finalization delivers bytes | Full capture, no missed unflushed bytes |
| Plain and gzip streaming | Client receives decoded prefix while handler is gated | No full-response buffering; gzip capture includes trailer |
| Gzip 2,000-byte payload | Successful encoded response | Encoded capture decodes exactly |
| Gzip 50,001-byte decoded payload | Successful compressed response under encoded cap | Harness-only bounded decoded check rejects decoded size; not SDK processing |
| Empty/ineligible, HEAD, 204, 304, file HEAD, range 416 | Native status/body/header behavior | No captured payload |
| 49,999 / 50,000 / 50,001 | Native bytes unchanged | Buffered / buffered / oversized sentinel |
| Mixed prefix + 49,990-byte file + suffix | All 50,004 bytes delivered | Oversized; largest staged prefix/file total 49,997 |
| Handled error | Full seven-byte 500 response | Captured normally |
| Healthy RequestAborted replacement | Normal full response | Captured; replacement diagnosed without blanket omission |
| App Abort and raw client reset | Incomplete HTTP transfer or explicit reset | Omitted; already-established oversized sentinel retained |
| Short Content-Length | Client gets seven bytes, expected 17, HTTP body read fails | Omitted via applicable-length mismatch |
| Failed oversized Stream write/Writer Advance | Same InvalidOperationException, handled 500/empty response | Accepted count remains zero; failed Advance tentatively stages eight bytes but exports none |
| Swallowed second Stream write | Native first seven bytes form full declared response; second write throws | Explicit failure retained, buffered export conservatively omitted |
| Canceled write/Stream flush/Writer flush | Same OperationCanceledException and unchanged prefix | Failure retained, buffered export omitted |
| CancelPendingFlush | Native FlushResult(IsCanceled=true, IsCompleted=false) preserved | Flag recorded, buffered export omitted |
| Start failure from intentional OnStarting exception | Baseline and observers expose ObjectDisposedException to route, handled 500/empty response | Feature-start failure retained |
| Explicit Complete with short Content-Length | Same completion exception and short transfer | Feature-complete failure retained |
| Synchronous Complete(IOException) after gated prefix | Client gets prefix then transport failure; app does not receive exception | Explicit completion argument retained, body omitted |
| CompleteAsync(IOException) after gated prefix | Client gets complete prefix response without transport error | Native ignores exception argument; observer still records it and omits body |
| Complete(IOException), RequestAborted replaced | Client prefix then failure; current token remains uncanceled | Original token and explicit failure tracked independently |
| Stable full/offset/count/remainder/mixed files | Identical client bytes/status/selected headers | A reads same eligible slice again; B captures copy writes |
| Minimal/MVC physical-file 206 | Correct 17-byte slice and range headers | Both modes capture exactly |
| Stream-backed file | Same 120 bytes | Stream writes; no observing file callback |
| Gzip file | Same encoded body, decoded 120-byte fixture | Outer observer sees encoded Stream writes, zero file callbacks, no double count |
| Range plus Accept-Encoding:gzip | Native 206, no Content-Encoding:gzip | File branch observes raw range |
| Missing/invalid-offset/invalid-count/canceled file | Same exception types, handled status and headers | No buffered export |
| Ineligible/known-overcap file | Native delegate branch | Zero side-read bytes; omitted/oversized respectively |
| Known-length fully consumed request | Exactly 37 bytes consumed, no EOF read added | Full request captured using length |
| Chunked Stream/BodyReader consumption | Exact 37-byte application consumption and EOF | Full request captured |
| Partial/unread request | Exactly seven/zero bytes consumed | Request body omitted, response still normally captured |
| Reset upload | Exactly seven consumed bytes of promised 100, native ConnectionResetException | Request body omitted |

Direct native file sends do not pass through the observer's Stream/Writer. Mode A logs native=1, helper=0, zero Stream writes for a standalone file. Mode B logs native=0, helper=1 and observing Stream writes for eligible files. Those branch assertions are part of the matrix.

### Actual side-read limitation, reproduced in every runtime

The same 120-byte file fixture is restored before each mode. Its real OnStarting callback runs after the native helper reads the first chunk:

| Callback | Control client | Mode A capture / extra content read | Mode B capture |
| --- | --- | --- | --- |
| Replace with 120 `X` bytes | Original 120 bytes | **Wrong 120 replacement bytes**, normal evidence passes, 120 extra bytes read | Exact original 120 bytes |
| Delete file | Original 120 bytes | Omitted, second open fails, zero extra bytes read | Exact original 120 bytes |
| Truncate to five bytes | Original 120 bytes | Omitted, partial second read consumes five extra bytes | Exact original 120 bytes |

This falsifies the hypothesis that a successful bounded post-send side read transparently captures the sent file body. A's stable-file successes do not rescue that hypothesis. Mode B passes this concrete comparison but changes the feature path and uses an infrastructure API. Neither mode is selected for the SDK.

### Fixture injection, separately labeled

`file-side-read-fault` and `file-mixed-side-read-fault` deliberately throw an observation-only IOException before the second open. In Mode A they omit the **entire** capture, including a buffered mixed prefix, without changing client bytes, status, headers or the application's native result. In Mode B the hook is not used and the actual copy bytes are captured. These are containment tests, not claimed naturally occurring filesystem failures.

### Completion evidence and interpretation

`OnCompleted` ran for real short responses, aborts and writer completion errors. It is not a success result. The original-style combined predicate sometimes passed synchronous writer-error callbacks before cancellation became visible, including replacement-token fixtures in development runs. It also returned false in many runs. No test requires that race to reproduce, and snapshots are never revised after publication.

CompleteAsync(IOException) consistently returned a normal response in all three versions. Source inspection with `gh` at all three pinned revisions confirms HttpResponsePipeWriter.CompleteAsync calls Complete() without forwarding its argument. The observer records the application's exception argument but preserves this native outcome.

These observations are **not all incomplete-capture counterexamples**. In the completion-error and canceled-flush fixtures, the seven-byte application body already observed can equal the client bytes. The conservative rule intentionally omits on explicit failure evidence. The clear wrong-capture counterexample is Mode A's file replacement. Short Content-Length demonstrates an unsupplied promised remainder. Internally swallowed network-flush failures, by themselves, do not establish a missing captured byte and are not presented as a new release blocker.

## Connection-reuse investigation

A separate subagent investigated the earlier timeout using temporary source copies, bounded deadlines and actual Kestrel connection IDs, remote ports and request identifiers. It ran 20 focused executions / 405 requests, including a reduced historical reconstruction; no timeout reproduced. The parent inspected the modified harness and independently reran its nine core executions / 153 requests on the exact paired runtimes:

- Nine sequential healthy Stream/Writer/completion requests per mode used one connection and remote port, with distinct request identifiers. Expected client bytes and observed captures matched.
- `stream -> writer-error -> stream -> stream` used the same connection for the first two requests. The writer error delivered the seven-byte prefix then a transport error. Both observers omitted capture and retained the explicit completion error. The next request succeeded on a new connection, reused by the last request. This is recovery by replacing an aborted connection, not reuse of that aborted connection.
- Gated and ungated variants both passed in all three capture modes and runtimes. Deadlines were failures with nonzero process exit, never accepted as ordinary transport outcomes.

The parent then independently ran the entire 68-case interleaved matrix with pooling enabled in a temporary copy. All 1,771 original assertions passed per runtime. The checked-in runner now repeats the full matrix with both connection modes and adds 11 connection-ID checks per run, producing the final table above. The first 12 healthy requests share one connection in pooled mode; fresh mode isolates them.

The old timeout remains unexplained. Historical Control requests shared a client pool with preceding observed requests, and those logs lack connection IDs, so a Control timeout does not establish an observer-independent cause. No observer-specific hang was reproduced, but these successful checks do not identify or fix the historical failure. Neither fresh connections nor prefix gates are presented as a compatibility repair.

## Development failures and rejected hypotheses

1. Initial build failed CA2022 on the partial-request fixture's ignored ReadAsync result under net9/net10. The fixture now uses ReadExactlyAsync for the intended seven-byte partial consumption. No production setting or analyzer suppression was added.
2. Early pooled-connection runs reached a client read deadline after synchronous writer completion with an exception. Diagnostic output showed handler-ended and OnCompleted already true while the control client was still waiting. The first versions let that timeout fail the run; a temporary diagnostic catch recorded it. Moving compression to gzip-only branches did **not** resolve that failure. Fresh connections isolated the first passing matrix, and a legitimate client-prefix gate was added to order writer-error completion. The temporary deadline-as-outcome code was removed. The later investigation and final fresh/pooled matrix above pass, but do not diagnose or claim to repair the historical timeout.
3. An initial assertion required identical IOException subtype after a native connection abort. Actual Kestrel/client timing produced IOException versus HttpIOException (reset versus premature end). The test now compares the transport-failure category for synchronous writer-error aborts, still compares exact prefix/status/headers, and logs the actual type. Deterministic file and cancellation exceptions continue comparing exact types. This does not relax any captured-byte assertion.
4. The expectation that CompleteAsync(IOException) must fail the client was rejected by source and runtime evidence. The test requires the actual successful native prefix response and separately requires the observer's explicit error flag and omission.
5. Treating any changed completion-time token as an application replacement was inaccurate: Kestrel can substitute its token during normal completion. The result separates an intercepted setter from a changed current token. Healthy explicit replacement remains a positive capture fixture.
6. An early buffer export copied the captured array. It was changed to transfer ownership of the bounded buffer as ReadOnlyMemory, avoiding a second retained body copy at the completion decision. Count/equality assertions remain unchanged.
7. The strong hypothesis for native-preserving side-read capture failed on the real OnStarting mutation. The mismatch assertion remains in the final matrix; it was not changed to an equality success.

Ignored development logs retain the failed local executions. The tracked description above preserves their meaning when those logs are absent.

## Remaining choices and gaps

- A native-preserving post-send side read is a limited stable-file experiment, not a reliable identity-preserving file-body capture mechanism.
- Mode B is an experimentally successful single-pass **Kestrel** comparison for these cases. Choosing a production architecture that bypasses a body feature and depends on a public infrastructure helper remains a separate decision.
- The original POC's native file omission remains a demonstrated limitation, not an approved v1 scope exception. This experiment does not turn metadata or a captured prefix into a full-body success.
- Unknown-length internal finalization failures are not all surfaced by public features. No arbitrary cancellation-settling delay is added. The absence of a universal TCP success signal is distinct from body capture completeness.
- Pooled reuse is demonstrated for the final matrix and focused sequences, while the historical timeout remains unexplained. HTTPS, HTTP/2/3, other servers/custom features, concurrent body writers, arbitrary middleware order and multi-chunk file mutation remain unqualified. IsCompleted terminal flush, explicitly conflicting Transfer-Encoding/Content-Length, escaped-handler exceptions and real internally swallowed network-flush exceptions are not independently exercised here.
- Changes are limited to this POC and companion/index documentation. Production SDK code, the original POC, tests, solution and design remain unchanged. A production file-capture architecture still requires separate approval.
