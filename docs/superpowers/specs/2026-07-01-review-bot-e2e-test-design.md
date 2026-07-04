# Code-Review Daemon ("Review Bot") — End-to-End Manual Test Plan

**Date:** 2026-07-01
**Author:** planning session (brainstorming)
**Target:** `samples/CodeReviewDaemon.Sample` (shipped in PR #121 / issue #118, commit `f67075f`)
**Goal:** Manually stand up the daemon on this box and drive one real, end-to-end review of a live
GitHub PR on `achieveai/LmDotnetTools` — through the sandbox, the git clone/diff, and the LLM review
agent — with the output landing in SQLite. Collect-only (nothing posted to the PR).

---

## 1. What we are (and are not) proving

**Milestone 1 (this plan) — the whole pipeline minus the outward action:**

```
PrPollingService  →  GitHubPrProvider.ListOpenPullRequests (real api.github.com, real token)
   →  PrOrchestrator drives the stage machine for PR #88:
        Discovered → ContextReady  (sandbox: git clone achieveai/LmDotnetTools, fetch base+head, git diff)
                   → Reviewed      (in-process Copilot Sonnet-5 Anthropic agent reasons over the diff)
                   → Judged        (skipped; EnableJudgeAgent=false)
                   → Posted        (ReviewPoster records "Collected" — NO comment posted)
   →  review artifact persisted in review.db (SQLite)
```

**Explicitly OUT of scope for milestone 1** (each a later milestone):
- Posting a review comment to the PR (`EnableCommentPosting`).
- ReviewBot retention git repo + `reviewbot init` + sandbox `git push` (`ReviewBotRepoUrl`).
- A/B variant, Knowledge agent, Judge agent.
- Azure DevOps.

**Success criteria (all must hold):**
1. Daemon boots with exactly one HTTP route (`POST /api/auth/webhook/{provider}`) and no crash.
2. A poll cycle lists PR #88 from the real GitHub API (token works).
3. The sandbox clones `achieveai/LmDotnetTools` and produces a non-empty `base...head` diff for #88.
4. A `review` artifact for the run exists in `review.db` with real Sonnet-5 review text.
5. No comment is posted to PR #88; `ReviewPoster` recorded `Collected`.

---

## 2. Environment preconditions (verified on this box 2026-07-01)

| Dependency | State | Note |
|---|---|---|
| Sandbox gateway (Docker) | **Up + healthy on `:3000`** | `sandboxedostoolsmcpserver-gateway-1`; use `CRD_SANDBOX_GATEWAY=http://127.0.0.1:3000` (NOT the daemon default `:8080`). IPv6 webhook fix already applied. |
| `gh` auth | logged in `gautam-achieveai` | scopes `repo, workflow, read:org, gist`; token reused for the daemon's GitHub token file. |
| Target repo | `achieveai/LmDotnetTools` **PUBLIC** | so `git clone` in the sandbox needs no injected token. 1 open PR: **#88**. |
| Copilot token | resolvable | proxy `/health` → healthy; `CliCredentialCopilotTokenProvider` path works. `claude-sonnet-5` is in Copilot's model list. |
| `.NET` build | daemon + tests in `LmDotnetTools.sln` | daemon test baseline ~282+ green. |
| Gateway workspaces (host) | `B:\sandbox-workspaces\workspaces\{demo,reviews,achieveai-office-work}` | a dedicated `reviews` workspace already exists. |

---

## 3. The one code change (the daemon can't do Sonnet-5 as shipped)

Two shipped facts force a small, contained code change (modifying existing files only — no v2/enhanced
files, per the repo's global rules):

1. **`LiveReviewAgentLoopFactory` builds an OpenAI client** (`OpenClient` → `/v1/chat/completions`) and
   reads `OPENAI_API_KEY`/`OPENAI_BASE_URL`. Copilot Sonnet-5 speaks the **Anthropic Messages** shape
   (`/v1/messages`), so an OpenAI client can't call it.
2. **The poller never sets `ReviewRun.ModelId`** (`PrPollTargetBuilder`/`PrPollingService` leave it
   null), so the primary review would call with an **empty model id** → provider 400.

**Change set (all in `samples/CodeReviewDaemon.Sample/`, + 2 ProjectReferences):**

- `Agents/LiveReviewAgentLoopFactory.cs` — build a **Copilot-backed Anthropic agent** instead of the
  OpenAI agent: reuse the proven `CopilotAnthropicAgentFactory.Create(...)` +
  `CliCredentialCopilotTokenProvider` + `CopilotSessionContext` (exactly as `LmStreaming.Sample`'s
  `CreateCopilotAnthropicAgent` does), wrapped in the same `MultiTurnAgentLoop`. Keep the
  `IReviewAgentLoopFactory` signature unchanged.
- `Configuration/CodeReviewDaemonOptions.cs` — add `ReviewModelId` (default `"claude-sonnet-5"`).
- Thread the model id into the run: set `ReviewRun.ModelId` from the option on the seed in
  `PrPollingService.PollTargetAsync` (or carry it on `PrPollTarget` via `PrPollTargetBuilder`). The
  B-variant already hardcodes its own model, so only the primary path needs this.
- `CodeReviewDaemon.Sample.csproj` — add `ProjectReference` to `AnthropicProvider` and
  `GithubCopilotProvider` (OpenAIProvider ref can stay).

**Guard:** after the change, `dotnet build LmDotnetTools.sln` is 0-warning and the existing daemon test
suite stays green (the factory change is behind the same interface the tests fake, so unit tests are
unaffected; a build + test run confirms). **The diff is shown to the user before any daemon run.**

---

## 4. Step-by-step execution

### Phase A — Build & baseline (no external calls)
1. `dotnet build LmDotnetTools.sln` — confirm clean before touching anything.
2. Apply the §3 change set. Rebuild 0-warning.
3. `dotnet test tests/CodeReviewDaemon.Sample.Tests` — daemon suite stays green (baseline parity).
   **Show the user the diff here.**

### Phase B — De-risk the sandbox egress FIRST (the #1 execution risk)
The daemon *connects* to a gateway session by `X-Session-ID` but never *creates* one with a network
allow-rule for `github.com`. Before wiring the whole daemon, prove a clone works in the session the
daemon will use:
4. Pick/create a gateway session id (`CRD_SANDBOX_SESSION`). Drive the gateway MCP `Bash` tool directly
   (same call the daemon makes) to run, inside the sandbox:
   `git clone https://github.com/achieveai/LmDotnetTools /work/target` (public → no token).
   - **If it succeeds:** egress to github.com is allowed for this session → proceed.
   - **If it's blocked (egress Deny):** the session needs a github.com allow rule. Fallback: create the
     session out-of-band with a network allow block (as `SandboxSessionRegistry.BuildAuthProviders`
     does), or reuse a `LmStreaming.Sample`-created session that already has the github rule, and pass
     that session id to the daemon. This branch is the main thing that could expand the plan; we learn
     it in one cheap probe rather than after wiring everything.

### Phase C — GitHub token for the PR-list API
5. Create `oauth-tokens/github.json` in `FileOAuthTokenStore` format (fields:
   `Provider="github"`, `Account`, `AccessToken=<gh token>`, `RefreshToken=""`,
   `AccessTokenExpiresAtUtc=<far future>`, `Scopes=[...]`). Source the token from the existing `gh`
   login. Point `Auth:TokenStoreDir` (or place it beside the binary) so the daemon hydrates it.
   - Sanity: one manual `GET api.github.com/repos/achieveai/LmDotnetTools/pulls?state=open` with the
     token returns PR #88 before the daemon relies on it.

### Phase D — Configure the daemon (collect-only)
6. `appsettings` / env for the run:
   - `CodeReviewDaemon:EnabledRepos = ["achieveai/LmDotnetTools"]`
   - `CodeReviewDaemon:EnableCommentPosting = false` (collect-only — default)
   - all other agent flags false; `ReviewBotRepoUrl` unset; `ReviewModelId = "claude-sonnet-5"`
   - `CRD_SANDBOX_GATEWAY=http://127.0.0.1:3000`, `CRD_SANDBOX_SESSION=<the Phase-B session>`
   - `CodeReviewDaemon:DatabasePath` = a throwaway path so the run is isolated & inspectable
   - `Auth:Webhook:SigningSecret` = any value (webhook route unused in milestone 1, but set so boot
     doesn't warn).

### Phase E — Run & observe
7. `dotnet run --project samples/CodeReviewDaemon.Sample` (Development). Watch structured logs for:
   the poll listing PR #88; the sandbox clone/fetch/diff; the review agent turn; the ReviewPoster
   `Collected` line.
8. Let one poll cycle complete (30s interval). Stop the daemon.

### Phase F — Verify the evidence (don't trust logs alone)
9. Query `review.db` (SQLite): confirm one `review_run` for PR 88 reached stage `Posted`/`Completed`,
   and a `review-context` artifact (non-empty diff) + a `review` artifact (Sonnet-5 text) exist.
10. Confirm on GitHub that **no comment** was posted to PR #88 (collect-only proof).
11. Read the actual review text — sanity-check it's a real review of #88's diff, not an error string.
12. Notify the user (HITL) with the outcome + the review text excerpt.

---

## 5. Risks & mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Sandbox session can't reach github.com (no allow rule; daemon doesn't create sessions) | **Medium — the main one** | Phase B probes it FIRST with a one-line clone; fallback = pre-create/reuse a session with a github allow rule. |
| Empty `ModelId` → provider 400 | High if unaddressed | §3 threads `ReviewModelId` into the run seed. |
| Copilot Sonnet-5 Anthropic wiring differs from OpenAI path | Low | Copy the exact proven `LmStreaming.Sample` `CreateCopilotAnthropicAgent` recipe; build+test gate. |
| PR #88 diff too large → truncated context / slow/pricey turn | Medium | `SandboxLimits.CapArtifactPayload` already bounds the diff; accept a bounded review for milestone 1. |
| Token file format drift | Low | Validate with a manual GitHub API call (Phase C) before the daemon uses it. |
| Accidental outward action | Low | Every posting/retention flag stays off; Phase F step 10 verifies no comment. |

---

## 6. After milestone 1 (future milestones, not now)
- **M2 – Posting:** flip `EnableCommentPosting`, needs a bot identity you're OK commenting as; verify
  idempotent single post at the head SHA.
- **M3 – Retention repo:** create an empty ReviewBot remote, `reviewbot init --url ... --gateway ...`,
  set `ReviewBotRepoUrl`; needs a sandbox git-auth path (the auth-webhook wiring the daemon doesn't set
  up today) for the `git push` — likely a small daemon change to create its session with auth_providers.
- **M4 – Judge / Knowledge / A-B:** flip those flags once the base loop is trusted.
- **M5 – ADO:** `EnableAdoProvider` + an `org/project/repo` entry + ADO sign-in.
