# Review daemon — PR lifecycle, review and workspace setup

Source: commit `48310974`. Production S2S path. Source inspection, not a live-service trace.

**The daemon prepares Git on the host first. The review host mounts it into a sandbox later.**

## PR lifecycle — discovery and admission

```mermaid
flowchart TD
    A["Poll configured GitHub / ADO repositories"] --> B{"Open PR passes recency filter?"}
    B -- No --> C["Skip this PR this pass"]
    B -- Yes --> D["Capture PR identity, base/head revisions and metadata"]
    D --> E{"Engagement coordinator enabled?"}
    E -- No --> F["Legacy: create or reuse durable run"]
    E -- Yes --> G["Observe engagement demand, active round and cooldown"]
    G --> H{"Executable CodeReview round?"}
    H -- No --> I["Observe / defer / no-op<br/>or separate discussion / merged-close intent"]
    H -- Yes --> J["Claim round and resolve linked run"]
    F --> K{"Run eligible to continue?"}
    J --> K
    K -- No --> L["Complete / parked / backoff / PR not open<br/>No stage work now"]
    K -- Yes --> M["Resume first incomplete stage<br/>ContextReady → Reviewed → Judged → Posted"]
    M --> N["Persist each stage success<br/>or failure / retry state"]
    N --> O["Finally: dispose held lease safely"]
```

- **PR**: the provider's pull request. It can receive new commits and discussion over time.
- **Engagement**: durable per-PR coordination state. **Round**: one admitted intent.
- **Run**: persisted execution record for a particular review identity. Repeated polling need not create new work.
- Both legacy and engagement CodeReview paths use the same five-stage machine.
- Engagement execution requires rollout eligibility, cutover seeding and shadow mode off. Optional paths shown are not claims about deployed configuration.
- Stage failures resume from the last completed stage. Governed failures use backoff and durable limits; persistent governed failure can park the run.

## Context and review — inputs before findings

```mermaid
flowchart TD
    A["ContextReady"] --> B{"Matching saved static context?"}
    B -- Yes --> C["Reuse diff and file manifest"]
    B -- No --> D["Prepare base/head checkout<br/>Build diff, manifest and changed paths"]
    C --> E{"Engagement manifest needed?"}
    D --> E
    E -- Yes --> F["Gather linked issues/work items, discussions,<br/>open questions and KB references"]
    F --> G["Await gatherer children<br/>Validate and persist context manifest"]
    E -- No / cached --> H["Compose reviewer input"]
    G --> H
    H --> I["Add conditional prior knowledge, feedback,<br/>repo guidance, existing comments and linked context"]
    I --> J["Ensure workspace<br/>Start or resume matching lifecycle checkpoint"]
    J --> K["Provisional review<br/>Inspect code and dispatch children"]
    K --> L{"Child tree settled within shared deadline?"}
    L -- No --> X["Stop incomplete attempt<br/>Retry handling; no final review artifact"]
    L -- Yes --> M["Final synthesis on same thread<br/>Use settled child results"]
    M --> N["Validate and persist final review<br/>Optional comparison arm if configured"]
    N --> O{"Judge enabled?"}
    O -- Yes --> P["Grade saved review<br/>Persist score and model provenance"]
    O -- No --> Q["Continue to publication"]
    P --> Q
```

- Saved static context matches **schema + BaseSha + HeadSha**, not directory existence or a live mount.
- Dynamic gathering is engagement-only and skips a matching saved manifest. It does not publish the review.
- Failure to fetch existing PR comments marks dedup context lost and later withdraws live posting authorization.
- A provisional answer is a **checkpoint**, not the final review. Synthesis waits for the child-completion barrier.
- Checkpoints match workspace/model/modality/tool mode/input generations. Resume preserves the absolute deadline and accepted-turn identity where compatible.
- A first-review lone no-new-findings sentinel is rejected rather than silently accepted.
- Optional grading is **not a score-based publication veto** in this stage executor. It can be self-graded when the effective judge and generator models match.
- Context-exhaustion recovery may use fresh attempt threads; it does not reset the shared deadline.

## Publication — comment delivery versus completion

```mermaid
flowchart TD
    A["Read saved review"] --> B{"Nonempty and not no-new-findings sentinel?"}
    B -- No --> N["Intentional no-comment"]
    B -- Yes --> C{"Moved head or non-open PR positively observed?"}
    C -- Yes --> N
    C -- No / unknown --> D["Filter infrastructure narration<br/>Add bot identity and conversation link"]
    D --> E{"Author-facing content remains?"}
    E -- No --> N
    E -- Yes --> F["Build logical post key<br/>Enqueue or retrieve durable outbox row"]
    F --> G{"Terminal for this request?"}
    G -- Yes --> R["Replay recorded outcome"]
    G -- No --> H{"Live posting authorized?"}
    H -- No --> I["Mark Collected<br/>No provider comment write"]
    H -- Yes --> J{"Provider backstop finds the same logical post?"}
    J -- Yes --> K["Adopt existing comment receipt"]
    J -- No --> L["Mark Sending<br/>Post comment to GitHub / ADO"]
    L --> M["Persist Posted + provider response ID"]
    K --> Z["Finalize notes retention and workspace"]
    M --> Z
    R --> Z
    I --> Z
    N --> Z
    Z --> P{"Required work and applicable delivery proof satisfied?"}
    P -- Yes --> Q["Complete with truthful outcome<br/>Posted / collected / deliberate no-post"]
    P -- No --> X["Retry or durable park<br/>Do not claim completion"]
```

