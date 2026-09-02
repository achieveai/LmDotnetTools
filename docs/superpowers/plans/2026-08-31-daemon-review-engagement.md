# CodeReviewDaemon Review Engagement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one durable PR engagement lifecycle with dynamic context, non-blocking discussion, typed agent-owned publication, complete audit evidence, and merged-close learning and outcome reporting.

**Architecture:** Keep the current five-stage code-review machine beneath one PR-scoped engagement coordinator. The coordinator coalesces provider activity behind a one-hour gate and admits typed `CodeReview`, `DiscussionFollowUp`, or `MergedClose` rounds. Complete model/tool evidence is captured at the hosted model-turn boundary and stored separately from bounded projections.

**Tech Stack:** .NET 9, C# 13, SQLite, xUnit, FluentAssertions, LmStreaming S2S, LmMultiTurn, GitHub REST/GraphQL, Azure DevOps Git REST 7.1, Markdown/HTML/SVG, Node acceptance checks, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-30-daemon-review-engagement-design.md`

## Global Constraints

- Keep `ReviewStage` exactly `Discovered → ContextReady → Reviewed → Judged → Posted`. Discussion and close processing are engagement rounds, not new review stages.
- Keep one coordinator per `(provider, repository, PR)`, identified internally by one surrogate `PrEngagementId`.
- Apply the minimum one-hour gate after a completed `CodeReview` or `DiscussionFollowUp` to both comments and pushed heads. Observe and coalesce immediately. Merge alone bypasses the gate.
- Prefer a new head over pending same-head discussion. One PR lease permits one active round.
- Questions are non-blocking. Publish supported conclusions now; withhold only the conclusion that depends on the unanswered assumption.
- The parent agent owns wording, grouping, action choice, and no-op choice. Typed backend code owns credentials, scope, staleness, anchor validity, idempotency, provider backstop, and receipts.
- Do not parse fenced Markdown/YAML to recover actions. Do not expose provider credentials or raw credential-bearing REST to model text or tool arguments.
- Preserve ADO thread/comment/parent/status/span/iteration identity and GitHub inline-thread identity. Record GitHub PR-level flat-comment degradation explicitly.
- Capture every complete model request and canonical returned message after boundary-level secret exclusion. Never tail-clip the sole audit copy. Missing or severed capture is a typed gap.
- Retain raw audit records and immutable observations indefinitely. A deletion policy is separate work.
- Produce `outcome.json`, `OUTCOME.md`, and promotions only for `Merged`. `Closed` and `Abandoned` retain evidence but receive no report or promotion.
- Run deterministic candidate collection before model classification. An independent verifier checks every semantic label and citation. Unresolved labels are `indeterminate`.
- Promote general knowledge before append-only `DeveloperLearnings/`. Promotion is idempotent by source-observation ID. The legacy whole-file developer writer becomes migration input only.
- SQLite migrations are append-only. Existing versions 1–8 are immutable; this plan starts at version 9.
- `ReviewStore` remains the synchronized owner of its SQLite connection. New persistence methods use its existing `_gate`; no competing connection is introduced.
- Feature flags default off. Collect-only remains the default. No live provider write is enabled by implementing or approving this plan.
- Use CSharpier. Do not use `dotnet format`.
- Preserve unrelated working-tree changes. Do not create `v2`, `improved`, or `enhanced` duplicate files.
- Do not add AI, Claude, or `Co-Authored-By` signatures to commits or PR text.
- Commit commands in this plan are optional execution checkpoints. Skip every commit unless the user explicitly authorizes commits for that implementation session; plan approval alone is not commit authorization.

## Delivery phases and gates

1. **Contract and report:** validate the existing standalone HTML report, add the clearly labeled target graph, and pin the companion plugin contract. No daemon behavior changes.
2. **Evidence and coordination foundation:** land exact turn capture, the durable engagement/round schema, authenticated storage/ingestion, coordinator admission, dynamic context, cooldown, and shadow seeding. Gates: complete-audit fixtures pass; eligibility and provider writes remain off.
3. **Participation and publication:** land immutable questions/observations, discussion-only execution, typed backend operations, provider fidelity, and parent-only tool exposure. Gates: collect-only action plans first; live writing remains separately gated per provider.
4. **Merged outcomes and cutover:** land durable close recovery, deterministic candidates, independent verification, reports, ordered promotions, end-to-end proof, and legacy retirement. Gates: reports precede promotion; migration evidence precedes retirement.

## File map

### New files in `LmDotnetTools`

- `src/LmMultiTurn/Audit/IMultiTurnAuditSink.cs` — optional exact-turn capture contract and inert default.
- `src/LmMultiTurn/Audit/AuditCaptureException.cs` — typed fail-closed audit-delivery failure.
- `src/LmMultiTurn/Audit/MultiTurnAuditModels.cs` — scope, source-record, message, completion, and gap contracts.
- `src/LmMultiTurn/Audit/AuditMessageSerializer.cs` — stable complete `IMessage` serialization used by request and response capture.
- `samples/CodeReviewDaemon.Sample/Persistence/Models/PrEngagement.cs` — coordinator and round domain types.
- `samples/CodeReviewDaemon.Sample/Persistence/Models/AuditSourceRecord.cs` — audit metadata, chunk, and redaction types.
- `samples/CodeReviewDaemon.Sample/Persistence/Models/ClarificationQuestion.cs` — durable question lifecycle.
- `samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewAction.cs` — typed action/rejection/receipt domain.
- `samples/CodeReviewDaemon.Sample/Persistence/Models/RoundObservation.cs` — immutable observation and close-item types.
- `samples/CodeReviewDaemon.Sample/Orchestration/PrEngagementCoordinator.cs` — event coalescing, lease, precedence, and execution dispatch.
- `samples/CodeReviewDaemon.Sample/Orchestration/EngagementCutoverSeeder.cs` — idempotent adoption of existing open PRs.
- `samples/CodeReviewDaemon.Sample/Orchestration/ProviderActivitySnapshot.cs` — provider-qualified monotonic activity boundary and native refs.
- `samples/CodeReviewDaemon.Sample/Orchestration/DiscussionRoundExecutor.cs` — triage and focused follow-up path.
- `samples/CodeReviewDaemon.Sample/Orchestration/IReviewPublicationOperations.cs` — six provider-neutral operations.
- `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPublicationCoordinator.cs` — validation, outbox, backstop, receipts, and collect-only behavior.
- `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseCandidateInventory.cs` — deterministic denominator.
- `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseRoundExecutor.cs` — durable close-stage orchestration.
- `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseOutcomeWriter.cs` — `outcome.json` and `OUTCOME.md` projections.
- `samples/CodeReviewDaemon.Sample/Orchestration/ClosePromotionPipeline.cs` — ordered, idempotent promotion.
- `samples/CodeReviewDaemon.Sample/Agents/DiscussionAgent.cs` — discussion triage/interpretation over a spawn-suppressed hosted turn.
- `samples/CodeReviewDaemon.Sample/Agents/CloseAnalystAgent.cs` — semantic candidate labels.
- `samples/CodeReviewDaemon.Sample/Agents/CloseVerifierAgent.cs` — source-based independent verification.
- `samples/CodeReviewDaemon.Sample/Controllers/ReviewAuditController.cs` — authenticated chunk ingestion and status.
- `samples/CodeReviewDaemon.Sample/Controllers/ReviewPublicationController.cs` — authenticated typed action endpoint.
- `samples/CodeReviewDaemon.Sample/Security/ReviewBridgeAuthAttribute.cs` — constant-time reverse-S2S authentication.
- `samples/LmStreaming.Sample/Services/ReviewAuditBridge.cs` — exact audit sink and retry/status client.
- `samples/LmStreaming.Sample/Services/ReviewPublicationFunctionProvider.cs` — six typed model tools backed by daemon HTTP.
- `samples/LmStreaming.Sample/Services/ConversationReviewScope.cs` — validated provisioned scope metadata.
- Focused test files named in each task below.

### Existing files modified

- `scratchpad/conversation_memories/review-daemon-control-flow-report/verify-report.mjs`
- `scratchpad/conversation_memories/review-daemon-control-flow-report/review-daemon-control-flow.html`
- `src/LmMultiTurn/Lifecycle/MultiTurnLifecycleServices.cs`
- `src/LmMultiTurn/MultiTurnAgentBase.cs`
- `src/LmMultiTurn/MultiTurnAgentLoop.cs`
- `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`
- `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/IPrProvider.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/AdoPrProvider.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/PrPollingService.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/PrOrchestrator.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/GitHubReviewCommentPublisher.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/AdoReviewCommentPublisher.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/ReviewNotesArtifactBuilder.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs`
- `samples/CodeReviewDaemon.Sample/Agents/IReviewAgentLoopFactory.cs`
- `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgentLoopFactory.cs`
- `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- `samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs`
- `samples/CodeReviewDaemon.Sample/Agents/AtCloseExtractionSeam.cs`
- `samples/CodeReviewDaemon.Sample/Agents/DeveloperLearnings/DeveloperObservation.cs`
- `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`
- `samples/CodeReviewDaemon.Sample/Program.cs`
- `samples/LmStreaming.Sample/Models/ConversationDtos.cs`
- `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- `samples/LmStreaming.Sample/Services/ModeCapabilities.cs`
- `samples/LmStreaming.Sample/Prompts.yaml`
- `samples/LmStreaming.Sample/Program.cs`

### Companion repository files

- `B:\sources\claude_plugins\code-reviewer\skills\post-pr-review\SKILL.md`
- `B:\sources\claude_plugins\code-reviewer\agents\pr-context-gatherer.md`
- `B:\sources\claude_plugins\docs\superpowers\plans\2026-09-02-review-daemon-typed-publication.md` — companion implementation/release plan created in Task 2.

---

## Phase 1 — Contract and report

### Task 1: Add the target-state graph to the existing HTML report

**Files:**
- Modify: `scratchpad/conversation_memories/review-daemon-control-flow-report/verify-report.mjs`
- Modify: `scratchpad/conversation_memories/review-daemon-control-flow-report/review-daemon-control-flow.html`

**Interfaces:**
- Consumes: approved spec sections 3–11 and the existing report's design tokens/navigation.
- Produces: one section with `id="target-state"`, visibly labeled `Target state — proposed, not implemented`, between Recovery and Evidence.

