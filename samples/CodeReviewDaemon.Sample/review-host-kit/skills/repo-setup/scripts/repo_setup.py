#!/usr/bin/env python3
"""Plan/apply detached root search checkouts without moving review baselines or slots."""
from __future__ import annotations

import argparse
from contextlib import nullcontext
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "review-setup" / "scripts"))
from review_common import (
    CleanupNotProvenError, ReviewError, ReviewState, SourcePool, assert_no_git_operation,
    check_acquisition_ready, check_outer_merge_ready, current_ref, discover_root,
    fetch_session, git_ignored_paths, git_status, lock_session, parse_ado_url, read_submodules,
    run_git, run_git_read, safe_path, validate_sha, worktree_rows,
    ensure_relative_worktrees, validate_relative_worktree,
)


def git(path, *args, check=True):
    return run_git_read(path, list(args), check=check).stdout.strip()


def require(condition, message):
    if not condition:
        raise ReviewError(message)


def config(root, key, *, module_file=False):
    args = ["config", *( ["--file", ".gitmodules"] if module_file else ["--local"] ), "--get-all", key]
    result = run_git_read(root, args, check=False)
    require(result.returncode in (0, 1), "Cannot read submodule branch configuration.")
    values = result.stdout.splitlines()
    require(len(values) <= 1, "Ambiguous submodule branch configuration.")
    return values[0] if values else None


def selected_branch(root, module):
    key = f"submodule.{module['name']}.branch"
    branch = config(root, key)
    source = "local-config"
    if branch is None:
        branch = config(root, key, module_file=True)
        source = "gitmodules"
    if branch == ".":
        branch = current_ref(root, read_only=True)
        require(branch is not None, "branch=. requires an attached outer branch.")
        source += ":outer-branch"
    if branch is None:
        advertised = git(root, "ls-remote", "--symref", "--", module["url"], "HEAD")
        branches = [line[len("ref: refs/heads/"):-len("\tHEAD")]
                    for line in advertised.splitlines()
                    if line.startswith("ref: refs/heads/") and line.endswith("\tHEAD")]
        require(len(branches) == 1, "Remote default branch is not unambiguous.")
        branch, source = branches[0], "remote-head"
    require(bool(branch) and not branch.startswith("-"), "Invalid selected branch.")
    require(run_git_read(root, ["check-ref-format", f"refs/heads/{branch}"], check=False).returncode == 0,
            "Invalid selected branch.")
    return branch, source


def validate_outer(root, modules):
    """Only unstaged declared gitlink changes are allowed in the outer repository."""
    require(not git(root, "diff", "--cached", "--ignore-submodules=none", "--name-only"), "Outer index is not clean.")
    declared = {m["path"] for m in modules}
    for row in git(root, "diff", "--raw", "--no-abbrev", "--no-renames", "--ignore-submodules=none").splitlines():
        metadata, path = row.split("\t", 1)
        fields = metadata.split()
        require(fields[0] == ":160000" and fields[1] == "160000" and fields[4] == "M" and path in declared,
                "Outer ordinary tracked files are not clean.")
    for module in modules:
        entry = git(root, "ls-files", "--stage", "--", module["path"])
        require(entry.startswith("160000 ") and entry.endswith("\t" + module["path"]) and " 0\t" in entry,
                "Declared module is not an unambiguous indexed gitlink.")


def inspect_checkout(root, pool, *, require_relative=True):
    path = safe_path(root, pool.module["path"])
    require(not path.is_symlink(), "Root module path is a symlink.")
    store = pool.store
    for part in (store, *store.parents):
        require(not part.is_symlink(), "Source store has a symlink ancestor.")
        if part == root:
            break
    if store.exists():
        require(git(store, "rev-parse", "--is-bare-repository") == "true", "Source store is not bare.")
        require(git(store, "rev-parse", "--is-shallow-repository") == "false", "Source store is shallow; prime separately.")
        require(parse_ado_url(git(store, "config", "--get", "remote.origin.url")) == parse_ado_url(pool.module["url"]),
                "Source store origin differs from .gitmodules.")
        check_acquisition_ready(store)
        assert_no_git_operation(store, read_only=True)
    if not path.exists():
        return None
    require(path.is_dir(), "Root module path is not a directory.")
    if not (path / ".git").exists():
        require(not any(path.iterdir()), "Root module contains unmanaged content.")
        return None
    require((path / ".git").is_file() and not (path / ".git").is_symlink(), "Root module is not a linked worktree.")
    require(store.exists() and Path(git(path, "rev-parse", "--path-format=absolute", "--git-common-dir")).resolve() == store.resolve(),
            "Root module uses a foreign source store.")
    validate_relative_worktree(root, path, store, require_relative=require_relative)
    require(path.resolve() in {r["worktree"] for r in worktree_rows(store, read_only=True)}, "Root module is not registered.")
    require(current_ref(path, read_only=True) is None, "Root module is attached to a local branch.")
    assert_no_git_operation(path, read_only=True)
    require(not git_status(path, read_only=True) and not git_ignored_paths(path, read_only=True),
            "Root module has tracked, staged, untracked or ignored work.")
    head = validate_sha(git(path, "rev-parse", "HEAD"))
    owned = run_git_read(store, ["rev-parse", "--verify", root_ref(pool)], check=False)
    require(owned.returncode == 0 and run_git_read(store, ["merge-base", "--is-ancestor", head, validate_sha(owned.stdout.strip())], check=False).returncode == 0,
            "Root module contains unowned commits; refusing adoption.")
    return head


