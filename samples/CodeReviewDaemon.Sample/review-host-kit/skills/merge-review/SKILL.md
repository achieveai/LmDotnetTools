---
name: merge-review
description: >
  Safely checkpoint one managed pooled NOVA review, merge its exact local branch tip into
  canonical local main, hand conflicts to the operator, and delete only that local review
  branch after verified integration. Never pushes or completes an Azure DevOps PR.
user-invocable: true
disable-model-invocation: false
allowed-tools: Read, Bash, TaskOutput
---

# Merge Review — local resumable integration

This is the local sister workflow to `review-setup`. It operates only on manifest-owned
schema-2 pooled reviews. It does not call Azure DevOps, fetch PR metadata, push, comment,
or complete a remote pull request.

## Invoke by absolute path

Run mutating commands in managed background execution and inspect the final exit code.
The durable transaction can remain reserved across a conflict handoff.

```bash
python3 /workspace/.claude/skills/review-setup/scripts/merge_review.py --repo Nova --slot 0 --dry-run
python3 /workspace/.claude/skills/review-setup/scripts/merge_review.py --repo Nova --slot 0
```

Options:
- `--history-timeout SECONDS`: bounded fresh tracking-baseline acquisition budget; default 300.
- `--max-depth COMMITS`: acquisition ceiling; default 1,000,000.
- `--lock-timeout SECONDS`: aggregate lock-wait budget; default 1,200.
- `--root PATH`: canonical review root; normally inferred from the installed script.

The tracking baseline is chosen from canonical local `submodule.<name>.branch`, then the
committed `.gitmodules` value, otherwise the remote symbolic HEAD. The reviewed PR target
is never substituted. The selected branch tip must be fetched freshly on first invocation,
then its exact SHA is retained privately and reused without a refetch on every retry.

The workflow checkpoints only the manifest-recorded PR directory plus the selected
gitlink. New runs record `PRs/{canonical-repo-name}-{pr}/`; existing legacy lowercase
`pr_dir` values remain authoritative verbatim. Merge never derives this path from the
normalized review key and never renames, migrates, merges, or adopts a case-colliding
directory. Unrelated slot edits, dirty nested source content, foreign stages, a changed
review branch, or an unsafe canonical root cause refusal. Canonical local `main` must be
checked out. Unrelated untracked and ignored root files are preserved unless they collide
with an incoming path. An uninitialized canonical source remains uninitialized; an
initialized one must already be clean, detached, correctly owned, and at the pinned
baseline.

The merge is nonrecursive and exact:

```text
git -c submodule.recurse=false merge --no-ff --no-commit <checkpoint-SHA>
```

A clean merge normalizes the selected gitlink to the pinned baseline before commit.
Other submodules are not recursively initialized, updated, or reset.

## Conflict handoff

Conflict exit code is `4`. The command prints:
- canonical root path,
- every unresolved path,
- required selected gitlink SHA,
- the resolve/stage/rerun instructions.

Resolve only the intended merge paths. Stage each resolution explicitly with `git add`.
Do not run another merge, switch branches, fetch, reset, or clear `MERGE_HEAD`. Rerun the
same command. It requires the exact recorded HEAD, MERGE_HEAD, branch, incoming path scope,
and pinned gitlink. It refuses unresolved or unstaged tracked edits. A manually created
commit is accepted only when it has the exact recorded two-parent merge topology and tree.

After verified integration, merge-review detaches only the selected outer slot at the new
main result, verifies the nested source at the pinned baseline, proves main contains the
checkpoint, and deletes only the exact local review ref with expected-old-SHA protection.
The run becomes `merged`; repeated invocation verifies and returns that same result.
Retention refs and terminal history remain.

## Explicit abort

Abort abandons canonical-root staged/worktree conflict resolutions. It preserves the
checkpointed review branch and source retention refs, and returns the run to retryable
`merge-aborted` state.

```bash
python3 /workspace/.claude/skills/review-setup/scripts/merge_review.py \
  --repo Nova --slot 0 --abort --confirm nova-12345
```

Abort requires the exact review key and exact transaction HEAD/MERGE_HEAD. It never aborts
a foreign Git operation and never clears JSON manually.

## Interrupted root writer recovery

A durable outer transaction reserves canonical main across the human pause. Every script
owned canonical-root mutating child has a separate pending-writer record. A free flock is
not retirement proof. Unknown process lifetime blocks mutation until an authorized operator
establishes broader-scope retirement and binds the exact transaction, journal, and current
Git topology.

Inspect without clearing:

```bash
python3 /workspace/.claude/skills/review-setup/scripts/merge_review.py \
  --repo Nova --slot 0 --recover-inspect
```

Copy the reported bindings into a JSON assertion. Add `prior_writer_retired: true`, a
non-empty `operator`, and a concise non-empty `basis`. Apply only that exact assertion:

```bash
python3 /workspace/.claude/skills/review-setup/scripts/merge_review.py \
  --repo Nova --slot 0 --recover-assert /path/to/assertion.json
```

Recovery rechecks observable process identity and unchanged topology under the canonical
locks. It performs no merge, commit, checkout, fetch, ref deletion, or transaction clear.
It writes an immutable recovery audit, then settles only the exact writer record. Retry the
normal merge command afterward. Never infer descendant death from a free lock and never
edit or unlink journals manually.

Exit codes: `0` success/read-only inspection; `4` manual conflict resolution required;
`1` refused or failed with durable state retained.
