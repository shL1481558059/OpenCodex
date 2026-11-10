# SSE event pipeline and bounded flush

The current pipeline keeps the pre-existing whole-round tool-contract validation/retry policy.
This is **not** the true-incremental rollout; audit spool, session byte governance and multi-agent
append-only accumulation are separate pending stages. Existing response audit capture budgets
and normalized JSON body contracts have not been replaced with raw SSE logs.

## Flow and ownership

* HTTP uses fixed 4096-character reads, a bounded pre-content probe, and one frame assembler.
  Probe, usage/capture, completion validation and cross-protocol conversion share the same
  decoded event. Probe buffers are released after replay. Idle cancellation is reused per stream.
* Responses WebSocket passes its already decoded dictionary to the same frame envelope; it no
  longer serializes and parses it again for downstream capture. Receive buffer, message storage
  and idle cancellation are reused per exclusive lease. Large receive storage is released after
  the event; no JsonDocument or borrowed JsonElement escapes decode.
* The six converters retain public string APIs; the native pipeline accepts shared decoded events
  and emits typed `SseFrame` snapshots. Built-in search observes and rewrites the same decoded
  output instead of parsing generated strings. Capture, usage and terminal validation share that
  graph; the writer never parses it back. Legacy wrappers alone retain string parsing.
* `SseFrame.FromJson` freezes generated container graphs before the producer can mutate later
  items/status. Serialization is lazy and cached once, as is the UTF8 wire byte count. No borrowed
  JsonElement/JsonDocument escapes. Input and output graphs remain read-only by consumer contract.
* Whole-round tool buffering is still enabled. It uses a queue that releases replayed frames,
  with explicit byte/frame limits; overflow fails rather than silently truncates. Typed frames
  retain snapshots as well as encoded wire, so their buffered heap can be larger than the old
  wire-only list. This is not a claim that full-response/audit memory is already bounded: converter
  final-response accumulation and the audit spool are still follow-up stages.
* Multi-agent model collection implements the typed writer directly (callbacks receive a private
  copy, not the shared graph); terminal Result is isolated from callback mutation. Its legacy
  string entry point uses the same chunk assembler, including multi-line data and CR/LF splits.
* Legacy standalone string capture/APIs remain compatible; those legacy callers may still parse.
  `SseFrameReader.ReadLines` accepts physical lines (with/without terminators) or full frames.
  Arbitrary character chunks must use `ReadChunks` (including CR/LF split across chunks).
  The legacy HTTP writer string adapter uses chunk semantics to preserve exact existing wire text.
* Wire line endings are normalized to LF. Multi-line `data` joins with LF; exactly one optional
  field-value space is removed. Comments/unknown fields are preserved on the wire. EOF data
  tolerance is retained for compatibility, but an unterminated frame is never marked as a
  successfully flushed terminal frame.

## Writer

`IProxyStreamWriter.WriteFramesAsync` has a compatibility default for existing integrations.
The production writer consumes event metadata and never reverse-parses JSON or searches event
names inside arbitrary payload text. It uses PipeWriter advance without implicit per-line flush.
First frame/first business frame, comments/heartbeats, errors and terminal frames flush immediately.
Other complete events coalesce up to a byte threshold or a reusable per-stream periodic timer.
The timer also flushes while the single upstream MoveNext is pending. Writes/flushes are awaited;
a downstream failure cancels and awaits that pending read before disposing the source. No
per-delta timer/linked CTS and no unbounded producer queue are introduced. A single large frame
may exceed the batching threshold but must fit the event limit; it is not silently truncated.
Response/proxy buffering is disabled where supported; upstream reverse proxies must still honor
SSE streaming. Flush acceptance is not a TCP delivery acknowledgment.

## Configuration (no database migration)

Defaults below are safety/latency choices, not production performance measurements:

```json
{
  "ProxyStreaming": {
    "MaxBatchBytes": 16384,
    "MaxFlushDelayMilliseconds": 10,
    "MaxEventBytes": 16777216,
    "MaxBufferedRoundBytes": 33554432,
    "MaxBufferedRoundFrames": 65536,
    "Upstream": {
      "MaxEventBytes": 16777216,
      "MaxProbeBytes": 16777216,
      "MaxProbeFrames": 1024,
      "MaxWebSocketMessageBytes": 33554432
    }
  }
}
```

Upstream event/probe limits fail explicitly rather than discarding data. The output adapter and writer share the root MaxEventBytes setting. The output wire event
limit is independent from upstream: WS JSON serialization may expand escaping. Size these together for large
terminal envelopes. Setting MaxBatchBytes=1 disables batching (event-boundary flush remains).
These are per-stream/event limits, **not** a completed tenant-wide heap/audit/session budget.
No schema changes or production provider credentials are required.

## Complete snapshot spool and multi-agent delta accumulation

Completed/attempt log slots now serialize **the complete supplied normalized JSON graph** into an
immutable, incrementally hashed staging stream. Small snapshots stay in bounded memory; larger
ones spill to private files. Ordinary string values use bounded escaped segments; normalized
container leaves flush to staging without per-leaf filesystem flush. Sealing precedes reading.
Existing content-addressed SHA/CDC boundaries, JSON bytes, slots and pricing inputs remain intact.
CDC planning retains a32KiB window; persistence batches at most16 chunks, detaching only their
inserted block entities after each batch. Metadata remains proportional to body size, capped by
body quota. Transaction rollback keeps externally owned snapshots retryable. Log-owned snapshots
are disposed even when encoding/DB writes fail; commit leaves only durable addresses.

This is **not yet** spool-backed streaming reconstruction: the old capture1MiB/256-item limits,
full converter accumulators, native-client tool batch/event lists and whole-round typed+wire queue
still exist. Their previous explicit capture metadata is not hidden or relabeled complete. The next
stage must replace those capture/accumulation paths and remove the whole-round dual representation
before the overall task is complete. A giant extension DTO/JsonElement/binary scalar can still need
serializer working memory; normalized ordinary string/list/dictionary paths are segmented.

Proposed configurable safety defaults (not production measurements):

```json
{
  "ProxyAuditSpool": {
    "Directory": "/var/lib/opencodex/audit-spool",
    "MemoryThresholdBytes": 262144,
    "MaxBodyBytes": 67108864,
    "MaxTotalBytes": 1073741824,
    "MaxFiles": 256,
    "OrphanAgeSeconds": 3600
  },
  "MultiAgent": {
    "Streaming": {
      "MaxAccumulatedDeltaCharacters": 8388608,
      "MaxAccumulatedDeltaFields": 4096
    }
  }
}
```

The default directory when omitted is `Path.GetTempPath()/opencodex-audit-spool`. Use a dedicated,
restricted directory and an OS disk quota; do not use an unrestricted shared multi-process path.
Snapshot bytes (including memory), open-body/file slots and fresh crash leftovers consume admission
quota. Limits are per spool singleton/process, not an atomic cross-process or tenant-wide quota.
Startup and lazy five-minute sweeps remove only expired exact managed filenames that can be opened
exclusively; live leases are protected, symlinks/unrelated files ignored. Disposal deletes spilled
files; crash leftovers remain quota-charged until reclaimed. Quota/disk errors propagate explicitly,
never produce truncated bodies. Client-cancelled requests still finalize accounting; the capture API
also supports explicit cancellation and releases partially created snapshots.

Multi-agent text/argument/input deltas append to request-owned chunked builders, sharing character
and field limits across concurrent live items. UTF16 character quota is not an exact heap-byte count.
Only closure/validation materializes a string; accepted authoritative terminal items discard builders
without another snapshot. Incomplete message closure retains exact accumulated whitespace; incomplete
tools remain non-executable. Runtime cancellation/finally clears builder references. Native-client
argument validation also uses builders but its retained event/tool batch policy is not changed yet.
HTTP/WS read the same options. No schema migration or production provider is needed.