- [ ] **Step 1: Make the static acceptance checker fail first**

Add checks before changing the HTML:

```js
["target-state section", /id="target-state"/i],
["target is explicitly proposed", /Target state[^<]{0,40}proposed[^<]{0,40}not implemented/i],
["target coordinator", /PR engagement coordinator/i],
["three target intents", /CodeReview[\s\S]*DiscussionFollowUp[\s\S]*MergedClose/i],
["one-hour coalescing", /(one.hour|1.hour)[\s\S]{0,240}coalesc/i],
["new-head precedence", /new head[\s\S]{0,180}(precedence|takes priority|wins)/i],
["typed publication", /typed[^<]{0,80}publication/i],
["complete audit source", /complete audit source/i],
["target text alternative", /id="target-state"[\s\S]*class="diagram-alternative"/i],
```

Also assert section order with index comparisons: `recovery < target-state < evidence`.

- [ ] **Step 2: Run RED**

```powershell
node scratchpad/conversation_memories/review-daemon-control-flow-report/verify-report.mjs
```

Expected: the new target checks fail because the report still has only current/as-is sections.

- [ ] **Step 3: Add the target section without changing current diagrams**

Add one navigation link. Insert one section after Recovery. Use the report's existing SVG/card styles and this exact flow/content:

```text
Provider poll
  → PR engagement coordinator
      → cooldown active: coalesce head + external activity
      → eligible + new head wins: CodeReview
          → dynamic pr-context-gatherer
          → specialists + recursive barrier
          → qualified synthesis + non-blocking questions
          → typed agent-owned publication
          → immutable observations + complete audit source
      → eligible + unchanged head + useful comments: DiscussionFollowUp
          → new discussion + prior observations
          → focused code reads only; no full review fleet
          → typed replies/questions + one summary delta
      → merged, bypass gate: MergedClose
          → deterministic candidates
          → analyst + independent verifier
          → KB then DeveloperLearnings promotion
          → outcome.json + OUTCOME.md
Completed code/discussion round → next eligible at +1 hour
```

Keep every existing current/as-is SVG byte-for-byte. Renumber Evidence to `07`. Give the new SVG `role="img"`, an `aria-labelledby` title/description, visible legend labels, arrowheads, and a complete HTML text alternative.

- [ ] **Step 4: Run GREEN and structural checks**

```powershell
node scratchpad/conversation_memories/review-daemon-control-flow-report/verify-report.mjs
```

Expected: every old and new check passes.

- [ ] **Step 5: Validate rendered behavior**

Serve the report and inspect desktop, mobile, dark, light, print, keyboard navigation, horizontal overflow, SVG labels, and console errors:

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory scratchpad/conversation_memories/review-daemon-control-flow-report
```

Use one self-contained Playwright call to visit `http://127.0.0.1:8765/review-daemon-control-flow.html`, test viewports `1440x1000` and `390x844`, toggle `prefers-color-scheme`, emulate print, tab through navigation, and return structured failures. Save desktop/mobile screenshots under the existing scratchpad folder. Stop the temporary server after inspection.

- [ ] **Step 6: Launch for human review**

Open the validated HTTP URL in the user's browser. Do not commit scratchpad artifacts unless the user separately asks.

---

### Task 2: Define and release the companion plugin contract

**Files:**
- Create in companion repo: `B:\sources\claude_plugins\docs\superpowers\plans\2026-09-02-review-daemon-typed-publication.md`
- Modify in companion repo: `B:\sources\claude_plugins\code-reviewer\skills\post-pr-review\SKILL.md`
- Modify in companion repo: `B:\sources\claude_plugins\code-reviewer\agents\pr-context-gatherer.md`
- Test in companion repo: use its existing plugin validation/eval surface named by that repo's `CLAUDE.md`.

**Interfaces:**
- Consumes: typed tool names `CreateRootSummary`, `AppendSummaryDelta`, `SubmitInlineFindings`, `PostClarificationQuestion`, `ReplyToDiscussion`, `FinalizeRound`.
- Produces: plugin content that never asks a daemon review agent to use raw `gh`, ADO REST, or fenced action blocks for publication.

- [ ] **Step 1: Write the companion plan before editing plugin content**

The companion plan must pin:

```yaml
publication_tools:
  - CreateRootSummary
  - AppendSummaryDelta
  - SubmitInlineFindings
  - PostClarificationQuestion
  - ReplyToDiscussion
  - FinalizeRound
context_contract:
  heading: "## Daemon-Supplied Context"
  linkage_states: [Linked, NoneLinked, Failed, Unavailable]
  context_agent_may_iterate: true
```

It must state that operation inputs are typed, provider credentials remain in the daemon, agent text is passed unchanged after mechanical validation, and ordinary non-daemon use retains the plugin's existing provider workflow.

- [ ] **Step 2: Add failing plugin assertions**

Add or extend the companion validator so daemon mode requires all six tool names and rejects instructions that tell the daemon agent to emit `review-actions` YAML or call raw provider posting endpoints.

- [ ] **Step 3: Run companion RED**

Run the exact validation command recorded in the companion repo plan. Expected: current `post-pr-review` content fails because it still directs provider-specific posting.

- [ ] **Step 4: Update the existing skill and agent files**

Add a daemon typed-backend branch to `post-pr-review`. Keep the current ordinary branch. Keep `pr-context-gatherer` read-only and iterative; update only the bootstrap contract names and sourced-manifest requirement. Do not create alternate skill/agent files.

- [ ] **Step 5: Run companion GREEN and package validation**

Run the companion validator and plugin package validator. Record the released plugin commit/version and installed-content hash in the execution ledger. Installing/releasing/publishing the companion is an external action and requires just-in-time user authorization during execution.

- [ ] **Step 6: Commit in the companion repository**

```powershell
git add docs/superpowers/plans/2026-09-02-review-daemon-typed-publication.md code-reviewer/skills/post-pr-review/SKILL.md code-reviewer/agents/pr-context-gatherer.md
git commit -m "feat(code-reviewer): use typed daemon review publication"
```

Do not push or publish without separate authorization.

---

## Phase 2 — Evidence and coordination foundation

### Ordering prerequisite for Tasks 3–10

Task 7 owns the coordinator domain and migration v9, but its schema/store slice must execute before Task 4 because v10 audit records have a required `engagement_round` foreign key. Execute **Task 7 Steps 1–7 immediately after Task 3**, then return to Tasks 4–6 and continue with Tasks 8–10. This preserves one migration owner and makes the audit foundation round-correlated from its first persisted record.

### Task 3: Add an exact model-turn audit seam to LmMultiTurn

**Files:**
- Create: `src/LmMultiTurn/Audit/IMultiTurnAuditSink.cs`
- Create: `src/LmMultiTurn/Audit/AuditCaptureException.cs`
- Create: `src/LmMultiTurn/Audit/MultiTurnAuditModels.cs`
- Create: `src/LmMultiTurn/Audit/AuditMessageSerializer.cs`
- Modify: `src/LmMultiTurn/Lifecycle/MultiTurnLifecycleServices.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentBase.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs`
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- Create: `tests/LmMultiTurn.Tests/Audit/MultiTurnAuditCaptureTests.cs`
- Modify: `tests/LmMultiTurn.Tests/MultiTurnAgentLoopConstructorCompatibilityTests.cs`
- Modify: `tests/LmMultiTurn.Tests/Lifecycle/SubAgentLineagePropagationTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record MultiTurnAuditScope(string EngagementId, string RoundId);

public enum AuditCaptureOutcome { Complete, Gap, Redacted }

public sealed record ModelTurnAuditRecord(
    string RecordId,
    MultiTurnAuditScope Scope,
    string ThreadId,
    string RunId,
    string GenerationId,
    string? ParentTurnId,
    long Sequence,
    string RecordType,
    string? Role,
    string? ModelId,
    string? ProviderId,
    ReadOnlyMemory<byte> Content,
    string ContentSha256,
    long ByteCount,
    AuditCaptureOutcome Outcome,
    string? GapReason,
    DateTimeOffset CapturedAtUtc
);

public interface IMultiTurnAuditSink
{
    ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default);
}
```

`MultiTurnLifecycleServices` gains `IMultiTurnAuditSink AuditSink` and `MultiTurnAuditScope? AuditScope`. `Disabled` remains allocation-free when both lifecycle and audit are absent. `ForSpawnedAgent` preserves scope and sink while lineage supplies `ParentTurnId`.

- [ ] **Step 1: Write failing request/response tests**

Create tests that send a system message, a 14,000-character user message ending in `REQUEST-TAIL-6f9a`, and two canonical assistant messages crossing a 64 KiB aggregate boundary. Assert exact ordered bytes deserialize through `IMessageJsonConverter`, the tail survives, fragments are not substituted for canonical messages, and request capture occurs before the fake provider observes dispatch.

Add this mutation discriminator: remove the last request message before audit serialization; the test must fail on the tail sentinel and message count.

- [ ] **Step 2: Write failing interruption and compatibility tests**

A provider stream that emits one canonical message and then throws `HttpIOException(ResponseEnded)` must produce one complete response record plus one `Gap` completion record. Existing constructors without audit arguments must still compile and produce no audit calls.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~MultiTurnAuditCaptureTests|FullyQualifiedName~MultiTurnAgentLoopConstructorCompatibilityTests|FullyQualifiedName~SubAgentLineagePropagationTests"
```

Expected: audit contracts and records do not exist.

- [ ] **Step 4: Implement the inert contract and stable serializer**

Use the same snake-case `IMessageJsonConverter` shape as `MessagePersistenceConverter`, but do not call `ToPersistedMessage` because its random ID and current timestamp are not source bytes. Serialize an ordered request envelope containing each runtime type and complete message JSON. Hash UTF-8 bytes with SHA-256 lowercase hex. Generate record IDs from `(scope, thread, run, generation, sequence, recordType, hash)`.

- [ ] **Step 5: Capture at the settled dispatch boundary**

In `MultiTurnAgentLoop`, capture `messagesToSend` after continuation instructions are appended and immediately before `GenerateReplyStreamingAsync`. Capture each canonical message at the same `attempt.Observe(msg)` decision that admits it to history. Capture tool-call results at their final typed result boundary. On a severed stream, write `Gap` with the exception class/code but no raw exception text.

Request-record failure is fail-closed: do not dispatch a model request that cannot be durably accepted by the configured sink. Response-record failure throws a typed `AuditCaptureException`; the round becomes retryable and cannot publish from uncaptured output.

- [ ] **Step 6: Propagate scope to descendants**

`MultiTurnLifecycleServices.ForSpawnedAgent` keeps the sink/scope. Build `ParentTurnId` from parent thread/run/generation plus spawning tool-call ID. Do not let a child choose or replace the engagement/round scope.

- [ ] **Step 7: Run GREEN, then full LmMultiTurn tests**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~MultiTurnAuditCaptureTests|FullyQualifiedName~MultiTurnAgentLoopConstructorCompatibilityTests|FullyQualifiedName~SubAgentLineagePropagationTests"
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj
```

