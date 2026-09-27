#!/usr/bin/env python3
"""Collect review-host usage without changing the daemon DB or posting to a PR."""
import argparse
import datetime
import decimal
import fcntl
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

TOKENS = (
    "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens",
    "reasoningTokens", "totalTokens", "attemptCount",
)
COSTS = ("estimatedPublicCostMicros", "providerReportedCostMicros", "preferredCostMicros")
USAGE_STATES = {0: "inProgress", 1: "partial", 2: "complete"}
COST_STATES = {0: "unavailable", 1: "partial", 2: "complete"}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def enum_name(value, names):
    if value in names:
        return names[value]
    if isinstance(value, str):
        for name in names.values():
            if value.lower() == name.lower():
                return name
    raise ValueError("Unknown usage schema enum")


def integer(value):
    if type(value) is not int or value < 0:
        raise ValueError("Invalid usage counter")
    return value


def validate_usage(data, thread):
    if data.get("schemaVersion") != 1 or data.get("rootConversationId") != thread:
        raise ValueError("Usage identity/schema mismatch")
    if data.get("currency") != "USD":
        raise ValueError("Unsupported currency; refusing mixed-money totals")
    integer(data["foldedRevision"])
    data["completeness"] = enum_name(data["completeness"], USAGE_STATES)
    data["estimatedCostCompleteness"] = enum_name(data["estimatedCostCompleteness"], COST_STATES)
    models = set()
    data["accountingWarnings"] = []
    for row in data["perModel"]:
        model = row["modelId"]
        if not isinstance(model, str) or not model or model in models:
            raise ValueError("Invalid/duplicate model identity")
        models.add(model)
        for field in TOKENS:
            integer(row[field])
        for field in COSTS:
            if row.get(field) is not None:
                integer(row[field])
        row["estimatedCostCompleteness"] = enum_name(row["estimatedCostCompleteness"], COST_STATES)
        if row["totalTokens"] != row["inputTokens"] + row["cacheWriteTokens"] + row["outputTokens"]:
            raise ValueError("Token accounting mismatch")
        if row["cacheReadTokens"] > row["inputTokens"] or row["reasoningTokens"] > row["outputTokens"]:
            data["accountingWarnings"].append({"modelId": model, "code": "tokenSubsetMismatch", "detail": "Host counters violate normalized subset semantics; preserved without correction"})
    if integer(data["totalTokens"]) != sum(row["totalTokens"] for row in data["perModel"]):
        raise ValueError("Aggregate token mismatch")
    for field in COSTS:
        if data.get(field) is not None:
            integer(data[field])
    return data


def fetch(opener, base, headers, path):
    request = urllib.request.Request(base + path, headers=headers)
    with opener.open(request, timeout=30) as response:
        body = response.read(4 * 1024 * 1024 + 1)
    if len(body) > 4 * 1024 * 1024:
        raise ValueError("Usage response too large")
    return json.loads(body)


def load_runs(database):
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        runs = [dict(row) for row in db.execute("""
            SELECT run.id AS runId, repo.normalized_key AS repository, run.pr_id AS prId,
                run.head_sha AS headSha, run.base_sha AS baseSha, run.generation,
                run.variant_id AS variantId, run.workflow_status AS workflowStatus
            FROM review_run run JOIN repo ON repo.id = run.repo_id ORDER BY run.id
        """)]
        bindings = list(db.execute("""
            SELECT review_run_id, payload FROM review_artifact
            WHERE artifact_kind LIKE 'workflow-session:%' ORDER BY id
        """))
    by_id = {run["runId"]: run for run in runs}
    owners = {}
    for run in runs:
        run["threads"] = []
    for row in bindings:
        thread = json.loads(row["payload"])["ThreadId"]
        if not isinstance(thread, str) or not thread:
            raise ValueError("Invalid root conversation identity")
        owner = row["review_run_id"]
        if thread in owners and owners[thread] != owner:
            raise ValueError("Root conversation belongs to multiple runs; attribution required")
        owners[thread] = owner
        if thread not in by_id[owner]["threads"]:
            by_id[owner]["threads"].append(thread)
    return runs


def known_sum(values):
    present = [value for value in values if value is not None]
    return sum(present) if present else None


def fold(rows):
    result = {field: sum(row[field] for row in rows) for field in TOKENS}
    for field in COSTS:
        result[field] = known_sum([row.get(field) for row in rows])
        # These are known subtotals, not proof that every billed attempt was priced.
    result["estimatedCostCompleteness"] = (
        "unavailable" if result["estimatedPublicCostMicros"] is None else
        "complete" if rows and all(row["estimatedCostCompleteness"] == "complete" for row in rows) else "partial"
    )
    return result


