"""Azure DevOps provider: read-only PR resolution and Git transport auth.

Provides two interchangeable ADO REST transports:

- ``GatewayCurlTransport`` -- the existing curl-based route (``AdoClient``),
  relying on the sandbox's ambient/injected proxy and CA environment. Used
  inside the Gateway sandbox, where outgoing request auth is injected
  transparently.
- ``AzureCliTransport`` -- invokes the Windows Azure CLI directly from WSL
  (``python.exe -IBm azure.cli ...``, argv list, no shell) so a bearer value
  never appears in process arguments. Used on the direct host, where the
  operator has already run ``az login``.

Transport selection is either explicit (``--transport``) or based on a
single documented Gateway marker: the presence of an ``HTTP_PROXY`` /
``HTTPS_PROXY`` (or lowercase) environment variable, which the Gateway
sandbox sets for its injected egress proxy.

``AdoProvider.collect_context()`` is stubbed in this task; it is implemented
in Task 6.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import html
import json
import logging
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import parse_qsl, quote, unquote, urlencode, urlsplit

from review_common import (
    AdoClient,
    GitTransportAuth,
    PullRequestMoved,
    ResolvedPullRequest,
    ReviewError,
    ReviewState,
    SourcePool,
    canonical_repo_name,
    discover_root,
    preflight,
    read_submodules,
    run_git,
    safe_path,
    validate_sha,
)
from review_context import fetch_pr, resolve_pr

LOGGER = logging.getLogger(__name__)
_API = "api-version=7.1"
_SHA = re.compile(r"^[0-9a-fA-F]{40}$")
_HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
_BLAME_HEADER = re.compile(r"^([0-9a-f]{40}) (\d+) (\d+)(?: (\d+))?$")

# Well-known Azure AD resource ID for Azure DevOps (used by both `az rest`
# and `az account get-access-token`); not a secret.
ADO_RESOURCE_ID = "499b84ac-1321-427f-aa17-267ca6975798"

_GATEWAY_MARKER_ENV_VARS = ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy")

_AZURE_CLI_PYTHON_CANDIDATES = (
    "/mnt/c/Program Files (x86)/Microsoft SDKs/Azure/CLI2/python.exe",
    "/mnt/c/Program Files/Microsoft SDKs/Azure/CLI2/python.exe",
)

# Matches both plain "key: value" / "key=value" diagnostic text and quoted
# JSON fields ("accessToken":"...", "access_token": "...", "pat": "..."),
# with or without an underscore/dash between a compound key's words. Does
# NOT match a bare "Authorization" key on its own -- that is deliberately
# left unmatched here so "Authorization: Bearer <token>" is redacted by the
# "bearer" alternative (matching the actual secret span, not just the word
# "Bearer"), rather than truncating the match at "Bearer" and leaking the
# token that follows.
_SECRET_PATTERN = re.compile(
    r'(?i)(?P<key>"?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|bearer|token|pat)"?)'
    r'(?P<sep>\s+|\s*[:=]\s*)'
    r'(?P<quote>"?)'
    r'(?P<value>[^"\s,;}\]]+)'
    r'(?P=quote)'
)


def _redact(text: str) -> str:
    """Scrub token/bearer/PAT-shaped substrings (plain or quoted-JSON) out of
    a diagnostic string, leaving unrelated text and JSON fields untouched."""
    return _SECRET_PATTERN.sub(
        lambda m: f"{m.group('key')}{m.group('sep')}{m.group('quote')}[REDACTED]{m.group('quote')}",
        text,
    )


def _decode_cli_stdout(raw: bytes) -> str:
    """Decode Azure CLI (Windows) output tolerant of CRLF line endings."""
    return raw.decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")


def _find_windows_az_python() -> str:
    override = os.environ.get("REVIEW_ADO_AZURE_CLI_PYTHON")
    candidates = (override,) if override else _AZURE_CLI_PYTHON_CANDIDATES
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    raise ReviewError(
        "Could not locate the Windows Azure CLI python.exe; set "
        "REVIEW_ADO_AZURE_CLI_PYTHON or install Azure CLI at one of the default locations."
    )


def _run_az(extra_args: list[str], *, timeout: int) -> subprocess.CompletedProcess:
    python_exe = _find_windows_az_python()
    argv = [python_exe, "-IBm", "azure.cli", *extra_args]
    try:
        return subprocess.run(argv, capture_output=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ReviewError(f"Azure CLI invocation failed: {_redact(str(exc))}") from None


# Substrings (checked case-insensitively) that indicate the Azure CLI itself,
# or Azure AD/ADO, rejected the request for authentication reasons rather
# than the resource being genuinely absent or unavailable. `az rest` already
# authenticates its own calls, so a failure here is unusual and worth
# calling out distinctly (mirrors the historical 302-misclassification
# incident that motivated the same fix in the curl transport).
_AUTH_FAILURE_MARKERS = (
    "aadsts",
    "az login",
    "interaction_required",
    "invalid_grant",
    "please run 'az login'",
    "please run az login",
    "token has expired",
    "reauthenticate",
    "re-authenticate",
    "authentication is needed",
    "authentication required",
)


def _looks_like_sign_in_html(text: str) -> bool:
    stripped = text.strip().lower()
    return (
        stripped.startswith("<!doctype html")
        or stripped.startswith("<html")
        or "<title>sign in" in stripped
        or "login.microsoftonline.com" in stripped
    )


def _looks_like_auth_failure(*texts: str) -> bool:
    combined = "\n".join(texts).lower()
    return _looks_like_sign_in_html(combined) or any(marker in combined for marker in _AUTH_FAILURE_MARKERS)


def _gateway_marker_present() -> bool:
    return any(os.environ.get(name) for name in _GATEWAY_MARKER_ENV_VARS)


def _validate_org_url(org_url: str) -> str:
    parsed = urlsplit(org_url)
    if (
        parsed.scheme != "https"
        or parsed.hostname != "dev.azure.com"
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
    ):
        raise ReviewError("ADO organization must be https://dev.azure.com/<organization>.")
    org = parsed.path.strip("/")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", org) or parsed.port not in (None, 443):
        raise ReviewError("Invalid Azure DevOps organization.")
    return f"https://dev.azure.com/{org.lower()}"


def _validate_relative_path(path: str) -> tuple[str, dict[str, str]]:
    parsed = urlsplit(path)
    if parsed.scheme or parsed.netloc or parsed.fragment or path.startswith("//") or "\\" in path:
        raise ReviewError("ADO request must use an organization-relative path.")
    if any(
        unquote(part) in (".", "..") or "/" in unquote(part) or "\\" in unquote(part)
        or any(ord(char) < 32 for char in unquote(part))
        for part in parsed.path.split("/")
    ):
        raise ReviewError("Unsafe ADO request path.")
    query = dict(parse_qsl(parsed.query, keep_blank_values=True))
    query.setdefault("api-version", "7.1")
    return parsed.path, query


class GatewayCurlTransport:
    """Retains the existing curl route so Gateway request injection keeps working."""

    def __init__(self, org_url: str, *, timeout: int = 30, retries: int = 2):
        self._client = AdoClient(org_url, timeout=timeout, retries=retries)
        self.org_url = self._client.org_url

    def request(self, method: str, path: str, body=None):
        return self._client.request(method, path, body)

    def get_pages(self, path: str) -> list:
        return self._client.get_pages(path)

    def git_auth(self) -> GitTransportAuth | None:
        # The Gateway sandbox injects Git-level auth transparently outside
        # this script's control; there is no explicit bearer to hand back.
        return None


class AzureCliTransport:
    """Read-only ADO REST access via the Windows Azure CLI, invoked directly
    from WSL as an argv list (no shell), so a bearer value never appears in
    process arguments (ruling R13)."""

    def __init__(self, org_url: str, *, timeout: int = 30):
        self.org_url = _validate_org_url(org_url)
        self.timeout = timeout

    def request(self, method: str, path: str, body=None):
        method = method.upper()
        if method != "GET":
            raise ReviewError("AzureCliTransport supports read-only GET requests only.")
        if body is not None:
            raise ReviewError("AzureCliTransport does not support request bodies.")
        relative_path, query = _validate_relative_path(path)
        url = self.org_url + "/" + relative_path.lstrip("/") + "?" + urlencode(query)
        result = _run_az(
            ["rest", "--method", "get", "--url", url, "--resource", ADO_RESOURCE_ID, "--output", "json"],
            timeout=self.timeout,
        )
        stdout_text = _decode_cli_stdout(result.stdout)
        stderr_text = _decode_cli_stdout(result.stderr)
        if result.returncode:
            if _looks_like_auth_failure(stdout_text, stderr_text):
                raise ReviewError(
                    "ADO authentication required (Azure CLI reported a sign-in/authentication "
                    "failure); run 'az login' on the Windows host and retry."
                )
            tail = _redact(stderr_text[-400:]).strip() or _redact(stdout_text[-400:]).strip() or "no diagnostic output"
            raise ReviewError(f"ADO CLI transport failed: {tail}")
        try:
            return json.loads(stdout_text)
        except ValueError:
            if _looks_like_sign_in_html(stdout_text):
                raise ReviewError(
                    "ADO authentication required (received a non-JSON sign-in page instead of "
                    "an API response)."
                ) from None
            raise ReviewError("ADO CLI transport returned a non-JSON response.") from None

    def git_auth(self) -> GitTransportAuth:
        result = _run_az(
            ["account", "get-access-token", "--resource", ADO_RESOURCE_ID, "--query", "accessToken", "--output", "tsv"],
            timeout=self.timeout,
        )
        if result.returncode:
            raise ReviewError("Azure CLI authentication required; run 'az login' on the Windows host.")
        token = _decode_cli_stdout(result.stdout).strip()
        if not token:
            raise ReviewError("Azure CLI returned an empty access token.")
        return GitTransportAuth.bearer(token)


def select_transport(org_url: str, *, transport: str | None = None):
    """Explicit transport name wins; otherwise detect the single documented
    Gateway marker (HTTP_PROXY/HTTPS_PROXY, injected by the Gateway sandbox)."""
    if transport is None:
        transport = "gateway" if _gateway_marker_present() else "cli"
    if transport == "gateway":
        return GatewayCurlTransport(org_url)
    if transport == "cli":
        return AzureCliTransport(org_url)
    raise ReviewError(f"Unknown ADO transport '{transport}'; expected 'gateway' or 'cli'.")


def _merge_sha_from_payload(pr: dict) -> str | None:
    """Extract the PR's merge-commit SHA from an already-fetched raw PR
    payload (no network call) -- the same payload fetch_pr()/resolve_pr()
    used, so the returned merge SHA and iteration both come from one
    observed PR state instead of two separate GETs of the same endpoint."""
    merge_commit = pr.get("lastMergeCommit")
    commit_id = merge_commit.get("commitId") if isinstance(merge_commit, dict) else None
    if commit_id is None:
        return None
    return validate_sha(commit_id)


# ---------------------------------------------------------------------------
# Context collection (Task 6): work-item traversal, changed-file provenance,
# origin-PR resolution, Markdown rendering -- ported from review_context.py
# (which is left in place, unmodified, deferred deletion per ruling R4/Task
# 9). `fetch_pr`/`resolve_pr` remain imported from there (already used by
# `resolve()` above); everything below is duplicated here rather than
# imported, since Task 9 deletes review_context.py only once this logic has
# its own tests and no longer depends on that module.
# ---------------------------------------------------------------------------


def _event(name, **counts):
    LOGGER.debug(json.dumps({"event": name, **counts}, sort_keys=True))


def _values(value):
    values = value if isinstance(value, list) else value.get("value", []) if isinstance(value, dict) else None
    if not isinstance(values, list):
        raise ReviewError("Provider returned an invalid collection.")
    return values


def _pages(client, path):
    if hasattr(client, "get_pages"):
        return _values(client.get_pages(path))
    return _values(client.request("GET", path))


def _positive_id(value):
    if isinstance(value, bool):
        return None
    text = str(value)
    return int(text) if re.fullmatch(r"[1-9][0-9]*", text) else None


def _commit(value, label, required=True):
    value = value.get("commitId") if isinstance(value, dict) else value
    if value is None and not required:
        return None
    if not isinstance(value, str) or not _SHA.fullmatch(value):
        raise ReviewError(f"PR snapshot has no valid {label} commit.")
    return value.lower()


def _repo_api(snapshot):
    repo = snapshot["repository"]
    return f"{quote(str(repo['project']['id']), safe='')}/_apis/git/repositories/{quote(str(repo['id']), safe='')}"


def _relation_id(relation):
    url = relation.get("url", "")
    if not isinstance(url, str):
        return None
    match = re.search(r"/_apis/wit/workitems/([1-9][0-9]*)/?$", urlsplit(url).path, re.IGNORECASE)
    return int(match[1]) if match else None


def _collect_work_items(client, snapshot, *, max_work_items=2000, max_depth=64):
    """Walk hierarchical parents to roots, then their complete child families."""
    if max_work_items < 1 or max_depth < 1:
        raise ReviewError("Work-item limits must be positive.")
    warnings, missing, nodes, attempted = [], set(), {}, set()

    def warn(message):
        if message not in warnings:
            warnings.append(message)

    try:
        refs = _pages(client, f"{_repo_api(snapshot)}/pullRequests/{snapshot['pr_number']}/workitems?{_API}")
        linked = sorted({number for ref in refs if isinstance(ref, dict) and (number := _positive_id(ref.get("id"))) is not None})
        if len(linked) != len({str(ref.get("id")) for ref in refs if isinstance(ref, dict)}):
            warn("Some linked work-item references contain invalid IDs.")
    except ReviewError:
        return {"linked_ids": [], "roots": [], "nodes": [], "edges": [], "missing_ids": [],
                "warnings": ["Could not fetch PR-linked work items."], "complete": False}

    def fetch(ids):
        pending = sorted(set(ids) - attempted)
        capacity = max_work_items - len(attempted)
        if len(pending) > capacity:
            warn(f"Work-item limit {max_work_items} reached; hierarchy is partial.")
            missing.update(pending[max(0, capacity):])
            pending = pending[:max(0, capacity)]
        for offset in range(0, len(pending), 200):
            batch = pending[offset:offset + 200]
            attempted.update(batch)
            try:
                response = _values(client.request("POST", f"_apis/wit/workitemsbatch?{_API}",
                                                  {"ids": batch, "$expand": "relations", "errorPolicy": "Omit"}))
            except ReviewError:
                missing.update(batch)
                warn("A work-item batch could not be fetched; requested IDs are recorded as missing.")
                continue
            returned = set()
            for raw in response:
                number = _positive_id(raw.get("id")) if isinstance(raw, dict) else None
                if number not in batch:
                    warn("Provider returned an unexpected work-item ID.")
                    continue
                fields = raw.get("fields") or {}
                if not isinstance(fields, dict) or not isinstance(raw.get("relations", []), list):
                    warn(f"Work item {number} has malformed fields or relations.")
                    missing.add(number)
                    continue
                returned.add(number)
                nodes[number] = {"id": number, "revision": raw.get("rev"),
                                 "type": fields.get("System.WorkItemType", "Unknown"),
                                 "title": fields.get("System.Title", ""), "state": fields.get("System.State", ""),
                                 "description": fields.get("System.Description", ""),
                                 "acceptance_criteria": fields.get("Microsoft.VSTS.Common.AcceptanceCriteria", ""),
                                 "assigned_to": fields.get("System.AssignedTo"), "project": fields.get("System.TeamProject"),
                                 "url": ((raw.get("_links") or {}).get("html") or {}).get("href") or raw.get("url", ""),
                                 "relations": raw.get("relations", [])}
            if set(batch) - returned:
                missing.update(set(batch) - returned)
                warn("Some requested work items were omitted (missing, deleted, or inaccessible).")

    def relatives(number, direction):
        found = []
        for rel in nodes[number]["relations"]:
            if not isinstance(rel, dict):
                warn(f"Work item {number} has an invalid relation.")
            elif rel.get("rel") == f"System.LinkTypes.Hierarchy-{direction}":
                other = _relation_id(rel)
                if other is None:
                    warn(f"Work item {number} has a hierarchy relation with no usable ID.")
                else:
                    found.append(other)
        return sorted(set(found))

    roots = set()
    frontier = [(number, ()) for number in linked]
    ancestry_seen = set()
    while frontier:
        fetch(number for number, _ in frontier)
        following = []
        for number, ancestors in frontier:
            if number not in nodes:
                if ancestors:
                    roots.add(ancestors[-1])
                continue
            if number in ancestors:
                warn("A parent hierarchy cycle was detected.")
                roots.add(number)
                continue
            if number in ancestry_seen:
                continue
            ancestry_seen.add(number)
            if str(nodes[number]["type"]).casefold() == "feature":
                roots.add(number)
                continue
            parents = relatives(number, "Reverse")
            if not parents:
                roots.add(number)
            elif len(ancestors) >= max_depth:
                warn(f"Parent hierarchy depth limit {max_depth} reached.")
                roots.add(number)
            else:
                if len(parents) > 1:
                    warn(f"Work item {number} has multiple parents; all known edges are retained.")
                following.extend((parent, ancestors + (number,)) for parent in parents)
        frontier = following

    descendants_seen = set()
    frontier = [(number, 0) for number in sorted(roots)]
    while frontier:
        fetch(number for number, _ in frontier)
        following = []
        for number, depth in frontier:
            if number not in nodes or number in descendants_seen:
                continue
            descendants_seen.add(number)
            children = relatives(number, "Forward")
            if children and depth >= max_depth:
                warn(f"Child hierarchy depth limit {max_depth} reached.")
                missing.update(set(children) - set(nodes))
            else:
                following.extend((child, depth + 1) for child in children)
        frontier = following

    edges = set()
    extra_relations = []
    for number, node in nodes.items():
        for relation in node.pop("relations"):
            if not isinstance(relation, dict):
                continue
            rel, other = relation.get("rel", ""), _relation_id(relation)
            if rel == "System.LinkTypes.Hierarchy-Forward" and other is not None:
                edges.add((number, other, "child"))
            elif rel == "System.LinkTypes.Hierarchy-Reverse" and other is not None:
                edges.add((other, number, "child"))
            elif other is not None:
                edges.add((number, other, "related" if rel == "System.LinkTypes.Related" else rel))
            elif rel not in ("System.LinkTypes.Hierarchy-Forward", "System.LinkTypes.Hierarchy-Reverse"):
                extra_relations.append({"source": number, "kind": rel, "url": relation.get("url", "")})

    # DFS over child edges catches malformed downward cycles even if ancestry stopped at a Feature.
    graph = {}
    for parent, child, kind in edges:
        if kind == "child" and parent in nodes and child in nodes:
            graph.setdefault(parent, []).append(child)
    color = {}
    for initial in nodes:
        stack = [(initial, False)]
        while stack:
            number, exiting = stack.pop()
            if exiting:
                color[number] = 2
                continue
            if color.get(number) == 1:
                warn("A child hierarchy cycle was detected.")
                continue
            if color.get(number) == 2:
                continue
            color[number] = 1
            stack.append((number, True))
            stack.extend((child, False) for child in graph.get(number, []))
    _event("work_item_family_collected", nodes=len(nodes), roots=len(roots), missing=len(missing))
    return {"linked_ids": linked, "roots": sorted(roots), "nodes": [nodes[key] for key in sorted(nodes)],
            "edges": [{"source": a, "target": b, "kind": kind} for a, b, kind in sorted(edges)] + extra_relations,
            "missing_ids": sorted(missing), "warnings": warnings, "complete": not warnings}


def _merge_ranges(ranges):
    merged = []
    for start, end in sorted(ranges):
        if start > end or start < 1:
            continue
        if merged and start <= merged[-1][1] + 1:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return merged


def _parse_diff_ranges(patch):
    old_ranges, added = [], []
    old_line = new_line = None
    for line in patch.split("\n"):
        match = _HUNK.match(line)
        if match:
            old_line, old_count = int(match[1]), int(match[2] if match[2] is not None else 1)
            new_line = int(match[3])
            if old_count:
                old_ranges.append([old_line, old_line + old_count - 1])
            continue
        if old_line is None:
            continue
        if line.startswith("+"):
            added.append([new_line, new_line])
            new_line += 1
        elif line.startswith("-"):
            old_line += 1
        elif line.startswith(" "):
            old_line += 1
            new_line += 1
    return _merge_ranges(old_ranges), _merge_ranges(added)


def _git_path(value):
    if not value.startswith('"'):
        return value
    # Git's C quoting escapes UTF-8 bytes as octal, not Unicode code points.
    output = bytearray()
    inner = value[1:-1]
    index = 0
    escapes = {"a": 7, "b": 8, "t": 9, "n": 10, "v": 11, "f": 12, "r": 13, '"': 34, "\\": 92}
    while index < len(inner):
        if inner[index] != "\\":
            output.extend(inner[index].encode("utf-8", "surrogateescape"))
            index += 1
            continue
        index += 1
        octal = re.match(r"[0-7]{1,3}", inner[index:])
        if octal:
            output.append(int(octal[0], 8))
            index += len(octal[0])
        else:
            char = inner[index]
            output.extend(bytes([escapes[char]]) if char in escapes else char.encode())
            index += 1
    return output.decode("utf-8", "surrogateescape")


def _parse_blame(text, shallow):
    records, current = [], None
    for line in text.split("\n"):
        header = _BLAME_HEADER.match(line)
        if header:
            current = {"commit": header[1], "original_line": int(header[2]), "base_line": int(header[3]),
                       "shallow_boundary": header[1] in shallow}
        elif current is not None and line.startswith("\t"):
            # The line's source text is already in the checkout; don't duplicate it in every report.
            records.append(current)
            current = None
        elif current is not None:
            key, _, value = line.partition(" ")
            if key in ("author", "author-time", "summary"):
                current[key.replace("-", "_")] = value
            elif key == "filename":
                current["original_path"] = _git_path(value)
            elif key == "boundary":
                current["boundary"] = True
    return records


def _collect_file_changes(source_root, base_sha, head_sha, *, current_pr, context_lines=3):
    """Collect per-file changed ranges and historical blame without altering Git state."""
    source_root = Path(source_root)
    base_sha, head_sha = _commit(base_sha, "base"), _commit(head_sha, "head")
    if context_lines < 0 or context_lines > 100:
        raise ReviewError("Context line count must be between 0 and 100.")
    actual = run_git(source_root, ["rev-parse", "--show-toplevel"]).stdout.strip()
    if Path(actual).resolve() != source_root.resolve():
        raise ReviewError("Source path is not its own Git worktree (possibly an uninitialized submodule).")
    tokens = run_git(source_root, ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M", base_sha, head_sha, "--"]).stdout.split("\0")
    shallow_path = run_git(source_root, ["rev-parse", "--git-path", "shallow"]).stdout.strip()
    shallow_file = Path(shallow_path)
    if not shallow_file.is_absolute():
        shallow_file = source_root / shallow_file
    shallow = set(shallow_file.read_text().splitlines()) if shallow_file.is_file() else set()
    files, warnings, historical = [], [], set()
    index = 0
    while index < len(tokens) and tokens[index]:
        status = tokens[index]
        index += 1
        if index >= len(tokens) or not tokens[index]:
            raise ReviewError("Malformed NUL-delimited Git diff output.")
        old_path = path = tokens[index]
        index += 1
        if status[0] in "RC":
            if index >= len(tokens) or not tokens[index]:
                raise ReviewError("Malformed Git rename/copy record.")
            path = tokens[index]
            index += 1
        for filename in (old_path, path):
            if filename.startswith("/") or ".." in Path(filename).parts:
                raise ReviewError("Git diff contains an unsafe repository path.")
        entry = {"file_id": hashlib.sha256((status + "\0" + old_path + "\0" + path).encode("utf-8", "surrogateescape")).hexdigest()[:16],
                 "status": status, "path": path, "old_path": old_path,
                 "base_ranges": [], "added_ranges": [], "added_lines_pr": current_pr,
                 "blame": [], "provenance_status": "complete"}
        paths = [old_path] if old_path == path else [old_path, path]
        modes = []
        for commit, filename in ((base_sha, old_path), (head_sha, path)):
            tree = run_git(source_root, ["--literal-pathspecs", "ls-tree", "-z", commit, "--", filename]).stdout
            modes.append(tree.split(" ", 1)[0] if tree else None)
        if "160000" in modes or "120000" in modes:
            entry["provenance_status"] = "not-applicable-submodule" if "160000" in modes else "not-applicable-symlink"
        else:
            patch = run_git(source_root, ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "-M", f"-U{context_lines}", base_sha, head_sha, "--", *paths]).stdout
            if "\nBinary files " in patch or "\nGIT binary patch" in patch:
                entry["provenance_status"] = "not-applicable-binary"
            else:
                entry["base_ranges"], entry["added_ranges"] = _parse_diff_ranges(patch)
                for start, end in entry["base_ranges"]:
                    result = run_git(source_root, ["blame", "--no-textconv", "--line-porcelain", "-M", "-C", "-L", f"{start},{end}", base_sha, "--", old_path], check=False)
                    if result.returncode:
                        entry["provenance_status"] = "partial-blame-failed"
                        warnings.append(f"Blame failed for file {entry['file_id']} range {start}-{end}.")
                        continue
                    records = _parse_blame(result.stdout, shallow)
                    if len(records) != end - start + 1:
                        entry["provenance_status"] = "partial-blame-output"
                        warnings.append(f"Blame returned incomplete line records for file {entry['file_id']}.")
                    entry["blame"].extend(records)
                    if any(record["shallow_boundary"] for record in records):
                        entry["provenance_status"] = "partial-shallow-history"
                        warnings.append(f"Shallow history limits provenance for file {entry['file_id']}; deepen history to complete it.")
                    historical.update(record["commit"] for record in records if not record["shallow_boundary"] and record["commit"] != "0" * 40)
                if not entry["base_ranges"]:
                    entry["provenance_status"] = "current-pr-only" if entry["added_ranges"] else "not-applicable-no-changed-lines"
        files.append(entry)
    _event("changed_regions_collected", files=len(files), commits=len(historical), warnings=len(warnings))
    return {"files": files, "historical_commits": sorted(historical), "warnings": warnings, "complete": not warnings}


def _resolve_origin_prs(client, snapshot, commits, *, max_commits=500):
    """Resolve all associated PRs, without pretending association is a unique origin."""
    if max_commits < 1:
        raise ReviewError("Commit query limit must be positive.")
    unique = sorted({_commit(commit, "blamed") for commit in commits})
    mappings = {commit: {"pr_ids": [], "status": "not-queried-limit"} for commit in unique}
    warnings, pull_requests = [], {}
    if len(unique) > max_commits:
        warnings.append(f"Commit query limit {max_commits} reached; provenance is partial.")
    selected = unique[:max_commits]
    for offset in range(0, len(selected), 100):
        batch = selected[offset:offset + 100]
        try:
            response = client.request("POST", f"{_repo_api(snapshot)}/pullrequestquery?{_API}",
                                      {"queries": [{"type": kind, "items": batch} for kind in ("commit", "lastMergeCommit")]})
            results = response.get("results") if isinstance(response, dict) else None
            if not isinstance(results, list) or len(results) != 2 or any(not isinstance(value, dict) for value in results):
                raise ReviewError("Provider returned an invalid commit-to-PR query response.")
            for commit in batch:
                matches = set()
                for query_result in results:
                    rows = query_result.get(commit, [])
                    if not isinstance(rows, list):
                        raise ReviewError("Provider returned an invalid commit association list.")
                    for row in rows:
                        number = _positive_id(row.get("pullRequestId")) if isinstance(row, dict) else None
                        if number is None:
                            raise ReviewError("Provider returned an invalid associated PR ID.")
                        matches.add(number)
                mappings[commit] = {"pr_ids": sorted(matches), "status": "associated" if matches else "no-associated-pr"}
        except ReviewError:
            warnings.append("A commit-to-PR query failed; association status is unknown for that batch.")
            for commit in batch:
                mappings[commit]["status"] = "query-failed"
    numbers = sorted({number for mapping in mappings.values() for number in mapping["pr_ids"]})
    for number in numbers:
        try:
            raw = client.request("GET", f"{_repo_api(snapshot)}/pullRequests/{number}?{_API}")
            if not isinstance(raw, dict) or _positive_id(raw.get("pullRequestId")) != number:
                raise ReviewError("Historical PR response has a mismatched ID.")
            pull_requests[str(number)] = {"id": number, "title": raw.get("title", ""), "description": raw.get("description", ""),
                                          "status": raw.get("status", "unknown"), "author": raw.get("createdBy", {}),
                                          "url": ((raw.get("_links") or {}).get("web") or {}).get("href") or raw.get("url", ""),
                                          "closed_date": raw.get("closedDate")}
        except ReviewError:
            warnings.append(f"Historical PR {number} metadata is unavailable.")
            pull_requests[str(number)] = {"id": number, "status": "metadata-unavailable"}
    _event("origin_prs_collected", commits=len(unique), pull_requests=len(pull_requests), warnings=len(warnings))
    return {"commits": mappings, "pull_requests": pull_requests, "warnings": warnings, "complete": not warnings}


def _build_context_report(client, snapshot, source_root, base_sha, *, context_lines=3, max_work_items=2000, max_depth=64, max_commits=500):
    work_items = _collect_work_items(client, snapshot, max_work_items=max_work_items, max_depth=max_depth)
    changes = _collect_file_changes(source_root, base_sha, snapshot["head_sha"], current_pr=snapshot["pr_number"], context_lines=context_lines)
    provenance = _resolve_origin_prs(client, snapshot, changes["historical_commits"], max_commits=max_commits)
    for entry in changes["files"]:
        entry["commits"] = sorted({line["commit"] for line in entry["blame"] if not line["shallow_boundary"]})
        entry["origin_pr_ids"] = sorted({number for commit in entry["commits"]
                                        for number in provenance["commits"].get(commit, {}).get("pr_ids", [])})
    return {"snapshot": snapshot, "base_sha": base_sha, "collected_at": datetime.now(timezone.utc).isoformat(),
            "work_items": work_items, "files": changes["files"], "provenance": provenance,
            "warnings": work_items["warnings"] + changes["warnings"] + provenance["warnings"],
            "complete": work_items["complete"] and changes["complete"] and provenance["complete"]}


def _text(value):
    """Keep provider text inert in Markdown, including embedded HTML and newlines."""
    value = html.escape(str(value or ""), quote=True)
    for char in ("\\", "`", "*", "_", "[", "]", "#", "|"):
        value = value.replace(char, "\\" + char)
    return value.replace("\r", " ").replace("\n", " ")


def render_context_markdown(report: dict) -> str:
    """Ported verbatim from review_context.py's render_markdown()."""
    snapshot = report.get("snapshot") or report.get("pr") or {}
    repo = snapshot.get("repository", {})
    lines = [f"# Review setup: {_text(repo.get('name'))} #{snapshot.get('pr_number', '')}", "",
             f"**Collection:** {'complete' if report.get('complete') else 'partial / requires attention'}", "",
             f"**Title:** {_text(snapshot.get('title'))}", f"**PR status:** {_text(snapshot.get('status'))}",
             f"**Iteration:** {snapshot.get('iteration_id', '')}",
             f"**Source:** {_text(snapshot.get('source_ref'))} — {_text(snapshot.get('head_sha'))}",
             f"**Target:** {_text(snapshot.get('target_ref'))} — {_text(snapshot.get('target_sha'))}",
             f"**Base:** {_text(report.get('base_sha') or snapshot.get('base_sha'))}", "",
             "## Description", "", _text(snapshot.get("description")), "", "## Work-item family", ""]
    family = report.get("work_items", {})
    nodes = {node["id"]: node for node in family.get("nodes", [])}
    children = {}
    for edge in family.get("edges", []):
        if edge.get("kind") == "child":
            children.setdefault(edge["source"], []).append(edge["target"])
    if not family.get("linked_ids", []) and not nodes:
        lines.append("No linked work items were returned; check collection warnings if incomplete.")
    seen = set()
    stack = [(root, 0) for root in reversed(family.get("roots", []))]
    while stack:
        number, depth = stack.pop()
        node = nodes.get(number)
        prefix = "  " * min(depth, 64) + "- "
        if number in seen:
            lines.append(prefix + f"#{number} (already shown / shared edge)")
            continue
        seen.add(number)
        if node is None:
            lines.append(prefix + f"#{number} (missing or inaccessible)")
            continue
        lines.append(prefix + f"#{number} {_text(node.get('type'))}: {_text(node.get('title'))} [{_text(node.get('state'))}]")
        stack.extend((child, depth + 1) for child in reversed(sorted(set(children.get(number, [])))))
    lines.extend(["", "## Work-item details", ""])
    for number, node in sorted(nodes.items()):
        lines.extend([f"### #{number}: {_text(node.get('title'))}", "",
                      f"Revision: {_text(node.get('revision'))}; project: {_text(node.get('project'))}", "",
                      _text(node.get("description")), "", "Acceptance criteria: " + _text(node.get("acceptance_criteria")), ""])
    lines.extend(["## Changed-file provenance", ""])
    associations = report.get("provenance", {}).get("commits", {})
    for entry in report.get("files", []):
        lines.extend([f"### {_text(entry['path'])}", "",
                      f"Change: {_text(entry['status'])}; provenance: {_text(entry['provenance_status'])}",
                      f"Base ranges: {_text(entry.get('base_ranges'))}; added ranges: {_text(entry.get('added_ranges'))}",
                      f"Added lines belong to current PR #{entry.get('added_lines_pr', '')}."])
        for commit in sorted({record["commit"] for record in entry.get("blame", [])}):
            association = associations.get(commit, {})
            numbers = association.get("pr_ids", [])
            detail = ", ".join(f"PR #{number}" for number in numbers) if numbers else association.get("status", "incomplete-history")
            lines.append(f"- {commit}: {_text(detail)}")
        lines.append("")
    lines.extend(["## Historical PR descriptions", ""])
    for number, pr in sorted(report.get("provenance", {}).get("pull_requests", {}).items(), key=lambda row: int(row[0])):
        lines.extend([f"### PR #{number}: {_text(pr.get('title'))}", "", _text(pr.get("description") or pr.get("status")), ""])
    if report.get("warnings"):
        lines.extend(["## Warnings / missing evidence", ""])
        lines.extend("- " + _text(warning) for warning in report["warnings"])
    return "\n".join(lines).rstrip() + "\n"


