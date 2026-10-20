"""Deterministic, read-only Azure DevOps review context and Git provenance."""
from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import html
import json
import logging
from pathlib import Path
import re
from urllib.parse import quote, urlsplit

from review_common import ReviewError, canonical_ado_metadata_url, run_git

LOGGER = logging.getLogger(__name__)
API = "api-version=7.1"
SHA = re.compile(r"^[0-9a-fA-F]{40}$")
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
BLAME_HEADER = re.compile(r"^([0-9a-f]{40}) (\d+) (\d+)(?: (\d+))?$")


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
    if not isinstance(value, str) or not SHA.fullmatch(value):
        raise ReviewError(f"PR snapshot has no valid {label} commit.")
    return value.lower()


def _repo_api(snapshot):
    repo = snapshot["repository"]
    return f"{quote(str(repo['project']['id']), safe='')}/_apis/git/repositories/{quote(str(repo['id']), safe='')}"


def fetch_pr(client, pr_number):
    """Fetch and validate the single raw PR payload for pr_number.

    Exposed so a caller that needs additional raw PR fields beyond what
    resolve_pr()'s snapshot exposes (for example the merge commit) can reuse
    the exact same fetched state resolve_pr() uses, via resolve_pr(...,
    pr_payload=...), instead of issuing a second GET and risking two
    different observed states of a PR that could mutate between calls.
    """
    pr_number = _positive_id(pr_number)
    if pr_number is None:
        raise ReviewError("PR number must be a positive integer.")
    pr = client.request("GET", f"_apis/git/pullrequests/{pr_number}?{API}")
    if not isinstance(pr, dict) or _positive_id(pr.get("pullRequestId")) != pr_number:
        raise ReviewError("Provider returned a different or invalid PR.")
    return pr_number, pr


def resolve_pr(client, pr_number, iteration=None, *, pr_payload=None):
    """Resolve a PR across the organization and pin one historical iteration.

    pr_payload, if given, must be the (pr_number, pr) tuple already returned
    by fetch_pr() for this same pr_number -- letting a caller pin its own
    additional reads of PR fields to this single fetched state rather than
    re-fetching.
    """
    if pr_payload is not None:
        fetched_number, pr = pr_payload
        if fetched_number != _positive_id(pr_number):
            raise ReviewError("Supplied PR payload does not match the requested PR number.")
        pr_number = fetched_number
    else:
        pr_number, pr = fetch_pr(client, pr_number)
    repository = pr.get("repository") or {}
    project = repository.get("project") or {}
    if not all(isinstance(v, str) and v for v in (repository.get("id"), repository.get("name"), project.get("id"), project.get("name"))):
        raise ReviewError("PR response lacks repository/project identity.")
    remote_url = repository.get("remoteUrl") or repository.get("webUrl")
    if not remote_url:
        # REST self links are not clone URLs. Build a clone URL from validated provider coordinates.
        remote_url = f"{client.org_url.rstrip('/')}/{quote(project['name'], safe='')}/_git/{quote(repository['name'], safe='')}"
    repo = {"id": repository["id"], "name": repository["name"],
            "url": canonical_ado_metadata_url(remote_url, client.org_url),
            "project": {"id": project["id"], "name": project["name"]}}
    snapshot = {"pr_number": pr_number, "repository": repo}
    iterations = _pages(client, f"{_repo_api(snapshot)}/pullRequests/{pr_number}/iterations?{API}")
    by_id = {number: value for value in iterations if isinstance(value, dict)
             and (number := _positive_id(value.get("id"))) is not None}
    requested = _positive_id(iteration) if iteration is not None else None
    if iteration is not None and requested is None:
        raise ReviewError("Iteration must be a positive integer.")
    if not by_id or (requested is not None and requested not in by_id):
        raise ReviewError("Requested PR iteration is unavailable; refusing to substitute current branch tips.")
    selected = by_id[requested if requested is not None else max(by_id)]
    source_ref, target_ref = pr.get("sourceRefName"), pr.get("targetRefName")
    # Retargeting can change the PR's current target name after the chosen iteration.
    # SHA anchoring, not these labels, controls checkout and comparison.
    later_retargets = sorted((value for key, value in by_id.items() if key > selected["id"]
                              and value.get("oldTargetRefName")), key=lambda value: value["id"])
    if later_retargets:
        target_ref = later_retargets[0]["oldTargetRefName"]
    if not all(isinstance(ref, str) and ref.startswith("refs/heads/") for ref in (source_ref, target_ref)):
        raise ReviewError("PR response has invalid source/target ref names.")
    snapshot.update({
        "source_ref": source_ref, "target_ref": target_ref,
        "head_sha": _commit(selected.get("sourceRefCommit"), "source"),
        "target_sha": _commit(selected.get("targetRefCommit"), "target"),
        "base_sha": _commit(selected.get("commonRefCommit"), "common ancestor", required=False),
        "iteration_id": selected["id"], "latest_iteration_id": max(by_id),
        "title": pr.get("title", ""), "description": pr.get("description", ""),
        "author": pr.get("createdBy", {}), "status": pr.get("status", "unknown"),
        "url": ((pr.get("_links") or {}).get("web") or {}).get("href") or pr.get("url", ""),
        "has_multiple_merge_bases": bool(pr.get("hasMultipleMergeBases")),
    })
    fingerprint = {key: snapshot[key] for key in ("pr_number", "iteration_id", "head_sha", "target_sha", "base_sha", "source_ref", "target_ref")}
    fingerprint["repository_id"] = repo["id"]
    snapshot["snapshot_fingerprint"] = hashlib.sha256(json.dumps(fingerprint, sort_keys=True).encode()).hexdigest()
    _event("pr_snapshot_resolved", iteration=snapshot["iteration_id"])
    return snapshot


