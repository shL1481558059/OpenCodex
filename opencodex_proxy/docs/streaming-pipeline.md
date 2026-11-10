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
* The six converters retain public string APIs; internal overloads accept shared decoded events.
  Consumers must not mutate the shared input graph. Cross-protocol generated **output** strings
  are decoded once in the output adapter for metadata/log observation. Native typed output
  emitters and the built-in-search output string reparsing are still follow-up work.
* Legacy standalone string capture/APIs remain compatible; those legacy callers may still parse.
  `SseFrameReader.ReadLines` accepts physical lines (with/without terminators) or full frames.
  Arbitrary character chunks must use `ReadChunks` (including CR/LF split across chunks).
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
