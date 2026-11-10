# Streaming audit and Responses context resource budgets

All sizes below are admission limits, not truncation targets or measured optimal settings.
The existing normalized JSON audit projection is unchanged (for example Responses request
instructions/tools are not copied into the response-body slot).

## Complete upstream capture

`ProxyStreaming` accepts `MaxCaptureBytes` (64 MiB), `MaxCaptureItems` (4096), and
`MaxCaptureFields` (8192). Production proxy capture no longer uses the legacy diagnostic
1 MiB / 256-item truncation mode. Byte accounting is conservative cumulative captured
work, including authoritative done/terminal payloads which may repeat earlier deltas;
it is **not** exactly the final response JSON length. Quota, malformed JSON, I/O and
cancellation errors are explicit, never a successful silently truncated audit.

Text/arguments append as raw UTF16, preserving split surrogate deltas. They spill through
the existing `ProxyAuditSpool` manager (memory threshold 256 KiB per active
buffer; body 64 MiB; process total 1 GiB; active buffers/files 256; orphan age 1 hour).
In-memory staging buffers count toward these quotas too. Thus file/body admission can
be reached before the higher collection/field limit. Operators must configure these
budgets together. Cancel/dispose releases staging files; existing startup/lazy orphan
cleanup also covers these buffers. Use a dedicated private directory and OS filesystem
quota if several processes share a filesystem; this manager is not a cross-process or
per-tenant disk-quota service.

A converted attempt shares its upstream accumulator with capture. Production converted
frames are now forwarded incrementally: there is no accepted-round typed+wire queue.
Converter final text/argument reconstruction spills rather than retaining every delta.
Argument suffixes are read without repeatedly materializing the old prefix. The custom
JSON-string decoder drops already consumed raw input while retaining escape/surrogate state;
prefix data before a recognized envelope value can still consume request capture budget. Deferred
Chat-to-Messages argument events preserve their original fragment boundaries on disk.
Final JSON strings are still materialized for response/audit contracts; end-to-end heap
is NOT constant, particularly during full Responses terminal generation/serialization.
Generated large events count encoded bytes without first retaining a UTF16 wire string;
legacy wire encoding uses bounded string segments, not a whole intermediate JSON string.
Diagnostic capture obtains at most its existing 2000-character prefix without encoding
an entire terminal. Small generated graphs with a conservative 32 KiB JSON upper bound
retain the single-encode fast path. Final legacy wire strings and full response graphs
still materialize; upstream/converter spill fields are still separate, so disk use can
include both. This is not a strict constant-heap or single-copy-spool guarantee.

Semantic commitment is conservative, before handing any role, response ID, tool start,
text or unknown business event to the writer. No transparent whole-round retry/failover
after that point. Headers/comments/ping alone do not commit. Internal search executes
only after the full upstream round is accepted and validated; later failures do not
repeat already executed searches. Already emitted reasoning cannot be retracted when
a later tool name is invalid (an intentional change from whole-round buffering).

One final protocol terminal plus optional Responses [DONE] is withheld until complete
audit persistence succeeds. Audit failure cannot publish success. A terminal write/flush
failure is never replayed and updates failure logging; bytes already delivered cannot be
retracted, and transport failure can still end as an aborted stream.

## Responses WebSocket context snapshots

`ProxyStreaming:Sessions` defaults:

| Setting | Default |
| --- | ---: |
| MaxBytes | 134217728 |
| MaxTenantBytes | 33554432 |
| MaxSnapshotBytes | 16777216 |
| MaxSnapshots | 2000 |
| SnapshotTtlSeconds | 900 |
| CleanupIntervalSeconds | 60 |
| MaxConversationLocks | 2048 |
| MaxWaitersPerConversation | 128 |
| MaxProbes | 1024 |
| ProbeTtlSeconds | 300 |

Weights conservatively estimate retained managed JSON graphs (including UTF16 strings
and collection/reference overhead), not exact physical heap. Read hits refresh idle TTL.
Admission/eviction updates response and latest aliases and tenant accounting together.
Unsupported/oversized graphs are not cloned/cached; successful proxy output is unaffected.
A subsequent `context_pool` continuation whose snapshot has expired/been evicted receives
the existing 409 full-history-required response. Native HTTP/passthrough recovery behavior
is unchanged. These limits govern this transport cache, not every native-client/search
continuation store or the separate already bounded WebSocket connection pool.

Conversation lock leases include registered waiters before waiting. Cancellation removes
that reference, and the final lease removes/disposes the semaphore under the same registry
lock. Active-key/waiter excess fails explicitly with 503. One service-level cleanup timer
expires idle snapshots/probes even without request traffic; no per-delta timer is added.

## Native client tool batches

`MultiAgent:Streaming:MaxBufferedEventBytes` defaults to 67108864. Executable tool batches
must still pass final identity/payload validation and continuation commit BEFORE executable
done/success publication. Their deferred events and validation events now share one
record-length-prefixed spool journal, not two in-memory DTO lists. Non-tool preceding
output remains live. Original event order/fragmentation is preserved when publishing.
Replay validation/publishing rereads/decode this journal; this is not a claim that disk
reconstruction is zero-parse. Quota/disk failure publishes failure, never partial executable
done; request cancellation/disposal releases the journal. Native session snapshot/cache lifecycle is governed separately below; WS transport cache
limits above are not interchangeable with native-client continuation limits.

## Outer native / multi-agent HTTP output