def _relation_id(relation):
    url = relation.get("url", "")
    if not isinstance(url, str):
        return None
    match = re.search(r"/_apis/wit/workitems/([1-9][0-9]*)/?$", urlsplit(url).path, re.IGNORECASE)
    return int(match[1]) if match else None


def collect_work_items(client, snapshot, *, max_work_items=2000, max_depth=64):
    """Walk hierarchical parents to roots, then their complete child families."""
    if max_work_items < 1 or max_depth < 1:
        raise ReviewError("Work-item limits must be positive.")
    warnings, missing, nodes, attempted = [], set(), {}, set()

    def warn(message):
        if message not in warnings:
            warnings.append(message)

    try:
        refs = _pages(client, f"{_repo_api(snapshot)}/pullRequests/{snapshot['pr_number']}/workitems?{API}")
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
                response = _values(client.request("POST", f"_apis/wit/workitemsbatch?{API}",
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
        match = HUNK.match(line)
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
        header = BLAME_HEADER.match(line)
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


def collect_file_changes(source_root, base_sha, head_sha, *, current_pr, context_lines=3):
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


def resolve_origin_prs(client, snapshot, commits, *, max_commits=500):
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
            response = client.request("POST", f"{_repo_api(snapshot)}/pullrequestquery?{API}",
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
            raw = client.request("GET", f"{_repo_api(snapshot)}/pullRequests/{number}?{API}")
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


def collect_context(client, snapshot, source_root, base_sha, *, context_lines=3, max_work_items=2000, max_depth=64, max_commits=500):
    work_items = collect_work_items(client, snapshot, max_work_items=max_work_items, max_depth=max_depth)
    changes = collect_file_changes(source_root, base_sha, snapshot["head_sha"], current_pr=snapshot["pr_number"], context_lines=context_lines)
    provenance = resolve_origin_prs(client, snapshot, changes["historical_commits"], max_commits=max_commits)
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


def render_markdown(report):
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