class PullRequestNotActive(ReviewError):
    """Confirmed terminal ADO PR; callers may skip it without preparing a slot."""

    def __init__(self, pr: int, status: str):
        super().__init__(
            f"PR_NOT_ACTIVE: ADO PR {pr} is {status}; skip this PR. "
            "No Git preparation was attempted.",
            code=4,
        )
        self.pr = pr
        self.status = status


class AdoProvider:
    """Azure DevOps provider: resolves a PR to a provider-neutral identity
    and supplies Git transport auth. Never fetches Git."""

    def __init__(self, org_url: str, *, transport: str | None = None):
        self._transport = select_transport(org_url, transport=transport)

    def resolve(self, pr: int, repo_hint: str | None = None) -> ResolvedPullRequest:
        pr_payload = fetch_pr(self._transport, pr)
        status = pr_payload[1].get("status")
        if status in ("completed", "abandoned"):
            repository = pr_payload[1].get("repository") or {}
            if repo_hint is not None and repo_hint.casefold() != str(repository.get("name", "")).casefold():
                raise ReviewError("Requested repository does not match the resolved PR; refusing to skip a different repository.")
            raise PullRequestNotActive(pr_payload[0], status)
        snapshot = resolve_pr(self._transport, pr, pr_payload=pr_payload)
        repository = snapshot["repository"]
        if repo_hint is not None and repo_hint.casefold() != repository["name"].casefold():
            raise ReviewError(
                f"Requested repository '{repo_hint}' does not match the resolved PR's "
                f"repository '{repository['name']}'; refusing to substitute a different repository."
            )
        merge_sha = _merge_sha_from_payload(pr_payload[1])
        return ResolvedPullRequest(
            provider="ado",
            pr=snapshot["pr_number"],
            repo=repository["name"],
            remote_url=repository["url"],
            merge_ref=f"refs/pull/{snapshot['pr_number']}/merge",
            merge_sha=merge_sha,
            source_branch=snapshot["source_ref"],
            target_branch=snapshot["target_ref"],
            iteration=snapshot["iteration_id"],
        )

    def git_auth(self) -> GitTransportAuth | None:
        return self._transport.git_auth()

    def collect_context(self, resolved: ResolvedPullRequest, worktree: Path) -> dict:
        """Read-only context collection bound to `resolved.merge_sha`/
        `resolved.iteration`. Never mutates `worktree` (only Git-reads
        against its already-fetched object database via review_common's
        `run_git`, e.g. `diff`/`blame`/`ls-tree` -- no `checkout`/`reset`/
        `fetch`). Raises `PullRequestMoved` if the provider's iteration or
        merge commit has changed since `resolve()`/`prepare()` last observed
        it (checked once before collection, and again after, to also catch
        drift that happens while collection is in flight)."""
        pre_payload = fetch_pr(self._transport, resolved.pr)
        pre_snapshot = resolve_pr(self._transport, resolved.pr, iteration=resolved.iteration, pr_payload=pre_payload)
        pre_merge_sha = _merge_sha_from_payload(pre_payload[1])
        if pre_snapshot["iteration_id"] != resolved.iteration or pre_merge_sha != resolved.merge_sha:
            raise PullRequestMoved(
                f"PR {resolved.pr} has moved since it was resolved/prepared (iteration "
                f"{resolved.iteration} -> {pre_snapshot['iteration_id']}, merge_sha "
                f"{resolved.merge_sha} -> {pre_merge_sha}); re-resolve and re-prepare before "
                f"collecting context."
            )
        report = _build_context_report(self._transport, pre_snapshot, worktree, pre_snapshot.get("base_sha"))
        post_payload = fetch_pr(self._transport, resolved.pr)
        post_snapshot = resolve_pr(self._transport, resolved.pr, iteration=resolved.iteration, pr_payload=post_payload)
        post_merge_sha = _merge_sha_from_payload(post_payload[1])
        if (
            post_snapshot["iteration_id"] != pre_snapshot["iteration_id"]
            or post_snapshot["snapshot_fingerprint"] != pre_snapshot["snapshot_fingerprint"]
            or post_merge_sha != pre_merge_sha
        ):
            raise PullRequestMoved(
                f"PR {resolved.pr} changed during context collection (iteration/merge commit no "
                f"longer match what collection started with); re-resolve and re-prepare before "
                f"trusting this report."
            )
        return report