- [ ] **Step 8: Format and commit**

```powershell
dotnet csharpier format src/LmMultiTurn/Audit src/LmMultiTurn/Lifecycle/MultiTurnLifecycleServices.cs src/LmMultiTurn/MultiTurnAgentBase.cs src/LmMultiTurn/MultiTurnAgentLoop.cs src/LmMultiTurn/SubAgents/SubAgentManager.cs tests/LmMultiTurn.Tests/Audit tests/LmMultiTurn.Tests/MultiTurnAgentLoopConstructorCompatibilityTests.cs tests/LmMultiTurn.Tests/Lifecycle/SubAgentLineagePropagationTests.cs
git add src/LmMultiTurn tests/LmMultiTurn.Tests
git commit -m "feat(audit): capture complete multi-turn model exchanges"
```

---

### Task 4: Persist chunked, content-addressed audit records in the daemon

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Persistence/Models/AuditSourceRecord.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Persistence/ReviewStoreAuditTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs`

**Interfaces:**
- Produces:

```csharp
internal enum AuditSourceCaptureOutcome { Complete, Gap, Redacted }

internal sealed record AuditSourceRecord(
    string Id,
    long EngagementRoundId,
    string ThreadId,
    string RunId,
    string GenerationId,
    string? ParentTurnId,
    long Sequence,
    string RecordType,
    string? Role,
    string? ModelId,
    string? ProviderId,
    string ContentSha256,
    long ByteCount,
    AuditSourceCaptureOutcome CaptureOutcome,
    string? GapReasonCode,
    DateTimeOffset CapturedAtUtc
);

internal sealed record AuditRedactionRecord(
    string Id,
    string SourceRecordId,
    int Version,
    string RedactedContentSha256,
    long RedactedByteCount,
    string AffectedRangesJson,
    string ReasonCode,
    DateTimeOffset CreatedAtUtc
);

internal const int AuditChunkBytes = 64 * 1024;
```

`ReviewStore` produces `BeginAuditRecord`, `AppendAuditChunk`, `CompleteAuditRecord`, `GetAuditRecord`, `ReadAuditContent`, `ListAuditRecordsForRound`, `CreateAuditRedaction`, `ReadAuditRedactionContent`, and `RecordLegacyAuditGap`.

- [ ] **Step 1: Add migration-v10 tests**

Migration v9 is reserved for coordinator tables in Task 7. Add v10 tables:

```sql
CREATE TABLE audit_blob (
  sha256 TEXT PRIMARY KEY,
  byte_count INTEGER NOT NULL,
  content BLOB NOT NULL
);
CREATE TABLE audit_source_record (
  id TEXT PRIMARY KEY,
  engagement_round_id INTEGER NOT NULL REFERENCES engagement_round(id),
  thread_id TEXT NOT NULL,
  run_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  parent_turn_id TEXT NULL,
  sequence INTEGER NOT NULL,
  record_type TEXT NOT NULL,
  role TEXT NULL,
  model_id TEXT NULL,
  provider_id TEXT NULL,
  content_sha256 TEXT NOT NULL,
  byte_count INTEGER NOT NULL,
  capture_outcome TEXT NOT NULL,
  gap_reason_code TEXT NULL,
  captured_at TEXT NOT NULL,
  completed_at TEXT NULL,
  UNIQUE(engagement_round_id, thread_id, run_id, generation_id, sequence, record_type)
);
CREATE TABLE audit_source_chunk (
  source_record_id TEXT NOT NULL REFERENCES audit_source_record(id),
  chunk_index INTEGER NOT NULL,
  blob_sha256 TEXT NOT NULL REFERENCES audit_blob(sha256),
  byte_offset INTEGER NOT NULL,
  byte_count INTEGER NOT NULL,
  PRIMARY KEY(source_record_id, chunk_index)
);
CREATE TABLE audit_redaction_record (
  id TEXT PRIMARY KEY,
  source_record_id TEXT NOT NULL REFERENCES audit_source_record(id),
  version INTEGER NOT NULL,
  redacted_content_sha256 TEXT NOT NULL,
  redacted_byte_count INTEGER NOT NULL,
  affected_ranges_json TEXT NOT NULL,
  reason_code TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE(source_record_id, version)
);
CREATE TABLE audit_redaction_chunk (
  redaction_record_id TEXT NOT NULL REFERENCES audit_redaction_record(id),
  chunk_index INTEGER NOT NULL,
  blob_sha256 TEXT NOT NULL REFERENCES audit_blob(sha256),
  byte_offset INTEGER NOT NULL,
  byte_count INTEGER NOT NULL,
  PRIMARY KEY(redaction_record_id, chunk_index)
);
```

Fresh schema must include these tables. A seeded v9 DB must retain rows. A failed v10 statement must roll back all v10 objects and keep `user_version = 9`.

- [ ] **Step 2: Add failing storage proofs**

Store 160 KiB with a tail sentinel. Assert three chunks, exact byte-for-byte reconstruction, shared chunks deduplicate by hash, replaying the same record/chunk is idempotent, conflicting bytes for the same record/chunk reject, an incomplete record cannot read as `Complete`, and a `Gap` record has zero content plus a non-empty reason code. Interleave two child runs whose local sequence values both start at one; both records must persist because uniqueness includes thread/run/generation identity rather than treating sequence as round-global.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~ReviewStoreAuditTests"
```

- [ ] **Step 4: Implement migration and guarded store methods**

Append v10 after v9 without editing older SQL. Hash chunks independently and verify final concatenated hash/length before setting `completed_at`. Use one transaction under `ReviewStore._gate` for each state transition. Never log content or redaction bodies.

- [ ] **Step 5: Run GREEN and mutation proof**

Run the focused command. Then temporarily change reconstruction ordering from `chunk_index ASC` to descending; the 160 KiB test must fail. Restore and rerun GREEN.

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Persistence tests/CodeReviewDaemon.Sample.Tests/Persistence tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs
git add samples/CodeReviewDaemon.Sample/Persistence tests/CodeReviewDaemon.Sample.Tests/Persistence tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs
git commit -m "feat(daemon): store complete chunked review audit records"
```

---

### Task 5: Add authenticated audit ingestion and boundary redaction

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Controllers/ReviewAuditController.cs`
- Create: `samples/CodeReviewDaemon.Sample/Security/ReviewBridgeAuthAttribute.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Hosting/DaemonControllerFeatureProvider.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewAuditRouteTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/RouteExposureTests.cs`

**Interfaces:**
- Produces authenticated routes:

```text
POST /api/review-audit/records/{recordId}
PUT  /api/review-audit/records/{recordId}/chunks/{chunkIndex}
POST /api/review-audit/records/{recordId}/complete
GET  /api/review-audit/rounds/{roundId}/status
```

Authentication header is `X-Review-Bridge-Auth`, compared in constant time against `CodeReviewDaemon:ReviewBridgeSecret`. Maximum decoded chunk size is exactly 64 KiB. Metadata identifies an existing round; the caller cannot create or retarget one.

- [ ] **Step 1: Write failing route/auth tests**

Assert the four route templates are exposed only when `EnableReviewAuditIngestion=true`; every other daemon/LmAgentInfra controller remains hidden. Missing/wrong secret returns 401. Unknown round returns 404. A 65 KiB chunk returns 413. Duplicate delivery with identical hash succeeds idempotently. Conflicting duplicate returns 409.

- [ ] **Step 2: Write failing secret-exclusion tests**

Use typed tool-call JSON containing fields `authorization`, `api_key`, `access_token`, `client_secret`, `password`, `X-Sbx-App-Key`, and `X-S2S-Auth`. The accepted source bytes must contain fixed boundary-exclusion tokens, not fixture secrets. A later security redaction creates an `audit_redaction_record` with version, field path/range, original source hash, redacted hash, and reason; it stores a new redacted view without rewriting the immutable source record or claiming byte identity with it.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewAuditRouteTests|FullyQualifiedName~RouteExposureTests"
```

- [ ] **Step 4: Implement the fail-closed boundary**

Add `EnableReviewAuditIngestion` and `ReviewBridgeSecret`, both default off/empty. Reject enabled ingestion with an empty secret at startup. Sanitize structured message/tool content before `ReviewStore.AppendAuditChunk`; do not scan arbitrary prose and claim it is secret-free. Unknown structured secret-bearing fields reject with `unsupported_sensitive_shape` rather than persist unclassified credential data.

Update `DaemonControllerFeatureProvider.IsController` in this task so `ReviewAuditController` is discoverable only when `EnableReviewAuditIngestion` is enabled; retain only `DiscoveryController` and `AuthWebhookController` otherwise. Task 13 separately adds `ReviewPublicationController` under its own flag. Update the XML comment and route-exposure assertion with the same explicit list.

- [ ] **Step 5: Run GREEN and route inventory**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewAuditRouteTests|FullyQualifiedName~RouteExposureTests"
```

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Controllers samples/CodeReviewDaemon.Sample/Security samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs samples/CodeReviewDaemon.Sample/Hosting/DaemonControllerFeatureProvider.cs samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests/Scenarios
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests/Scenarios
git commit -m "feat(daemon): ingest authenticated review audit records"
```

---

### Task 6: Correlate hosted conversations and deliver exact audit records

**Files:**
- Create: `samples/LmStreaming.Sample/Services/ConversationReviewScope.cs`
- Create: `samples/LmStreaming.Sample/Services/ReviewAuditBridge.cs`
- Modify: `samples/LmStreaming.Sample/Models/ConversationDtos.cs`
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/LmStreamingS2SClient.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgentLoopFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/IReviewAgentLoopFactory.cs`
- Create: `tests/LmStreaming.Sample.Tests/Services/ReviewAuditBridgeTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/Services/ProvisionedPropertyRoundTripTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Agents/LmStreamingS2SClientTests.cs`

