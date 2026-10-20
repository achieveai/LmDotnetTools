---
name: repo-setup
description: Prepare every declared top-level ADO source repository at the canonical NOVA_reviews root for cross-repository search, without moving review slots or cached review baselines.
---

# Root repository setup

Use this for root cross-repository search checkouts, not PR setup. Run against the remote **NOVA_reviews** outer repository, never the LmDotnetTools SDK checkout. It reads existing `.gitmodules`; it does not add modules.

## Installation

The operator installs this skill and its sibling helper at workspace root:

```text
.claude/skills/repo-setup/SKILL.md
.claude/skills/repo-setup/scripts/repo_setup.py
.claude/skills/review-setup/scripts/review_common.py
```

The sibling import is relative to the script, not the working directory. Distribute both together. The canonical sibling directory contains only the shared helper and its provenance; it is **not** a complete review-setup skill distribution. Existing review slots require their independently installed full review-setup skill.

Python 3.10+, Git 2.48+, Linux `fcntl`, and existing Gateway egress authentication are required. New worktrees use native `--relative-paths` links, allowing the whole workspace hierarchy to resolve at different mount roots. This enables Git's `relativeWorktrees` repository extension; older Git and embedded Git clients may not support it. Apply automatically normalizes valid existing absolute forward/back links with native repair, under the existing lifecycle lock and before network work. All owned registrations are preflighted before the first repair; already-relative stores are no-ops. Foreign, broken, symlinked or locked metadata is refused. An absolute `commondir` is noncanonical and refused because native repair does not convert it. HEAD/index state is preserved, and root/slot verification requires relative forward, back and common-directory links. Plan remains read-only.

Never copy credentials or run setup against Gateway host storage. Invoke through the existing Gateway SDK/session; the daemon does not install skills. Operator-only host inspection is separate from daemon execution.

## Plan, inspect, apply

```bash
python3 .claude/skills/repo-setup/scripts/repo_setup.py plan --root /workspace
python3 .claude/skills/repo-setup/scripts/repo_setup.py apply --root /workspace --timeout 3600
```

`inventory` returns the authoritative declared repository/path set without network or mutation; Step 0 compares the apply result against this independently read set. Cleanup uncertainty returns exit 75 and `outcome:unknown`; never treat this as a settled failure. The daemon quarantines its bootstrap and all leased slots.

The daemon passes its existing `CodeReviewDaemon:Limits:CommandTimeout` to the script. For the initial five-repository run use the process-only override `CodeReviewDaemon__Limits__CommandTimeout=04:00:00`: script budget 14,400 seconds, Gateway execution plus 30 seconds, outer transport/local wait plus 60 seconds. Do not persist this override into skill/profile files or kill healthy Git early. Existing slots are verified without prime/warm/reset/clean; only a wholly absent slot can be initialized. Partial or foreign slots fail closed.

`plan` is filesystem-read-only; it may query remote HEAD to resolve a missing branch setting. Its results are advisory, not frozen apply authorization. `apply` repeats all preconditions. Use the daemon's existing durable Step 0 bootstrap when applying through automation. Long downloads must run with the existing monitored long-command budget, not an interactive short timeout.

Branch precedence: local `submodule.<name>.branch`, then `.gitmodules`, then advertised remote HEAD. `branch=.` uses the attached outer branch and refuses detached outer HEAD. The script supports direct `repos/<Repo>` ADO modules only; unsupported URLs, duplicate paths/keys and ambiguous branches fail closed.

Each root checkout is detached at that run's fetched branch tip. All source objects live in the existing per-repository SourcePool bare stores. Fetch uses no depth/shallow options. Existing shallow stores require separate explicit history priming. Root target refs are `refs/repo-setup/roots/<repo-key>`, distinct from cached review baselines.

## Safety and evidence

- Preflight every local module before the first fetch.
- Refuse symlinks, foreign clones/stores, dirty/staged/untracked/ignored source content, in-flight Git operations, attached local branches and unowned commits.
- Existing root checkouts must be owned by this command; a divergent or rewritten upstream is refused rather than abandoning commits.
- Never force, reset, clean, merge, rebase, push, publish a review baseline, or warm/reset/prepare slots.
- Outer ordinary tracked files and index must be clean. Expected unstaged gitlink differences are allowed; do not stage them.
- Apply uses the existing exclusive lifecycle barrier and per-repository acquisition/store locks. Ordinary external Git processes must not modify the same root concurrently.
- Success is strict JSON with `complete:true`, a nonzero module count, and every repository `ready` with its exact target SHA. Progress goes to stderr. Any failed module makes the invocation fail; earlier completed modules may remain prepared. No automatic rollback or destructive cleanup.
- A transport loss is an unknown outcome: preserve state and reconcile the daemon bootstrap before any rerun. A definite failure preserves all unexpected content.

After apply, verify root HEADs match reported target SHAs, Nova shares its existing source store, and review-slot HEAD/index/ownership, cached baselines and prepared manifests are unchanged. This verifies root setup, not PR checkout or Bluebird readiness.

## Local tests

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s samples/CodeReviewDaemon.Sample/skills/repo-setup/tests -v
```

Tests use disposable local Git repositories and synthetic ADO URLs; no live credentials or PRs.
