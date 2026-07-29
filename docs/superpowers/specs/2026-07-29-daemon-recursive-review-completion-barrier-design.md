# CodeReviewDaemon Recursive Review Completion Barrier

**Date:** 2026-07-29  
**Status:** Approved design, adversarially reviewed

## Problem

CodeReviewDaemon accepts a parent review run's final assistant text as authoritative as soon as the parent reaches terminal status. Background review sub-agents have independent lifecycles, so the parent can answer and the daemon can post before every child result is available.

Live PR #230 evidence proves the race:

- eight background review agents spawned at `2026-07-29T16:27:56Z`;
- inline findings were posted at `16:33:11Z`;
- the parent emitted its first final review at `16:33:43Z`;
- architecture and class-design child transcripts continued through `16:33:46Z`;
- three background spawns never delivered a `subagent-completion` notification to the parent before it finalized.

The S2S PR #230 and #231 reviews happened to complete their direct children before the parent final answer, but neither LmStreaming nor the daemon enforced that ordering. The daemon currently polls only the parent run status.

## Goals

1. Treat the first parent response as provisional and never post it.
2. Wait for every review descendant visible through the review sub-agent provenance contract to reach a durable terminal state.
3. Detect roster growth, shrinkage, relationship changes, and status changes through stability checks.
4. Fail closed when the single 30-minute Reviewed-stage deadline expires rather than post an incomplete review.
5. Send a same-thread synthesis nudge after settlement and treat only that answer as authoritative.
6. Prevent the synthesis turn from spawning a new review generation after the barrier opens.
7. Allow in-process agents to post inline after the barrier, verify the required summary marker, and use one idempotent host summary only when no provider receipt exists.
8. Use the host-side summary directly for S2S, whose hosted agent intentionally has no PR write credential.
9. Preserve safe behavior across daemon and LmStreaming host restarts without persisting live execution handles.
10. Apply the same completion contract to S2S and in-process reviews while acknowledging their different recovery capabilities.

## Non-goals and explicit assumptions

- Making lifecycle webhooks authoritative. Lifecycle delivery is bounded and best-effort; it may optimize wakeups later but cannot determine correctness.
- Replacing the current LmStreaming conversation/workspace REST API.
- Enabling nested live `Agent` delegation. Child loops intentionally do not inherit `Agent`/`CheckAgent` today.
- Accounting for LmWorkflow controller/worker threads. They use a separate registry and do not currently stamp `sample.subAgentOf`; CodeReviewDaemon does not use them for review work. Any future review workflow integration must adopt this provenance contract before it can be considered barrier-safe.
- Parallelizing `PrPollingService`. A long Reviewed stage currently delays later PRs in the serial poller; this is an accepted initial operational limitation and must be measured during rollout.
- Resuming an in-process live child execution graph after daemon process loss. The safe recovery is to restart the review attempt, never to promote its provisional artifact.
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
- recursive depth when reconstructed from persisted provenance;
- `Running`, `Completed`, `Error`, `Stopped`, or `Unknown` status;
- terminal timestamp when known;
- optional safe failure code.

`Completed`, `Error`, and `Stopped` are terminal. `Running` and `Unknown` are not. Unknown or malformed wire statuses map to `Unknown`; they never deserialize to a terminal default.

### In-process source

The current live producer permits direct review children only: spawned child loops deliberately do not inherit sub-agent tools and have no nested `SubAgentManager`. The in-process source reads the complete direct roster from the exact parent loop's manager through immutable snapshots. It does not claim or enable live nested delegation, expose execution handles, or introduce a run-ID-to-loop registry.

The parent loop remains alive in one executor call stack for provisional collection, barrier waiting, and synthesis. Its `await using` scope ends only after synthesis finishes or the attempt fails. Disposing it between provisional and barrier would cancel children and clear the very roster the barrier must observe.

If nested live delegation is enabled later, that producer must extend the completion source and add equivalent descendant tests before CodeReviewDaemon may use it.

### S2S source and persisted provenance graph

`LmStreamingS2SClient` reads a versioned recursive completion response. LmStreaming performs one bounded conversation-store scan per request, builds a parent-thread-to-children index in memory, and walks from the requested root with a visited set. This graph reader is depth-agnostic even though current producers normally create depth-one trees. It must not perform another full store scan per descendant.

The response carries an explicit schema/capability version. A daemon receiving an old flat response, missing required relationship fields, or an unsupported version fails closed. Deployment order is host first, then daemon barrier enablement; ASP.NET's silent acceptance of an unknown `?recursive=true` query parameter must never be mistaken for capability support.

The existing non-recursive response remains additive and compatible. For the recursive contract, legacy persisted children whose old metadata says only `persisted` map deliberately to `Unknown`.

### Durable terminal status

