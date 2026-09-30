# Apitally .NET v1 memory review

Date: 2026-09-30. Branch: `v1`. Reviewed commit: `449a35a`.

## Summary

The `sdk-tests` benchmark reports that enabling the SDK adds about 85 MB of peak RSS to `aspnetcore-controllers-latest` and about 97 MB to `aspnetcore-minimal-latest`. This review looked for leaks and unnecessary memory use in `src/Apitally`, and measured where the extra memory comes from.

There is no leak. Every Apitally buffer, cache and queue has a fixed bound, and the managed heap left after a full GC grows by only about 8 MB. The measured breakdown of the controllers delta is:

- **About 57 MB of garbage collector headroom.** ASP.NET Core uses Server GC by default. Under Server GC, the heap grows for a long time before the first collection. The SDK allocates about 29 KB per request on top of the app's 12 KB, so the heap grows 3.4 times faster within the 60-second run. The heap shrinks back when it is collected, so this memory is not retained.
- **About 8 MB of retained managed memory.** Almost all of it is OpenTelemetry metric storage.
- **About 10 MB of fixed overhead.** This is loaded assemblies and JIT-compiled code.
- **About 5 to 8 MB of native memory.**

With workstation GC or a heap limit, the delta drops to about 23 MB. Over 300 seconds with Server GC, the process footprint gap settles at about 30 MB.

The findings are therefore about allocation volume and bounded storage, not leaks:

- Two Medium findings. A1: the span export path allocates about 15 KB per request, the largest single share of the SDK's allocations. R1: metric histograms keep about 12.5 KB per distinct attribute combination, which is up to about 125 MB at the configured capacity.
- Five Low findings about smaller allocations and retention.

Each finding records the decision made after the review. The review itself changed no code. All fixes were applied afterwards, and the suite passes 169/169 on .NET 10.

## Method and scope

The review had two parts:

1. **Code reading.** The runtime code in `src/Apitally` was read with a focus on object lifetimes, per-request allocations, caches, queues and buffers. Relevant OpenTelemetry .NET 1.19.0 and ASP.NET Core 10.0 sources were checked where the SDK's memory use depends on them.
2. **Measurement.** A copy of the published benchmark app ran under the harness's own benchmark load generator: 10 users, 50 requests per second, full request and response capture, and the local OTLP endpoint. Each configuration was run with the SDK disabled (`APITALLY_DISABLED=true`) and enabled. The runs recorded the following:
   - `dotnet-counters` for GC and heap counters.
   - `dotnet-gcdump` and `dotnet-dump` (`dumpheap -stat -live`) for retained objects.
   - `vmmap --summary` for native and mapped memory.
   - `dotnet-trace` with the `gc-verbose` profile for sampled allocations by type and call stack.

   A separate console program measured the per-combination cost of the three request histograms with OpenTelemetry 1.19.0 in isolation.

Caveats:

- All measured runs used the controllers app. The minimal app's larger delta (97 MB) was not measured separately. It adds validation and more endpoints, but nothing in the code suggests a different memory pattern.
- The machine used about 6.6 GB of swap during the runs. RSS understated one disabled run by about 41 MB because pages were compressed out of it, so the vmmap physical footprint was used for that comparison. The benchmark's own RSS numbers may carry the same noise.
- Measurements ran on .NET 10.0.9 only.

### Severity

- **High:** a leak or unbounded growth.
- **Medium:** large retained memory within a bound, or an allocation source that dominates the SDK's overhead.
- **Low:** a small allocation or retention cost, or one limited to specific setups.

## Where the benchmark's extra memory comes from

### Measured results