**Interfaces:**
- `ProvisionConversationRequest` gains:

```csharp
public ReviewConversationScope? ReviewScope { get; init; }
public sealed record ReviewConversationScope(string EngagementId, string RoundId);
```

- `IReviewAgentLoopFactory.Create` gains one trailing optional `ReviewConversationScope? reviewScope = null`.
- `ReviewAuditBridge` implements `IMultiTurnAuditSink` and sends the chunk protocol from Task 5.

- [ ] **Step 1: Add failing provision round-trip tests**

Provision a review conversation with engagement `17` and round `31`. Recreate its pooled agent and assert the exact scope reaches `MultiTurnLifecycleServices.AuditScope`. Assert an ordinary conversation has no scope or audit sink. Reject blank/non-numeric IDs, scope on a non-`code-review-daemon` mode, and attempts to change scope on an existing thread.

- [ ] **Step 2: Add failing bridge tests**

Assert metadata → ordered chunks → complete calls; all carry `X-Review-Bridge-Auth`; retry reuses record/chunk IDs; 401 and 409 are permanent; 408/429/5xx are bounded retries; no request contains provider OAuth tokens or sandbox app keys. Status must distinguish `Complete`, `Gap`, `Pending`, and `Unavailable`.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewAuditBridgeTests|FullyQualifiedName~ProvisionedPropertyRoundTripTests"
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~LmStreamingS2SClientTests"
```

- [ ] **Step 4: Persist and restore scope**

Store scope in immutable thread metadata keys `review.engagementId` and `review.roundId`. `ConversationReviewScope.ReadAsync` validates both-or-neither. At the per-conversation construction point, derive the host lifecycle bundle with the global `ReviewAuditBridge` plus this scope. Descendants inherit it through Task 3.

- [ ] **Step 5: Extend daemon provisioning**

Thread scope through `DaemonReviewStageExecutor → IReviewAgentLoopFactory → S2SReviewAgent → LmStreamingS2SClient.ProvisionAsync`. Resumed conversations must match the persisted round; do not re-provision under a new scope.

- [ ] **Step 6: Implement bounded authenticated delivery**

Post request metadata before provider dispatch. Upload exact chunks. Complete only after server hash/length confirmation. A permanent rejection throws `AuditCaptureException`. A temporary outage retries within the existing turn deadline; exhaustion records round-level `audit_delivery_unavailable` and prevents publication.

- [ ] **Step 7: Run GREEN and S2S contract regression**

Run both focused commands from Step 3 plus:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ConversationsRestContractTests|FullyQualifiedName~ConversationScopingTests"
```

- [ ] **Step 8: Format and commit**

```powershell
dotnet csharpier format samples/LmStreaming.Sample samples/CodeReviewDaemon.Sample/Agents tests/LmStreaming.Sample.Tests tests/CodeReviewDaemon.Sample.Tests/Agents
git add samples/LmStreaming.Sample samples/CodeReviewDaemon.Sample/Agents tests/LmStreaming.Sample.Tests tests/CodeReviewDaemon.Sample.Tests/Agents
git commit -m "feat(streaming): correlate review conversations with durable audit"
```

---

### Task 7: Add coordinator and round persistence with migration v9 *(execute after Task 3)*

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Persistence/Models/PrEngagement.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewRun.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Persistence/ReviewStoreEngagementTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs`

**Dependencies:**
- Execute after Task 3 and before Task 4. Task 4's v10 schema requires this task's v9 `engagement_round` table.

**Interfaces:**

```csharp
internal enum EngagementRoundIntent { CodeReview, DiscussionFollowUp, MergedClose }
internal enum EngagementRoundStatus { Pending, Running, RetryPending, Completed, Superseded, Parked }

internal sealed record PrEngagement(
    long Id,
    long RepoId,
    string Provider,
    string PrId,
    PrLifecycleState Lifecycle,
    string LatestHeadSha,
    string LatestBaseSha,
    string? LastReviewedHeadSha,
    ProviderActivityWatermark LatestActivity,
    ProviderActivityWatermark? ConsumedActivity,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? NextEligibleAt,
    long? ActiveRoundId,
    long? LatestRoundId,
    string? RootSummaryReceiptJson,
    DateTimeOffset UpdatedAt
);

internal sealed record EngagementRound(
    long Id,
    long PrEngagementId,
    EngagementRoundIntent Intent,
    EngagementRoundStatus Status,
    string HeadSha,
    string BaseSha,
    ProviderActivityWatermark? ActivityLowerBound,
    ProviderActivityWatermark? ActivityUpperBound,
    long PriorObservationBoundary,
    long? ReviewRunId,
    int GovernedFailureCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? SupersededAt,
    DateTimeOffset? ParkedAt,
    string? ParkReason
);
```

- [ ] **Step 1: Add migration-v9 RED tests**

Append v9 tables `pr_engagement` and `engagement_round`, plus nullable `engagement_round_id` on `review_run`. Persist the latest and consumed watermarks, `prior_observation_boundary`, lifecycle, cooldown, active/latest round, root-summary receipt, and governed parking fields named in the interfaces above. Enforce `UNIQUE(provider, repo_id, pr_id)`, one active round through a partial unique index on `status IN ('Pending','Running','RetryPending')`, valid intent/status checks, and FK integrity.

A v8 seeded run must survive. Reapplying migration must be idempotent through `user_version`. Concurrent migration must leave one valid schema.

- [ ] **Step 2: Add store transition RED tests**

Prove:

```text
CreateOrGetEngagement is idempotent on provider/repo/PR.
TryAdmitRound allows one active round.
Pending → Running → Completed succeeds.
Running → Superseded succeeds without incrementing governed failures.
Completed cannot return to Running.
Completing CodeReview/Discussion sets next_eligible_at = completed_at + 1 hour.
Completing MergedClose does not set another cooldown.
```

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~ReviewStoreEngagementTests"
```

- [ ] **Step 4: Implement v9 before v10**

Order `SchemaMigrations.All` as v1…v8, v9 engagement, v10 audit. Store activity as canonical JSON with explicit provider, timestamp, and stable object ID. All compare-and-swap transitions include expected prior status in SQL.

- [ ] **Step 5: Keep review-run identity unchanged**

Add only the nullable round FK. Do not add `TriggerWatermark` or mode to `CreateOrGetReviewRun` lookup. A `CodeReview` round links one commit-identified `ReviewRun`; discussion and close rounds do not fabricate review runs.

- [ ] **Step 6: Run GREEN and full migration tests**

Run the focused command. Mutate the active-round partial index away; the concurrent-admission test must fail. Restore and rerun.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Persistence tests/CodeReviewDaemon.Sample.Tests/Persistence tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs
git add samples/CodeReviewDaemon.Sample/Persistence tests/CodeReviewDaemon.Sample.Tests/Persistence tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs
git commit -m "feat(daemon): persist PR engagements and typed rounds"
```

---

### Task 8: Normalize provider activity without self-triggering

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/ProviderActivitySnapshot.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/IPrProvider.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/AdoPrProvider.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/GitHubPrProviderTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/AdoPrProviderTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/IPrProviderContractTests.cs`

**Interfaces:**

```csharp
internal sealed record ProviderActivityWatermark(
    string Provider,
    DateTimeOffset PublishedAt,
    string StableObjectId
) : IComparable<ProviderActivityWatermark>;

internal sealed record ProviderDiscussionRef(
    string Provider,
    string ThreadId,
    string CommentId,
    string? ParentCommentId,
    string? Permalink,
    string? Path,
    string? Side,
    int? StartLine,
    int? EndLine,
    string? Status,
    string? IterationContext,
    DateTimeOffset PublishedAt,
    string Author,
    string Body
);

internal sealed record ProviderEngagementSnapshot(
    PrLifecycleState Lifecycle,
    string HeadSha,
    string BaseSha,
    ProviderActivityWatermark LatestObserved,
    IReadOnlyList<ProviderDiscussionRef> ExternalActivity
);

Task<ProviderEngagementSnapshot> GetEngagementSnapshotAsync(
    RepoIdentity repo,
    string prId,
    ProviderActivityWatermark? after,
    IReadOnlySet<string> daemonReceiptIds,
    CancellationToken cancellationToken
);
```

- [ ] **Step 1: Write provider contract RED tests**

GitHub must merge review comments, review replies, submitted review bodies, and flat issue comments into one monotonic sequence. ADO must retain thread ID, comment ID, `parentCommentId`, status, path/span, and iteration context. Equal timestamps order by ordinal stable provider ID.

- [ ] **Step 2: Write self-trigger RED tests**

Seed daemon `ProviderResponseId` receipts. Assert matching provider objects are excluded while a same-author human comment is retained. Author name/body markers alone must not exclude activity. Deletion/system events are observed for audit but do not become discussion demand.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~GitHubPrProviderTests|FullyQualifiedName~AdoPrProviderTests|FullyQualifiedName~IPrProviderContractTests"
```

- [ ] **Step 4: Implement bounded paginated reads**

Read through the frozen upper bound. Preserve provider-native refs before constructing any bounded prompt projection. Compare watermarks lexicographically by UTC timestamp then provider-qualified stable ID. Never substitute GitHub `updated_at` alone for external activity.

- [ ] **Step 5: Run GREEN and tie mutation**

Temporarily remove the stable-ID tie-break. The equal-timestamp consume-once test must fail. Restore and rerun GREEN.

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration tests/CodeReviewDaemon.Sample.Tests/Scenarios
git add samples/CodeReviewDaemon.Sample/Orchestration/IPrProvider.cs samples/CodeReviewDaemon.Sample/Orchestration/ProviderActivitySnapshot.cs samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs samples/CodeReviewDaemon.Sample/Orchestration/AdoPrProvider.cs samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs tests/CodeReviewDaemon.Sample.Tests/Scenarios
git commit -m "feat(daemon): normalize external PR activity"
```