The manager's terminal-transition path actively persists exact status and terminal timestamp to the child thread metadata when it sets `Completed`, `Error`, or `Stopped`. It does not wait for the terminal child to perform another metadata write. Existing metadata-write projection may continue to refresh provenance, but it is not the causal terminal persistence mechanism.

`SubAgentProvenance.TryProject` restores exact state after review-host restart. Legacy children without exact state remain `Unknown`; they are never guessed complete.

## One Reviewed-stage deadline

A single absolute deadline is computed when the provisional Reviewed-stage attempt starts and is persisted with that provisional artifact. The default is 30 minutes for the entire sequence:

1. provisional parent turn;
2. descendant barrier;
3. authoritative synthesis turn.

Each phase receives the same absolute deadline and may use only the remaining time. S2S polling must accept the explicit deadline instead of creating a fresh 30-minute window per turn. A daemon restart reloads the original timestamp/deadline; it does not grant another 30 minutes.

A dedicated barrier-deadline exception fails closed and consumes the existing bounded retry governor budget. Other transient Reviewed-stage errors retain their current poll-cycle retry behavior.

## Completion barrier

The barrier starts after the parent returns its provisional answer:

1. Poll the completion source with bounded 1–5 second backoff within the shared deadline.
2. Revalidate that the PR remains open and the reviewed head is unchanged before accepting a candidate terminal snapshot.
3. Wait until every discovered descendant is terminal.
4. Require two identical all-terminal snapshots separated by a two-second quiet period.
5. Snapshot identity compares node IDs, parent relationships, and statuses in deterministic order.
6. Any roster growth, roster shrinkage, relationship change, or status change resets stability.
7. An empty tree also requires two matching snapshots, then opens quickly.
8. Snapshot API failures retry within the same deadline.
9. At the deadline, throw; no authoritative review is created and nothing posts.

A legacy `Unknown` child keeps the barrier closed. `Error` and `Stopped` are terminal and are safely disclosed to synthesis.

## Provisional and authoritative review flow

### Initial turn

The initial review attempt is always collect-only:

- `should_post=false` regardless of daemon posting configuration;
- no post-enforcement prompt;
- output is persisted under `review-provisional` using the existing review payload shape plus the original Reviewed-stage timestamp/deadline;
- notes may be written, but the answer is not judged or posted.

### Post-barrier synthesis nudge

After the barrier opens, send a second turn to the same parent conversation:

```text
All review sub-agents visible to the completion contract are now terminal.
Completed: <safe inventory>
Failed/stopped: <safe inventory>

Re-read every delivered sub-agent result. Reconcile them with your provisional
review and notes. Disclose any failed review dimensions. Produce the definitive
review now; do not reuse the provisional answer unchanged unless it already
incorporates every completed result.
```

For in-process reviews, the nudge also instructs the agent to post line-inline findings and one summary carrying the canonical idempotency marker. For S2S, it requests synthesis only.

The synthesis execution profile must not expose the `Agent` spawn tool. It retains only the capabilities needed to read delivered results and, for in-process review, publish findings. This makes a new child generation impossible after the barrier. As defense in depth, the daemon takes a post-synthesis snapshot; any changed roster or status invalidates the answer and fails closed.

The synthesis response:

- must be nonblank and successful;
- replaces the provisional answer;
- becomes the only `review` artifact;
- is the only text consumed by judge, notes retention, and host fallback.

A synthesis-generation failure never falls back to provisional text. Provider-delivery verification failure is different: it invokes the idempotent host fallback rather than discarding a valid synthesis.

## Restart behavior and checkpoints

Persist only the state needed to recover safely:

- provisional response, parent thread ID, and original Reviewed-stage start/deadline;
- S2S synthesis input ID/run ID after the input is accepted;
- authoritative synthesis once complete;
- normal posting/outbox state through existing mechanisms.

Do not persist a duplicate full descendant-tree snapshot. Exact S2S child states already live in child provenance; after restart the barrier re-queries them and repeats the two-snapshot stability check.

On S2S restart:

- construct the S2S agent with the persisted parent thread ID rather than provisioning a new conversation;
- resume the barrier from durable child provenance and the original deadline;
- if synthesis was already accepted, poll its existing input ID instead of sending a duplicate input;
- if authoritative text exists, resume delivery verification/posting.

On in-process daemon restart:

- live loop and child execution state are gone;
- discard the incomplete attempt semantically and re-run the review from collect-only under the remaining retry policy;
- never treat the saved provisional answer as authoritative and never fabricate a resumable input ID.

A rollback to a pre-barrier daemon cannot interpret an in-flight provisional checkpoint. Operational rollback must stop intake and reset affected runs to re-enter from `ContextReady`; provisional text is never promoted.

## Posting and verification

### In-process

