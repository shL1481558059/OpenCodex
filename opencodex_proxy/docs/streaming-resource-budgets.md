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
Argument suffixes are read without repeatedly materializing the old prefix. Deferred
Chat-to-Messages argument events preserve their original fragment boundaries on disk.
Final JSON strings are still materialized for response/audit contracts; end-to-end heap
is NOT constant, particularly during full Responses terminal generation/serialization.

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
done; request cancellation/disposal releases the journal. Native session snapshot/cache
lifecycle governance remains a separate pending change; WS transport cache limits above
must NOT be interpreted as bounding native-client session dictionaries.
