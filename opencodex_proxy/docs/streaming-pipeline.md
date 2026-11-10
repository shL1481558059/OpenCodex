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