| Run (60 s unless noted) | Peak RSS off / on (delta) | Allocated per request off / on | GCs during load off / on | Live heap after full GC off / on |
| --- | --- | --- | --- | --- |
| Server GC (default) | 169 / 252 MB (83 MB) | 12.0 / 41 KB | 0 / 1 | 2.2 / 10.6 MB |
| Workstation GC (`DOTNET_gcServer=0`) | 140 / 163 MB (23 MB) | 12.3 / 41 KB | 3 / 12 | 2.0 / 10.6 MB |
| Server GC, `GCHeapHardLimit` 48 MB | 143 / 166 MB (23 MB) | same | 3 / 10 | 2.0 / 12.4 MB |
| Server GC, 300 s | footprint gap 30 MB | 11.4 / 38 KB | 1 / 7 | 2.2 / 12.0 MB |

Disabling DATAS, `GCConserveMemory=9`, `GCgen0size=32MB` and `GCgen0MaxBudget=16MB` each left the delta between 80 and 107 MB.

The disabled app allocates about 35 MB during the run and never collects, so its RSS also rises throughout the run. The enabled app allocates about 120 MB and collects for the first time after about 51 seconds, when GC committed memory is about 105 MB. The benchmark's "max RSS in 60 seconds" therefore mostly measures how much each app allocated before its first collection, not how much memory it retains. The two workstation GC and heap-limit runs agree on a retained delta of about 23 MB, and the 300-second run agrees on about 30 MB.

Just after startup, before any traffic, the enabled app uses 5.5 MB more than the disabled app.

### Retained managed memory