---

### Task 9: Admit rounds with cooldown, precedence, lease, and cutover seeding

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/PrEngagementCoordinator.cs`
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/EngagementCutoverSeeder.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/PrPollingService.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/PrEngagementCoordinatorTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/EngagementCutoverSeederTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/PrPollingServiceTests.cs`

**Interfaces:**

```csharp
internal enum EngagementDecisionKind { None, Coalesced, AdmitCodeReview, AdmitDiscussion, AdmitMergedClose, CleanupTerminal }
internal sealed record EngagementDecision(EngagementDecisionKind Kind, long? RoundId, string ReasonCode);

internal interface IEngagementRoundExecutor
{
    EngagementRoundIntent Intent { get; }
    Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken);
}

internal sealed class PrEngagementCoordinator
{
    public Task<EngagementDecision> ObserveAsync(
        long repoId,
        RepoIdentity repo,
        PullRequestDescriptor descriptor,
        ProviderEngagementSnapshot snapshot,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write the cooldown/precedence RED matrix**

Use `FakeTimeProvider`. Prove first review is immediate; a comment at +59:59 coalesces; at +1:00:00 exactly one discussion round admits; a pushed head at +20m replaces pending discussion and admits one code round at eligibility; further comments extend the pending upper bound; merge admits immediately; failed/superseded rounds do not start cooldown; `Closed`/`Abandoned` select cleanup.

- [ ] **Step 2: Write lease/crash RED tests**

Two concurrent `ObserveAsync` calls must yield one admitted round. Restart with `Running` work must reconcile to `RetryPending` or `Superseded` from provider truth. Merge during running ordinary work marks it `Superseded`, releases lease, and admits close without charging failure budget.

- [ ] **Step 3: Write cutover RED tests**

Given historical runs, seed one coordinator per open PR. Derive last reviewed head/completion, `next_eligible_at`, root summary only from outbox/provider receipt, and unconsumed current external activity. Record legacy gaps for unavailable prompt/transcript/judge sources. Running the seeder twice must produce byte-identical coordinator state and no duplicate gaps.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~PrEngagementCoordinatorTests|FullyQualifiedName~EngagementCutoverSeederTests|FullyQualifiedName~PrPollingServiceTests"
```

- [ ] **Step 5: Implement decision logic and flags**

Add flags `EnableEngagementCoordinator`, `EnableEngagementEligibility`, and `EnableEngagementShadowMode`, all false. Polling always updates observed coordinator state when the coordinator flag is on. Shadow mode records the decision but invokes no round executor. Eligibility remains off until `EngagementCutoverSeeder` records a completed global seed marker.

- [ ] **Step 6: Intercept the poller before `PrOrchestrator`**

When disabled, preserve the current direct seed/run path. When enabled, build the snapshot and call the coordinator. Only an admitted `CodeReview` executor calls the existing `PrOrchestrator.RunAsync` with `EngagementRoundId`; discussion and close use their dedicated executors.

- [ ] **Step 7: Run GREEN and direct-path regression**

Run the focused command. Also prove all flags false still call the old orchestrator once with the same seed.

- [ ] **Step 8: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests/Orchestration tests/CodeReviewDaemon.Sample.Tests/Scenarios/PrPollingServiceTests.cs
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): coordinate PR engagement rounds"
```

---

### Task 10: Make `ContextReady` mean a sourced dynamic context manifest

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/ReviewAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/DaemonReviewStageExecutorTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/DynamicContextGatheringTests.cs`

**Interfaces:**
- Produces schema-v1 artifact `context-manifest` with `EngagementRoundId`, hosted thread ID, gatherer agent ID, exact source-record refs, claims, refs, and typed gaps.
- Adds `ReviewAgent.GatherContextAsync(string bootstrap, DateTimeOffset deadlineUtc, CancellationToken)`.

- [ ] **Step 1: Write a failing real-catalog resolution test**

Resolve installed `code-reviewer:pr-context-gatherer`, send a dedicated first turn, and assert the roster contains that exact template—not a prompt claim with the same name. The fake gatherer must perform at least two scoped reads before returning cited context. Missing required repo/head/workspace scope fails before `Reviewed` or publication.

- [ ] **Step 2: Write failing bootstrap and manifest tests**

The bootstrap includes compact refs only: provider/repo/PR, base/head/merge-base, complete changed-path inventory, linked issue/work-item/related-PR refs, discussion refs, open-question refs, prior observation boundary, `/workspace` checkout, and exact KB paths. It does not embed full diff/bodies. `Linked`, `NoneLinked`, `Failed`, `Unavailable`, and `Truncated` remain distinct.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~DynamicContextGatheringTests|FullyQualifiedName~DaemonReviewStageExecutorTests"
```

- [ ] **Step 4: Run gatherer inside `ContextReady`**

Keep checkout/static provider preparation first. Provision the round-scoped S2S conversation, send only the gatherer dispatch turn, settle its recursive descendants, fetch its complete response through audit records, validate cited scope, and persist the manifest plus resume identity. Do not mark `ContextReady` complete on a mere dispatch request.

- [ ] **Step 5: Resume the same parent thread for review**

`Reviewed` reads the persisted manifest and same hosted thread. The review prompt may dispatch evidence-driven specialists. It must not rerun PR-level discovery. Optional context gaps become explicit uncertainty; required-scope gaps stop the round.

- [ ] **Step 6: Run GREEN and mutation proof**

Remove the second fake read; the iterative-read assertion must fail. Restore. Remove the manifest persistence; the resume-after-restart test must fail. Restore and rerun.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs samples/CodeReviewDaemon.Sample/Agents samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): gather sourced PR context before review"
```

---

## Phase 3 — Participation and publication

### Task 11: Persist questions, actions, and immutable round observations

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Persistence/Models/ClarificationQuestion.cs`
- Create: `samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewAction.cs`
- Create: `samples/CodeReviewDaemon.Sample/Persistence/Models/RoundObservation.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewNotesArtifactBuilder.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Persistence/ReviewStoreEngagementEvidenceTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewAuditProjectionTests.cs`

**Interfaces:**

```csharp
internal enum ClarificationQuestionState { Open, Answered, Contested, Superseded, UnansweredAtMerge }
internal enum ReviewActionKind { CreateRootSummary, AppendSummaryDelta, SubmitInlineFindings, PostClarificationQuestion, ReplyToDiscussion, FinalizeRound }
internal enum ReviewActionStatus { Planned, CollectedOnly, Sending, Accepted, Rejected }
internal enum ObservationKind { ContextClaim, Finding, Question, Answer, Correction, DiscussionContribution, DeliberateNoAction, JudgeResult, Gap }
```

Migration v11 adds `clarification_question`, `clarification_candidate_answer`, `review_action`, `round_observation`, and source-record link tables. A question stores wording, evidence refs, withheld conclusion, target/action/receipt, state, candidates, and interpretation refs. An action stores provider-native receipt/rejection JSON.

- [ ] **Step 1: Write migration/store RED tests**

Prove append-only observations reject update/delete through the public store API; question transitions use expected-old-state CAS; a question counts as asked only after its action is `Accepted`; replay by `(round_id, action_id)` is idempotent; every structured entity references existing source record IDs and hashes.

- [ ] **Step 2: Write projection RED tests**

A 20 KiB source message remains complete in SQLite while Markdown is bounded. The projection states omitted item/byte counts, links source record IDs/hashes, distinguishes deliberate omission from unavailable source, and renders judge score/rationale/model/ballot/self-grade/rubric with links. A missing source renders `GAP`, never empty success.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewStoreEngagementEvidenceTests|FullyQualifiedName~ReviewAuditProjectionTests|FullyQualifiedName~MigrationTests"
```

- [ ] **Step 4: Implement v11 and projections**

Store exact content only in audit tables. Structured tables store IDs/hashes and semantic fields. Extend `ReviewNotesArtifactBuilder`; keep `MaxEntryChars`/`MaxArtifactChars` as projection limits and disclose every omission.

- [ ] **Step 5: Link judge input/output explicitly**

Update `JudgeAgent` artifact payload to include request/response source IDs/hashes, ballot status, abstention/exclusion reason, rubric version, and effective model provenance. Unknown remains null/unknown.

- [ ] **Step 6: Run GREEN and tail mutation**

Change the audit tail sentinel while leaving the projection unchanged. Source-hash verification must fail. Restore and rerun.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Persistence samples/CodeReviewDaemon.Sample/Orchestration/ReviewNotesArtifactBuilder.cs samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): retain questions actions and immutable observations"
```

---

### Task 12: Implement discussion-only rounds and answer interpretation

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Agents/DiscussionAgent.cs`
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/DiscussionRoundExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/DiscussionAgentTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DiscussionRoundExecutorTests.cs`

**Interfaces:**

```csharp
internal sealed record DiscussionDecision(
    bool IsRelevant,
    IReadOnlyList<QuestionInterpretation> QuestionInterpretations,
    IReadOnlyList<PlannedReviewAction> Actions,
    IReadOnlyList<RoundObservationDraft> Observations
);

internal sealed record QuestionInterpretation(
    long QuestionId,
    ClarificationQuestionState State,
    IReadOnlyList<string> CandidateCommentIds,
    IReadOnlyList<AuditSourceReference> Evidence
);
```

- [ ] **Step 1: Write prompt/tool-isolation RED tests**

Assert the exact prominent text:

```text
The code head has not changed. This is a discussion follow-up. Answer or add value to the new comments. Do not repeat the code review.
```

The S2S send uses spawn suppression. No specialist roster can appear. Focused `Read`, `Grep`, and provider-discussion reads remain available. A comment-only round cannot invoke `PrOrchestrator` or mutate `ReviewStage`.

- [ ] **Step 2: Write interpretation RED tests**

Thread ancestry, stable question/action ID, direct reply, mention, and permalink create candidates. Timing, thread closure, or generic “thanks” alone do not answer. Conflicting sourced answers produce `Contested`. A supported answer produces `Answered` plus interpretation source refs.

- [ ] **Step 3: Write value-gating RED tests**

Agent may answer, correct, add evidence, ask a focused follow-up, or deliberately no-op. A new finding is accepted only when tied to new discussion and focused code evidence. Broad-review need becomes an observation, not a silent code-review dispatch. One material round plans one root-summary delta; no-op plans none.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~DiscussionAgentTests|FullyQualifiedName~DiscussionRoundExecutorTests"
```

