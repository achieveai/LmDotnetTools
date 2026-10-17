# NOVA code review — how we review now

The live review pipeline is driven by **`Reviewd.Sample`** (the S2S/Gateway operator CLI), not by
running `CodeReviewDaemon.Sample` as a service. `Reviewd.Sample review batch` is the producer: it
drives one review per selected PR through the **S2S review host**, each review runs inside a
**Gateway sandbox**, posts review comments back to the Azure DevOps (ADO) PR, and records the review
under the **NOVA_reviews** cross-repo store (branch `review/{repo}-{pr}`). This file is the operator
map for that flow. It records observed, load-bearing facts; secrets are never included here.

> Posting PR comments and pushing review branches are **external, mutating actions requiring explicit
> user approval** each time. Context gathering (PR/diff/blame/discussion reads) is read-only and does
> not. Approval for one run/scope does not extend to another.

## The three live pieces

1. **Gateway** (Docker compose) — sandbox gateway on `http://127.0.0.1:3000` + the `egress-proxy`
   container. All sandbox→internet traffic (ADO, GitHub) is mediated here; default-deny.
2. **Review host** — the S2S streaming host (`LmStreaming.Sample`) on **`http://0.0.0.0:5000`** (the
   current review-service host port). It hosts the review-agent conversations and exposes the
   freeze / run-state control endpoints used by slot reclaim. Started with
   `--SandboxGateway:AppId codereview-daemon-nova --SandboxGateway:BaseUrl http://127.0.0.1:3000`.
   Only one host may bind :5000 — never start a second host on the same port.
3. **Producer** — `Reviewd.Sample review batch …`. Reads a selection file, claims slots, and for each
   PR runs `review run` against the host + a sandbox session.

Verify what is actually live before acting:

```bash
ss -ltnp | grep -E ':(3000|5000|5206)\b'
docker ps --format '{{.Names}}' | grep -iE 'gateway|egress'
ps -eo pid,etime,cmd | grep -E 'LmStreaming.Sample|Reviewd.Sample.dll' | grep -v grep
```

## Running the batch (the producer)

```bash
dotnet samples/Reviewd.Sample/bin/Debug/net9.0/Reviewd.Sample.dll review batch \
  --selection <selection.json> --db <pending.db> \
  --session <sandbox-session-id> --workspace <host-workspace-id> \
  --provider gpt-6.1-sol --mode code-review-daemon --reasoning-effort xhigh \
  --max-age-hours 72 --config <appsettings.nova.json> --execute
# optional --listen/--callback give the batch its OWN embedded webhook; omit to rely on a
# standalone `auth serve` (see egress). Default embedded listen is 127.0.0.1:5203.
```

- The batch **watches the selection file**; when its digest changes it re-claims and drains the new
  queue. It has **no auto-discovery** — to review newly-opened PRs, regenerate the selection (from a
  fresh `pr check`/`pr pick`) and the running batch picks it up. Failed PRs requeue; ambiguous owners
  hold their slot.
- Per-PR lifecycle (`ReviewRunner.cs`): **prepare** (fetch PR merge ref into the sandbox) →
  **review** (model reviews; stage `review-running` = InProgress) → **confirmation** → **verify**.
  The `code-review-daemon` workflow publishes: `ReviewRunnerSandbox.py` **requires a posted summary +
  new comment IDs and independently verifies publication** — i.e. the review posts to the PR, it is
  not collect-only.
- Env/credentials: the producer and `auth serve` load their config from `--config` and env; the ADO
  OAuth cache + the per-session secret must live in `Auth:TokenStoreDir` (see the token-store note
  below).

### Selection format

```json
{
  "requestId": "…", "maxAgeHours": 72,
  "lanes":        { "O365Exchange/Weve_DA/Nova": [1,2,3,4,5] },
  "repositories": { "O365Exchange/Weve_DA/Nova": [
     { "repository": "O365Exchange/Weve_DA/Nova", "prId": "5762407",
       "headSha": "<LIVE head>" } ] }
}
```