The HTTP push callbacks now feed the same complete-event writer through one request-owned
bounded consumer. `ProxyStreaming:MaxQueuedEvents` defaults to 8 and `MaxQueuedBytes` to
1048576 (UTF8 wire weight, not exact managed heap). Queued/in-flight weight is bounded by
`max(MaxQueuedBytes, MaxEventBytes)`; a single larger event must be alone, and one producer
may hold a not-yet-admitted event. `MaxEventBytes` defaults to 16777216. Limits reject or
backpressure; no silent truncation or unbounded task/channel queue. Payload graphs are
frozen before enqueue. Native/multi-agent HTTP still ends at its Responses terminal,
without newly adding [DONE]. Existing general proxy DONE behavior stays unchanged.

`MaxBatchBytes` (16384) and `MaxFlushDelayMilliseconds` (10) govern complete-event batches.
First/business/terminal events flush immediately; one reusable timer flushes while the
producer is idle. There is no per-delta CTS/timer or parallel MoveNext. Ordinary Emit
means bounded acceptance, not immediate flush. Terminal Emit waits for the actual flush;
once attempted, a failed terminal is not replaced with a contradictory second terminal.
Consumer failure cancels the producer request; disposal awaits/drains it and releases the
budget. Real socket buffers/proxies can add their own buffering, and delivered bytes
cannot be retracted after a later flush failure. WebSocket retains its message boundaries,
not an artificial HTTP flush policy.


## Native client continuations and request leases

`MultiAgent:NativeClientCache` configures the client-owned session store (not legacy
`MultiAgentClientStore`/`MultiAgentRunStore` runtime bindings). Defaults:

| Setting | Default |
|---|---:|
| MaxBytes / MaxTenantBytes / MaxSnapshotBytes | 128 / 32 / 16 MiB |
| MaxResponses / MaxTenantResponses | 2048 / 512 |
| SnapshotTtlSeconds / SessionTtlSeconds / CleanupIntervalSeconds | 900 / 900 / 60 |
| MaxSessions / MaxTenantSessions / MaxGroupSessions | 4096 / 512 / 256 |
| MaxWaitersPerSession / MaxIdentityCharacters | 128 / 512 |
| MaxReplayIdentities / MaxTenantReplayIdentities / MaxSessionReplayIdentities | 65536 / 8192 / 4096 |
| MaxEphemeralIdentities / MaxTenantEphemeralIdentities | 65536 / 8192 |

Weights conservatively include graph, strings, dictionaries and snapshot-intern metadata
(JSON graph estimate times four plus fixed overhead), not an exact CLR heap measurement.
`persist=false` response/replay aliases share eviction; dropping a record also drops its
session intern table so interned prefixes cannot outlive the budget via that table. TTL
is checked on read as well as periodic cleanup. Oversize commit fails explicitly (503),
before executable tool completion/success is published. There is no hidden truncation.

Request references include registered thread-gate waiters and child-model-permit waiters.
Cancellation and lease disposal release the reference. Only whole parent/child groups
without any owners or waiters can retire; live groups reject excess admission instead of
creating a second lock. Final release refreshes idle time before the count reaches zero.
Persistent groups restore lazily, with all group agent names checked before admitting a
new binding. The old record/index formats and crash recovery remain intact. A cold restore
still scans historical identity directories: RAM admission is bounded, scan CPU/I/O is not
constant. Durable record retention and complete per-request history materialization are
not limited by these **memory-cache** settings. Persistent writes stream JSON instead of
building an additional full JSON string; serializer buffers are not claimed constant-size.

Evicting a replay payload must never re-run its tools. An exact bounded SHA256 turn-key
set distinguishes an evicted old turn (409) from a genuinely new turn. A live session's
safety-key quota is checked before the model call. On ephemeral whole-group retirement,
process-lifetime hashed thread/root tombstones prevent recreating an old lock or executing
an old turn. Identity slots are reserved before admission and deliberately **not recycled**:
new identities fail 503 when these finite safety quotas fill, even when payloads/gates have
returned to zero. `persist=false` writes no recovery data to disk; replay protection does
not survive process restart. This is a safety/availability tradeoff, not unbounded uptime
with zero retained metadata. Start a new thread/turn with full history on an expired
continuation; a persistent identity can instead recover its existing durable group/record.

## Responses WebSocket connection history / interrupt IDs

`MultiAgent:ClientWebSocketCache` governs the `/v1/responses` client socket's optional
response records and known interrupt IDs, separately from the upstream WS pool and the
transport conversation cache. Defaults:

| Setting | Default |
|---|---:|
| MaxBytes / MaxTenantBytes / MaxConnectionBytes / MaxSnapshotBytes | 128 / 32 / 16 / 8 MiB |
| MaxEntries / MaxConnectionEntries | 4096 / 256 |
| TtlSeconds / CleanupIntervalSeconds / MaxKeyCharacters | 900 / 60 / 512 |

Global, trusted-API-key tenant and connection budgets all apply. Weights include IDs and
owned request/response graphs; records/known IDs share the entry budget and TTL/LRU.
An oversize result is delivered but not cached. Eviction yields the existing
`previous_response_not_found` / full-history requirement, never a model replay. Interrupts
can still target the current active/terminal request; older IDs are not retained forever.
Connection disposal clears its entries/IDs. One weak-owner periodic timer per registry,
not per delta, reclaims inactive entries. Closed-socket sends fail explicitly.

## Audited internal model stream batches

`IProxyStreamWriter.WriteFrameBatchAsync` is an additive default-method API: a nonfinal
live segment need not contain a terminal. `WriteFramesAsync` still finishes/validates a
complete stream. Stateful multi-agent Responses collection retains accepted output items
across nonfinal segments and releases them after the audited terminal/error. Tracking
writers propagate this distinction without weakening semantic commitment or retry rules.
A malformed empty success is rejected before invoking the public success callback.

Missing final usage still follows the existing numeric fallback; this patch does not
change billing or claim unknown usage is confirmed zero. No schema or migration is added.
