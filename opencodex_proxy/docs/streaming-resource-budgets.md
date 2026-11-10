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

A converted attempt shares its upstream accumulator with capture. Final JSON strings
are still materialized for the existing response/audit contracts; converter output
lists and accepted-round queues have NOT yet been removed. This change alone is not
the true-incremental rollout or a guarantee of constant end-to-end request heap.

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