- `lanes` = the slots (0–5) the batch may use for that repo; `repositories` = the PR queue. **The
  `headSha` must be the current live head** — the batch refuses a PR whose head moved ("Selected PR
  head changed"). Build selections from a fresh `pr check` snapshot, not stale data.
- `--repo` / repository is `org/project/repo` (3 segments). Casing of the trailing repo segment must
  be canonical (`Nova`, `NovaClient`, `Astra`, `WeveNova`, `MODISService`) — the sandbox pool matches
  the source module **case-sensitively**.

## ADO / GitHub egress and the `502 policy_evaluation_failed` you will hit

Sandbox→ADO and sandbox→GitHub egress is default-deny at `egress-proxy`; managed `ado`/`github` rules
exist (`OAuthProviderHosts.cs`). For each allowed request the proxy calls an **auth webhook** to
inject the credential first. The webhook endpoint is **baked into each sandbox session at creation**
from the creating process's `--callback`, e.g. `http://host.docker.internal:<port>/api/auth/webhook/ado`
(`host.docker.internal` → the docker bridge `172.17.0.1` inside the gateway containers).

**Symptom:** sandbox ADO calls fail `502 … policy_evaluation_failed`; egress-proxy / gateway logs show
`auth webhook failed: error sending request for url (http://host.docker.internal:<port>/api/auth/webhook/ado)`.

**Root cause:** nothing is serving the webhook on `<port>` — the producer that embeds it, or a
standalone `auth serve`, is **down**. NOT an ADO outage, NOT temporary (a non-ADO host still cleanly
403-default-denies, proving proxy+policy healthy).

```bash
docker logs --since 5m egress-proxy 2>&1 | grep -iE 'dev.azure|webhook|Deny|502'
ss -ltnp | grep ':<port>'
docker exec egress-proxy getent hosts host.docker.internal   # -> 172.17.0.1
```

**Fix — serve the webhook on the exact baked port:**

```bash
dotnet samples/Reviewd.Sample/bin/Debug/net9.0/Reviewd.Sample.dll auth serve \
  --listen http://172.17.0.1:<port> --callback http://host.docker.internal:<port> \
  --config <appsettings with Auth:TokenStoreDir + Auth:Ado (+ Auth:Github)>
```

`auth serve` (`AuthCommands.cs`) hosts only `AuthWebhookController` and registers whichever OAuth
providers are configured (ADO from `<TokenStoreDir>/msal-ado.bin`; GitHub if `Auth:Github:ClientId`
set) — so one `auth serve` can answer both `/api/auth/webhook/ado` and `/.../github`. `--listen` must
bind loopback or a real local interface (use `172.17.0.1`, not `0.0.0.0`); `--callback` may use
`host.docker.internal` over http. Healthy webhook: `GET→405`, unauth `POST→401`, the gateway's real
`POST→200`, and the egress decision flips to `Allow { reason: "allowed by rule ado",
auth_cache_key: "<session>:ado-auth:…" }`.

Keep `auth serve` **independent of the batch** so the batch can be stopped/restarted to drain or fix
without taking the webhook down (the egress policy is pinned to the baked port, not to the batch).

## Token store

The ADO MSAL cache (`msal-ado.bin`), `github.json`, and `session-secrets/<sha256(session)>.secret`
live in a shared `Auth:TokenStoreDir`. A review on session `<S>` needs that session's secret present
in the store being used — env files may point at a different store than the one holding the secret,
so set `Auth__TokenStoreDir` explicitly to the store that has both `msal-ado.bin` and the session
secret (the live NOVA store is `/home/gautamb/repos/LmDotnetTools/.run/nova/oauth-tokens`).

## Slots and reclaiming stuck owners

Each repo/slot has a native owner marker; **one owner per slot**. A slot held by a dead/idle admitted
conversation blocks new prepares. Reclaim (freeze the conversation → confirm quiescence → discard the
native slot → release the SQLite owner):

```bash
dotnet samples/Reviewd.Sample/bin/Debug/net9.0/Reviewd.Sample.dll review reclaim-slot \
  --repo <org/project/repo> --pr <id> --slot <0-5> --db <db> --workspace <ws> --session <sess> --execute
```

- An **actively generating** review will not quiesce inside reclaim's 20s window — reclaim only after
  its model turn finishes (or it is idle). A dry run (no `--execute`) freezes, checks quiescence, then
  unfreezes.
- "Native claim changed; no cleanup allowed" means the native marker's claim token no longer matches
  the DB attempt — the slot is out of sync; do not force it, exclude that slot and investigate.

## NOVA_reviews cross-repo store

`CrossRepoStoreUrl`/`ReviewBotRepoUrl` → `https://github.com/gautamb_microsoft/NOVA_reviews.git`, a
**superproject whose submodules are the 5 source repos** (`.gitmodules`:
`repos/{Nova,NovaClient,Astra,WeveNova,MODISService}`). Host-side checkouts live under the Gateway
workspaces, e.g. `~/Gateway/workspaces/review-host-60daa51f5caf4e3e/nova-reviews` and
`~/Gateway/workspaces/codereview-daemon-nova-383c13b649323497/nova-reviews` (`store -> /workspace`
symlink; `PRs/`, `DeveloperLearnings/`, `KnowledgeBase/`, `Memory/`, `.conversations/`). Review
branches are named **`review/{repoSlug}-{prNumber}`** (lowercased slug); a re-review of an updated PR
reuses the **same** branch; many `review/*` branches already exist there. In-store slot operations
(prepare / inspect / reset / archive / merge) go through the **`review-setup` skill**
(`.claude/skills/review-setup/`), never raw git — never hand-edit `.git/review-setup/` manifests/locks
or `rm -rf`/`git clean`/`reset --hard` a managed slot; always check `review_pool_admin.py status`
first. Push auth to github.com is the GitHub **OAuth** token (`github.json`, scopes `repo`,`read:org`),
never a PAT/SSH.

## Config knobs (values redacted)

`appsettings.nova.json` → `CodeReviewDaemon`: `EnabledRepos` (allow-list, also enforced by the posting
policy), `EnableCommentPosting`, `UseS2SReviewAgent=true` (review runs via the S2S host; the hosted
agent publishes), `CrossRepoStoreUrl`/`ReviewBotRepoUrl`, `ReviewPoolSizesByRepository` (`Nova`:6),
`LmStreamingBaseUrl` (the host, :5000). ADO identity `Auth:Ado` (ClientId `0d50963b-…`, tenant
`72f988bf-…`); GitHub `Auth:Github` (scopes `repo`,`read:org`).

Packaged form (older publish, reference only): manual-start systemd **user** units
`codereview-stack.target` → `gateway-compose` + `codereview-host.service` + `codereview-daemon.service`
under `~/.local/share/lmdotnettools-review-daemon/{publish,env}` (that host pins :5051). The current
live flow uses the `Reviewd.Sample` producer against the :5000 host instead.

## Preconditions checklist

**Any review:** gateway (3000) up; review host up on :5000; sandbox session prepared; the ADO auth
**webhook alive on the session's baked port**; signed-in ADO OAuth (`msal-ado.bin`) + the session
secret in `Auth:TokenStoreDir`; canonical repo casing; the selection `headSha` = current live head.

**Post a comment / record the review branch:** `EnableCommentPosting=true`; target repo in
`EnabledRepos`; signed-in ADO OAuth; GitHub OAuth token with push rights on NOVA_reviews; **explicit
user approval**.
