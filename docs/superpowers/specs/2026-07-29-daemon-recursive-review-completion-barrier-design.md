# CodeReviewDaemon Recursive Review Completion Barrier

**Date:** 2026-07-29  
**Status:** Approved design

## Problem

CodeReviewDaemon accepts a parent review run's final assistant text as authoritative as soon as the parent reaches terminal status. Background review sub-agents have independent lifecycles, so the parent can answer and the daemon can post before every child result is available.

Live PR #230 evidence proves the race:

- eight background review agents spawned at `2026-07-29T16:27:56Z`;
- inline findings were posted at `16:33:11Z`;
- the parent emitted its first final review at `16:33:43Z`;
- architecture and class-design child transcripts continued through `16:33:46Z`;
- three background spawns never delivered a `subagent-completion` notification to the parent before it finalized.

The S2S PR #230 and #231 reviews happened to complete all direct children before the parent final answer, but neither LmStreaming nor the daemon enforced that ordering. The daemon currently polls only the parent run status.

## Goals

1. Treat the first parent response as provisional and never post it.
2. Wait for every recursive descendant to reach a durable terminal state.
3. Detect late/nested child creation through roster stability checks.
4. Fail closed after 30 minutes rather than post an incomplete review.
5. Send a same-thread synthesis nudge after settlement and treat only that answer as authoritative.
6. Allow in-process agents to post inline after the barrier, verify delivery, and use one idempotent host summary only when no provider receipt/marker exists.
7. Use the host-side summary directly for S2S, whose hosted agent intentionally has no PR write credential.
8. Preserve correctness across daemon and LmStreaming host restarts.
9. Apply the same contract to S2S and in-process reviews.

## Non-goals

- Making PR #231 lifecycle webhooks authoritative. Lifecycle delivery is bounded and best-effort; it may optimize wakeups later but cannot determine correctness.
- Replacing the current LmStreaming conversation/workspace REST API.
- Changing review-agent selection or methodology.
- Blocking forever on failed children.

## Completion contract

Introduce a provider-neutral read seam:

```csharp
internal interface IReviewSubAgentCompletionSource
{
    Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run,
        string parentThreadId,
        CancellationToken cancellationToken);
}
```

Each node contains:

- agent ID and child thread ID;
- parent thread ID;
- name/template;
- recursive depth;
- `Running`, `Completed`, `Error`, `Stopped`, or `Unknown` status;
- terminal timestamp when known;
- optional safe failure code/summary.

`Completed`, `Error`, and `Stopped` are terminal. `Running` and `Unknown` are not.

### In-process implementation

Read live `SubAgentManager` snapshots recursively through a narrow read-only adapter. No execution handles leave the manager. Nested managers are traversed through their live child agent loops.

### S2S implementation

`LmStreamingS2SClient` reads a recursive completion endpoint. LmStreaming builds the response from live managers and durable child provenance.

### Durable terminal status

`SubAgentManager` stamps status and terminal timestamp into the child thread's persisted metadata when a terminal transition occurs. `SubAgentProvenance.TryProject` restores the exact state after restart. Legacy children without this data remain `Unknown`; they are never guessed complete.

## Recursive completion barrier

The barrier starts after the parent returns its provisional answer:

1. Poll the recursive snapshot with bounded 1–5 second backoff.
2. Wait until every discovered descendant is terminal.
3. Require two identical terminal snapshots separated by a two-second quiet period.
4. Snapshot identity compares node IDs, parent relationships, and terminal statuses.
5. A newly discovered late/nested child or a status change resets stability.
6. An empty tree also requires two matching snapshots, then opens quickly.
7. At 30 minutes, throw `TimeoutException`; no authoritative review is created and nothing posts.

Snapshot API failures retry within the same deadline. A legacy `Unknown` child keeps the barrier closed.

## Provisional and authoritative review flow

### Initial turn

The initial review attempt is always collect-only:

- `should_post=false` regardless of daemon posting configuration;
- no post-enforcement prompt;
- output is persisted as diagnostic `review-provisional` data only;
- notes may be written, but the answer is not judged or posted.

