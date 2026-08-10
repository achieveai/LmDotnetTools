# Per-repo worktree pool — layout

One **mount per reviewed repo** replaces one mount per slot. Everything a review of that repo needs is
inside it, because the gateway mounts exactly one host directory at `/workspace` and a git worktree's
pointer files must reach its object store *within that mount*.

```
{workspaceBase}/review-{repoSlug}/          host mount  →  /workspace  in the sandbox
├── store/                                  THE store superproject clone. Parked on the store default
│   │                                       branch and never branch-switched. Owns every object.
│   ├── KnowledgeBase/
│   ├── PRs/
│   ├── repos/{Name}/                       reviewed repo — object-store owner AND sibling context
│   └── repos/{Sibling}/…                   sibling repos — read-only context pointers
├── slot-{i}/
│   ├── notes/                              `git worktree` of store/ @ review/{repo}/pr-{id}
│   │                                       submodules deliberately left uninitialized (repos/* empty)
│   ├── repo/                               `git worktree` of store/repos/{Name} @ the PR head  ← HOME
│   └── {scratchDirName}/
└── slot-{i+1}/…
```

Container paths follow by substituting `/workspace` for the mount root, so a slot addresses
`/workspace/store`, `/workspace/slot-{i}/notes`, `/workspace/slot-{i}/repo`.

## Why this shape

* **Objects are fetched and stored once per repo, not once per slot.** That is the whole point: the old
  pool gave every slot a full independent clone of the store *and* of the reviewed submodule, so disk and
  clone time scaled with `ReviewPoolSize × repoSize`.
* **The notes branch is per-PR** (`BuildNotesBranchName(repo, prId)`), so concurrent reviews of one repo
  cannot share a working tree — they would fight over `HEAD`. Each slot therefore gets its *own* worktree
  of the store, on its own notes branch, while `store/` itself stays on the default branch.
* **Siblings live in `store/`, not in the slot — for cost, not for correctness.** This bullet used to claim
  that a superproject worktree shares `.git/modules/<path>` with every other worktree, so initializing
  submodules inside `slot-{i}/notes` would make slots contend for one submodule `HEAD`. **That is false** —
  see invariant 6. Each worktree initializes submodules into its *own* private module directory, and slots
  can hold different submodule commits simultaneously. Nothing contends.
  <br><br>
  The real reason is the first bullet's: those private module directories are **per worktree**, so
  initializing siblings per slot re-fetches and re-stores every sibling's objects `ReviewPoolSize` times.
  Siblings in `store/` is therefore a **disk-and-fetch tradeoff**, not a constraint — and it should be
  re-weighed whenever the other side of the trade moves, because a sibling in a shared `store/` is readable
  by *every* slot, including a review that was never entitled to it. The confidentiality gate that has to
  hold that line is `DaemonReviewStageExecutor.AllowsCrossRepoCoLocation`.
  <br><br>
  Recording why the wrong reason mattered: "slots would contend" reads as an immovable correctness
  constraint, so a reader concludes there is nothing to trade and stops. "It costs disk" invites the
  comparison against confidentiality that the false claim foreclosed.

## Invariants this depends on (all verified against git 2.53.0)

1. `git worktree add --relative-paths` writes **relative** pointers in both directions (the worktree's
   `.git` file and the repo's `worktrees/{name}/gitdir`), so the mount survives being at a different
   absolute path on the host than in the container. Without `--relative-paths` (git < 2.48) the pointers
   are absolute and the sandbox sees a broken worktree.
2. A submodule's clone can host worktrees placed outside the superproject; two of them can sit at
   different commits at once, and the superproject reports clean.
3. A relocated worktree can still be `checkout --force`-ed to a new commit — that is the slot-reuse path.
4. `worktree prune` after deleting a slot directory reclaims the registration.
5. A store worktree with uninitialized submodules reports clean, commits notes normally, and leaves the
   gitlinks untouched — so the notes commit never accidentally moves a submodule pointer.
6. A worktree of the superproject initializes submodules into its **own** private module directory
   (`.git/worktrees/{name}/modules/{path}`), *not* the shared `.git/modules/{path}`. Measured with three
   worktrees holding three different submodule commits at once (`store`, `slot-0`, `slot-1`), and it holds
   under `--relative-paths`. So worktrees do **not** contend for a submodule `HEAD` — and equally, they do
   **not** share submodule objects. The second half is the cost that keeps siblings in `store/`; it is the
   only thing that keeps them there.

   Measure this one production-shaped — submodules initialized in the main working tree *first*. A scratch
   repo that never initializes them there cannot distinguish this behaviour from its negation, which is how
   the false claim above survived.
