# Review Host Kit

A committable, reusable bundle for standing up a **NOVA_reviews-style review
repository** — an outer Git repo that reviews one or more product repos (added
as submodules) in isolated, disposable worktree slots.

It is a **distribution template**, not a runnable config. Nothing here runs from
inside this kit. You copy the pieces into a *new* review repo and run them there.

> Source: extracted from a live review host at
> `~/Gateway/workspaces/<host>/nova-reviews`. Runtime data (conversations, PR
> artifacts, learnings, live slots, submodule checkouts) was intentionally left
> behind — only the portable tooling is here.

## What's in the kit

```
review-host-kit/
├── CLAUDE.md.example          # review-host operating rules (SEE "Host-specific" below)
└── skills/
    ├── review-setup/          # prepare / reset / merge a PR review slot  ← core
    │   ├── SKILL.md
    │   └── scripts/
    │       ├── review_setup.py        # prepare a slot (git + read-only PR context)
    │       ├── review_git_prepare.py  # prepare a slot (git checkout only)
    │       ├── review_reset.py        # RESET a slot (archive or discard) — destructive
    │       ├── review_pool_admin.py   # check / warm the shared slot pool
    │       ├── review_ado.py           # Azure DevOps PR / work-item / blame context
    │       ├── review_context.py       # context gathering
    │       ├── merge_review.py          # merge a finished local review to main
    │       └── review_common.py         # shared library — imported by all the above
    ├── repo-setup/            # prepare the outer repo's submodules for cross-repo search
    │   ├── SKILL.md
    │   └── scripts/repo_setup.py        # imports review-setup/scripts/review_common.py
    ├── merge-review/          # SKILL.md only — drives review-setup's merge_review.py
    ├── bluebird/             # committed-code + wiki search bridge (MCP) + policy
    └── working_with_bluebird/ # SKILL.md — the canonical live-bridge route
```

### Dependency note (don't split these)

- Every `review-setup` script imports `review_common.py` as a **sibling** — keep
  the `scripts/` directory intact.
- `repo-setup/scripts/repo_setup.py` imports `review_common.py` from
  **`review-setup/scripts/`** by relative path. Install `repo-setup` and
  `review-setup` **together**.
- `merge-review` has no scripts of its own; it calls `review-setup`'s
  `merge_review.py`.

## Requirements

- Python 3.10+
- Git 2.48+ (uses worktree `--relative-paths`; older Git / embedded Git clients
  may not support the `relativeWorktrees` extension)
- Linux `fcntl` (lifecycle locking)
- Existing Gateway egress authentication for the product repos' remotes
  (Azure DevOps). **No credentials are stored in this kit** — auth is supplied at
  runtime from the environment.

## Bootstrapping a new review repo

1. **Create the outer repo** that will hold reviews:

   ```bash
   mkdir my-reviews && cd my-reviews && git init
   ```

2. **Add product repos as submodules** under `repos/`. (A `.gitmodules` is *not*
   shipped in this kit — you declare the repos you actually review.) Example:

   ```ini
   # .gitmodules
   [submodule "repos/Nova"]
       path = repos/Nova
       url = https://dev.azure.com/<org>/<project>/_git/Nova
       shallow = true
   ```

   ```bash
   git submodule add --depth 1 https://dev.azure.com/<org>/<project>/_git/Nova repos/Nova
   ```

3. **Install the skills** into the review repo's Claude skills dir:

   ```bash
   mkdir -p .claude/skills
   cp -r /path/to/review-host-kit/skills/* .claude/skills/
   ```

4. **Add operating rules**: copy the example and edit it for your repos
   (see "Host-specific" below):

   ```bash
   cp /path/to/review-host-kit/CLAUDE.md.example CLAUDE.md
   ```

5. **Prepare the submodules** for cross-repo search (outer repo only — reads the
   existing `.gitmodules`, does not add modules):

   ```bash
   python3 .claude/skills/repo-setup/scripts/repo_setup.py --apply
   ```

6. **Prepare a review slot**, then reset it when done. **`review-setup` is
   destructive** — a setup permanently deletes the target slot's staged,
   unstaged, untracked, and ignored content (no archive, no reuse). Always
   discard-reset the exact slot first:

   ```bash
   python3 .claude/skills/review-setup/scripts/review_reset.py --slot Nova-0 --mode discard --confirm YES
   python3 .claude/skills/review-setup/scripts/review_setup.py <PR_ID> --repo Nova --slot 0
   ```

   Read `.claude/skills/review-setup/SKILL.md` in full before running setup.

## Host-specific — read before copying `CLAUDE.md.example`

`CLAUDE.md.example` is a **verbatim copy of a real review host's `CLAUDE.md`**.
It names specific repos (`Nova, NovaClient, Astra, WeveNova, MODISService`) and
hard-codes that host's Bluebird MCP bridge route and absolute `/workspace/...`
skill paths. Treat it as a worked example, not a drop-in default: adjust the
repo list, the Bluebird route, and the paths to your own host before using it.

The reviewer-onboarding docs and the host's `.gitmodules` were **deliberately
not** included — they are repo-specific, not portable tooling.
