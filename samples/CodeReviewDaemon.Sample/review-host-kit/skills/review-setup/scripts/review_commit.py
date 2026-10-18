#!/usr/bin/env python3
"""Retain every selected-slot review artifact locally, then publish the review branch.

Artifacts are committed to the local ``review/{repo}-{pr}`` branch exactly as before, and
the branch is then pushed to the parent repository's own HTTPS ``origin`` -- our review/
artifact repository. ``create_or_update_parent_remote_ref`` refuses any Azure DevOps origin,
so this never touches a customer source remote. The push runs inside the slot-writer scope
the commit already holds and is idempotent: a re-review fast-forwards the existing remote
branch; an unchanged re-run whose remote tip already matches is a no-op. Never resets.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

from review_common import (
    ReviewError, ReviewState, SourcePool, assert_no_git_operation,
    create_or_update_parent_remote_ref, current_ref, discover_root, file_hash,
    lock_session, read_parent_remote_ref, repo_slug, require_commit_identity,
    run_git, run_git_read, safe_path, slot_writer_scope,
)
from review_pool_admin import select_module_by_name


def observe(repo: Path, *args: str) -> str:
    command = list(args)
    if command and command[0] == "diff":
        command.insert(1, "--no-ext-diff")
    return run_git_read(repo, command).stdout


def changed_paths(repo: Path) -> set[str]:
    paths = set()
    for args in (("diff", "--name-only", "-z"),
                 ("ls-files", "--others", "--exclude-standard", "-z")):
        paths.update(filter(None, observe(repo, *args).split("\0")))
    return paths


def source_fingerprint(source: Path) -> dict:
    """Read-only evidence: hooks are detected after execution, not sandboxed."""
    index = Path(observe(source, "rev-parse", "--path-format=absolute", "--git-path", "index").strip())
    paths = changed_paths(source)
    files = {}
    for relative in sorted(paths):
        path = safe_path(source, relative)
        if path.is_file():
            files[relative] = file_hash(path)
        elif not path.exists():
            files[relative] = None
        else:
            raise ReviewError("Source fingerprint requires regular files; inspect manually.")
    return {
        "head": observe(source, "rev-parse", "HEAD").strip(),
        "ref": current_ref(source, read_only=True),
        "refs": observe(source, "for-each-ref", "--format=%(refname) %(objectname)"),
        "index": file_hash(index) if index.is_file() else None,
        "stagedDiff": observe(source, "diff", "--cached", "--binary"),
        "diff": observe(source, "diff", "--binary"),
        "status": observe(source, "status", "--porcelain=v1", "--untracked-files=all"),
        "files": files,
    }


def assert_source_unchanged(source: Path, before: dict) -> None:
    if source_fingerprint(source) != before:
        raise ReviewError("Source changed during artifact capture or commit hook; side effects preserved for inspection.")


def inventory(root: Path, directory: str) -> dict[str, str]:
    folder = safe_path(root, directory)
    result = {}
    for path in folder.rglob("*"):
        relative = path.relative_to(root).as_posix()
        safe_path(root, relative)
        if path.is_file():
            result[relative] = file_hash(path)
        elif not path.is_dir():
            raise ReviewError("Review artifacts must be regular files or directories.")
    return result


def branch_for(outer: Path, pool: SourcePool, pr: int, expected: str | None) -> str:
    branch = current_ref(outer, read_only=True)
    key = f"{repo_slug(pool.repo_name)}-{pr}"
    requested = expected or f"review/{key}"
    if run_git_read(outer, ["check-ref-format", "--branch", requested], check=False).returncode:
        raise ReviewError("Invalid expected review branch.")
    if not (requested == f"review/{key}" or requested.startswith(f"review/{key}-")):
        raise ReviewError("Expected branch must identify this repository and PR.")
    if branch is not None:
        if branch != requested and not (expected is None and
                (branch == f"review/{key}" or branch.startswith(f"review/{key}-"))):
            raise ReviewError("Slot is on a different branch; no files were changed.")
        requested = branch
        base = observe(outer, "rev-parse", "main").strip()
        prior = set(filter(None, observe(outer, "diff", "--name-only", "-z", base, "HEAD").split("\0")))
        if any(not p.startswith(f"PRs/{key}/") and p != pool.module["path"] for p in prior):
            raise ReviewError("Existing review branch contains changes outside this review directory.")
    else:
        if observe(outer, "rev-parse", "HEAD").strip() != observe(outer, "rev-parse", "main").strip():
            raise ReviewError("Detached outer slot is not at local main baseline.")
        if observe(outer, "branch", "--list", requested).strip():
            raise ReviewError("Review branch already exists elsewhere; refusing to switch or overwrite it.")
    return requested


def push_review_branch(outer: Path, branch: str, *, push: bool = True) -> dict | None:
    """Publish the committed review branch to the parent repository's own HTTPS origin.

    The branch physically lives in this sandbox outer checkout, so the publish must run
    here, inside the slot-writer scope ``commit_review`` already holds. Only this top-level
    review repository is ever pushed: ``create_or_update_parent_remote_ref`` operates on
    ``outer`` alone and refuses an Azure DevOps origin, so the nested source submodule is
    never pushed and no customer source remote is touched. The top-level branch carries the
    source only as a gitlink pointer, so ``git push`` sends no submodule objects. Idempotent:
    a first publish creates the ref (``expected_old=None``); a re-review fast-forwards the
    existing remote tip; an unchanged re-run whose remote tip already equals HEAD is a no-op.
    ``push=False`` skips publication entirely and retains the local branch only.
    """
    if not push:
        return {"branch": branch, "status": "local-only"}
    ref = "refs/heads/" + branch
    head = observe(outer, "rev-parse", "HEAD").strip()
    prior = read_parent_remote_ref(outer, ref)
    if prior == head:
        return {"branch": branch, "ref": ref, "pushed": head, "status": "already-current"}
    pushed = create_or_update_parent_remote_ref(
        outer, ref, head, expected_old=prior, writer_scope="slot")
    return {"branch": branch, "ref": ref, "pushed": pushed,
            "status": "created" if prior is None else "updated"}


def commit_review(root: Path, repo: str, pr: int, slot: int, *,
                  source_sha: str | None = None, dry_run: bool = False,
                  expected_branch: str | None = None, lock_timeout: float = 0,
                  push: bool = True) -> dict:
    if pr <= 0:
        raise ReviewError("PR number must be positive.")
    if not isinstance(source_sha, str) or not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ReviewError("An explicit full PR source SHA is required.")
    state = ReviewState(root, read_only=True)
    pool = SourcePool(state, select_module_by_name(root, repo))
    SourcePool._validate_slots([slot])
    outer = safe_path(root, pool.slot_relative_path(slot))
    source = safe_path(outer, pool.module["path"])
    directory = f"PRs/{repo_slug(pool.repo_name)}-{pr}"
    with lock_session(lock_timeout), state.lock(shared=True, phase="review-commit"):
        with state.slot_lock(pool.repo_key, slot, phase="review-commit"):
            with slot_writer_scope(state, pool.repo_key, slot, phase="review-commit"):
                manifest_path = state.prepared_slot_path(pool.repo_key, slot)
                if manifest_path.is_symlink():
                    raise ReviewError("Symlink prepared manifest.")
                manifest = json.loads(manifest_path.read_text())
                if (manifest.get("schema") != 1 or manifest.get("repo") != pool.repo_name
                        or manifest.get("slot") != slot or manifest.get("pr") != pr):
                    raise ReviewError("Prepared slot does not identify the requested PR.")
                merge = manifest.get("merge_sha")
                if not isinstance(merge, str) or not re.fullmatch(r"[0-9a-f]{40}", merge):
                    raise ReviewError("Invalid prepared merge SHA.")
                if observe(source, "rev-parse", "HEAD").strip() != merge or current_ref(source, read_only=True):
                    raise ReviewError("Source must remain detached at its prepared PR merge.")
                for key in ("source_sha", "head_sha"):
                    if manifest.get(key) is not None and manifest[key] != source_sha:
                        raise ReviewError("PR source SHA differs from prepared source metadata.")
                if run_git_read(source, ["cat-file", "-e", source_sha + "^{commit}"], check=False).returncode:
                    raise ReviewError("PR source SHA is not an available source commit.")
                for checkout in (outer, source):
                    assert_no_git_operation(checkout, read_only=True)
                if observe(outer, "diff", "--cached", "--name-only", "-z") or observe(source, "diff", "--cached", "--name-only", "-z"):
                    raise ReviewError("Existing staged changes must be inspected before committing.")
                if Path(observe(outer, "rev-parse", "--path-format=absolute", "--git-common-dir").strip()).resolve() != state.common:
                    raise ReviewError("Outer slot is not owned by this review repository.")
                if Path(observe(source, "rev-parse", "--path-format=absolute", "--git-common-dir").strip()).resolve() != pool.store.resolve():
                    raise ReviewError("Nested source is not owned by its configured pool.")
                source_before = source_fingerprint(source)
                branch = branch_for(outer, pool, pr, expected_branch)
                before = inventory(outer, directory)
                copies: dict[str, bytes] = {}
                deleted = []
                def capture(checkout: Path, relative: str, namespace: str) -> None:
                    path = safe_path(checkout, relative)
                    if any(part.lower() in ("secrets", "oauth-tokens", ".env", "node_modules", "vendor") for part in path.relative_to(checkout).parts):
                        raise ReviewError("Sensitive or dependency path outside review directory; inspect it manually.")
                    destination = f"{directory}/outside/{namespace}/{relative}"
                    if path.exists():
                        if not path.is_file():
                            raise ReviewError("Outside change is not a regular file.")
                        copies[destination] = path.read_bytes()
                    else:
                        deleted.append(f"{namespace}/{relative}")
                for relative in changed_paths(outer):
                    if relative.startswith(directory + "/") or relative == pool.module["path"]:
                        continue
                    if relative.startswith("repos/") or relative.startswith("PRs/"):
                        raise ReviewError("Changes belong to another repository or PR; refusing to mix reviews.")
                    capture(outer, relative, "outer")
                for relative in changed_paths(source):
                    capture(source, relative, "source")
                if copies or deleted or observe(outer, "diff", "--", pool.module["path"]).strip():
                    evidence = {
                        "repo": pool.repo_name, "pr": pr, "slot": slot, "sourceMerge": merge,
                        "sourceSha": source_sha,
                        "sourceDiff": observe(source, "diff", "--binary"),
                        "submoduleDiff": observe(outer, "diff", "main", "--", pool.module["path"]),
                        "deletedOutsideFiles": sorted(deleted),
                        "copiedFiles": {p: hashlib.sha256(data).hexdigest() for p, data in sorted(copies.items())},
                    }
                    data = (json.dumps(evidence, indent=2, sort_keys=True) + "\n").encode()
                    digest = hashlib.sha256(data).hexdigest()[:16]
                    copies[f"{directory}/outside/checkout-{digest}.json"] = data
                for destination, data in copies.items():
                    path = safe_path(outer, destination)
                    if path.exists() and path.read_bytes() != data:
                        raise ReviewError("Outside-file capture conflicts with retained evidence; no overwrite performed.")
                assert_source_unchanged(source, source_before)
                plan = {"repo": pool.repo_name, "pr": pr, "slot": slot, "branch": branch,
                        "sourceSha": source_sha, "reviewDirectory": directory, "files": len(set(before) | set(copies)),
                        "copiedFiles": sorted(copies), "dryRun": dry_run}
                if dry_run:
                    return plan
                require_commit_identity(outer, operation="Review artifact commit")
                if current_ref(outer, read_only=True) is None:
                    run_git(outer, ["switch", "-c", branch])
                for destination, data in copies.items():
                    path = safe_path(outer, destination)
                    if not path.exists():
                        path.parent.mkdir(parents=True, exist_ok=True)
                        with path.open("xb") as stream:
                            stream.write(data)
                after = inventory(outer, directory)
                if any(after.get(path) != digest for path, digest in before.items()):
                    raise ReviewError("Artifacts changed during capture; inspect before retrying.")
                assert_source_unchanged(source, source_before)
                run_git(outer, ["add", "--all", "--force", "--sparse", "--", directory])
                run_git(outer, ["update-index", "--add", "--cacheinfo",
                                "160000," + source_sha + "," + pool.module["path"]])
                staged = set(filter(None, observe(outer, "diff", "--cached", "--name-only", "-z").split("\0")))
                if any(not p.startswith(directory + "/") and p != pool.module["path"] for p in staged):
                    raise ReviewError("Staged paths escaped the review directory; no commit made.")
                commit_start = observe(outer, "rev-parse", "HEAD").strip()
                if staged:
                    message = f"Retain {pool.repo_name} PR {pr} review artifacts"
                    try:
                        run_git(outer, ["commit", "-m", message])
                    finally:
                        assert_source_unchanged(source, source_before)
                if (observe(outer, "rev-parse", "HEAD:" + pool.module["path"]).strip() != source_sha
                        or observe(outer, "ls-files", "--stage", "--", pool.module["path"]).strip()
                        != "160000 " + source_sha + " 0\t" + pool.module["path"]):
                    raise ReviewError("Commit/index gitlink differs from the explicit PR source SHA; inspect hook outcome.")
                committed_paths = set(filter(None, observe(outer, "diff", "--name-only", "-z", commit_start, "HEAD").split("\0")))
                if any(not p.startswith(directory + "/") and p != pool.module["path"] for p in committed_paths):
                    raise ReviewError("Commit contains paths outside the review directory; preserved for inspection.")
                if current_ref(outer, read_only=True) != branch or observe(outer, "diff", "--cached", "--name-only", "-z"):
                    raise ReviewError("Commit hook changed branch or left staged changes; inspect before retrying.")
                tracked = set(filter(None, observe(outer, "ls-files", "-z", "--", directory).split("\0")))
                if tracked != set(after) or inventory(outer, directory) != after:
                    raise ReviewError("Review directory changed or files were omitted; inspect commit outcome.")
                for relative in tracked:
                    blob = observe(outer, "rev-parse", f"HEAD:{relative}").strip()
                    disk = observe(outer, "hash-object", "--no-filters", "--", relative).strip()
                    if blob != disk:
                        raise ReviewError("Committed artifact differs from disk; inspect filters or concurrent writes.")
                assert_source_unchanged(source, source_before)
                publish = push_review_branch(outer, branch, push=push)
                assert_source_unchanged(source, source_before)
                return {**plan, "status": "committed" if staged else "unchanged",
                        "commit": observe(outer, "rev-parse", "HEAD").strip(),
                        "publish": publish}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pr", type=int)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--slot", required=True, type=int)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--root")
    parser.add_argument("--expected-branch")
    parser.add_argument("--lock-timeout", type=float, default=0)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--no-push", dest="push", action="store_false",
                        help="Commit artifacts locally but do not publish the review branch.")
    args = parser.parse_args()
    try:
        result = commit_review(discover_root(args.root), args.repo, args.pr, args.slot,
                               source_sha=args.source_sha, dry_run=args.dry_run,
                               expected_branch=args.expected_branch,
                               lock_timeout=args.lock_timeout, push=args.push)
        print(json.dumps(result, indent=2))
        return 0
    except (ReviewError, OSError, ValueError) as error:
        print(f"review_commit: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