### Post-barrier synthesis nudge

After the barrier opens, send a second turn to the same parent conversation:

```text
All recursive review sub-agents are now terminal.
Completed: <safe inventory>
Failed/stopped: <safe inventory>

Re-read every delivered sub-agent result. Reconcile them with your provisional
review and notes. Disclose any failed review dimensions. Produce the definitive
review now; do not reuse the provisional answer unchanged unless it already
incorporates every completed result.
```

For in-process reviews, the nudge also instructs the agent to post line-inline findings and its summary. For S2S, it requests synthesis only.

The synthesis response:

- must be nonblank and successful;
- replaces the provisional answer;
- becomes the only `review` artifact;
- is the only text consumed by judge, notes retention, and host fallback.

A synthesis failure never falls back to provisional text.

## Posting and verification

### In-process

1. Allow the synthesis turn to post inline.
2. Verify delivery using the provider's existing head-scoped marker/backstop scan.
3. If a matching receipt/marker exists, do not post a host summary.
4. If missing or verification is unavailable, post the authoritative synthesis once through `ReviewPoster` using its existing idempotency key.

### S2S

The hosted LmStreaming agent has no PR write credential. After synthesis, the daemon posts the authoritative review through the existing host-side idempotent publisher.

No provisional answer can reach either delivery path.

## Child errors

`Error` and `Stopped` children are terminal and do not block forever. The synthesis inventory lists the failed dimensions without raw exceptions, secrets, or full prompts. The parent must use successful results and disclose missing dimensions.

If all dimensions fail, synthesis still runs and discloses that fact; the judge may reject the review.

## Restart and idempotency

Persist stage artifacts containing:

- provisional response and parent thread ID;
- latest recursive snapshot;
- barrier-open flag/timestamp;
- synthesis input ID and run ID;
- authoritative synthesis;
- provider verification outcome.

On restart:

- resume polling from durable child states;
- if synthesis was queued, poll its existing input ID instead of sending another;
- if the authoritative answer exists, resume posting verification;
- existing head-scoped idempotency prevents duplicate comments.

## Error policy

| Failure | Behavior |
|---|---|
| Child still running after 30m | Throw; no post; run becomes `RetryPending` |
| Snapshot API unavailable until deadline | Throw; no post |
| Legacy child status unknown | Keep waiting; timeout fail-closed |
| Child error/stopped | Terminal; include safe inventory in synthesis |
| Synthesis error/blank | Throw; never use provisional output |
| Inline verification unavailable/missing marker | Use idempotent host summary fallback |

## Testing

1. Parent final arrives while a direct child runs: no authoritative artifact, judge, or post.
2. A nested child appears between terminal snapshots: stability resets.
3. Recursive descendants all terminal: barrier opens only after two stable snapshots.
4. Completed/error/stopped children: synthesis inventory includes failures and opens barrier.
5. Running/unknown child at 30 minutes: timeout, no post, `RetryPending`.
6. Empty child tree: two quick empty snapshots open barrier.
7. Synthesis is a second turn on the same parent thread.
8. Synthesis answer replaces provisional text for artifact, judge, and notes.
9. Synthesis error/blank: no fallback to provisional.
10. Inline provider marker present: no host summary.
11. Marker absent: exactly one host summary across restart/retry.
12. S2S host-side delivery uses only authoritative synthesis.
13. In-process and S2S completion sources pass the same contract tests.
14. Child terminal state and timestamp reconstruct after LmStreaming restart.
15. Live timing gate: every descendant terminal timestamp precedes authoritative parent response and provider post.

## Acceptance criteria

- Parent completion alone can never trigger posting.
- Every recursive descendant is accounted for.
- Late nested spawns reset the barrier.
- No incomplete review posts on timeout or snapshot failure.
- The authoritative answer is generated after settlement on the same conversation.
- Failed dimensions are disclosed.
- Inline posting is verified and host fallback remains exactly-once.
- Correctness survives daemon and review-host restarts.
