#!/usr/bin/env python3
"""Pool administration: status, warm, warm-all, prime-history, recover-cache.

Relocated out of review_setup.py (Task 6) so the PR-setup entrypoint is a
thin router with no pool-admin flags or branches (ruling R11). Every
function below only orchestrates existing `SourcePool`/`ReviewState`
operations -- no new pool behavior is introduced here.

`--operator-assertion` and the selected-slot `--recover-slot` mode are not
ported: `recover-cache` here only ever recovers via same-namespace
owned-process-termination proof (`assertion=None`), which is
`recover_acquisition_cache()`'s default and most common path.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from review_common import (
    ReviewError,
    ReviewState,
    SourcePool,
    assert_no_git_operation,
    current_ref,
    discover_root,
    ensure_repository,
    git_ignored_paths,
    git_status,
    lock_session,
    read_submodules,
    recover_acquisition_cache,
    run_git_read,
    safe_path,
    validate_sha,
)

POOL_DISK_RESERVE_BYTES = 2 * 1024 * 1024 * 1024


def select_module_by_name(root: Path, repo: str) -> dict:
    if not isinstance(repo, str):
        raise ReviewError("Repository must be a canonical configured name.")
    matches = [module for module in read_submodules(root) if Path(module["path"]).name == repo]
    if len(matches) != 1:
        raise ReviewError("Repository is not a unique canonical configured source name.")
    return matches[0]


def _pool_status(root: Path, module: dict, slot: int) -> dict:
    state = ReviewState(root, read_only=True)
    pool = SourcePool(state, module)
    name = pool.slot_name(slot)
    outer = safe_path(root, pool.slot_relative_path(slot))
    nested = safe_path(root, f"{pool.slot_relative_path(slot)}/{module['path']}")
    row = {
        "repository": pool.repo_name,
        "slot_number": slot,
        "slot": name,
        "slot_path": pool.slot_relative_path(slot),
        "status": "absent",
    }
    try:
        manifest = state.load_pool_run(pool.repo_key, slot)
    except ReviewError as exc:
        row["status"] = "blocked"
        row["reason"] = str(exc)
        return row
    if outer.is_symlink() or nested.is_symlink():
        row["status"] = "blocked"
        row["reason"] = "symlinked managed path"
        return row
    if not outer.exists():
        if manifest and manifest.get("status") not in ("archived", "discarded", "superseded", "merged"):
            row["status"] = "partial"
            row["reason"] = "owned reservation has no outer worktree yet"
        return row
    registered = state.registered(read_only=True)
    if outer.resolve() not in registered:
        row["status"] = "occupied"
        row["reason"] = "foreign outer directory"
        return row
    try:
        ensure_repository(outer, read_only=True)
        assert_no_git_operation(outer, read_only=True)
        outer_status = git_status(outer, read_only=True)
        expected_delta = all(
            path == module["path"] and code[0] == " "
            for code, path in outer_status
        ) and not git_ignored_paths(
            outer, protected=module["path"], read_only=True
        )
        branch = current_ref(outer, read_only=True)
        if manifest and manifest.get("status") not in ("archived", "discarded", "superseded", "merged", "ready"):
            if branch not in (None, manifest.get("branch")) or not expected_delta:
                row["status"] = "blocked"
                row["reason"] = "owned partial topology drifted from its manifest"
            else:
                row["status"] = "partial"
                row["reason"] = f"owned operation interrupted during {manifest.get('phase', manifest.get('status'))}"
            return row
        if branch is not None or not expected_delta:
            row["status"] = "occupied"
            row["reason"] = "active or unexpectedly dirty outer worktree"
            return row
        if not (nested / ".git").exists():
            row["status"] = "partial" if manifest else "blocked"
            row["reason"] = "nested source is not initialized"
            return row
        ensure_repository(nested, read_only=True)
        common = run_git_read(
            nested, ["rev-parse", "--path-format=absolute", "--git-common-dir"]
        ).stdout.strip()
        if (
            Path(common).resolve() != pool.store.resolve()
            or git_status(nested, read_only=True)
            or git_ignored_paths(nested, read_only=True)
            or current_ref(nested, read_only=True) is not None
        ):
            row["status"] = "occupied"
            row["reason"] = "foreign, attached, or dirty nested source"
            return row
        head_before = validate_sha(
            run_git_read(nested, ["rev-parse", "HEAD"]).stdout.strip()
        )
        baseline_before = pool.cached_baseline_sha_read_only()
        classification = pool.classify_source_baseline(
            nested, manifest=manifest, read_only=True
        )
        head_after = validate_sha(
            run_git_read(nested, ["rev-parse", "HEAD"]).stdout.strip()
        )
        baseline_after = pool.cached_baseline_sha_read_only()
        if (
            head_before != head_after
            or baseline_before != baseline_after
            or classification["head_sha"] != head_after
            or classification["baseline_sha"] != baseline_after
        ):
            row["status"] = "blocked"
            row["reason"] = "source or baseline changed during read-only status"
            return row
        if classification["status"] == "unsafe":
            row["status"] = "blocked"
            row["reason"] = "source HEAD has no exact managed baseline provenance"
            row.update(head_sha=head_after, baseline_sha=baseline_after)
            return row
        row.update(classification)
    except ReviewError as exc:
        row["status"] = "blocked"
        row["reason"] = str(exc)
    return row


def status_run(root: Path) -> list[dict]:
    ensure_repository(root, read_only=True)
    rows = []
    for module in read_submodules(root, read_only=True):
        for slot in range(SourcePool.SLOT_COUNT):
            rows.append(_pool_status(root, module, slot))
    return rows


def warm_run(root: Path, repo: str, slot: int, *, dry_run: bool = False,
             lock_timeout: float = 1200) -> dict:
    # Resolve repository identity first, before interpreting the repo-local slot.
    module = select_module_by_name(root, repo)
    numbers = SourcePool._validate_slots([slot])
    existing = _pool_status(root, module, numbers[0])
    if dry_run:
        existing["action"] = (
            "reuse"
            if existing["status"] == "warm"
            else "would-normalize"
            if existing["status"] == "stale"
            else "would-warm"
        )
        return existing
    if existing["status"] in ("occupied", "blocked", "partial"):
        return existing
    state = ReviewState(root)
    pool = SourcePool(state, module, reserve_bytes=POOL_DISK_RESERVE_BYTES)
    with lock_session(lock_timeout), state.lock(shared=True, phase="warm-slot"), state.slot_lock(pool.repo_key, numbers[0], phase="warm-slot"):
        result = pool.warm(numbers)[0]
    return {"repository": pool.repo_name, "slot_number": numbers[0], "status": "warm", **result}


def warm_all_run(root: Path, *, dry_run: bool = False,
                 lock_timeout: float = 1200) -> list[dict]:
    rows = []
    halted = False
    for module in read_submodules(root):
        repo = Path(module["path"]).name
        for slot in range(SourcePool.SLOT_COUNT):
            if halted and not dry_run:
                row = _pool_status(root, module, slot)
                row["reason"] = "not attempted after an earlier warm failure"
                rows.append(row)
                continue
            try:
                row = warm_run(
                    root, repo, slot, dry_run=dry_run, lock_timeout=lock_timeout
                )
            except ReviewError as exc:
                row = _pool_status(root, module, slot)
                row["status"] = "partial" if row["status"] != "occupied" else "occupied"
                row["reason"] = str(exc)
                halted = True
            rows.append(row)
    return rows


def prime_history_run(root: Path, repo: str, *, dry_run: bool = False,
                       depth: int = 100000, timeout: float = 3600,
                       lock_timeout: float = 1200) -> dict:
    module = select_module_by_name(root, repo)
    state = ReviewState(root, read_only=dry_run)
    pool = SourcePool(state, module)
    options = {"depth": depth, "timeout": timeout, "dry_run": dry_run}
    if dry_run:
        return pool.prime_history(**options)
    with lock_session(lock_timeout), state.lock(shared=True, phase="prime-history"):
        return pool.prime_history(**options)


def recover_cache_run(root: Path, repo: str, *, dry_run: bool = False,
                      lock_timeout: float = 1200) -> dict:
    module = select_module_by_name(root, repo)
    state = ReviewState(root)
    pool = SourcePool(state, module)
    with (
        lock_session(lock_timeout),
        state.lock(shared=True, phase="cache-recovery"),
        pool.acquisition_lock(phase="cache-recovery"),
        pool.store_lock(phase="cache-recovery"),
    ):
        return recover_acquisition_cache(pool.store, assertion=None, inspect=dry_run)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", help="Canonical review store root (normally inferred from this script).")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("status", help="Report all configured repository slots without mutation.")

    warm_parser = subparsers.add_parser("warm", help="Warm one canonical repository slot.")
    warm_parser.add_argument("--repo", required=True)
    warm_parser.add_argument("--slot", type=int, required=True)
    warm_parser.add_argument("--dry-run", action="store_true", help="Plan warming without mutation.")
    warm_parser.add_argument("--lock-timeout", type=float, default=1200)

    warm_all_parser = subparsers.add_parser("warm-all", help="Warm all configured repository slots sequentially.")
    warm_all_parser.add_argument("--dry-run", action="store_true", help="Plan warming without mutation.")
    warm_all_parser.add_argument("--lock-timeout", type=float, default=1200)

    prime_parser = subparsers.add_parser("prime-history", help="Prime shared baseline history without moving slots.")
    prime_parser.add_argument("--repo", required=True)
    prime_parser.add_argument("--dry-run", action="store_true", help="Plan history priming without mutation.")
    prime_parser.add_argument("--baseline-depth", type=int, default=100000)
    prime_parser.add_argument("--history-timeout", type=float, default=3600)
    prime_parser.add_argument("--lock-timeout", type=float, default=1200)

    recover_parser = subparsers.add_parser(
        "recover-cache",
        help="Verify and resolve owned acquisition cleanup without moving slots or fetching.",
    )
    recover_parser.add_argument("--repo", required=True)
    recover_parser.add_argument("--dry-run", action="store_true", help="Inspect only, without resolving.")
    recover_parser.add_argument("--lock-timeout", type=float, default=1200)

    return parser


def _main(argv=None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    try:
        root = discover_root(args.root)
        if args.command == "status":
            print(json.dumps(status_run(root), indent=2))
            return 0
        if args.command == "warm":
            row = warm_run(root, args.repo, args.slot, dry_run=args.dry_run, lock_timeout=args.lock_timeout)
            print(json.dumps(row, indent=2))
            return 0 if row["status"] not in ("occupied", "blocked", "partial") else 2
        if args.command == "warm-all":
            rows = warm_all_run(root, dry_run=args.dry_run, lock_timeout=args.lock_timeout)
            print(json.dumps(rows, indent=2))
            return 2 if any(row["status"] in ("occupied", "blocked", "partial") for row in rows) else 0
        if args.command == "prime-history":
            row = prime_history_run(
                root, args.repo, dry_run=args.dry_run, depth=args.baseline_depth,
                timeout=args.history_timeout, lock_timeout=args.lock_timeout,
            )
            print(json.dumps(row, indent=2))
            return 0
        if args.command == "recover-cache":
            row = recover_cache_run(root, args.repo, dry_run=args.dry_run, lock_timeout=args.lock_timeout)
            print(json.dumps(row, indent=2))
            return 0
        raise ReviewError(f"Unknown command '{args.command}'.")
    except ReviewError as exc:
        print(f"Pool admin stopped: {exc}", file=sys.stderr)
        return exc.code


def main(argv=None) -> int:
    return _main(argv)


if __name__ == "__main__":
    raise SystemExit(main())