def _resolve_org_url(explicit_org: str | None) -> str:
    if explicit_org:
        return explicit_org
    root = discover_root(None)
    modules = read_submodules(root, read_only=True)
    organizations = sorted({module["org_url"] for module in modules if module["provider"] == "ado"})
    if len(organizations) != 1:
        raise ReviewError(
            "Could not determine a single Azure DevOps organization from .gitmodules; pass --org explicitly."
        )
    return organizations[0]


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--transport", choices=("auto", "gateway", "cli"), default="auto",
        help="ADO transport: 'gateway' (Gateway-injected curl auth), 'cli' "
             "(Azure CLI via WSL), or 'auto' (Gateway-marker detected).",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    resolve_parser = subparsers.add_parser("resolve", help="Resolve a PR to a provider-neutral identity (read-only).")
    resolve_parser.add_argument("pr", type=int)
    resolve_parser.add_argument("--repo")
    resolve_parser.add_argument("--org")

    context_parser = subparsers.add_parser(
        "context",
        help="Collect read-only review context for a PR against its already-prepared slot worktree.",
    )
    context_parser.add_argument("pr", type=int)
    context_parser.add_argument("--repo", required=True)
    context_parser.add_argument("--slot", type=int, required=True)
    context_parser.add_argument("--root")
    context_parser.add_argument("--org")

    return parser