def root_ref(pool):
    return f"refs/repo-setup/roots/{pool.repo_key}"


def synchronize(root: Path, *, apply=False, timeout=3600, diagnostic=None):
    diagnostic = diagnostic if diagnostic is not None else {}
    diagnostic.update(stage="preflight", repository=None)
    root = discover_root(str(root))
    state = ReviewState(root, read_only=True)
    # An exclusive existing lifecycle barrier keeps review writers out. Plan never creates lock/state files.
    with fetch_session(timeout), lock_session(timeout), (state.lock(phase="repo-setup") if apply else nullcontext()):
        require(state.common == root / ".git", "Run repo-setup only at the canonical outer root.")
        check_outer_merge_ready(state)
        assert_no_git_operation(root, read_only=True)
        modules = read_submodules(root, read_only=True)
        require(len({m['path'] for m in modules}) == len(modules), "Duplicate module paths.")
        pools = [SourcePool(state, m) for m in modules]
        require(len({p.repo_key for p in pools}) == len(pools), "Module names collide in the source store.")
        validate_outer(root, modules)
        rows = []
        # Only this read-only preflight permits absolute but correctly resolving links.
        heads = [inspect_checkout(root, pool, require_relative=False) for pool in pools]
        if apply:
            ensure_relative_worktrees(state, pools)
            for pool in pools:
                inspect_checkout(root, pool)
        # Resolve remote branches only after all local ownership/capability checks.
        for pool, head in zip(pools, heads):
            diagnostic.update(stage="preflight", repository=pool.repo_key)
            branch, provenance = selected_branch(root, pool.module)
            rows.append({"repository": pool.repo_name, "path": pool.module["path"], "branch": branch,
                         "branchSource": provenance, "previousSha": head, "targetSha": None,
                         "store": pool.store.relative_to(root).as_posix(), "status": "planned"})
        if apply:
            for pool, row in zip(pools, rows):
                diagnostic.update(stage="fetch", repository=pool.repo_key)
                print(f"repo-setup: fetching {pool.repo_name}", file=sys.stderr, flush=True)
                pool._ensure_store()
                # Full normal fetch: no depth/unshallow options, no cached review-baseline publication.
                with pool.acquisition_lock(phase="root-fetch"), pool.store_lock(phase="root-fetch"):
                    check_acquisition_ready(pool.store)
                    ref = root_ref(pool)
                    run_git(pool.store, ["-c", "gc.auto=0", "-c", "maintenance.auto=false", "-c", "core.hooksPath=/dev/null",
                                         "fetch", "--no-tags", "--no-recurse-submodules", "--", pool.module["url"],
                                         f"refs/heads/{row['branch']}"] , timeout=timeout)
                    target = validate_sha(git(pool.store, "rev-parse", "FETCH_HEAD^{commit}"))
                    old = inspect_checkout(root, pool)
                    require(old == row["previousSha"], "Root checkout changed after preflight.")
                    if old is not None:
                        require(run_git_read(pool.store, ["merge-base", "--is-ancestor", old, target], check=False).returncode == 0,
                                "Selected branch would abandon root commits; refusing non-fast-forward update.")
                    diagnostic["stage"] = "checkout"
                    run_git(pool.store, ["update-ref", ref, target])
                    path = root / pool.module["path"]
                    if old is None:
                        run_git(pool.store, ["-c", "core.hooksPath=/dev/null", "worktree", "add", "--relative-paths", "--detach", str(path), target], timeout=timeout)
                    elif old != target:
                        run_git(path, ["-c", "core.hooksPath=/dev/null", "-c", "submodule.recurse=false", "checkout", "--detach", target], timeout=timeout)
                    require(inspect_checkout(root, pool) == target, "Root checkout verification failed.")
                    row.update(targetSha=target, status="ready")
        return {"schema": 1, "mode": "apply" if apply else "plan", "complete": apply,
                "moduleCount": len(rows), "repositories": rows}


def inventory(root):
    root = discover_root(str(root))
    modules = read_submodules(root, read_only=True)
    return {"schema": 1, "repositories": [
        {"repository": module["path"].split("/")[1], "path": module["path"]}
        for module in modules
    ]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("plan", "apply", "inventory"))
    parser.add_argument("--root", default=".")
    parser.add_argument("--timeout", type=int, default=3600)
    args = parser.parse_args()
    diagnostic = {"stage": "inventory" if args.command == "inventory" else "preflight", "repository": None}
    try:
        require(args.timeout > 0, "Timeout must be positive.")
        result = inventory(Path(args.root)) if args.command == "inventory" else synchronize(
            Path(args.root), apply=args.command == "apply", timeout=args.timeout, diagnostic=diagnostic)
        print(json.dumps(result))
        return 0
    except CleanupNotProvenError:
        print(json.dumps({"schema": 1, "complete": False, "outcome": "unknown", "error": "child-cleanup-not-proven", **diagnostic}))
        return 75
    except (ReviewError, OSError, ValueError):
        # URLs, Git stderr and credential-bearing exception text never enter the public result.
        print(json.dumps({"schema": 1, "complete": False, "error": "repo-setup refused or failed; preserve state and inspect preconditions", **diagnostic}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