- [ ] **Step 5: Implement triage and follow-up**

Freeze the activity window. Build bounded inputs from new comments, ancestors, open questions, prior manifest/observations, and focused checkout refs. Persist all candidates and decisions. Do not advance consumed activity until local finalization and all accepted/collected action receipts are durable.

- [ ] **Step 6: Run GREEN and fleet mutation**

Turn spawn suppression off; the no-specialist test must fail. Restore and rerun.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Agents samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): participate in PR discussion without rereview"
```

---

### Task 13: Add the six daemon-owned publication operations

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/IReviewPublicationOperations.cs`
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPublicationCoordinator.cs`
- Create: `samples/CodeReviewDaemon.Sample/Controllers/ReviewPublicationController.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPoster.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Hosting/DaemonControllerFeatureProvider.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewPublicationCoordinatorTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewPublicationRouteTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/RouteExposureTests.cs`

**Interfaces:**

```csharp
internal interface IReviewPublicationOperations
{
    Task<PublicationOutcome> CreateRootSummaryAsync(RootSummaryRequest request, CancellationToken cancellationToken);
    Task<PublicationOutcome> AppendSummaryDeltaAsync(SummaryDeltaRequest request, CancellationToken cancellationToken);
    Task<PublicationOutcome> SubmitInlineFindingsAsync(InlineFindingsRequest request, CancellationToken cancellationToken);
    Task<PublicationOutcome> PostClarificationQuestionAsync(ClarificationQuestionRequest request, CancellationToken cancellationToken);
    Task<PublicationOutcome> ReplyToDiscussionAsync(DiscussionReplyRequest request, CancellationToken cancellationToken);
    Task<PublicationOutcome> FinalizeRoundAsync(FinalizeRoundRequest request, CancellationToken cancellationToken);
}

internal sealed record PublicationOutcome(
    ReviewActionStatus Status,
    string ActionId,
    string? ProviderReviewId,
    string? ProviderThreadId,
    string? ProviderCommentId,
    string? ProviderPermalink,
    bool RelationshipDegraded,
    DateTimeOffset? AcceptedAt,
    string? RejectionCode
);
```

Every request carries round ID, stable action ID, expected provider/repo/PR/head, typed target, body, and `LivePostingAuthorized=false` by default.

- [ ] **Step 1: Write mechanical-validation RED tests**

Reject unknown/non-running rounds, provider/repo/PR mismatch, closed PR, moved head, nonexistent/unreplyable ref, invalid path/side/line/range, oversized body, action-kind mismatch, and reused action ID with different payload. Never retarget a stale inline finding into a summary.

- [ ] **Step 2: Write exactly-once RED tests**

Collect-only records action with no publisher call. Authorized replay returns prior receipt. Crash after provider acceptance but before local acceptance is recovered through provider backstop and does not repost. Partial batch resume sends only missing actions. Host never authors a replacement summary.

- [ ] **Step 3: Write route/auth RED tests**

Expose one authenticated `POST /api/review-publication/rounds/{roundId}/actions/{operation}` route. Require Task 5's bridge secret. The route accepts only the six operation names, caps bodies, and returns typed 200/409/422 responses without raw exception/provider bodies. Assert `DaemonControllerFeatureProvider.IsController` admits `ReviewPublicationController` only when `EnableTypedReviewPublication` is enabled and continues to reject every unrelated controller.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewPublicationCoordinatorTests|FullyQualifiedName~ReviewPublicationRouteTests|FullyQualifiedName~RouteExposureTests"
```

- [ ] **Step 5: Implement the typed route and explicit controller allow-list**

Add `ReviewPublicationController` and update `DaemonControllerFeatureProvider.IsController` in the same change. With `EnableTypedReviewPublication=false`, only the existing auth/discovery controllers plus any independently enabled audit controller are discoverable. With the flag true, add exactly the publication controller. Keep the XML comment and route inventory synchronized.

- [ ] **Step 6: Implement over the existing outbox and publisher adapters**

Keep `IReviewCommentPublisher` as transport. Generalize `ReviewPoster` logic into the coordinator. Idempotency key is derived from `(provider, repo, PR, round ID, action ID, action kind)`; body hash is audit only. Persist provider-native receipt fields on `review_action` and outbox response metadata.

- [ ] **Step 7: Implement root/no-op invariants**

`CreateRootSummary` succeeds once per engagement. Later material rounds use `AppendSummaryDelta`. `FinalizeRound` verifies required audit records and all planned action terminal states before completing; a genuine no-op is explicit and posts nothing.

- [ ] **Step 8: Run GREEN and crash mutation**

Disable provider backstop adoption; the crash-window test must duplicate/fail. Restore and rerun GREEN.

- [ ] **Step 9: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Controllers samples/CodeReviewDaemon.Sample/Hosting/DaemonControllerFeatureProvider.cs samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): validate and receipt typed review actions"
```

---

### Task 14: Preserve GitHub and ADO native publication fidelity

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/GitHubReviewCommentPublisher.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/AdoReviewCommentPublisher.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/GitHubReviewCommentPublisherTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/AdoReviewCommentPublisherTests.cs`

**Interfaces:**
- Adds transport methods for batched inline review, inline-thread reply, flat conversation comment, new ADO thread, and ADO existing-thread reply.
- `PostedComment` becomes a provider-native receipt with review/thread/comment/parent/permalink/status/span/iteration and degradation fields.

- [ ] **Step 1: Write GitHub RED tests**

`SubmitInlineFindings` sends one `POST /pulls/{pr}/reviews` with `event=COMMENT` after prevalidating every anchor. Inline reply sends `POST /pulls/{pr}/comments/{topLevelCommentId}/replies`; a reply-to-reply resolves to its top-level thread ID. Root delta and PR-level discussion response use flat issue comments, quote/link target permalink, and set `RelationshipDegraded=true`.

- [ ] **Step 2: Write ADO RED tests**

New finding creates a thread with file/right-left span and pull-request iteration context. Reply posts to `/pullRequests/{id}/threads/{threadId}/comments`, carries `parentCommentId`, and retains thread status/context in the receipt. It must not create a new thread.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~GitHubReviewCommentPublisherTests|FullyQualifiedName~AdoReviewCommentPublisherTests"
```

- [ ] **Step 4: Implement adapters without lowest-common-denominator flattening**

Keep provider-specific request/receipt types behind the transport interface. Validate all GitHub batch anchors before sending the atomic review. Do not leave pending GitHub reviews. Preserve ADO hierarchy and iteration fields exactly.

- [ ] **Step 5: Run GREEN and endpoint mutations**

Point GitHub reply at `/comments`; the endpoint test must fail. Point ADO reply at `/threads`; its test must fail. Restore and rerun.

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration tests/CodeReviewDaemon.Sample.Tests/Orchestration
git add samples/CodeReviewDaemon.Sample/Orchestration tests/CodeReviewDaemon.Sample.Tests/Orchestration
git commit -m "feat(daemon): preserve provider-native review threads"
```

---

### Task 15: Expose typed publication only to the parent review conversation

**Files:**
- Create: `samples/LmStreaming.Sample/Services/ReviewPublicationFunctionProvider.cs`
- Modify: `samples/LmStreaming.Sample/Models/ConversationDtos.cs`
- Modify: `samples/LmStreaming.Sample/Services/ModeCapabilities.cs`
- Modify: `samples/LmStreaming.Sample/Prompts.yaml`
- Modify: `samples/LmStreaming.Sample/Program.cs`
- Create: `tests/LmStreaming.Sample.Tests/Services/ReviewPublicationFunctionProviderTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/ProgramModeToolNarrowingTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/CodeReviewDaemonModeSpawnTests.cs`
- Modify: `tests/LmMultiTurn.Tests/SubAgents/SubAgentStateLifecycleTests.cs`

**Interfaces:**
- The function provider publishes exactly the six names from Task 2 and forwards typed requests to Task 13.
- Reuse the existing `SubAgentOptions.NonInheritedToolNames : IReadOnlyCollection<string>?` contract. Add the six publication names when building the parent loop; the existing `MultiTurnAgentLoop` inheritable-snapshot filter prevents descendants from recovering them through inherit-all, external tools, `add_tools`, or required-tool configuration.

- [ ] **Step 1: Write parent-only tool RED tests**

A round-scoped `code-review-daemon` parent sees all six functions. Ordinary modes see none. A context gatherer/specialist child sees none even with `add_tools:["*"]` or required-tool configuration. Child attempts return tool-not-found and cannot reach daemon HTTP. The posting skill remains invokable by the parent.

- [ ] **Step 2: Write forwarding/auth RED tests**

Assert operation name maps one-to-one, typed JSON preserves body text byte-for-byte, bridge auth is header-only, response receipts map without losing IDs/degradation, and error bodies are reduced to typed rejection codes before model visibility.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewPublicationFunctionProviderTests|FullyQualifiedName~ProgramModeToolNarrowingTests|FullyQualifiedName~CodeReviewDaemonModeSpawnTests"
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentStateLifecycleTests"
```

- [ ] **Step 4: Register only for validated review scope**

At per-conversation registry construction, require mode `code-review-daemon`, non-null immutable review scope, configured daemon bridge URL/secret, and `EnableTypedReviewPublication=true`. Add the provider after mode filtering. Put all six names in the existing `SubAgentOptions.NonInheritedToolNames` collection; do not introduce a second exclusion property.

- [ ] **Step 5: Update mode and installed plugin contract checks**

`Prompts.yaml` must not rely on `sandbox:*` to grant publication. Add explicit typed capability selection understood only by the host. At startup/first review, verify installed `post-pr-review` content hash/version from Task 2 before enabling typed publication; mismatch fails closed to collect-only.

- [ ] **Step 6: Run GREEN and real mode→spawn tests**

Run Step 3 commands. Also exercise the real mode → parent registry → child spawn path; do not stop at `ModeCapabilities` unit output.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/LmStreaming.Sample tests/LmStreaming.Sample.Tests tests/LmMultiTurn.Tests/SubAgents/SubAgentStateLifecycleTests.cs
git add samples/LmStreaming.Sample tests/LmStreaming.Sample.Tests tests/LmMultiTurn.Tests/SubAgents/SubAgentStateLifecycleTests.cs
git commit -m "feat(streaming): expose parent-only typed review publication"
```

