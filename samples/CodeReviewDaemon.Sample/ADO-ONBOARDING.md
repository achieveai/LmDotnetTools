# Onboarding an Azure DevOps repo (dedicated ADO daemon + ADO cross-repo store)

This is the runbook for reviewing PRs in a **private Azure DevOps** repository with a **cross-repo
store that itself lives in ADO**, run as a **second daemon instance in parallel** with the default
GitHub daemon. The worked example is `MCQdbDEV` reviewed through the ADO store `MCQdbReview`; substitute
your own org/project/repo throughout.

## How it fits together

The daemon is already provider-agnostic for PR discovery and comment posting: `AdoPrProvider` polls
active PRs and `AdoReviewCommentPublisher` posts review threads, both gated behind
`CodeReviewDaemon:EnableAdoProvider` and authenticated by the MSAL `AdoOAuthProvider`. The review diff,
however, is produced from a **host-side `git clone` + `git diff`**, and the host-git credential is now
injected per signed-in OAuth provider (`HostGitCredentialEnv`): GitHub for `github.com` remotes, Azure
DevOps (the Entra token in the Basic password field) for `dev.azure.com` remotes. That is what lets a
private ADO store + submodule be checked out for a review.

The cross-repo store model puts the reviewed repo in as a git **submodule** of the store:

```
MCQdbReview (ADO repo, default branch main)
├── KnowledgeBase/        # seeded by `reviewbot init`
├── PRs/                  # seeded by `reviewbot init`
└── repos/
    └── MCQdbDEV          # submodule → MCQdbDEV @ branch dev
```

## ⚠️ Use the `dev.azure.com` URL form everywhere — never `*.visualstudio.com`

The daemon matches a `.gitmodules` submodule against the reviewed repo on **exact host + path**
(`DaemonReviewStageExecutor.SubmoduleTargetsRepo`), and it always normalizes an ADO repo to
`https://dev.azure.com/{org}/{project}/_git/{repo}` (`TargetRemoteUrl`). A legacy
`https://mcqdbdev.visualstudio.com/...` URL in `.gitmodules`, the store clone URL, or config will **not
match** — the daemon silently falls back to a single-repo checkout and the cross-repo store is lost. So:

- Store clone URL / `CrossRepoStoreUrl` / `ReviewBotRepoUrl`: `https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbReview`
- Submodule URL in `.gitmodules`: `https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbDEV`

## Configuration

`appsettings.MCQdb.json` (in this project) holds the ADO/repo/store overrides and is selected at launch
by `DOTNET_ENVIRONMENT=MCQdb`; everything else is inherited from `appsettings.json`. It sets
`EnableAdoProvider`, the tool-assisted + reviewer-writes flags, `EnabledRepos`, the store URL, isolated
DB/workspace/pool paths, a distinct webhook port (5081), and `Auth:Ado` (the azure-devops-mcp public
client id, `common` tenant, and the ADO resource `.default` scope). See
`../LmStreaming.Sample/AuthProviderGuide.md` for the ADO Entra/MSAL details.

Comment posting stays **off** (`EnableCommentPosting` inherited `false`) until you explicitly enable it —
a freshly-onboarded repo is collect-only.

## One-time setup

Run these once, in order. The daemon has **no `auth` subcommand** — it restores tokens at boot from the
token store, so ADO sign-in is done out-of-band and shared via `Auth__TokenStoreDir`.

### 1. Register/confirm the Entra app and sign in to ADO (out-of-band)

The daemon reads a token store that must already contain a valid ADO (MSAL) sign-in. The simplest way to
seed it is `LmStreaming.Sample`, which has the full interactive ADO sign-in flow:

1. Point `LmStreaming.Sample`'s `Auth:Ado` at the same client id/scopes as `appsettings.MCQdb.json`, and
   set its `Auth:TokenStoreDir` to a **shared, absolute** path (e.g. a fixed `oauth-tokens` dir).
2. Run it and sign in to Azure DevOps in the browser (MSAL loopback + PKCE).
3. Confirm the store now holds `msal-ado.bin`.

The daemon's `OAuthTokenHydrator` restores that sign-in at boot, and `GetAccessTokenAsync` silently
refreshes it thereafter.

### 2. Seed the `MCQdbReview` store skeleton

Create the `MCQdbReview` repo in ADO **empty** (no README/initial commit), then seed the store skeleton
(default branch `main`, `KnowledgeBase/`, `PRs/`):

```bash
CodeReviewDaemon reviewbot init \
  --url https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbReview \
  --gateway <sandbox-gateway-base-url>
```

`reviewbot init` runs git **inside the sandbox** (gateway-authed), which is a different path from the
runtime host-git credential above; the gateway session must have ADO git egress authorized. If that is
not available, seed the store host-side instead (a normal `git clone` + skeleton commit + push with your
ADO credentials) — the end state is identical.

### 3. Add the reviewed repo as a submodule pinned to `dev`

On a checkout of `MCQdbReview` at `main`:

```bash
git submodule add -b dev \
  https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbDEV \
  repos/MCQdbDEV
git commit -m "Add MCQdbDEV submodule (branch dev)"
git push origin main
```

The `dev.azure.com` URL is mandatory (see the warning above). MCQdbDEV's integration branch is `dev`, so
PRs are reviewed against base `dev`.

### 4. Launch the dedicated instance

Run the second daemon alongside the GitHub daemon with a distinct environment, port, DB/workspace roots
(from `appsettings.MCQdb.json`), a shared token store, and its own sandbox app id:

```bash
DOTNET_ENVIRONMENT=MCQdb \
ASPNETCORE_URLS=http://127.0.0.1:5081 \
Auth__TokenStoreDir=<shared-oauth-tokens-dir> \
CRD_SANDBOX_GATEWAY=<sandbox-gateway-base-url> \
CRD_SANDBOX_APP_ID=codereview-daemon-mcqdb \
dotnet run --project samples/CodeReviewDaemon.Sample
```

(`CRD_SANDBOX_APP_KEY` is only needed when the gateway runs with `AUTH_ENFORCE=on`; omit it for the
keyless dev path.)

### 5. Verify

- Boot logs an ADO poll target for `mcqdbdev/MCQdb_Development/MCQdbDEV`.
- On the first review of an active MCQdbDEV PR, the log shows the repo resolved as a store submodule —
  **not** `… is not a submodule of the pooled store; using the per-run checkout` (that line means the
  submodule URL didn't match; re-check the `dev.azure.com` form).
- Review output is collected in `mcqdb-review.db`; nothing is posted until `EnableCommentPosting` is set.