- The head/lifecycle guards suppress only **positively observed** change. Unknown reads continue; they do not prove freshness. ADO head state can lag a push.
- **ReviewPoster uses the outbox for the PR comment itself.** It is not just a notes-push mechanism.
- Live posting authorization requires posting enabled and dedup context not lost. Current restrictions are not overridden by a run's original mode.
- `Posted` rows are replay-terminal. `Collected` rows are terminal only while still unauthorized; later authorization can reopen the request.
- The provider backstop handles a crash after a comment landed but before its local receipt was saved.
- `Sending` is not proof of delivery. An authorized post-mode attempt must have a proven outcome; replay requires a Posted row and receipt.
- Finalization retains notes and settles workspace ownership **after** the provider boundary. Its failure remains retryable/governed without reposting the same logical comment.
- **Posted is a stage name, not a guarantee of a new public comment.** Collect-only and intentional no-post outcomes can complete truthfully.
- Errors before finalization propagate through orchestration; the graph shows the normal decision paths, not every exception edge.

### PR-flow source references

All below are under `samples/CodeReviewDaemon.Sample/`:

- `Orchestration/PrPollingService.cs:265` — discovery and routing.
- `Orchestration/PrEngagementCoordinator.cs:41` — per-PR admission.
- `Orchestration/EngagementRoundExecutionPolicy.cs:24` — rollout/shadow gates.
- `Orchestration/PrOrchestrator.cs:125` — stage resume, retry and completion.
- `Orchestration/DaemonReviewStageExecutor.cs:1154,1372` — static and dynamic context.
- Same executor `:4314,4888,4908,4935` — checkpoint, provisional turn, barrier and synthesis.
- Same executor `:5644,5899,5993,6148` — optional judge, posting, finalization and public body.
- `Orchestration/ReviewPoster.cs:37` — comment outbox, authorization, backstop and receipt.

## 1. Choose and prepare a workspace

```mermaid
flowchart TD
    A["Daemon starts"] --> B["Load profile and resolve per-app workspace root"]
    B --> C["Create slot pool<br/>Quarantine addresses with unresolved claims"]
    C --> D["Review run needs context"]
    D --> E{"Matching saved context?"}
    E -- Yes --> F["Reuse context artifact<br/>No checkout preparation"]
    E -- No --> G{"Pooled review configured?"}
    G -- No --> X["Fail closed<br/>Do not create an unmanaged PR clone"]
    G -- Yes --> H["Lease an available slot"]
    H --> I["Persist slot claim in SQLite<br/>BEFORE preparation"]
    I --> J["Clone or validate review-store checkout"]
    J --> K{"Target repo exists<br/>as a store submodule?"}
    K -- No --> Y["Decline setup and clean up lease"]
    K -- Yes --> L["Clean checkout and check workspace health"]
    L --> M["Fetch review-store origin"]
    M --> N["Check out PR notes branch<br/>Existing remote branch or default branch"]
    N --> O["Initialize permitted submodules"]
    O --> P["Fetch target PR base and head commits"]
    P --> Q["Resolve merge base<br/>Check out PR head"]
    Q --> R["Reset scratch directory"]
    R --> S["Build diff, file manifest and changed-path list"]
    S --> T["Persist context artifact<br/>Keep slot leased for this run"]
```

## 2. Mount the prepared workspace for review

```mermaid
flowchart TD
    A["Review stage begins"] --> B{"Run still holds a slot?"}
    B -- Yes --> F["Adopt prepared slot as S2S workspace"]
    B -- No --> C["Settle prior hosted-workspace ownership"]
    C --> D{"Settlement result"}
    D -- Blocked --> X["Stop this attempt<br/>Do not prepare a replacement"]
    D -- Address withheld --> E["Keep old address quarantined<br/>Lease and prepare another slot"]
    D -- Reusable --> R["Lease and prepare a safe slot"]
    E --> F
    R --> F
    F --> G["Persist provision intent<br/>BEFORE requesting a sandbox"]
    G --> H["Ask review host to provision conversation<br/>and mount the prepared workspace"]
    H --> I["Associate returned thread ID<br/>with the provision intent"]
    I --> J["Run review tools inside sandbox"]
```

## 3. If preparation fails

```mermaid
flowchart TD
    A["Preparation failure"] --> B{"Failure type"}
    B -- Recoverable checkout corruption --> C["One re-clone and preparation retry"]
    C -- Success --> D["Continue setup"]
    C -- Failure --> E["Clean up current lease"]
    B -- Unsafe path or other failure --> E
    E --> F{"Is THIS slot safe to reuse?"}
    F -- No provisioning or confirmed release --> G["Return slot to pool"]
    F -- Unsafe or uncertain ownership --> H["Retire address<br/>Release concurrency capacity"]
    G --> I["Propagate failure to retry handling"]
    H --> I
```

Recovery is simplified here: repeated generic preparation failures can also trigger the existing recovery escalator. Unsafe-path and unanswered-probe failures are excluded from blind re-cloning.

## What the fix changes

Uncertainty about an **old slot** no longer forces retirement of a **fresh, never-provisioned slot**. Claim identity, conflicting ownership and physical-path safety still gate reuse.

The **host-retention checkout** is separate. It stores/pushes review artifacts. It is not a pooled slot and is not mounted into the review sandbox.

## Source references

Paths relative to the repository root:

- `samples/CodeReviewDaemon.Sample/Program.cs:712` — pool wiring.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:1561` — lease and durable claim.
- `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs:230` — checkout preparation.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:1733` — failed-setup cleanup.
- `samples/CodeReviewDaemon.Sample/Agents/S2SReviewAgent.cs:349` — intent before provisioning.

Companion: [Offline HTML diagrams](workspace-setup.html).
