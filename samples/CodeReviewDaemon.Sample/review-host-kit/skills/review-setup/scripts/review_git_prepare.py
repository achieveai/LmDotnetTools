"""Provider-neutral, topology-aware Git preparation for a single review slot.

Fetches an already-resolved pull request's merge commit into a shared
per-repository bare source store, then destructively resets one numbered
slot's outer meta-repo worktree and nested source worktree to that commit --
discarding all prior dirt (staged/unstaged/untracked/ignored content and
stale Git operation markers) -- and records a minimal, atomically-written
manifest at ``.git/review-setup/prepared-slots/{repo-key}-{slot}.json``.

Ruling R5 (provider neutrality): this module, and everything ``prepare()``
calls, is loaded without ever importing ``review_ado`` or any other
provider module. The direct CLI at the bottom of this file is the only
place that ever touches a provider, and it does so lazily, inside function
bodies only, so importing this module -- or calling ``prepare()`` -- never
pulls in a provider.

Fetch-auth fallback (applied only through the child environment, never
argv, never logged): (1) caller-supplied ``GitTransportAuth`` if given;
(2) a per-command-scoped Windows Credential Manager credential helper (never
global Git config) if a ``git-credential-manager.exe`` is found; (3) the
ambient environment only. ``git.exe`` is never used for `prepare()`'s own
local repository operations -- those are always Linux ``git -C`` into a
WSL-native path, which ``git.exe`` cannot practically address. ``git.exe``
is only used by the standalone, read-only ``probe_git_exe_ls_remote()``
reachability check below, which is never part of ``prepare()``'s internal
fetch logic.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Mapping

from review_common import (
    GitTransportAuth,
    PrepareResult,
    ResolvedPullRequest,
    ReviewError,
    ReviewState,
    SourcePool,
    assert_no_git_operation,
    atomic_json,
    canonical_repo_name,
    current_ref,
    discover_root,
    git_ignored_paths,
    git_status,
    lock_session,
    preflight,
    read_submodules,
    run_git,
    run_git_read,
    safe_path,
    source_lock,
    utc_now,
    validate_sha,
    worktree_rows,
)

# Mirrors `review_common.assert_no_git_operation`'s literal marker list --
# needed here (unlike that read-only assertion) to actually clear markers
# from the nested target, which is always about to be fully discarded and
# rebuilt under the slot lock. Kept in sync by hand; if this drifts from
# `review_common`'s list, `assert_no_git_operation`'s own postcondition
# check below still fails closed rather than silently passing.
_OPERATION_MARKERS = (
    "MERGE_HEAD",
    "CHERRY_PICK_HEAD",
    "REVERT_HEAD",
    "rebase-merge",
    "rebase-apply",
    "index.lock",
)


# ---------------------------------------------------------------------------
# Fetch-auth fallback chain
# ---------------------------------------------------------------------------

_GCM_CANDIDATES = (
    "/mnt/c/Program Files/Git/mingw64/bin/git-credential-manager.exe",
    "/mnt/c/Program Files (x86)/Git/mingw64/bin/git-credential-manager.exe",
)

# Deliberately restrictive: this path is embedded, unescaped, into the
# `!"<path>"` shell-eval credential-helper string below. A double quote
# breaks out of that string early; `$`, backticks, and control characters
# are still interpreted by `/bin/sh -c` even *inside* double quotes. The
# hardcoded candidates above, and any real Windows/WSL install path, only
# ever need letters, digits, spaces, and `. _ / : ( ) -`; anything else is
# rejected rather than risk command injection via `REVIEW_GIT_GCM_PATH`.
_GCM_PATH_RE = re.compile(r"^[A-Za-z0-9 ._/:()\-]+$")


def _validate_gcm_path(path: str) -> str:
    if not path or not _GCM_PATH_RE.match(path):
        raise ReviewError(
            "Refusing git-credential-manager.exe path with characters outside "
            "the allowed safe set (letters, digits, spaces, . _ / : ( ) -)."
        )
    return path


def _find_gcm_binary() -> str | None:
    override = os.environ.get("REVIEW_GIT_GCM_PATH")
    candidates = (override,) if override else _GCM_CANDIDATES
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return _validate_gcm_path(candidate)
    return None


def _append_git_config(base_env: Mapping[str, str], entries: list[tuple[str, str]]) -> dict[str, str]:
    """Append Git config entries via `GIT_CONFIG_KEY_N`/`GIT_CONFIG_VALUE_N`,
    continuing from any `GIT_CONFIG_COUNT` already present in `base_env` --
    never overwriting or dropping entries the caller already carries (the
    same composability contract as `GitTransportAuth.apply()`)."""
    result = dict(base_env)
    count = int(result.get("GIT_CONFIG_COUNT", "0"))
    for key, value in entries:
        result[f"GIT_CONFIG_KEY_{count}"] = key
        result[f"GIT_CONFIG_VALUE_{count}"] = value
        count += 1
    result["GIT_CONFIG_COUNT"] = str(count)
    return result


def _fetch_env(resolved: ResolvedPullRequest, auth: GitTransportAuth | None) -> dict[str, str]:
    if auth is not None:
        return auth._bound_to(resolved.remote_url).apply(os.environ)
    gcm = _find_gcm_binary()
    if gcm is not None:
        # Per-command-scoped only (this dict becomes one child process's
        # environment) -- never written to global or repository Git config.
        # The shell-eval `!"<path>"` form lets `/bin/sh -c` parse the space
        # in "Program Files" correctly; `credential.useHttpPath=true` is
        # required for ADO's `dev.azure.com` URLs, which otherwise cannot
        # resolve an organization name from the URL path.
        return _append_git_config(
            os.environ,
            [
                ("credential.helper", ""),
                ("credential.helper", f'!"{gcm}"'),
                ("credential.useHttpPath", "true"),
            ],
        )
    return dict(os.environ)


def probe_git_exe_ls_remote(remote_url: str, ref: str, *, timeout: float = 20) -> str:
    """Standalone, read-only reachability proof using Windows `git.exe`
    directly. Never part of `prepare()`'s own fetch (which always uses
    Linux `git -C`'d into a WSL-native path -- `git.exe` cannot practically
    operate on those paths). Prints and persists nothing; returns the
    resolved commit SHA for the caller to display or discard."""
    try:
        result = subprocess.run(
            ["git.exe", "ls-remote", remote_url, ref],
            capture_output=True, text=True, timeout=timeout,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ReviewError(f"git.exe ls-remote invocation failed: {exc}") from None
    if result.returncode or not result.stdout.strip():
        raise ReviewError("git.exe ls-remote did not resolve the requested ref.")
    return validate_sha(result.stdout.split()[0])


# ---------------------------------------------------------------------------
# Module / pool resolution
# ---------------------------------------------------------------------------

def _resolve_module(root: Path, repo: str, *, read_only: bool) -> dict:
    modules = read_submodules(root, read_only=read_only)
    matches = [m for m in modules if canonical_repo_name(m).casefold() == repo.casefold()]
    if len(matches) != 1:
        raise ReviewError(
            f"Could not find exactly one configured source submodule named '{repo}'."
        )
    return matches[0]


def _ensure_bare_store(store: Path) -> None:
    """Minimal, journal-free bare-store existence check/init -- deliberately
    not `SourcePool._ensure_store_locked()`, which pulls in origin-binding
    assertions this fetch-by-explicit-URL design does not need. Must be
    called while holding `source_lock(store)` exclusively."""
    if store.is_symlink():
        raise ReviewError("Refusing symlink source store.")
    if store.exists():
        result = run_git(store, ["rev-parse", "--is-bare-repository"], check=False)
        if result.returncode or result.stdout.strip() != "true":
            raise ReviewError("Existing source-store path is not a compatible bare repository.")
        return
    parent = store.parent
    if parent.is_symlink():
        raise ReviewError("Refusing symlink source-store parent directory.")
    parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    run_git(parent, ["init", "--bare", "-q", str(store)])


# ---------------------------------------------------------------------------
# Worktree registration helpers
# ---------------------------------------------------------------------------

def _find_worktree_row(rows: list[dict], path: Path) -> dict | None:
    target = path.resolve()
    for row in rows:
        if Path(row["worktree"]).resolve() == target:
            return row
    return None


def _worktree_functional(path: Path, common: Path) -> bool:
    """True only if `path` is itself the root of a linked worktree backed by
    `common` -- not merely some directory *inside* one.

    `git -C <path> rev-parse --is-inside-work-tree` is trivially true for
    ANY subdirectory of ANY repository, including a plain, un-checked-out
    gitlink placeholder directory that another step (e.g. the outer
    meta-repo's own `reset --hard`, which recreates `repos/<Repo>` as an
    empty placeholder whenever that path is tracked as a gitlink and
    currently missing) can silently recreate before this check ever runs.
    Without the `--git-common-dir` comparison below, such a placeholder
    would be misreported as a valid, already-prepared nested worktree --
    skipping repair and then failing later when `merge_sha` (which only
    ever lives in `common`, e.g. the shared source store) cannot be read
    from the wrong repository's object database.
    """
    if not path.is_dir() or path.is_symlink():
        return False
    if not (path / ".git").exists():
        return False
    result = run_git_read(path, ["rev-parse", "--is-inside-work-tree"], check=False)
    if result.returncode != 0 or result.stdout.strip() != "true":
        return False
    common_result = run_git_read(path, ["rev-parse", "--git-common-dir"], check=False)
    if common_result.returncode != 0:
        return False
    resolved_common = Path(common_result.stdout.strip())
    if not resolved_common.is_absolute():
        resolved_common = (path / resolved_common).resolve()
    else:
        resolved_common = resolved_common.resolve()
    return resolved_common == common.resolve()


def _validate_contained(root: Path, path: Path) -> None:
    if path.is_symlink():
        raise ReviewError("Refusing to remove a symlinked managed path.")
    if not path.resolve().is_relative_to(root.resolve()):
        raise ReviewError("Refusing to remove a managed path outside its recorded root.")


def _clear_operation_markers(repo: Path) -> None:
    """Clear stale Git operation markers from a worktree that is entirely
    owned under the slot lock and about to be fully discarded/rebuilt.
    Plain `reset --hard`/`clean -ffdx` do not remove these on their own.
    Never applied to the outer meta-repo -- see `_prepare_outer`."""
    for name in _OPERATION_MARKERS:
        resolved = run_git_read(repo, ["rev-parse", "--git-path", name], check=False)
        if resolved.returncode:
            continue
        path = Path(resolved.stdout.strip())
        if not path.is_absolute():
            path = repo / path
        if path.is_symlink():
            raise ReviewError("Refusing symlink Git operation marker path.")
        if path.is_dir():
            shutil.rmtree(path)
        elif path.exists():
            path.unlink()


# ---------------------------------------------------------------------------
# Step 3a: outer meta-repo
# ---------------------------------------------------------------------------

def _prepare_outer(state: ReviewState, outer: Path, root_head: str) -> None:
    rows = worktree_rows(state.root, read_only=state.read_only)
    row = _find_worktree_row(rows, outer)
    # `_worktree_functional` compares `outer`'s own `--git-common-dir` against
    # the repository it is expected to be a linked worktree *of*. For the
    # outer meta-repo that repository is `state.root`'s own admin/common
    # directory (`state.common`), not the worktree path `state.root` itself
    # -- those are never equal for a normal (non-bare) checkout, so passing
    # `state.root` here made this check fail unconditionally, silently
    # forcing every single `prepare()` call through the expensive
    # tear-down-and-recreate repair path even when the outer was already
    # healthy. Fixed and proven via Scenario F in the fix-round-1 report.
    valid = row is not None and _worktree_functional(outer, state.common)
    if not valid:
        with state.outer_lock(phase="outer-repair"):
            _repair_outer_registration(state, outer, root_head)
        return
    # A safely-identifiable stale operation on the outer is aborted rather
    # than auto-cleared: unlike the nested target (exclusively owned and
    # about to be discarded), the outer worktree's markers cannot be proven
    # stale here without process-census machinery this task deliberately
    # excludes. Fail closed instead.
    assert_no_git_operation(outer)
    # `reset --hard <root_head>` both discards any dirt (staged/unstaged)
    # and moves HEAD to the baseline in one step, regardless of how dirty
    # the working tree currently is -- unlike `checkout --detach <sha>`,
    # which refuses when local changes would be overwritten. If HEAD is
    # attached to a branch (never expected from this worktree's own
    # lifecycle, but checked defensively), detach it first so the reset
    # cannot instead move a branch pointer.
    if current_ref(outer, read_only=state.read_only) is not None:
        run_git(outer, ["checkout", "--detach", "-q", "HEAD"])
    run_git(outer, ["reset", "--hard", root_head])
    run_git(outer, ["clean", "-ffdx", "-e", "/repos"])


def _repair_outer_registration(state: ReviewState, outer: Path, root_head: str) -> None:
    rows = worktree_rows(state.root, read_only=state.read_only)
    if _find_worktree_row(rows, outer) is not None:
        run_git(state.root, ["worktree", "remove", "--force", str(outer)], check=False)
    run_git(state.root, ["worktree", "prune"], check=False)
    if outer.exists():
        _validate_contained(state.root, outer)
        shutil.rmtree(outer)
    # A foreign, non-worktree Git repo sitting at `outer` fails `worktree
    # remove` outright ("is not a .git file") *and* survives the prune
    # above -- prune only drops an admin entry once its gitdir pointer
    # resolves to a missing location, and the path still existed (just as
    # the wrong thing) when that first prune ran. Once the manual `rmtree`
    # above has actually removed it, the pointer now IS missing, so a
    # second prune is required or the admin entry lingers and the `add`
    # below fails with "is a missing but already registered worktree".
    run_git(state.root, ["worktree", "prune"], check=False)
    outer.parent.mkdir(parents=True, exist_ok=True)
    run_git(state.root, ["worktree", "add", "--detach", "-q", str(outer), root_head])


# ---------------------------------------------------------------------------
# Step 3b: nested target worktree
# ---------------------------------------------------------------------------

def _prepare_nested(state: ReviewState, pool: SourcePool, nested: Path, merge_sha: str) -> None:
    rows = worktree_rows(pool.store, read_only=state.read_only)
    row = _find_worktree_row(rows, nested)
    valid = row is not None and _worktree_functional(nested, pool.store)
    if not valid:
        with state.outer_lock(phase="nested-repair"):
            _repair_nested_registration(state, pool, nested, merge_sha)
        # `_repair_nested_registration` always ends with a fresh
        # `worktree add --detach <merge_sha>` into a directory it just
        # confirmed does not exist -- that checkout is already exactly at
        # `merge_sha`, clean, and detached, so repeating the
        # discard-and-checkout sequence below would be redundant no-op work
        # on every repair. `_verify_postconditions` (called unconditionally
        # by `prepare()` after this function returns) still fully re-checks
        # this worktree, so skipping the redundant steps here does not
        # weaken final verification.
        return
    _clear_operation_markers(nested)
    run_git(nested, ["reset", "--hard"])
    run_git(nested, ["clean", "-ffdx"])
    run_git(nested, ["checkout", "--detach", "-q", merge_sha])
    run_git(nested, ["reset", "--hard", merge_sha])
    run_git(nested, ["clean", "-ffdx"])


def _repair_nested_registration(state: ReviewState, pool: SourcePool, nested: Path, merge_sha: str) -> None:
    rows = worktree_rows(pool.store, read_only=state.read_only)
    if _find_worktree_row(rows, nested) is not None:
        run_git(pool.store, ["worktree", "remove", "--force", str(nested)], check=False)
    run_git(pool.store, ["worktree", "prune"], check=False)
    if nested.exists():
        _validate_contained(state.root, nested)
        shutil.rmtree(nested)
    # See the matching second prune in `_repair_outer_registration`: a
    # foreign repo at `nested` survives the first prune above (its path
    # still existed then) and, once removed by the `rmtree` above, leaves a
    # stale "missing but already registered worktree" admin entry that the
    # `add` below would otherwise refuse.
    run_git(pool.store, ["worktree", "prune"], check=False)
    nested.parent.mkdir(parents=True, exist_ok=True)
    # `merge_sha` was fetched and validated (Step 2) before this repair
    # ever runs, so it is already a reachable object in `pool.store` --
    # no separate "baseline tip" is needed, and the destructive sequence
    # right after this call re-confirms/re-applies the same target anyway.
    run_git(pool.store, ["worktree", "add", "--detach", "-q", str(nested), merge_sha])


# ---------------------------------------------------------------------------
# Step 4: postconditions
# ---------------------------------------------------------------------------

def _verify_postconditions(
    state: ReviewState,
    pool: SourcePool,
    outer: Path,
    nested: Path,
    root_head: str,
    merge_sha: str,
) -> None:
    if run_git_read(state.root, ["rev-parse", "HEAD"]).stdout.strip() != root_head:
        raise ReviewError("Canonical root HEAD moved during preparation; refusing to publish a manifest.")

    if run_git_read(outer, ["rev-parse", "HEAD"]).stdout.strip() != root_head:
        raise ReviewError("Outer worktree HEAD is not at the expected baseline.")
    if current_ref(outer, read_only=True) is not None:
        raise ReviewError("Outer worktree must remain detached.")
    module_path = pool.module["path"]
    unexpected = [
        entry
        for entry in git_status(outer, read_only=True)
        if entry[1] != module_path and not entry[1].startswith(module_path + "/")
    ]
    if unexpected:
        raise ReviewError("Outer worktree is not clean outside its managed source module.")
    if git_ignored_paths(outer, protected=module_path, read_only=True):
        raise ReviewError("Outer worktree has unexpected ignored content.")
    assert_no_git_operation(outer, read_only=True)

    common = run_git_read(nested, ["rev-parse", "--git-common-dir"]).stdout.strip()
    common_path = Path(common)
    common_path = common_path.resolve() if common_path.is_absolute() else (nested / common_path).resolve()
    if common_path != pool.store.resolve():
        raise ReviewError("Target worktree's common directory does not match the shared source store.")
    if validate_sha(run_git_read(nested, ["rev-parse", "HEAD"]).stdout.strip()) != merge_sha:
        raise ReviewError("Target worktree HEAD does not match the fetched merge commit.")
    if current_ref(nested, read_only=True) is not None:
        raise ReviewError("Target worktree must remain detached.")
    if git_status(nested, read_only=True):
        raise ReviewError("Target worktree is not clean.")
    if git_ignored_paths(nested, read_only=True):
        raise ReviewError("Target worktree has unexpected ignored content.")
    assert_no_git_operation(nested, read_only=True)


# ---------------------------------------------------------------------------
# prepare()
# ---------------------------------------------------------------------------

def prepare(
    *,
    root: Path,
    resolved: ResolvedPullRequest,
    slot: int,
    auth: GitTransportAuth | None,
    lock_timeout: float = 0,
) -> PrepareResult:
    state = ReviewState(root)
    with lock_session(lock_timeout):
        # `prepare()` is a pooled caller of the compatibility barrier -- like
        # every other pooled/slot call site in this codebase (review_setup.py
        # "pooled-setup"/"slot-recovery", review_reset.py "pooled-reset",
        # merge_review.py "merge-review", and every test in
        # tests/test_concurrency.py that proves "different slots overlap,
        # same slot excludes") -- it must hold this barrier SHARED, not
        # exclusive. `state.slot_lock()` right below is what actually
        # enforces per-slot exclusion; an exclusive compatibility hold here
        # would instead serialize every slot's prepare() against every other
        # slot on the same repo, which manual six-process validation proved
        # was happening before this fix (Task 4).
        with state.lock(shared=True, phase="prepare"):
            module = _resolve_module(state.root, resolved.repo, read_only=state.read_only)
            pool = SourcePool(state, module)
            repo_key = pool.repo_key
            with state.slot_lock(repo_key, slot, phase="prepare"):
                outer = safe_path(state.root, pool.slot_relative_path(slot))
                nested = safe_path(state.root, f"{pool.slot_relative_path(slot)}/{pool.module['path']}")
                root_head = validate_sha(run_git_read(state.root, ["rev-parse", "HEAD"]).stdout.strip())

                # Step 2: fetch-before-destruction, inside the source-fetch lock.
                tmp_ref = f"refs/review-setup/prepare/{repo_key}-{slot}"
                with source_lock(pool.store, phase="prepare-fetch"):
                    _ensure_bare_store(pool.store)
                    env = _fetch_env(resolved, auth)
                    run_git(
                        pool.store,
                        ["fetch", "--no-tags", resolved.remote_url, f"+{resolved.merge_ref}:{tmp_ref}"],
                        extra_env=env,
                    )
                    merge_sha = validate_sha(
                        run_git_read(pool.store, ["rev-parse", f"{tmp_ref}^{{commit}}"]).stdout.strip()
                    )

                # Step 3: topology-aware destructive cleanup + checkout.
                _prepare_outer(state, outer, root_head)
                _prepare_nested(state, pool, nested, merge_sha)

                # Step 4: postconditions + minimal manifest.
                _verify_postconditions(state, pool, outer, nested, root_head, merge_sha)
                prepared_at = utc_now()
                manifest = {
                    "schema": 1,
                    "repo": pool.repo_name,
                    "slot": slot,
                    "pr": resolved.pr,
                    "merge_sha": merge_sha,
                    "prepared_at": prepared_at,
                }
                atomic_json(state.prepared_slot_path(repo_key, slot), manifest)
                return PrepareResult(
                    repo=pool.repo_name,
                    slot=slot,
                    pr=resolved.pr,
                    merge_sha=merge_sha,
                    worktree=nested,
                    prepared_at=prepared_at,
                )


# ---------------------------------------------------------------------------
# Direct CLI (lazy provider import only)
# ---------------------------------------------------------------------------

def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pr", type=int)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--slot", type=int, required=True)
    parser.add_argument("--root")
    parser.add_argument("--org")
    parser.add_argument("--transport", choices=("auto", "gateway", "cli"), default="auto")
    parser.add_argument("--lock-timeout", type=float, default=0)
    parser.add_argument(
        "--probe-git-exe",
        action="store_true",
        help=(
            "Read-only: resolve the PR, then prove git.exe can reach its merge ref via "
            "ls-remote, and exit without calling prepare(). Never used by prepare()'s own fetch."
        ),
    )
    return parser


def _run(args: argparse.Namespace) -> int:
    # Lazy, function-body-only provider import (ruling R5): importing this
    # module, or calling `prepare()` directly, never imports `review_ado`.
    import review_ado

    root = discover_root(args.root)
    org_url = review_ado._resolve_org_url(args.org)
    transport = None if args.transport == "auto" else args.transport
    provider = review_ado.AdoProvider(org_url, transport=transport)
    print(f"Resolving PR {args.pr}...", file=sys.stderr)
    resolved = provider.resolve(args.pr, repo_hint=args.repo)
    if args.probe_git_exe:
        sha = probe_git_exe_ls_remote(resolved.remote_url, resolved.merge_ref)
        print(f"git.exe reached {resolved.remote_url} {resolved.merge_ref} -> {sha}", file=sys.stderr)
        return 0
    auth = provider.git_auth()
    result = prepare(root=root, resolved=resolved, slot=args.slot, auth=auth, lock_timeout=args.lock_timeout)
    print(json.dumps(
        {
            "repo": result.repo,
            "slot": result.slot,
            "pr": result.pr,
            "merge_sha": result.merge_sha,
            "worktree": str(result.worktree),
            "prepared_at": result.prepared_at,
        },
        indent=2,
        sort_keys=True,
    ))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    try:
        preflight()
        return _run(args)
    except ReviewError as exc:
        print(f"Git prepare stopped: {exc}", file=sys.stderr)
        return exc.code


if __name__ == "__main__":
    raise SystemExit(main())