---

## Phase 4 — Merged outcomes and cutover

### Task 16: Replace in-memory close extraction with a durable `MergedClose` round

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseRoundExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/PrEngagementCoordinator.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/PrLifecycleSweeperTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/MergedCloseRoundExecutorTests.cs`

**Interfaces:**
- `MergedCloseRoundExecutor : IEngagementRoundExecutor` uses the round's existing `GovernedFailureCount`, `RetryPending`, `ParkedAt`, and `ParkReason` fields.
- Close substeps are persisted as observations: `EvidenceFrozen`, `CandidatesBuilt`, `Classified`, `Verified`, `KnowledgePromoted`, `DeveloperLearningPromoted`, `ReportsWritten`, `Archived`.

- [ ] **Step 1: Write the current defect as RED**

After the current `MaxExtractionAttempts` failures, assert the notes branch is not merged/deleted, the close round is `Parked`, disposition is visibly `Blocked`, and evidence remains. Construct a second coordinator/executor over the same DB and assert retry count survives restart.

- [ ] **Step 2: Write lifecycle RED tests**

Merge bypasses cooldown. An active ordinary round becomes `Superseded` and stops unsent publication. Accepted prior actions remain evidence. `Closed`/`Abandoned` clean workspace/branch according to current policy but invoke no close analyst, report writer, or promoter.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~PrLifecycleSweeperTests|FullyQualifiedName~MergedCloseRoundExecutorTests"
```

- [ ] **Step 4: Move authority from sweeper dictionaries to round state**

Remove `_extractionAttempts` and `_terminallyResolved` as correctness state. The sweeper reports provider lifecycle to the coordinator. `MergedCloseRoundExecutor` resumes after the last completed durable substep. Exhaustion parks; it never merges/finalizes over failed required output.

- [ ] **Step 5: Run GREEN and restart mutation**

Reset failure count in the executor constructor; the restart test must fail. Restore and rerun.

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests/Orchestration
git add samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests/Orchestration
git commit -m "fix(daemon): make merged-close recovery durable"
```

---

### Task 17: Build deterministic close candidates and independent semantic verification

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseCandidateInventory.cs`
- Create: `samples/CodeReviewDaemon.Sample/Agents/CloseAnalystAgent.cs`
- Create: `samples/CodeReviewDaemon.Sample/Agents/CloseVerifierAgent.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/MergedCloseCandidateInventoryTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/CloseAnalystAgentTests.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Agents/CloseVerifierAgentTests.cs`

**Interfaces:**

```csharp
internal enum CloseOutcomeLabel { Confirmed, NotAddressed, Addressed, Novel, IndependentlyRaised, Indeterminate }
internal sealed record CloseOutcomeCandidate(string Id, string Kind, IReadOnlyList<AuditSourceReference> Sources);
internal sealed record ProposedCloseLabel(string CandidateId, string Label, IReadOnlyList<AuditSourceReference> Evidence);
internal sealed record VerifiedCloseLabel(string CandidateId, CloseOutcomeLabel Label, IReadOnlyList<AuditSourceReference> Evidence, string VerificationReasonCode);
```

- [ ] **Step 1: Write denominator RED tests**

Inventory every typed question, actionable finding/suggestion, severe finding, substantive agent post/reply, provider thread disposition, related commit/final-diff change, and promotion candidate. A model response cannot add/remove candidate IDs. `UnansweredAtMerge` questions remain candidates.

- [ ] **Step 2: Write semantic RED tests**

Novelty compares against discussion before the agent timestamp. Addressed compares final merged code against the finding, not thread status. “Followed by” requires temporal match; causal credit additionally requires explicit discussion or commit evidence. Equivalent severe findings from others retain their independent timestamp/source.

- [ ] **Step 3: Write verifier RED tests**

Verifier receives source records, provider discussion, and final code—not only analyst prose. Unsupported/missing/mutated citations become `Indeterminate`. Only confirmed labels enter definitive counts. Analyst and verifier use distinct hosted conversations and source records.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~MergedCloseCandidateInventoryTests|FullyQualifiedName~CloseAnalystAgentTests|FullyQualifiedName~CloseVerifierAgentTests"
```

- [ ] **Step 5: Implement deterministic-first close analysis**

Freeze merged commit, final diff, complete discussion, rounds, questions, actions, receipts, observations, and destination snapshots. Persist candidates before sending a bounded linked projection to the analyst. Persist proposals, then independently verify each candidate against immutable sources.

- [ ] **Step 6: Run GREEN and source-removal mutation**

Delete one cited source from the fixture. Its label must become `Indeterminate` and definitive count must decrease. Restore and rerun.

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseCandidateInventory.cs samples/CodeReviewDaemon.Sample/Agents samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): verify merged review outcomes from source evidence"
```

---

### Task 18: Render reproducible `outcome.json` and `OUTCOME.md`

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseOutcomeWriter.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseRoundExecutor.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/MergedCloseOutcomeWriterTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/MigrationTests.cs`

**Interfaces:**
- Migration v12 adds `close_outcome_item` and `promotion_outcome` with candidate/source/proposed/verified/promotion links.
- `MergedCloseOutcomeWriter.WriteAsync(long roundId, string notesRoot, CancellationToken)` writes deterministic JSON and Markdown from source tables.

- [ ] **Step 1: Write schema/writer RED tests**

Assert `outcome.json` contains candidate IDs, proposed and verified labels, confidence/indeterminate state, source links, counts, and promotion results. `OUTCOME.md` contains concise linked counts/examples and no unsupported score/testimonial. Stable inputs produce byte-identical outputs except a separately stored generation timestamp excluded from the content hash.

- [ ] **Step 2: Write recomputation RED tests**

Recompute aggregates from item rows and compare rendered counts. Mutating/removing a source invalidates verification or changes the item to `Indeterminate`; rerender changes the count. Writing twice is idempotent. Report files are staged only under the PR notes path.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~MergedCloseOutcomeWriterTests|FullyQualifiedName~MigrationTests"
```

- [ ] **Step 4: Implement v12 and writer**

Use ordinal ordering by candidate ID and evidence ref. Include all required measures from spec §9.4. Render promotion outcomes after Task 19's passes; a declined/failed promotion is visible, not omitted. Finalization checks both files and their stored hashes.

- [ ] **Step 5: Run GREEN and regenerate proof**

Run focused tests. Delete `OUTCOME.md` after first write; resume must regenerate it from immutable records without rerunning analyst/verifier.

- [ ] **Step 6: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Persistence samples/CodeReviewDaemon.Sample/Orchestration tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample/Persistence samples/CodeReviewDaemon.Sample/Orchestration tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): write linked merged-review outcome reports"
```

---

### Task 19: Promote verified knowledge and append-only developer learning

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/ClosePromotionPipeline.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DeveloperLearnings/DeveloperObservation.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/DeveloperLearnings/DeveloperLearningsLedger.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/AtCloseExtractionSeam.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/KnowledgeExtractionCommitter.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/MergedCloseRoundExecutor.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs`
- Create: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ClosePromotionPipelineTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Agents/DeveloperLearningsLedgerTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/AtCloseExtractionSeamTests.cs`

**Interfaces:**

```csharp
internal enum PromotionDisposition { Written, Declined, Failed }
internal sealed record PromotionOutcome(
    string SourceObservationId,
    string DestinationKind,
    PromotionDisposition Disposition,
    string? DestinationPath,
    string? DestinationContentHash,
    string? ReasonCode
);