1. Build a synthesis-specific key with existing `IdempotencyKey.Build` components; do not introduce another marker grammar.
2. Require the agent's issue-level summary to embed that canonical marker. Inline line comments need not duplicate it.
3. Verify the summary with existing `IReviewCommentPublisher.FindPostedCommentAsync` and provider backstop scan.
4. If found, record/adopt delivery without posting another host summary.
5. If missing or verification throws, post the authoritative synthesis once through `ReviewPoster` using its existing idempotent outbox flow.

Verification proves the required summary receipt, not the existence of every line-level inline comment.

### S2S

The hosted LmStreaming agent has no PR write credential. Skip inline verification and post the authoritative synthesis directly through the existing host-side idempotent publisher.

Immediately before delivery, revalidate that the PR is still open and the head SHA still matches. A closed/merged PR or changed head aborts this run without posting stale output.

No provisional answer can reach either delivery path.

## Child errors

`Error` and `Stopped` children are terminal and do not block forever. The synthesis inventory lists failed dimensions using name/template/status and an optional safe failure code only—never raw exceptions, secrets, prompts, or transcript contents. The parent uses successful results and discloses missing dimensions.

If every dimension fails, synthesis still runs and discloses that fact; the judge may reject the review.

## Error policy

| Failure | Behavior |
|---|---|
| Shared Reviewed-stage deadline expires | Throw dedicated barrier/deadline exception; no post; `RetryPending`; bounded retry governor applies |
| Snapshot API unavailable until deadline | Same fail-closed deadline behavior |
| Legacy or malformed child status | Map to `Unknown`; keep waiting; deadline fails closed |
| Child error/stopped | Terminal; include safe inventory in synthesis |
| PR closes/merges or head changes while waiting | Abort stale attempt; no synthesis/post for the old head |
| Synthesis spawns/changes roster despite tool restriction | Reject synthesis; fail closed |
| Synthesis generation error/blank | Throw; never use provisional output |
| Inline verification unavailable/missing marker | Use idempotent host summary fallback |
| Daemon restart during in-process barrier | Restart collect-only attempt; never resume/promote provisional |

## Rollout and compatibility

1. Deploy the LmStreaming host implementation and recursive schema capability first.
2. Confirm the recursive endpoint returns the supported schema version and required relationship/status fields.
3. Deploy/enable the daemon barrier second.
4. Update the bundled TypeScript sub-agent DTO with `unknown` and additive relationship/timestamp fields.
5. Preserve the old non-recursive endpoint shape; disclose that recursive legacy `persisted` maps to `unknown`.
6. For rollback, stop intake and reset in-flight provisional runs to `ContextReady` before running an older daemon.

## Testing

1. Parent final arrives while several background children run: no authoritative artifact, judge, or post.
2. Multiple children complete in different orders: barrier opens only after every child is terminal.
3. Persisted child→grandchild provenance through a terminal ancestor is traversed; this tests graph reading, not currently-disabled live nested spawning.
4. Roster addition, removal, parent change, or status change between snapshots resets stability.
5. Completed/error/stopped children are terminal; running/unknown block.
6. Empty child tree requires two quick matching snapshots.
7. Manager terminal transition persists exact status/timestamp even when the child performs no later metadata write.
8. S2S malformed/unknown status maps to nonterminal `Unknown`.
9. Old host/flat response or unsupported schema fails closed.
10. In-process parent loop is not disposed until after synthesis.
11. Synthesis is a second turn on the same parent thread and cannot spawn agents.
12. Post-synthesis roster change rejects the answer.
13. S2S restart reuses the persisted parent thread and existing accepted synthesis input.
14. Restart after 25 elapsed minutes receives only five remaining minutes.
15. Synthesis answer replaces provisional text for artifact, judge, notes, and fallback.
16. Synthesis error/blank never falls back to provisional.
17. In-process summary marker present means no host summary; absent/verification exception means exactly one fallback across restart.
18. S2S skips inline verification and posts only through `ReviewPoster`.
19. Closed/merged PR or changed head during wait/post produces no stale comment.
20. Live timing gate uses multiple delayed children and proves:
   `max(descendantTerminalAt) <= synthesisQueuedAt < authoritativeAnswerAt <= providerPostAt < judgeAt`.
21. Live rollout includes another queued PR to measure and disclose serial-poller delay.

## Acceptance criteria

- Parent completion alone can never trigger judging or posting.
- Every descendant visible through the declared review provenance contract is accounted for.
- Roster or status changes reset the barrier.
- The initial loop remains alive until its children settle and synthesis finishes.
- One persisted 30-minute deadline covers provisional, barrier, and synthesis across restart.
- No incomplete or stale-head review posts on timeout, snapshot failure, lifecycle change, or synthesis roster change.
- The authoritative answer is generated after settlement on the same conversation.
- Failed dimensions are safely disclosed.
- Inline summary delivery is verified with the existing canonical marker, and host fallback remains exactly once.
- S2S restart resumes the known thread/input; in-process restart safely reruns rather than fabricating resumability.
- Host/daemon version skew fails closed.