def summarize(sessions):
    groups = {}
    for session in sessions:
        usage = session.get("usage")
        if usage is None:
            continue
        for row in usage["perModel"]:
            groups.setdefault(row["modelId"], []).append(row)
    per_model = [{"modelId": model, **fold(rows)} for model, rows in sorted(groups.items())]
    totals = fold(per_model)
    coverage = bool(sessions) and all(s.get("usage") is not None and s.get("error") is None for s in sessions)
    totals["accountingWarningCount"] = sum(len(s.get("usage", {}).get("accountingWarnings", [])) for s in sessions if s.get("usage"))
    totals["hasRecordedUsage"] = bool(per_model)
    final = coverage and not totals["accountingWarningCount"] and all(s["usage"]["completeness"] == "complete" for s in sessions)
    totals["usageCompleteness"] = "complete" if final else "partialOrInProgress"
    totals["allRootSnapshotsAvailable"] = coverage
    totals["estimatedCostIsComplete"] = final and totals["estimatedCostCompleteness"] == "complete"
    return {"totals": totals, "perModel": per_model}


def atomic_write(path, text):
    if path.is_symlink():
        raise ValueError("Refusing symlink output")
    fd, temporary = tempfile.mkstemp(prefix=".usage-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def money(value):
    return "unknown" if value is None else f"${decimal.Decimal(value) / 1000000:.6f}"


def markdown(report):
    lines = ["# Review usage", "", f"Updated: {report['collectedAt']}", "",
             "Costs below are **known public estimates in USD**, not provider invoices. Missing/stale usage is not zero.",
             "Cache reads are included in input; reasoning is included in output. Total = input + cache write + output.",
             "", "## Runs", "", "| Run | PR | Status | Tokens | Estimate | Fresh roots |",
             "|---|---|---|---:|---:|---:|"]
    for run in report["runs"]:
        totals = run["totals"]
        fresh = sum(s.get("usage") is not None and s.get("error") is None for s in run["sessions"])
        tokens = f"{totals['totalTokens']:,}" if totals["hasRecordedUsage"] else "unknown"
        lines.append(f"| {run['runId']} | {run['prId']} | {run['workflowStatus']} | {tokens} | {money(totals['estimatedPublicCostMicros'])} | {fresh}/{len(run['sessions'])} |")
    lines += ["", "## Cumulative per PR", "", "| Repository | PR | Runs | Tokens | Estimate | Usage complete |", "|---|---|---|---:|---:|---|"]
    for pr in report["pullRequests"]:
        totals = pr["totals"]
        tokens = f"{totals['totalTokens']:,}" if totals["hasRecordedUsage"] else "unknown"
        lines.append(f"| {pr['repository']} | {pr['prId']} | {', '.join(map(str, pr['runIds']))} | {tokens} | {money(totals['estimatedPublicCostMicros'])} | {totals['usageCompleteness']} |")
    for run in report["runs"]:
        lines += ["", f"## Run {run['runId']} — PR {run['prId']} by model", "",
                  "| Model | Input | Output | Cache read* | Cache write | Reasoning* | Attempts | Estimate |",
                  "|---|---:|---:|---:|---:|---:|---:|---:|"]
        for row in run["perModel"]:
            lines.append(f"| {row['modelId']} | {row['inputTokens']:,} | {row['outputTokens']:,} | {row['cacheReadTokens']:,} | {row['cacheWriteTokens']:,} | {row['reasoningTokens']:,} | {row['attemptCount']} | {money(row['estimatedPublicCostMicros'])} |")
        for session in run["sessions"]:
            if session.get("error"):
                lines.append(f"- Unavailable/stale root `{session['threadId']}`: {session['error']}.")
            for warning in (session.get("usage") or {}).get("accountingWarnings", []):
                lines.append(f"- **Accounting warning** for `{warning['modelId']}`: {warning['detail']}.")
        if not run["sessions"]:
            lines.append("- No durable root binding: usage unavailable, not proven zero.")
    return "\n".join(lines) + "\n"


def collect(args, ledger, opener, headers):
    runs = load_runs(args.database)
    now = utc()
    for run in runs:
        run["sessions"] = []
        for thread in run.pop("threads"):
            cached = ledger.execute("SELECT run_id, payload FROM snapshot WHERE thread_id=?", (thread,)).fetchone()
            if cached and cached[0] != run["runId"]:
                raise ValueError("Stored conversation attribution conflict")
            session = json.loads(cached[1]) if cached else {"threadId": thread, "usage": None}
            session["lastCheckedAt"] = now
            try:
                data = validate_usage(fetch(opener, args.base_url, headers, "/api/conversations/" + urllib.parse.quote(thread, safe="") + "/usage"), thread)
                previous = session.get("usage")
                if previous and data["foldedRevision"] < previous["foldedRevision"]:
                    raise ValueError("Usage revision regressed")
                session.update(usage=data, capturedAt=utc(), error=None)
            except urllib.error.HTTPError as error:
                session["error"] = f"HTTP {error.code}"
            except (urllib.error.URLError, TimeoutError, OSError, ValueError, KeyError, TypeError):
                session["error"] = "Usage unavailable or invalid; last good snapshot retained if present"
            ledger.execute("INSERT INTO snapshot VALUES(?,?,?) ON CONFLICT(thread_id) DO UPDATE SET payload=excluded.payload", (thread, run["runId"], json.dumps(session)))
            ledger.commit()
            run["sessions"].append(session)
        run.update(summarize(run["sessions"]))
    prs = {}
    for run in runs:
        key = (run["repository"], run["prId"])
        pr = prs.setdefault(key, {"repository": key[0], "prId": key[1], "runIds": [], "sessions": [], "unboundRunIds": []})
        pr["runIds"].append(run["runId"])
        pr["sessions"].extend(run["sessions"])
        if not run["sessions"]:
            pr["unboundRunIds"].append(run["runId"])
    for pr in prs.values():
        pr.update(summarize(pr.pop("sessions")))
        if pr["unboundRunIds"]:
            pr["totals"].update(usageCompleteness="partialOrInProgress", allRootSnapshotsAvailable=False, estimatedCostIsComplete=False)
    report = {"schemaVersion": 1, "collectedAt": utc(), "currency": "USD", "costAmounts": "integer micro-USD; known subtotals, not invoices", "runs": runs, "pullRequests": list(prs.values())}
    atomic_write(args.output / "review-usage.json", json.dumps(report, indent=2) + "\n")
    atomic_write(args.output / "review-usage.md", markdown(report))
    print(json.dumps({"collectedAt": report["collectedAt"], "runs": len(runs), "pullRequests": len(prs), "missingOrStaleRoots": sum(bool(s.get("error")) for run in runs for s in run["sessions"]), "report": str(args.output / "review-usage.md")}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True, type=Path)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--interval", type=int, default=0, help="Poll every N seconds; 0 collects once, minimum 60")
    args = parser.parse_args()
    args.database = args.database.resolve(strict=True)
    args.base_url = args.base_url.rstrip("/")
    origin = urllib.parse.urlparse(args.base_url)
    if origin.username or origin.password or origin.query or origin.fragment or origin.path not in ("", "/"):
        parser.error("Use a credential-free host origin")
    if origin.scheme != "https" and not (origin.scheme == "http" and origin.hostname in ("localhost", "127.0.0.1", "::1")):
        parser.error("HTTPS required except loopback")
    if args.interval != 0 and args.interval < 60:
        parser.error("Polling interval must be at least 60 seconds")
    headers = {"Accept": "application/json"}
    for env, header in (("REVIEW_USAGE_APP_ID", "X-Sbx-App-Id"), ("REVIEW_USAGE_APP_KEY", "X-Sbx-App-Key"), ("REVIEW_USAGE_S2S_SECRET", "X-S2S-Auth")):
        if os.environ.get(env):
            headers[header] = os.environ[env]
    if "X-Sbx-App-Id" not in headers or "X-Sbx-App-Key" not in headers:
        parser.error("Set REVIEW_USAGE_APP_ID and REVIEW_USAGE_APP_KEY in the environment")
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=True)
    if args.output.is_symlink():
        parser.error("Output directory cannot be a symlink")
    args.output = args.output.resolve()
    args.output.chmod(0o700)
    lock_path = args.output / ".collector.lock"
    if lock_path.is_symlink():
        raise ValueError("Refusing symlink lock")
    with lock_path.open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        os.fchmod(lock.fileno(), 0o600)
        ledger_path = args.output / "usage-snapshots.sqlite"
        if ledger_path.is_symlink():
            raise ValueError("Refusing symlink ledger")
        with sqlite3.connect(ledger_path) as ledger:
            ledger_path.chmod(0o600)
            ledger.execute("CREATE TABLE IF NOT EXISTS source (database_path TEXT NOT NULL, host TEXT NOT NULL)")
            identity = (str(args.database), args.base_url)
            prior = ledger.execute("SELECT database_path,host FROM source").fetchone()
            if prior and prior != identity:
                raise ValueError("Use a separate output directory for another database/host")
            if not prior:
                ledger.execute("INSERT INTO source VALUES(?,?)", identity)
            ledger.execute("CREATE TABLE IF NOT EXISTS snapshot(thread_id TEXT PRIMARY KEY, run_id INTEGER NOT NULL, payload TEXT NOT NULL)")
            ledger.commit()
            opener = urllib.request.build_opener(NoRedirect())
            while True:
                collect(args, ledger, opener, headers)
                if not args.interval:
                    break
                time.sleep(args.interval)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except Exception as error:
        # Never print HTTP exception bodies, headers, or environment values.
        raise SystemExit(f"Usage collection stopped ({type(error).__name__}); existing snapshots preserved")
