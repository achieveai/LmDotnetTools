#!/usr/bin/env python3
"""GitHub provider for review-setup: resolve a PR, read live state, collect read-only context.

Mirrors ``review_ado.AdoProvider``. Every request is a read-only REST GET or GraphQL query to
``api.github.com`` through curl's inherited sandbox proxy, which injects the credential; no token ever
reaches this process.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from urllib.parse import parse_qsl, quote, unquote, urlencode, urlsplit

from review_common import (
    GitTransportAuth,
    PullRequestMoved,
    ResolvedPullRequest,
    ReviewError,
    discover_root,
    preflight,
    run_git,
    validate_sha,
)

API = "https://api.github.com"
_PAGE_LIMIT = 50
_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*")


class PullRequestNotActive(ReviewError):
    """Confirmed closed GitHub PR; callers may skip it without preparing a slot."""

    def __init__(self, pr: int, status: str):
        super().__init__(
            f"PR_NOT_ACTIVE: GitHub PR {pr} is {status}; skip this PR. No Git preparation was attempted.",
            code=4,
        )
        self.pr = pr
        self.status = status


class GitHubClient:
    """Read-only GitHub REST queries for one repository through the sandbox proxy."""

    def __init__(self, owner: str, repository: str, *, timeout: int = 30, retries: int = 2):
        for part in (owner, repository):
            if not _NAME.fullmatch(part) or part in (".", ".."):
                raise ReviewError("Unsafe GitHub owner or repository name.")
        self.owner, self.repository = owner, repository
        self.timeout, self.retries = timeout, retries
        self.last_link = ""

    def request(self, path: str):
        """GET ``/repos/<owner>/<repo>/<path>``; ``path`` is repository-relative."""
        parsed = urlsplit(path)
        if parsed.scheme or parsed.netloc or parsed.fragment or path.startswith("/") or "\\" in path:
            raise ReviewError("GitHub request must use a repository-relative path.")
        if any(unquote(part) in ("", ".", "..") or any(ord(c) < 32 for c in unquote(part))
               for part in parsed.path.split("/")):
            raise ReviewError("Unsafe GitHub request path.")
        url = (f"{API}/repos/{quote(self.owner, safe='')}/{quote(self.repository, safe='')}/{parsed.path}"
               + (f"?{parsed.query}" if parsed.query else ""))
        return self._send(url)

    def graphql(self, query: str, variables: dict | None = None) -> dict:
        """A read-only GraphQL query (POST is GraphQL's transport, not a write); returns ``data``."""
        if not query.lstrip().startswith("query"):
            raise ReviewError("Only read-only GraphQL queries are allowed.")
        result = self._send(f"{API}/graphql", json.dumps({"query": query, "variables": variables or {}}))
        if not isinstance(result, dict) or result.get("errors") or not isinstance(result.get("data"), dict):
            raise ReviewError("GitHub GraphQL query failed.")
        return result["data"]

    def _send(self, url: str, body: str | None = None):
        for attempt in range(self.retries + 1):
            with tempfile.TemporaryDirectory(prefix="review-github-") as directory:
                headers, output = Path(directory) / "headers", Path(directory) / "body"
                args = ["curl", "--disable", "--silent", "--show-error", "--proto", "=https",
                        "--max-time", str(self.timeout), "--request", "GET" if body is None else "POST",
                        "--dump-header", str(headers),
                        "--output", str(output), "--write-out", "%{http_code}",
                        "--header", "Accept: application/vnd.github+json",
                        "--header", "X-GitHub-Api-Version: 2022-11-28",
                        "--header", "User-Agent: review-setup"]
                if body is not None:
                    request = Path(directory) / "request"
                    request.write_text(body, encoding="utf-8")
                    args += ["--header", "Content-Type: application/json", "--data-binary", f"@{request}"]
                args.append(url)
                try:
                    result = subprocess.run(args, capture_output=True, timeout=self.timeout + 5)
                except (OSError, subprocess.TimeoutExpired):
                    raise ReviewError("GitHub transport failed; check sandbox proxy connectivity.") from None
                if result.returncode:
                    raise ReviewError(f"GitHub transport failed (curl exit {result.returncode}); no response or credentials logged.")
                try:
                    status = int(result.stdout)
                except ValueError:
                    raise ReviewError("GitHub transport returned an invalid HTTP status.") from None
                retry_after, self.last_link = "", ""
                if headers.exists():
                    for line in headers.read_text(encoding="iso-8859-1").splitlines():
                        key, sep, value = line.partition(":")
                        if sep and key.lower() == "retry-after":
                            retry_after = value.strip()
                        elif sep and key.lower() == "link":
                            self.last_link = value.strip()
                if status in (429, 500, 502, 503, 504) and attempt < self.retries:
                    time.sleep(min(30, int(retry_after)) if retry_after.isdigit() else min(2 ** attempt, 8))
                    continue
                if status in (401, 403):
                    raise ReviewError(f"GitHub authentication or egress policy refused the read (HTTP {status}).")
                if not 200 <= status < 300:
                    raise ReviewError(f"GitHub query failed (HTTP {status}); the resource may be inaccessible.")
                try:
                    return json.loads(output.read_bytes())
                except (ValueError, OSError):
                    raise ReviewError("GitHub returned a non-JSON response.") from None
        raise ReviewError("GitHub request retry limit reached.")

    def get_pages(self, path: str) -> list:
        """Every item of a paginated list; fails rather than returning a truncated one."""
        values = []
        parsed = urlsplit(path)
        query = dict(parse_qsl(parsed.query, keep_blank_values=True))
        query["per_page"] = "100"
        for page in range(1, _PAGE_LIMIT + 1):
            query["page"] = str(page)
            result = self.request(parsed.path + "?" + urlencode(query))
            if not isinstance(result, list):
                raise ReviewError("GitHub list response is not an array.")
            values.extend(result)
            if 'rel="next"' not in self.last_link:
                return values
        raise ReviewError("GitHub pagination exceeded the safety limit.")


def _positive(value) -> int | None:
    return value if type(value) is int and value > 0 else None


def _lifecycle(payload: dict) -> str:
    if payload.get("state") == "open":
        return "active"
    return "completed" if payload.get("merged_at") else "abandoned"


def fetch_pr(client: GitHubClient, pr: int, *, settle_seconds: float = 30) -> dict:
    """The PR payload, waiting briefly while GitHub computes a fresh test merge."""
    deadline = time.monotonic() + settle_seconds
    while True:
        payload = client.request(f"pulls/{pr}")
        if not isinstance(payload, dict) or _positive(payload.get("number")) != pr:
            raise ReviewError("GitHub returned a PR with a mismatched number.")
        if payload.get("state") != "open" or payload.get("mergeable") is not None or time.monotonic() >= deadline:
            return payload
        time.sleep(3)


def live_state(payload: dict) -> dict:
    """Provider-neutral live view, the same shape ReviewRunnerSandbox reports for ADO."""
    merge = payload.get("merge_commit_sha") if payload.get("mergeable") is True else None
    return {"prId": str(payload["number"]), "status": _lifecycle(payload), "isDraft": payload.get("draft"),
            "createdAt": payload.get("created_at"), "headSha": (payload.get("head") or {}).get("sha"),
            "mergeSha": merge}


def _snapshot(payload: dict, owner: str, repository: str) -> dict:
    head, base = payload.get("head") or {}, payload.get("base") or {}
    fingerprint = hashlib.sha256(json.dumps(
        [head.get("sha"), base.get("sha"), payload.get("merge_commit_sha"), payload.get("updated_at")]
    ).encode()).hexdigest()
    return {"pr_number": payload["number"],
            "repository": {"provider": "github", "owner": owner, "name": repository,
                           "url": f"https://github.com/{owner}/{repository}"},
            "title": payload.get("title") or "", "description": payload.get("body") or "",
            "author": (payload.get("user") or {}).get("login"),
            "source_ref": "refs/heads/" + str(head.get("ref") or ""),
            "target_ref": "refs/heads/" + str(base.get("ref") or ""),
            "head_sha": validate_sha(head.get("sha")), "target_sha": validate_sha(base.get("sha")),
            "merge_sha": payload.get("merge_commit_sha"), "iteration_id": None,
            "created_at": payload.get("created_at"), "updated_at": payload.get("updated_at"),
            "web_url": payload.get("html_url"), "snapshot_fingerprint": fingerprint}


def _linked_issues(client: GitHubClient, snapshot: dict) -> dict:
    """Issues the PR text names with GitHub closing keywords or a bare #N, fetched read-only."""
    text = snapshot["title"] + "\n" + snapshot["description"]
    numbers = sorted({int(n) for n in re.findall(r"(?<![\w/])#(\d{1,7})\b", text)} - {snapshot["pr_number"]})
    issues, warnings = {}, []
    for number in numbers[:50]:
        try:
            raw = client.request(f"issues/{number}")
            issues[str(number)] = {"id": number, "title": raw.get("title", ""), "body": raw.get("body") or "",
                                   "state": raw.get("state"), "is_pull_request": "pull_request" in raw,
                                   "url": raw.get("html_url")}
        except ReviewError:
            warnings.append(f"Linked issue {number} is unavailable.")
    if len(numbers) > 50:
        warnings.append("More than 50 linked issue references; only the first 50 were read.")
    return {"items": issues, "warnings": warnings, "complete": not warnings}


def _discussion(client: GitHubClient, pr: int) -> dict:
    """The PR's existing conversation: summary comments, reviews, and inline threads."""
    def brief(item, *extra):
        row = {"id": item.get("id"), "author": (item.get("user") or {}).get("login"),
               "created_at": item.get("created_at") or item.get("submitted_at"), "body": item.get("body") or ""}
        for key in extra:
            row[key] = item.get(key)
        return row
    return {"issue_comments": [brief(c) for c in client.get_pages(f"issues/{pr}/comments")],
            "reviews": [brief(r, "state", "commit_id") for r in client.get_pages(f"pulls/{pr}/reviews")],
            "review_comments": [brief(c, "path", "line", "in_reply_to_id", "commit_id")
                                for c in client.get_pages(f"pulls/{pr}/comments")]}


_ORIGIN_QUERY = """query($owner: String!, $name: String!) {
  repository(owner: $owner, name: $name) {
%s
  }
}"""
_ORIGIN_FIELD = """    c%d: object(oid: "%s") { ... on Commit { associatedPullRequests(first: 10) { nodes {
      number title body state mergedAt closedAt url author { login } } } } }"""
_GRAPHQL_STATUS = {"OPEN": "active", "MERGED": "completed", "CLOSED": "abandoned"}


def _origin_prs(client: GitHubClient, commits, *, max_commits=2000, batch=100) -> dict:
    """Associated PRs for blamed commits, without pretending association is a unique origin.

    One GraphQL query per ``batch`` commits: a large PR blames thousands of lines, and one REST call
    per commit both ran past any sane limit and spent the shared token's rate budget.
    """
    unique = sorted(set(commits))
    mappings = {commit: {"pr_ids": [], "status": "not-queried-limit"} for commit in unique}
    pull_requests, warnings = {}, []
    if len(unique) > max_commits:
        warnings.append(f"Commit query limit {max_commits} reached; provenance is partial.")
    selected = unique[:max_commits]
    for start in range(0, len(selected), batch):
        chunk = selected[start:start + batch]
        try:
            fields = "\n".join(_ORIGIN_FIELD % (index, validate_sha(commit)) for index, commit in enumerate(chunk))
            repository = client.graphql(_ORIGIN_QUERY % fields,
                                        {"owner": client.owner, "name": client.repository}).get("repository")
            if not isinstance(repository, dict):
                raise ReviewError("Invalid commit-to-PR response.")
        except ReviewError:
            warnings.append("A commit-to-PR query failed; association status is unknown for that batch.")
            for commit in chunk:
                mappings[commit]["status"] = "query-failed"
            continue
        for index, commit in enumerate(chunk):
            node = repository.get(f"c{index}")
            if not isinstance(node, dict) or not isinstance(node.get("associatedPullRequests"), dict):
                warnings.append(f"Commit {commit[:12]} is not on GitHub; its association status is unknown.")
                mappings[commit]["status"] = "query-failed"
                continue
            rows = [row for row in node["associatedPullRequests"].get("nodes") or [] if isinstance(row, dict)]
            numbers = sorted({n for n in (_positive(row.get("number")) for row in rows) if n})
            mappings[commit] = {"pr_ids": numbers, "status": "associated" if numbers else "no-associated-pr"}
            for row in rows:
                number = _positive(row.get("number"))
                if number and str(number) not in pull_requests:
                    pull_requests[str(number)] = {"id": number, "title": row.get("title", ""),
                                                  "description": row.get("body") or "",
                                                  "status": _GRAPHQL_STATUS.get(row.get("state"), "unknown"),
                                                  "author": (row.get("author") or {}).get("login"),
                                                  "url": row.get("url"), "closed_date": row.get("closedAt")}
    return {"commits": mappings, "pull_requests": pull_requests, "warnings": warnings, "complete": not warnings}


class GitHubProvider:
    """GitHub provider: resolves a PR to a provider-neutral identity. Never fetches Git."""

    def __init__(self, owner: str, repository: str):
        self.client = GitHubClient(owner, repository)
        self.owner, self.repository = owner, repository

    def resolve(self, pr: int, repo_hint: str | None = None) -> ResolvedPullRequest:
        if repo_hint is not None and repo_hint.casefold() != self.repository.casefold():
            raise ReviewError("Requested repository does not match this GitHub provider's repository.")
        payload = fetch_pr(self.client, pr)
        status = _lifecycle(payload)
        if status != "active":
            raise PullRequestNotActive(pr, status)
        snapshot = _snapshot(payload, self.owner, self.repository)
        return ResolvedPullRequest(
            provider="github",
            pr=pr,
            repo=self.repository,
            remote_url=f"https://github.com/{self.owner}/{self.repository}.git",
            merge_ref=f"refs/pull/{pr}/merge",
            merge_sha=live_state(payload)["mergeSha"],
            source_branch=snapshot["source_ref"],
            target_branch=snapshot["target_ref"],
            iteration=None,
        )

    def git_auth(self) -> GitTransportAuth | None:
        # The Gateway sandbox proxy injects Git-level auth for github.com; there is no bearer to hand back.
        return None

    def collect_context(self, resolved: ResolvedPullRequest, worktree: Path) -> dict:
        """Read-only context bound to the prepared merge. Raises PullRequestMoved on any drift."""
        from review_ado import _collect_file_changes, _history  # provider-neutral Git diff/blame provenance

        pre = fetch_pr(self.client, resolved.pr)
        pre_live = live_state(pre)
        prepared = validate_sha(run_git(worktree, ["rev-parse", "HEAD"]).stdout.strip())
        if pre_live["status"] != "active" or pre_live["mergeSha"] != prepared:
            raise PullRequestMoved(
                f"PR {resolved.pr} has moved since it was prepared (merge {prepared} -> {pre_live['mergeSha']}); "
                "re-resolve and re-prepare before collecting context."
            )
        snapshot = _snapshot(pre, self.owner, self.repository)
        parents = run_git(worktree, ["rev-list", "--parents", "-n", "1", prepared]).stdout.split()
        if len(parents) != 3 or parents[2] != snapshot["head_sha"]:
            raise PullRequestMoved(f"PR {resolved.pr} test merge does not combine the current head; re-prepare.")
        base_sha = validate_sha(run_git(worktree, ["merge-base", parents[1], snapshot["head_sha"]]).stdout.strip())
        changes = _collect_file_changes(worktree, base_sha, snapshot["head_sha"], current_pr=resolved.pr)
        provenance = _origin_prs(self.client, changes["historical_commits"])
        issues = _linked_issues(self.client, snapshot)
        discussion = _discussion(self.client, resolved.pr)
        post = live_state(fetch_pr(self.client, resolved.pr))
        if post["mergeSha"] != pre_live["mergeSha"] or post["headSha"] != pre_live["headSha"]:
            raise PullRequestMoved(f"PR {resolved.pr} changed during context collection; re-resolve and re-prepare.")
        warnings = changes["warnings"] + provenance["warnings"] + issues["warnings"]
        return {"snapshot": snapshot, "base_sha": base_sha, "collected_at": datetime.now(timezone.utc).isoformat(),
                "linked_issues": issues, "discussion": discussion, "files": changes["files"],
                "history": _history(changes, provenance), "warnings": warnings,
                "complete": changes["complete"] and provenance["complete"] and issues["complete"]}


def provider_for_module(module: dict) -> GitHubProvider:
    if module.get("provider") != "github":
        raise ReviewError("Source module is not a GitHub repository.")
    return GitHubProvider(module["owner"], module["repository"])


def select_provider(root: Path, repo: str, *, org: str | None = None, transport: str | None = None):
    """The provider for the configured source module ``repo``: GitHub or Azure DevOps."""
    from review_pool_admin import select_module_by_name

    module = select_module_by_name(root, repo)
    if module["provider"] == "github":
        return provider_for_module(module)
    import review_ado

    return review_ado.AdoProvider(org or module["org_url"], transport=transport)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pr", type=int)
    parser.add_argument("--repo", required=True, help="Source module name under repos/.")
    parser.add_argument("--root")
    args = parser.parse_args(argv)
    try:
        preflight()
        provider = select_provider(discover_root(args.root), args.repo)
        print(json.dumps(provider.resolve(args.pr, repo_hint=None).to_public_dict(), indent=2, sort_keys=True))
        return 0
    except ReviewError as exc:
        print(f"GitHub provider stopped: {exc}", file=sys.stderr)
        return exc.code


if __name__ == "__main__":
    raise SystemExit(main())
