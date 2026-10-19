#!/usr/bin/env python3
"""Provider-neutral router for a single NOVA review slot: Resolve -> Git setup -> Context.

Replaces the prior lifecycle monolith (Task 6, review-setup-simplification
plan). Pool administration (status / warm / warm-all / prime-history /
recover-cache) now lives in `review_pool_admin.py`; the selected-slot
recovery / reclaim-inspection modes this parser used to expose
(`--recover-slot`, `--inspect-reclaim`, `--reclaim-generation`,
`--operator-assertion`) are retired entirely, not ported anywhere. This is
the only edit this plan makes to this parser (ruling R11).

Exit codes:
    0  complete: Git workspace ready and context collected without warnings.
    1  workspace failure: the PR could not be resolved, or Git setup
       (`review_git_prepare.prepare()`) failed.
    2  workspace ready but context collection warned or failed -- the
       prepared Git worktree is still valid and usable.
    3  the PR's iteration or merge commit moved during or after context
       collection (`PullRequestMoved`); re-resolve and re-prepare.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Protocol

from review_common import PullRequestMoved, ResolvedPullRequest, ReviewError, discover_root, preflight
from review_git_prepare import prepare


class ReviewProvider(Protocol):
    """Structural contract a concrete provider (e.g. `review_ado.AdoProvider`)
    must satisfy for `route()` below. Deliberately not bound to any concrete
    provider import here, so this module and `route()` stay provider-neutral
    -- only `_main()`'s CLI wrapper binds to a real provider, and does so
    lazily (see below)."""

    def resolve(self, pr: int, repo_hint: str | None = None) -> ResolvedPullRequest: ...

    def git_auth(self): ...

    def collect_context(self, resolved: ResolvedPullRequest, worktree: Path) -> dict: ...


def route(*, provider: ReviewProvider, pr: int, repo: str | None, slot: int, root: Path) -> int:
    """Resolve -> Git setup -> Context, in that exact order.

    A Git-ready workspace survives a context failure: once
    `review_git_prepare.prepare()` succeeds, only a `PullRequestMoved`
    (exit 3) or another context failure (exit 2) can follow -- neither
    tears down or invalidates the prepared worktree.
    """
    resolved = provider.resolve(pr, repo_hint=repo)
    result = prepare(root=root, resolved=resolved, slot=slot, auth=provider.git_auth())
    print(
        f"Resolved: {resolved.repo} PR {resolved.pr} "
        f"(iteration {resolved.iteration}, provider {resolved.provider})",
        file=sys.stderr,
    )
    print(
        f"Git ready: slot {result.slot} -> {result.worktree} @ {result.merge_sha} "
        f"(prepared {result.prepared_at})",
        file=sys.stderr,
    )
    try:
        report = provider.collect_context(resolved, result.worktree)
    except PullRequestMoved as exc:
        print(f"PR moved: {exc}", file=sys.stderr)
        return 3
    except ReviewError as exc:
        print(f"Context collection failed (Git workspace remains ready): {exc}", file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2, sort_keys=True, default=str))
    warnings = report.get("warnings") or []
    if warnings or not report.get("complete", False):
        print(f"Context collected with {len(warnings)} warning(s).", file=sys.stderr)
        return 2
    return 0


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pr_number", type=int)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--slot", type=int, required=True)
    parser.add_argument("--org")
    parser.add_argument("--root", help="Canonical review store root (normally inferred from this script).")
    parser.add_argument(
        "--transport", choices=("auto", "gateway", "cli"), default="auto",
        help="ADO transport: 'gateway' (Gateway-injected curl auth), 'cli' "
             "(Azure CLI via WSL), or 'auto' (Gateway-marker detected).",
    )
    return parser


def _main(argv=None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    try:
        preflight()
        root = discover_root(args.root)
        # Lazy, function-body-only provider import (mirrors
        # review_git_prepare.py's own ruling-R5 pattern): only this CLI
        # entrypoint ever binds to a concrete provider. `route()` above, and
        # this module's own top-level imports, never import a provider.
        from review_github import select_provider

        transport = None if args.transport == "auto" else args.transport
        provider = select_provider(root, args.repo, org=args.org, transport=transport)
        return route(provider=provider, pr=args.pr_number, repo=args.repo, slot=args.slot, root=root)
    except ReviewError as exc:
        print(f"Review setup stopped: {exc}", file=sys.stderr)
        return exc.code


def main(argv=None) -> int:
    return _main(argv)


if __name__ == "__main__":
    raise SystemExit(main())