def _run_resolve(args: argparse.Namespace) -> int:
    org_url = _resolve_org_url(args.org)
    transport = None if args.transport == "auto" else args.transport
    provider = AdoProvider(org_url, transport=transport)
    print(f"Resolving PR {args.pr}...", file=sys.stderr)
    resolved = provider.resolve(args.pr, repo_hint=args.repo)
    print(f"Resolved PR {args.pr} -> {resolved.repo} (iteration {resolved.iteration}).", file=sys.stderr)
    print(json.dumps(resolved.to_public_dict(), indent=2, sort_keys=True))
    return 0


def _run_context(args: argparse.Namespace) -> int:
    """Read-only manual diagnostic: collect context for `args.pr` against the
    worktree an earlier `review_git_prepare.py` run already prepared for
    `--repo`/`--slot`. Never calls `prepare()` itself and never mutates the
    worktree -- if no matching prepared-slot manifest exists (or it names a
    different PR), this refuses rather than preparing or guessing one."""
    root = discover_root(args.root)
    org_url = _resolve_org_url(args.org)
    transport = None if args.transport == "auto" else args.transport
    provider = AdoProvider(org_url, transport=transport)
    print(f"Resolving PR {args.pr}...", file=sys.stderr)
    resolved = provider.resolve(args.pr, repo_hint=args.repo)

    state = ReviewState(root, read_only=True)
    modules = read_submodules(root, read_only=True)
    matches = [m for m in modules if canonical_repo_name(m).casefold() == resolved.repo.casefold()]
    if len(matches) != 1:
        raise ReviewError(f"Could not find exactly one configured source submodule named '{resolved.repo}'.")
    pool = SourcePool(state, matches[0])

    manifest_path = state.prepared_slot_path(pool.repo_key, args.slot)
    if manifest_path.is_symlink():
        raise ReviewError("Refusing symlink prepared-slot manifest.")
    if not manifest_path.is_file():
        raise ReviewError(
            f"No prepared-slot manifest for {resolved.repo} slot {args.slot}; run "
            f"review_git_prepare.py for this PR before collecting context."
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (
        not isinstance(manifest, dict)
        or manifest.get("schema") != 1
        or manifest.get("repo") != pool.repo_name
        or manifest.get("slot") != args.slot
    ):
        raise ReviewError("Prepared-slot manifest has an unexpected shape for this slot.")
    if manifest.get("pr") != resolved.pr:
        raise ReviewError(
            f"Prepared slot {args.slot} holds PR {manifest.get('pr')}, not the requested PR "
            f"{resolved.pr}; re-run review_git_prepare.py for this PR before collecting context."
        )

    worktree = safe_path(root, f"{pool.slot_relative_path(args.slot)}/{pool.module['path']}")
    report = provider.collect_context(resolved, worktree)
    print(json.dumps(report, indent=2, sort_keys=True, default=str))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    try:
        preflight()
        if args.command == "resolve":
            return _run_resolve(args)
        if args.command == "context":
            return _run_context(args)
        raise ReviewError(f"Unknown command '{args.command}'.")
    except ReviewError as exc:
        print(f"ADO provider stopped: {exc}", file=sys.stderr)
        return exc.code


if __name__ == "__main__":
    raise SystemExit(main())