The live heap after a full GC is 10.6 MB with the SDK and 2.2 MB without it. Nearly all of the difference is OpenTelemetry metric storage (see [R1](#r1-metric-histograms-keep-about-125-kb-per-attribute-combination)):

| Type | Size |
| --- | ---: |
| `Int64[]` histogram buckets (2,457 arrays) | 3.2 MB |
| `MetricPoint[]` (6 arrays, sized by the cardinality limit) | 2.6 MB |
| `ConcurrentDictionary<Tags, ...>` tables | 1.2 MB |
| Histogram, lookup and bucket objects (885 points) | 0.5 MB |

Apitally's own live objects total under 50 KB: two batch queue arrays and a few in-flight `RequestState` objects. The spool is on disk.

### Allocation sources

The enabled run allocated 121.7 MB in 60 seconds, including the app's own allocations of about 36 MB. The largest SDK sources were:

| Call site | MB | Per request |
| --- | ---: | ---: |
| Span export under `ApitallyBatchProcessor.DelegatingExporter.Export` ([A1](#a1-span-export-allocates-about-15-kb-per-request)) | 44.8 | 15 KB |
| - `SpanRedaction.RedactJson` (strings 7.3, `byte[]` 3.3) | 11.1 | |
| - `OtlpEncoder.ToAnyValue` | 8.2 | |
| - protobuf `RepeatedField.AddRange` | 8.0 | |
| - `SpanRedaction.TryRedact` (strings, dictionary entries) | 7.7 | |
| - `AttributeValues.Normalize` | 3.1 | |
| - `MessageExtensions.ToByteArray` ([A3](#a3-encoded-payloads-are-copied-into-large-arrays)) | 2.8 | |
| - `OtlpEncoder.EncodeChunk` | 2.6 | |
| `BodyCapture.Stage` from `ObservedPipeWriter.Advance` ([A2](#a2-response-body-staging-starts-with-a-4-kb-buffer)) | 13.8 | 4.6 KB |
| Transport completion: `CreateCompletion` 2.5, `ConsumerUpdates.Hash` 2.5 ([A4](#a4-consumer-change-detection-allocates-about-800-bytes-per-request)), `CopyHeaders` | 8.7 | 2.9 KB |
| `ApitallySpanProcessor.OnEnd` / `OnStart` | 5.7 / 2.3 | 2.7 KB |
| Export cycle: histogram snapshots 2.1, `ReadStoredBytes` 0.8 | 3.8 | |
| `BodyCapture.GetBody` trim copy | 1.5 | |

Reducing allocations shrinks the Server GC headroom roughly in proportion, but does not remove it. Implementing A1 to A4 would plausibly cut the SDK's allocations by a quarter to a third. This is an estimate, not a measurement.

### Interpreting the benchmark

A short fixed-length run under Server GC reports allocation volume rather than retained memory, and the memory grows with run length until the first collection. Neither the SDK nor the benchmark app should switch GC mode to change the result. For a retention figure, compare the live heap after a full GC, or run long enough for both apps to collect several times.

## Priority summary

| ID | Severity | Finding | Kind |
| --- | --- | --- | --- |
| A1 | Medium | Span export allocates about 15 KB per request | Allocation |
| R1 | Medium | Metric histograms keep about 12.5 KB per attribute combination, up to about 125 MB | Retention, bounded |
| A2 | Low | Response body staging starts with a 4 KB buffer | Allocation |
| A3 | Low | Encoded payloads are copied into large arrays | Allocation |
| A4 | Low | Consumer change detection allocates about 800 bytes per request | Allocation |
| R2 | Low | Idle keep-alive connections keep the last request's captured bodies and headers | Retention |
| R3 | Low | Request log messages are held at full length until export | Retention, bounded |

Recommended order: decide R1 first, because it is the only retained memory that can grow large. Then implement A1 and A2, which cover most of the SDK's allocations. A3, A4, R2 and R3 are small, independent changes.

## Findings

### A1. Span export allocates about 15 KB per request

- **Severity:** Medium. It is 37% of all allocations in the enabled run and about half of the SDK's share.
- **Verification:** Measured with sampled allocation traces.
- **Location:** [SpanRedaction.cs:111](../src/Apitally/Export/SpanRedaction.cs#L111), [SpanRedaction.cs:134](../src/Apitally/Export/SpanRedaction.cs#L134), [SpanRedaction.cs:243-294](../src/Apitally/Export/SpanRedaction.cs#L243), [OtlpTraceMapper.cs:59](../src/Apitally/Export/OtlpTraceMapper.cs#L59), [OtlpEncoder.cs:83](../src/Apitally/Export/OtlpEncoder.cs#L83).

The batch worker redacts each queued span, builds the protobuf object graph, and serializes it. Most of the allocations come from four places.

**JSON body redaction rewrites every body.** `RedactJson` reads each token, materializes every property name and string value with `reader.GetString()`, writes them into a new `ArrayBufferWriter<byte>`, and decodes the result into a string. That is one string per token, a buffer of the body's size, and the output string at twice the body's size in UTF-16. Most bodies contain no field that matches the mask patterns, so the rewrite produces the same content as the input.

**Header attributes build new strings and grow the attribute dictionary.** Each captured header creates `name.ToLowerInvariant()` and `prefix + name`. The span dictionary starts at the size of the activity's tags, then grows again during `EnrichServer` and again for about 20 header attributes. `RedactQueryAndHeaderAttributes` also copies the whole dictionary with `ToList()` for every span, including child spans without headers.

**Repeated fields grow by doubling.** `output.Attributes.Add(OtlpEncoder.ToKeyValues(...))` passes a LINQ `Select`, so `RepeatedField.AddRange` cannot presize and grows its array several times per span.

**The resource is rebuilt for every chunk.** `ToOtlpResource` creates a new resource message with all resource attributes for every request of 32 records. This cost is small.

**Recommendation:**

1. Add a fast path to `RedactJson`. First, scan the body with `Utf8JsonReader` and check only property names. Decode each name into a stack buffer with `CopyString`, and match it with the `ReadOnlySpan<char>` overload of `Regex.IsMatch`. `MatchesAny` needs a span overload for this. If no name matches, return `Encoding.UTF8.GetString(bytes)`. Otherwise, run the existing rewrite. Invalid JSON still throws in the first pass and falls back to text as it does today.

   This changes one behavior: an unmasked JSON body is exported with its original whitespace and escaping instead of re-serialized compactly. That matches the captured bytes more closely, but it is a visible change and needs a decision.
2. In `SpanRedaction`, iterate only the keys that need rewriting instead of copying the dictionary. Call `attributes.EnsureCapacity(attributes.Count + headers.Count)` before adding header attributes.
3. Set `RepeatedField.Capacity` before `AddRange` in the trace and log mappers.
4. Cache the converted `OtlpResource` per `Resource`, for example in a `ConditionalWeakTable` like the one in `ApitallySpanProcessor`.

**Option not recommended now:** write OTLP protobuf directly into the output buffer without building message objects, as the official OpenTelemetry .NET OTLP exporter does. This removes most of `ToAnyValue`, `AddRange` and `ToByteArray`, which is about 19 MB of the 45 MB. It needs a hand-written serializer for spans, logs and metrics, which is a lot of code to maintain. Revisit it only if the cheaper changes are not enough.

**Decision:** Items 2 to 4 applied as recommended. For item 1, the fast path was rejected, because the Python and JavaScript SDKs always export compact re-serialized JSON and the .NET SDK should match them. Instead, `RedactJson` keeps its output byte for byte, but it decodes property names into a stack buffer, matches them with the span overload of `Regex.IsMatch`, and copies unescaped string values as UTF-8. Only escaped values still become strings. `MatchesAny` now takes a `ReadOnlySpan<char>`. The log mapper adds only one attribute per record, so only the trace mapper sets `RepeatedField.Capacity`.

Measured on .NET 10 for one span with a 1.3 KB JSON response and five request and response headers, `TryRedact` allocations dropped from 16.3 KB to 6.2 KB. `EscapedAndLongJsonNamesAndValuesAreDecodedBeforeRedaction` covers escaped names, escaped values and names longer than the stack buffer.

### R1. Metric histograms keep about 12.5 KB per attribute combination

- **Severity:** Medium. The storage is bounded, but the bound is large, and it scales with the number of consumers.
- **Verification:** Measured. In the app, 885 histogram points held about 3.7 MB, on top of about 3.8 MB of storage allocated upfront. In an isolated OpenTelemetry 1.19.0 program with the SDK's view configuration, 2,000 attribute combinations held 29 MB of live heap. That heap included 16,000 `Int64[]` bucket arrays of 1,304 bytes each (20.9 MB).
- **Location:** [ApitallyMetrics.cs:21](../src/Apitally/Metrics/ApitallyMetrics.cs#L21), [ApitallyMetrics.cs:53-57](../src/Apitally/Metrics/ApitallyMetrics.cs#L53).

Each distinct combination of method, route, status code, scheme and consumer creates one point in each of the three request histograms. With the default `MaxSize` of 160 buckets, an exponential histogram point allocates the following, each a `long[160]` array:

- a running positive bucket array, allocated on the first nonzero value
- a positive snapshot array at the first collection
- a negative snapshot array at the first collection

Request metrics never record negative values, but OpenTelemetry sizes the negative snapshot array to the full capacity anyway. This accounts for 2 of the 8 bucket arrays per combination and is an upstream inefficiency.

With the histogram and lookup objects added, one combination costs about 12.5 KB across the three histograms. The 10,000-point cardinality limit bounds this at about 125 MB, plus about 4 MB allocated upfront. Delta collection reclaims inactive points, so the storage follows the combinations active in the last one or two intervals, not the process lifetime. A reclaimed point that is reused allocates new histogram objects, so consumer turnover also creates gen2 garbage every interval.

The benchmark uses 3 routes, one status code and about 100 active consumers. A public API with 2,000 active consumers across 5 routes and 2 status codes would reach the cap and use about 125 MB for metrics alone. [design.md](design.md) quantifies only the upfront slot storage at 100 to 200 bytes per slot, so this cost was not part of the capacity decision.

**Options:**

1. Keep the current configuration, and document the per-combination cost and the 125 MB bound in design.md. Recommended, because memory follows actual usage, and only APIs with thousands of concurrently active consumer, route and status combinations approach the bound. Both alternatives trade telemetry quality for memory that most applications never use.
2. Lower `MaxSize`. Bucket memory scales linearly with it; 64 buckets would roughly halve the per-combination cost. Wide value ranges would then be downscaled below scale 3, which the spec allows, at the cost of coarser percentiles.
3. Lower `CardinalityLimit` toward OpenTelemetry's default of 2,000, which the Python and JavaScript SDKs use. This bounds metrics at about 25 MB. APIs above the limit would lose request metrics to the overflow point, which carries no request dimensions.

Independently, the negative bucket allocation could be reported to OpenTelemetry .NET.

**Decision:** Option 1. The configuration is unchanged. design.md now states the upfront cost, the per-combination cost and the resulting bound of about 125 MB.

### A2. Response body staging starts with a 4 KB buffer

- **Severity:** Low. It is the second-largest allocation source, at 13.8 MB, or 11% of the enabled run.
- **Verification:** Measured with sampled allocation traces.
- **Location:** [BodyCapture.cs:11](../src/Apitally/AspNetCore/BodyCapture.cs#L11), [BodyCapture.cs:62](../src/Apitally/AspNetCore/BodyCapture.cs#L62), [BodyCapture.cs:128](../src/Apitally/AspNetCore/BodyCapture.cs#L128).

JSON responses from MVC and Minimal APIs usually have no `Content-Length`, so the first staged write allocates `InitialBufferSize` (4,096 bytes) or more, and the buffer doubles from there. `GetBytes` then copies the used part into an array of the exact size. A typical 1 to 3 KB response therefore allocates a 4 KB buffer and a trimmed copy. A body just over 4 KB allocates 4 KB, then 8 KB, then the trimmed copy.

System.Text.Json writes most small responses with a single `Advance`, so the first write usually contains the whole body.

**Recommendation:** Size the first allocation to the first write when the length is not declared: `declaredLength() ?? bytes.Length`. Keep the doubling for later writes, using `Math.Max(buffer.Length * 2, required)`. A body written in one call then needs one exact allocation, and `GetBytes` skips the trim copy because the length already matches. Remove `InitialBufferSize`. Streaming bodies that are written in many small chunks, such as NDJSON, need a few more resizes, but the total copying stays below twice the final size.

`ArrayPool<byte>` would remove the growth allocations entirely, but it is not recommended. A write through a retained `ObservedStream` after the buffer is returned to the pool would copy one request's body into memory that another request's capture is using.

**Decision:** Option 1, as recommended. Without a declared length, the first allocation is the first write's length. `Math.Clamp(size, required, MaxBodySize)` already provides the `Math.Max` behavior for later growth, so only the initial size changed, and `InitialBufferSize` is removed. A body written in one call now has one exact allocation and no trim copy. Existing capture tests cover multi-write bodies and the size cap.

### A3. Encoded payloads are copied into large arrays

- **Severity:** Low.
- **Verification:** Measured for `ToByteArray` (2.8 MB) and `ReadStoredBytes` (0.8 MB). The large object heap effect is reasoned from array sizes, not measured.
- **Location:** [OtlpEncoder.cs:151](../src/Apitally/Export/OtlpEncoder.cs#L151), [ApitallyMetrics.cs:170](../src/Apitally/Metrics/ApitallyMetrics.cs#L170), [SpoolFile.cs:112-114](../src/Apitally/Export/SpoolFile.cs#L112).

Every OTLP request is serialized into a new `byte[]` with `ToByteArray()`, which the spool immediately writes into its gzip stream. A chunk of 32 spans with captured bodies is often larger than 85,000 bytes, so it goes on the large object heap, which is collected only in gen2 collections.

`ReadStoredBytes` copies each spool file into a `MemoryStream` presized to the file length, then copies it again with `ToArray()`. That is two large arrays per sent file.

**Recommendation:**

1. Pass the message and its already calculated size to the spool, and serialize with `message.WriteTo(gzip)`. Protobuf then writes through a small internal buffer. The size check in `Append` uses the calculated size instead of the array length. Serialization moves inside the spool lock. That lock is shared only by the three signal writers and the export worker, so contention does not increase noticeably.
2. In `ReadStoredBytes`, allocate `new byte[stream.Length]` and fill it with `stream.ReadExactly`. Keep the current `FileShare` options.

**Decision:** Option 1, fixing both. The longer lock hold was checked first. Only the span and log batch workers and the export worker take the spool lock. Request threads only enqueue to the batch processors without waiting, and metric recording uses a separate lock that is released before spooling. For one 32-span chunk, `WriteTo(gzip)` holds the lock 0.06 ms longer at 136 KB and 0.3 ms longer at 1.3 MB. It still does less total work than `ToByteArray` followed by gzip.

**Implementation:**

- `TelemetrySpool.Append` and `SpoolFile.Append` take the serialized size and a delegate that writes into the gzip stream.
- `OtlpEncoder.EncodeRequests` passes each request with the size it already calculated, and callers pass `request.WriteTo`.
- `ReadStoredBytes` makes one exact allocation.

The spool and export worker tests append raw bytes through a test extension method. The encoder tests assert that the passed size matches the serialized length.

### A4. Consumer change detection allocates about 800 bytes per request

- **Severity:** Low.
- **Verification:** Measured: 2.5 MB over 3,000 requests.
- **Location:** [ConsumerUpdates.cs:113-125](../src/Apitally/Requests/ConsumerUpdates.cs#L113).

`EmitIfChanged` runs for every request whose consumer has a name, group or attributes, which is the common case for apps that call `SetConsumer` with a name. `Hash` allocates the following on every such request, even when nothing has changed:

- a `string[]` of parts
- the joined canonical string
- its UTF-8 bytes
- the 32-byte hash
- a 44-character Base64 string

Round 1 P4 kept SHA-256 so that cache entries stay small, and this finding does not reopen that decision.

**Recommendation:** Keep the canonical form and SHA-256, and remove the intermediate allocations:

1. Compute the UTF-8 length of the parts.
2. Write them into a `stackalloc` buffer, or a rented buffer for large attribute sets.
3. Hash into a `stackalloc` destination with `SHA256.HashData(source, destination)`.
4. Store the hash in the cache as a `UInt128` of the first 16 bytes instead of a Base64 string. 128 bits is ample for change detection.

This leaves only the sorted attribute enumeration, and that allocates only when attributes are set.

**Decision:** Option 2, a smaller version of the recommendation. The joined canonical string and its UTF-8 bytes stay. Writing the parts directly into a stack buffer would need buffer-length and pooling code, and it would save only about 400 more bytes per request. The hash is now written into a stack buffer, and its first 128 bits are stored as a `UInt128` instead of a Base64 string. This also shrinks each cache entry by about 100 bytes. Measured on .NET 10, allocations per `EmitIfChanged` dropped from 568 to 400 bytes without attributes, and from 912 to 744 bytes with one attribute. The existing consumer update tests cover suppression and eviction.

### R2. Idle keep-alive connections keep the last request's captured bodies and headers

- **Severity:** Low. The cost is one set of request detail per idle connection, and it matters only for servers with many idle keep-alive connections that return large captured bodies.
- **Verification:** Reasoned from source. Kestrel's `Http1Connection.BeginRequestProcessing` calls `Reset()`, which clears the feature collection, at the start of the next request on the connection or when the connection closes. It does not clear it when a request ends. This was not measured.
- **Location:** [RequestState.cs:56](../src/Apitally/Requests/RequestState.cs#L56), [RequestState.cs:235-248](../src/Apitally/Requests/RequestState.cs#L235), [RequestState.cs:83-84](../src/Apitally/Requests/RequestState.cs#L83).

`RequestState` is stored as an `HttpContext` feature. After finalization, `TakeDetail` clears the spans and logs but keeps two sets of references:

- `transport`, which holds copied request and response headers, both `CapturedBody` arrays and the retained validation response
- the `RequestBody` and `ResponseBody` captures, which reference the same arrays

On an idle HTTP/1.1 connection, these stay reachable until the client sends another request or the connection times out, which is 130 seconds by default. For example, 5,000 idle browser or mobile connections with 10 KB responses keep about 50 MB alive that the SDK no longer needs.

**Recommendation:** In `TakeDetail`, set `transport`, `RequestBody` and `ResponseBody` to null. Nothing reads them after finalization, because a request is finalized only after `CreateCompletion` has run. Leave `Cutoff` unchanged: a request cut off at shutdown can still complete its transport afterwards, and its body sizes need the captures.

**Decision:** Option 1, as recommended. `TakeDetail` now also clears `transport`, `RequestBody` and `ResponseBody`, and `Cutoff` is unchanged. `TakeDetail` runs only after transport completion, so `CreateCompletion` has already read the captures. No test was added, because the change only affects whether the garbage collector can reach the data, as with round 2 E1.

### R3. Request log messages are held at full length until export

- **Severity:** Low. The storage is bounded by 1,000 records per request and by the log batch queue, but individual messages have no size limit.
- **Verification:** Reasoned from source.
- **Location:** [ApitallyLoggerProvider.cs:100](../src/Apitally/Logging/ApitallyLoggerProvider.cs#L100), [OtlpLogMapper.cs:41](../src/Apitally/Export/OtlpLogMapper.cs#L41).

The rendered message is stored untruncated in `RequestState` until the request is finalized, and then in the log batch queue until export. It is truncated to 2,048 characters only when it is encoded. An application that logs serialized payloads at `Information` keeps every full message for the duration of the request plus up to a second in the queue.

**Recommendation:** Truncate `record.Body` to 2,048 characters in `ApitallyLogger.Log`, after `TryMask` accepts the record, and remove the truncation from `OtlpLogMapper`. Truncating before the mask would be unsafe, because the cut could split a secret so that the mask pattern no longer matches it.

**Decision:** Option 1, as recommended. `ApitallyLogger.Log` truncates the body to 2,048 UTF-16 code units after `TryMask` accepts the record, and `OtlpLogMapper` no longer truncates. The mapper's truncation test is replaced by `MasksSeeFullMessagesAndExportedMessagesAreTruncated`. It checks that the mask receives the full 3,000-character message and that the export holds 2,048 characters.

## Smaller items

These are too small to raise as findings, but they are cheap if the surrounding code is changed anyway:

- `RequestState` eagerly allocates five collections and a lock object for every request, including requests without detail. Most of them stay empty. Creating them on first use saves a few hundred bytes per request.
- `SpanSnapshots.Copy` builds the attribute dictionary without a capacity, and the SERVER snapshot then grows twice more in `EnrichServer` and `TryRedact`. A capacity based on the tag count plus the enrichment keys avoids those resizes. This overlaps with A1 item 2.
- `SpanRedaction.Decompress` allocates a 50,001-byte buffer for every compressed body, whatever its size. Rent the buffer from `ArrayPool<byte>.Shared`, since it never leaves the method, and copy out the decoded length as today.

## Verified bounded

These structures were checked for unbounded growth and are bounded:

| Structure | Bound |
| --- | --- |
| Registry associations | In-flight requests with kept detail. Each request keeps at most 1,001 keys and releases them at finalization or cutoff. |
| Per-request spans and logs | 1,000 each, see `RequestState.MaxBufferedSpans` and `MaxBufferedLogs`. |
| Body captures | 50,000 bytes per direction. A declared oversized body is never staged. |
| Span and log batch queues | 2,048 entries each. In the worst case, when the export worker falls behind at high request rates with large bodies, the span queue can hold about 200 MB of bodies. The worker exports whenever 512 entries are queued, so in practice the queue holds about a second of traffic. |
| Spool | 50 MB on disk, or 10 MB in memory mode. It uses no managed memory in disk mode. |
| Consumer cache | 10,000 identifiers, about 5 MB at most. |
| Error aggregates | 100 groups of each kind between drains. The stack traces in server error groups are capped at 65,536 characters each. |
| Exception text cache and export resource cache | `ConditionalWeakTable`, so entries live only as long as the exception or provider does. |
| Diagnostic warning keys | A fixed set of keys, plus one per rejected HTTP status code. |
| Spool gzip streams | At most one open stream per signal. Their native zlib state is released at rotation. |