internal sealed class ClosePromotionPipeline
{
    public Task<IReadOnlyList<PromotionOutcome>> RunAsync(
        EngagementRound round,
        IReadOnlyList<VerifiedCloseLabel> verified,
        CancellationToken cancellationToken);
}
```

`DeveloperObservation` gains required deterministic `SourceObservationId`.

- [ ] **Step 1: Write stable-ID/idempotency RED tests**

Derive source observation ID from provider/repo/PR/round/observation IDs and source hashes in ordinal order. Running promotion twice with identical IDs creates no duplicate KB or DeveloperLearnings contribution. Same human-readable text from two different source observations remains two traceable contributions.

- [ ] **Step 2: Write order/failure RED tests**

KB promotion completes or explicitly declines before developer promotion starts. Developer views regenerate only after append succeeds. A failure in either pass records `Failed`, leaves close pending/retryable, keeps all source evidence, and appears in the eventual report. No failed pass is dropped merely because the other wrote.

- [ ] **Step 3: Write legacy-retirement RED tests**

Existing `KnowledgeBase/developers/*.reviewfeedbacks.md` can be read as migration input but no new merged close writes it. The canonical write is one append-only `DeveloperObservation`, then regenerated views. `Closed`/`Abandoned` invoke neither pass.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ClosePromotionPipelineTests|FullyQualifiedName~DeveloperLearningsLedgerTests|FullyQualifiedName~AtCloseExtractionSeamTests"
```

- [ ] **Step 5: Implement ordered promotion behind separate flags**

Add `EnableMergedCloseReporting` and `EnableMergedLearningPromotion`, both false. Report-only mode classifies/verifies/writes reports with promotion outcomes `Declined(feature_disabled)`. Promotion mode calls confirmed general knowledge first, then confirmed developer patterns. Persist receipts before report rendering.

- [ ] **Step 6: Retire competing writes without deleting migration input**

Make `AtCloseExtractionSeam` delegate to the durable pipeline or become read-only migration compatibility. Remove the Program composition that runs `ReviewFeedbackAgent` as a whole-file writer. Keep existing files readable and archived.

- [ ] **Step 7: Run GREEN and replay proof**

Run the focused command twice against one temp store. Assert one contribution and identical destination hashes.

- [ ] **Step 8: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample/Agents samples/CodeReviewDaemon.Sample/Orchestration samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs samples/CodeReviewDaemon.Sample/Program.cs tests/CodeReviewDaemon.Sample.Tests
git add samples/CodeReviewDaemon.Sample tests/CodeReviewDaemon.Sample.Tests
git commit -m "feat(daemon): promote verified merged review learning"
```

---

### Task 20: Prove the whole engagement lifecycle in collect-only mode

**Files:**
- Create: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewEngagementEndToEndTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/CollectOnlyProviderWriteGateTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/CodeReviewDaemonModeSpawnTests.cs`
- Modify: `samples/CodeReviewDaemon.Sample/appsettings.json`
- Modify: `samples/CodeReviewDaemon.Sample/appsettings.s2s.json`

**Interfaces:**
- Consumes all prior tasks.
- Produces deterministic proof with scripted provider/model/bridge; no real provider/model spend and no external writes.

- [ ] **Step 1: Write the end-to-end scenario before enabling flags**

Script:

```text
open PR/head A → dynamic context → code review → clarification question plan → qualified review plan
+30m external answer → coalesced
+45m head B → discussion demand superseded
+60m eligibility → one code review for head B consuming answer
merge during completed cooldown → immediate MergedClose
classify → verify → report-only outcome
```

Assert one coordinator, three rounds (`CodeReview A`, `CodeReview B`, `MergedClose`), no discussion round, no provider calls in collect-only, complete audit tails, question answer correlation, one root-summary plan plus one delta plan, and reproducible reports.

- [ ] **Step 2: Add same-head discussion scenario**

With no head change, a useful comment at +10m yields one `DiscussionFollowUp` at +60m, spawn suppression remains on, one typed reply/delta is collected, and no `ReviewStage` row is created for it.

- [ ] **Step 3: Add terminal and crash scenarios**

`Closed`/`Abandoned` produce no report/promotion. Crash after simulated provider acceptance adopts receipt. Audit ingestion outage prevents publication/finalization and records a gap. Promotion failure parks visible close work.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "FullyQualifiedName~ReviewEngagementEndToEndTests|FullyQualifiedName~CollectOnlyProviderWriteGateTests"
```

Expected: flags/config/integration are not yet wired as one path.

- [ ] **Step 5: Wire conservative sample configuration**

Document all new flags as false. Add bridge URL/secret comments without checked-in secret values. Startup validation enforces dependency order:

```text
eligibility requires coordinator + completed seed;
discussion requires coordinator + audit;
typed publication requires audit + bridge secret + compatible plugin;
promotion requires merged reporting.
```

- [ ] **Step 6: Run GREEN and focused project suites**

```powershell
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj
```

- [ ] **Step 7: Format and commit**

```powershell
dotnet csharpier format samples/CodeReviewDaemon.Sample samples/LmStreaming.Sample tests/CodeReviewDaemon.Sample.Tests tests/LmStreaming.Sample.Tests
git add samples/CodeReviewDaemon.Sample samples/LmStreaming.Sample tests/CodeReviewDaemon.Sample.Tests tests/LmStreaming.Sample.Tests
git commit -m "test(daemon): prove collect-only review engagement lifecycle"
```

---

### Task 21: Run shadow/provider gates and retire obsolete action paths

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewActions.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewActionsTests.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Agents/ReviewSpawnGate.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `docs/superpowers/specs/2026-08-30-daemon-review-engagement-design.md` only if observed rollout facts require a factual status addendum.

**Interfaces:**
- Produces an operator gate record for each phase/provider with seed count, shadow decisions, action plans/rejections, audit gaps, receipt replays, fidelity proofs, report traceability, and promotion traceability.

- [ ] **Step 1: Run idempotent cutover seeding with eligibility off**

On a deployment copy, run seeding twice. Compare coordinator/round/gap counts and hashes. Verify every known open PR has one coordinator and no duplicate root adoption. Keep provider writes off.

- [ ] **Step 2: Run shadow admission**

Observe at least one full cooldown window. Compare decisions to provider heads/activity. Confirm daemon receipts never self-trigger, equal timestamps consume once, new heads win, and merge bypasses. Any mismatch blocks eligibility.

- [ ] **Step 3: Run collect-only typed publication per provider**

Inspect planned request, mechanical rejection, idempotency key, and projected receipt for GitHub and ADO. Execute crash-window tests against provider test doubles. Do not enable live writing from this step.

- [ ] **Step 4: Obtain just-in-time authorization before each external gate**

Before enabling real GitHub or ADO writes, state affected deployment/repositories, expected comments, rollback flag, receipt validation, and risk. Enable one provider at a time only after explicit approval. A plan approval is not posting approval.

- [ ] **Step 5: Gate reports, then promotion**

Enable merged reporting first. Recompute one report from sources and verify all links. Only then request authorization to enable KB/DeveloperLearnings promotion. Confirm replay creates no duplicate destination entry.

- [ ] **Step 6: Remove obsolete parser/correction and observational spawn controls**

After migration evidence shows no active run depends on fenced actions, delete the parser implementation from the existing `ReviewActions.cs` path or reduce it to an explicit legacy-read rejection, update its existing tests to assert fenced actions are rejected, remove correction-turn prompt text, and remove posting-marker observation from `ReviewSpawnGate` as a safety claim. Parent-only typed-tool enforcement from Task 15 is the actual boundary.

- [ ] **Step 7: Run full verification**

```powershell
dotnet csharpier check .
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
dotnet test LmDotnetTools.sln --logger "trx;LogFileName=review-engagement-results.trx" --results-directory .logs/test-results
node scratchpad/conversation_memories/review-daemon-control-flow-report/verify-report.mjs
```

Expected: formatter clean, build exit 0, all tests pass, report checks pass. If any relevant check fails, do not call the work complete or enable the next gate.

- [ ] **Step 8: Commit retirement changes**

```powershell
git add samples/CodeReviewDaemon.Sample/Orchestration/ReviewActions.cs tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewActionsTests.cs samples/CodeReviewDaemon.Sample/Agents/ReviewSpawnGate.cs samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs docs/superpowers/specs/2026-08-30-daemon-review-engagement-design.md
git commit -m "refactor(daemon): retire legacy review action handoff"
```

Do not push, publish, deploy, merge, or delete remote branches without separate authorization.

---

## Final acceptance matrix

| Criterion | Primary proof |
|---|---|
| Current report preserved; target graph clear | Task 1 static checks + one-call browser validation |
| Exact request/response evidence | Tasks 3–6 long-tail, chunk-order, interruption, scope, and secret tests |
| One durable coordinator/one active round | Tasks 7 and 9 migration/CAS/concurrency tests |
| One-hour coalescing and new-head precedence | Task 9 `FakeTimeProvider` matrix + Task 20 E2E |
| Dynamic iterative PR context | Task 10 real installed-agent/iterative-read/source-manifest tests |
| Non-blocking questions | Tasks 11–12 receipt/state/withheld-conclusion tests |
| Discussion does not re-review | Task 12 spawn-suppression and no-`ReviewStage` tests |
| Agent-owned typed publication | Tasks 13 and 15 unchanged-body/parent-only tool tests |
| Exactly-once provider effects | Task 13 crash/backstop/replay tests |
| Provider-native fidelity | Task 14 endpoint/payload/receipt tests |
| Merged-only outcomes | Tasks 16–18 terminal-state and report tests |
| Deterministic denominator + independent labels | Task 17 source-removal mutation |
| Ordered idempotent promotion | Task 19 replay/order/failure tests |
| Safe staged rollout | Tasks 20–21 collect-only, shadow, per-provider, and traceability gates |

## Plan self-review evidence

- [x] **Spec coverage:** §§0–3 map to global constraints and Tasks 7–9; §4 to Tasks 3, 6, 10, and 11; §5 to Tasks 11–12; §6 to Tasks 2 and 13–15; §7 to Tasks 3–6 and 11; §8 to Task 11; §9 to Tasks 16–19; §§10–11 to Tasks 9, 13, 16, and 20; §12 to Tasks 4, 7, 11, and 18; §13 to each task's RED/GREEN and mutation proof; §14 to Tasks 2, 9, 19–21; §15 to the global constraints and rollout gates; §16 is enforced by the no-stage-expansion, no-sidecar, no-fenced-action, no-raw-REST, no-fallback-summary, non-blocking-question, merged-only-promotion, and provider-fidelity requirements; §17 supplies the provider contracts in Tasks 8 and 14; §18 is the approved input to this plan.
- [x] **Stage and authority:** no task extends `ReviewStage`; one coordinator owns admission, cooldown, lease, and close authority. Discussion and close do not fabricate `ReviewRun` rows.
- [x] **Migration order:** v9 engagement precedes v10 audit; v11 adds questions/actions/observations; v12 adds close outcomes/promotions. Existing v1–v8 remain immutable.
- [x] **Identity consistency:** `PrEngagementId`, engagement-round ID, review-run ID, hosted thread ID, model run/generation IDs, parent-turn ID, action ID, question ID, observation ID, and source-record ID remain separate. Audit ordering is unique per round plus thread/run/generation/sequence/type, so child-local sequence values cannot collide.
- [x] **Complete evidence:** exact source bytes are captured before dispatch and during canonical enumeration; chunked source remains uncapped. Markdown/model projections are bounded, count omissions, distinguish gaps, and link exact source IDs/hashes.
- [x] **Redaction consistency:** known secret fields are excluded before the immutable source is accepted. Later redaction uses separate versioned redaction records and never rewrites or impersonates source bytes.
- [x] **External authority:** live posting, plugin install/release, commits, push, deployment, and provider gates each retain their own just-in-time authorization boundary. All feature flags default off/collect-only.
- [x] **Companion dependency:** typed publication cannot enable until the companion plugin contract is released, installed, and hash/version verified. The daemon build does not assume that release exists.
- [x] **Placeholder scan:** no unresolved `TBD`, `TODO`, `FIXME`, “implement later”, vague error-handling instruction, or unnamed generic test step remains; the only appearances are this evidence statement.
- [x] **TDD/verification:** every behavior task names a RED command, minimal implementation boundary, GREEN command, and mutation/non-vacuity discriminator. Formatting and focused checks are explicit; Task 21 carries final format/build/test/report gates.

## Human review gate

This plan changes no production behavior by itself. Implementation may start only after line-anchored review approval. That approval does not authorize commits, companion publication/install, live PR comments, provider writes, deployment, push, merge, or any other external action.
