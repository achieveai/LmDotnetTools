#!/usr/bin/env python3
"""Local immutable CodeReview release builder and crash-safe activator.

Release ownership is an OS boundary: mode bits prevent accidental modification by the
release owner, but an owner/root process can always replace files. Deploy under a
separate unprivileged account when that distinction is required.
"""
from __future__ import annotations

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import shutil
import signal
import socket
import sqlite3
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Callable

POLICY = "codereview-v2"
MANIFEST_FORMAT_VERSION = 2
VERIFICATION_FORMAT_VERSION = 3
COMMAND_TIMEOUT_SECONDS = 900
SEAL = ".release-seal.json"
EXCLUDED_PARTS = {".git", ".run", ".logs", "bin", "obj", "node_modules"}
# The single supervised handle for the two listeners. Both units declare PartOf= this target,
# so stopping it is the deliberate release of ports 5080/5081 and starting it is the hand-back.
SUPERVISOR_UNIT = "codereview-pair.target"
# Candidate processes are started by this tool rather than the supervisor, so their identity has
# to survive the tool: a crashed activation must still be able to take the ports back from them.
CANDIDATE_PROCESSES = "candidate-processes.json"
PORT_HANDOVER_TIMEOUT_SECONDS = 30.0


def _utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def _json(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False) + "\n").encode()


def _fsync_dir(path: Path) -> None:
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_write(path: Path, value: str | bytes, fault: bool = False, mode: int = 0o600) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    data = value.encode() if isinstance(value, str) else value
    fd, name = tempfile.mkstemp(prefix=path.name + ".tmp-", dir=path.parent)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as stream:
            stream.write(data.rstrip(b"\n") + b"\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
        _fsync_dir(path.parent)
        if fault:
            raise RuntimeError("injected fault after atomic replacement")
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(name)


class CommandRunner:
    """Injectable command boundary; tests can execute fixtures without live services."""

    def __init__(self, execute: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run):
        self.execute = execute

    def run(
        self,
        args: list[str],
        cwd: Path,
        env: dict[str, str] | None = None,
        timeout: int = COMMAND_TIMEOUT_SECONDS,
    ) -> dict[str, Any]:
        started = _utc()
        command_hash = hashlib.sha256(_json(args)).hexdigest()
        try:
            process = self.execute(
                args,
                cwd=cwd,
                env=env,
                text=True,
                capture_output=True,
                check=False,
                timeout=timeout,
                start_new_session=True,
            )
        except subprocess.TimeoutExpired as exc:
            # subprocess.run kills and waits for the direct child. start_new_session also prevents
            # descendants from sharing the release tool's process group.
            raise RuntimeError(f"command timed out after {timeout}s: {' '.join(args)}") from exc
        result = {
            "args": args,
            "commandSha256": command_hash,
            "startedAtUtc": started,
            "finishedAtUtc": _utc(),
            "exitCode": process.returncode,
            "stdoutSha256": hashlib.sha256(process.stdout.encode()).hexdigest(),
            "stderrSha256": hashlib.sha256(process.stderr.encode()).hexdigest(),
            "stdout": process.stdout,
            "stderr": process.stderr,
        }
        if process.returncode:
            raise RuntimeError(f"command failed ({process.returncode}): {' '.join(args)}\n{process.stderr}")
        return result


def run(*args: str, cwd: Path, capture: bool = True) -> str:
    process = subprocess.run(args, cwd=cwd, text=True, capture_output=capture, check=False)
    if process.returncode:
        raise RuntimeError(f"command failed ({process.returncode}): {' '.join(args)}\n{process.stderr}")
    return process.stdout


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_relative(value: str) -> PurePosixPath:
    if not value or "\\" in value or "\0" in value:
        raise RuntimeError(f"noncanonical path: {value!r}")
    path = PurePosixPath(value)
    if path.is_absolute() or value != path.as_posix() or any(part in ("", ".", "..") for part in path.parts):
        raise RuntimeError(f"unsafe path: {value!r}")
    return path


def physical_file(root: Path, relative: str, writable_ok: bool = False) -> Path:
    rel = _canonical_relative(relative)
    root = root.resolve(strict=True)
    current = root
    for part in rel.parts:
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode):
            raise RuntimeError(f"symlink rejected: {relative}")
        if not writable_ok and info.st_mode & 0o222:
            raise RuntimeError(f"writable release entry rejected: {relative}")
    if not stat.S_ISREG(current.lstat().st_mode):
        raise RuntimeError(f"not a regular file: {relative}")
    if current.resolve(strict=True).parent != current.parent.resolve(strict=True):
        raise RuntimeError(f"physical containment failed: {relative}")
    return current


def validate_tree(root: Path, writable_ok: bool = True, exclude: set[str] | None = None) -> list[Path]:
    root_info = root.lstat()
    if stat.S_ISLNK(root_info.st_mode) or not stat.S_ISDIR(root_info.st_mode):
        raise RuntimeError(f"invalid tree root: {root}")
    if not writable_ok and root_info.st_mode & 0o222:
        raise RuntimeError(f"writable release root rejected: {root}")
    seen: set[str] = set()
    files: list[Path] = []
    for directory, names, filenames in os.walk(root, topdown=True, followlinks=False):
        base = Path(directory)
        for name in list(names) + list(filenames):
            path = base / name
            rel = path.relative_to(root).as_posix()
            if exclude and rel in exclude:
                continue
            key = os.path.normcase(rel)
            if key in seen:
                raise RuntimeError(f"path collision: {rel}")
            seen.add(key)
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode):
                raise RuntimeError(f"symlink rejected: {rel}")
            if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
                raise RuntimeError(f"special file rejected: {rel}")
            if not writable_ok and info.st_mode & 0o222:
                raise RuntimeError(f"writable release entry rejected: {rel}")
            if stat.S_ISREG(info.st_mode):
                files.append(path)
    return sorted(files, key=lambda p: p.relative_to(root).as_posix().encode())


def inventory(root: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for path in validate_tree(root):
        rel = path.relative_to(root).as_posix()
        if rel == "source-inventory.json" or any(part in EXCLUDED_PARTS for part in PurePosixPath(rel).parts):
            continue
        info = path.lstat()
        rows.append({"path": rel, "kind": "file", "mode": stat.S_IMODE(info.st_mode), "size": info.st_size, "sha256": sha256(path)})
    return rows


def canonical_hash(rows: list[dict[str, Any]]) -> str:
    return hashlib.sha256(json.dumps(rows, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def copy_dirty(repo: Path, dest: Path) -> None:
    names = run("git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", cwd=repo).split("\0")
    for name in sorted(filter(None, names), key=lambda item: item.encode()):
        rel = _canonical_relative(name)
        src = repo.joinpath(*rel.parts)
        if not src.exists() and not src.is_symlink():
            continue
        if src.is_symlink():
            raise RuntimeError(f"source symlink rejected: {name}")
        if not src.is_file():
            raise RuntimeError(f"source special file rejected: {name}")
        out = dest.joinpath(*rel.parts)
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, out, follow_symlinks=False)


def snapshot(repo: Path, dest: Path, include_dirty: bool) -> dict[str, Any]:
    if dest.exists():
        raise RuntimeError(f"snapshot destination exists: {dest}")
    dest.mkdir(parents=True)
    status_text = run("git", "status", "--porcelain=v1", "--untracked-files=all", cwd=repo)
    base = run("git", "rev-parse", "HEAD", cwd=repo).strip()
    tree = run("git", "rev-parse", "HEAD^{tree}", cwd=repo).strip()
    if status_text and not include_dirty:
        raise RuntimeError("working tree is dirty; pass --include-dirty explicitly")
    if include_dirty:
        copy_dirty(repo, dest)
        kind = "dirty-snapshot"
    else:
        archive = subprocess.Popen(["git", "archive", "--format=tar", base], cwd=repo, stdout=subprocess.PIPE)
        extract = subprocess.run(["tar", "-xf", "-", "-C", str(dest)], stdin=archive.stdout, check=False)
        assert archive.stdout is not None
        archive.stdout.close()
        if archive.wait() or extract.returncode:
            raise RuntimeError("git archive failed")
        kind = "clean-commit"
    rows = inventory(dest)
    dirty_paths = []
    if include_dirty:
        for line in status_text.splitlines():
            value = line[3:]
            if " -> " in value:
                value = value.split(" -> ", 1)[1]
            dirty_paths.append(value.strip('"'))
    metadata = {"formatVersion": 2, "sourceKind": kind, "baseCommit": base, "gitTree": tree, "sourceContentSha256": canonical_hash(rows), "dirtyPaths": sorted(dirty_paths), "inventory": rows}
    atomic_write(dest / "source-inventory.json", _json(metadata), mode=0o644)
    return metadata


def prepare(repo: Path, candidate: Path, include_dirty: bool) -> dict[str, Any]:
    candidate.mkdir(parents=True, exist_ok=False)
    source = candidate / "source"
    metadata = snapshot(repo, source, include_dirty)
    identity = metadata["sourceContentSha256"][:20]
    state = {"formatVersion": 2, "releaseId": identity, "preparedAtUtc": _utc(), "source": metadata}
    atomic_write(candidate / "candidate.json", _json(state))
    return state


def _policy_commands(source: Path, artifacts: Path, trx: Path) -> list[list[str]]:
    projects = [
        "tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj",
        "tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj",
        "tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj",
    ]
    commands = [["dotnet", "restore", "LmDotnetTools.sln", "--artifacts-path", str(artifacts)]]
    commands += [["dotnet", "build", project, "--no-restore", "--artifacts-path", str(artifacts)] for project in projects]
    commands += [["dotnet", "test", project, "--no-build", "--artifacts-path", str(artifacts), "--logger", f"trx;LogFileName={Path(project).stem}.trx", "--results-directory", str(trx)] for project in projects]
    csharpier = shutil.which("csharpier") or str(Path.home() / ".dotnet" / "tools" / "csharpier")
    source_metadata = json.loads((source / "source-inventory.json").read_text())
    changed_csharp = [path for path in source_metadata.get("dirtyPaths", []) if path.endswith(".cs")]
    if changed_csharp:
        commands += [[csharpier, "check", *changed_csharp]]
    commands += [
        ["dotnet", "publish", "samples/LmStreaming.Sample/LmStreaming.Sample.csproj", "--no-restore", "--artifacts-path", str(artifacts), "-o", str(source.parent / "stage" / "host")],
        ["dotnet", "publish", "samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj", "--no-restore", "--artifacts-path", str(artifacts), "-o", str(source.parent / "stage" / "daemon")],
    ]
    return commands


def verify(candidate: Path, policy_path: Path, runner: CommandRunner | None = None) -> dict[str, Any]:
    runner = runner or CommandRunner()
    source = candidate / "source"
    validate_tree(source)
    state = json.loads((candidate / "candidate.json").read_text())
    policy = json.loads(policy_path.read_text())
    policy_hash = sha256(policy_path)
    if policy.get("policyId") != POLICY:
        raise RuntimeError(f"unsupported verification policy: {policy.get('policyId')}")
    artifacts, trx = candidate / "artifacts", candidate / "trx"
    artifacts.mkdir(); trx.mkdir()
    commands = _policy_commands(source, artifacts, trx)
    results: list[dict[str, Any]] = []
    try:
        for command in commands:
            results.append(runner.run(command, source))
        stage = candidate / "stage"
        manifest_path = stage / "manifest.json"
        atomic_write(manifest_path, _json(_provisional_manifest(state, stage, policy_hash)), mode=0o444)
        for component, executable in (("host", "LmStreaming.Sample"), ("daemon", "CodeReviewDaemon.Sample")):
            path = stage / component / executable
            if not path.is_file():
                raise RuntimeError(f"published executable missing: {path}")
            self_check = runner.run(
                [str(path), "--verify-release", str(manifest_path)],
                stage / component,
                env={**os.environ, "ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production"},
                timeout=30,
            )
            try:
                normalized = json.loads(self_check["stdout"])
            except (KeyError, json.JSONDecodeError) as exc:
                raise RuntimeError(f"{component} self-check did not return its normalized JSON identity") from exc
            expected_identity = validate_manifest_contract(json.loads(manifest_path.read_text()))
            if normalized != {
                "status": "verified",
                "component": component,
                "releaseId": expected_identity["releaseId"],
                "sourceContentSha256": expected_identity["sourceContentSha256"],
                "manifestFormatVersion": expected_identity["formatVersion"],
            }:
                raise RuntimeError(f"{component} normalized release identity did not match the Python manifest")
            self_check["normalizedIdentity"] = normalized
            results.append(self_check)
        trx_rows = [
            {
                "path": p.relative_to(candidate).as_posix(),
                "sha256": sha256(p),
                "counts": _trx_counts(p),
            }
            for p in sorted(trx.glob("*.trx"))
        ]
        if len(trx_rows) != 3:
            raise RuntimeError("verification did not produce all three required TRX files")
        _make_readonly(stage)
        stage_hash = _tree_hash(stage)
        evidence = {
            "formatVersion": VERIFICATION_FORMAT_VERSION,
            "status": "passed",
            "candidateId": state["releaseId"],
            "policyId": POLICY,
            "policySha256": policy_hash,
            "expectedCommands": commands
            + [
                [str(stage / component / executable), "--verify-release", str(manifest_path)]
                for component, executable in (("host", "LmStreaming.Sample"), ("daemon", "CodeReviewDaemon.Sample"))
            ],
            "commands": results,
            "trx": trx_rows,
            "stageTreeSha256": stage_hash,
            "stageFiles": _tree_rows(stage),
            "completedAtUtc": _utc(),
        }
        atomic_write(candidate / "verification.json", _json(evidence))
        return evidence
    except Exception:
        atomic_write(
            candidate / "verification.json",
            _json(
                {
                    "formatVersion": VERIFICATION_FORMAT_VERSION,
                    "status": "failed",
                    "candidateId": state.get("releaseId"),
                    "policyId": POLICY,
                    "commands": results,
                    "completedAtUtc": _utc(),
                }
            ),
        )
        raise


def _artifact_rows(stage: Path) -> list[dict[str, Any]]:
    rows = []
    for path in validate_tree(stage):
        relative = path.relative_to(stage)
        if relative.parts[0] not in ("host", "daemon"):
            continue
        rows.append({"path": relative.as_posix(), "sha256": sha256(path), "component": relative.parts[0]})
    return rows


def _tree_rows(root: Path) -> list[dict[str, Any]]:
    return [
        {
            "path": path.relative_to(root).as_posix(),
            "mode": stat.S_IMODE(path.lstat().st_mode),
            "size": path.lstat().st_size,
            "sha256": sha256(path),
        }
        for path in validate_tree(root)
    ]


def _tree_hash(root: Path) -> str:
    return canonical_hash(_tree_rows(root))


def _trx_counts(path: Path) -> dict[str, int]:
    import xml.etree.ElementTree as etree

    root = etree.parse(path).getroot()
    counters = next((element for element in root.iter() if element.tag.endswith("Counters")), None)
    if counters is None:
        raise RuntimeError(f"TRX has no result counters: {path}")
    return {
        key: int(counters.attrib.get(key, "0"))
        for key in ("total", "executed", "passed", "failed", "error", "timeout", "aborted")
    }


def _identity(state: dict[str, Any]) -> dict[str, Any]:
    return {
        "releaseId": state["releaseId"],
        "sourceContentSha256": state["source"]["sourceContentSha256"],
        "baseCommit": state["source"]["baseCommit"],
        "isDirty": state["source"]["sourceKind"] == "dirty-snapshot",
        "hostApiContractVersion": 1,
        "databaseSchemaMinimum": 0,
        "databaseSchemaMaximum": 8,
        "capabilities": [
            "collaboration",
            "message-idempotency",
            "spawn-suppression",
            "recursive-subagents",
            "per-turn-model-override",
        ],
    }


def validate_manifest_contract(manifest: dict[str, Any]) -> dict[str, Any]:
    required_root = {"formatVersion", "identity", "artifacts", "verifiedAtUtc", "verificationPolicy"}
    allowed_root = required_root | {
        "verificationPolicySha256",
        "verificationSha256",
        "sourceInventorySha256",
    }
    required_identity = {
        "releaseId",
        "sourceContentSha256",
        "baseCommit",
        "isDirty",
        "hostApiContractVersion",
        "databaseSchemaMinimum",
        "databaseSchemaMaximum",
        "capabilities",
    }
    if set(manifest) - allowed_root or not required_root <= set(manifest):
        raise RuntimeError("manifest root fields or casing violate format version 2")
    if manifest.get("formatVersion") != MANIFEST_FORMAT_VERSION:
        raise RuntimeError("unsupported manifest format version")
    identity = manifest.get("identity")
    if not isinstance(identity, dict) or set(identity) != required_identity:
        raise RuntimeError("manifest identity fields or casing violate format version 2")
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list):
        raise RuntimeError("manifest artifacts must be an array")
    for artifact in artifacts:
        if not isinstance(artifact, dict) or set(artifact) != {"path", "sha256", "component"}:
            raise RuntimeError("manifest artifact fields or casing violate format version 2")
        digest = artifact.get("sha256")
        if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise RuntimeError("manifest artifact hash must be lowercase SHA-256")
    return {
        "formatVersion": manifest["formatVersion"],
        "releaseId": identity["releaseId"],
        "sourceContentSha256": identity["sourceContentSha256"],
        "baseCommit": identity["baseCommit"],
        "isDirty": identity["isDirty"],
        "hostApiContractVersion": identity["hostApiContractVersion"],
        "databaseSchemaMinimum": identity["databaseSchemaMinimum"],
        "databaseSchemaMaximum": identity["databaseSchemaMaximum"],
        "capabilities": identity["capabilities"],
    }


def _provisional_manifest(state: dict[str, Any], stage: Path, policy_hash: str) -> dict[str, Any]:
    return {
        "formatVersion": MANIFEST_FORMAT_VERSION,
        "identity": _identity(state),
        "artifacts": _artifact_rows(stage),
        "verifiedAtUtc": _utc(),
        "verificationPolicy": POLICY,
        "verificationPolicySha256": policy_hash,
    }


def _release_hash(root: Path) -> str:
    rows = []
    for path in validate_tree(root, writable_ok=True, exclude={SEAL}):
        rel = path.relative_to(root).as_posix()
        if rel == SEAL:
            continue
        rows.append({"path": rel, "size": path.lstat().st_size, "sha256": sha256(path)})
    return canonical_hash(rows)


def _make_readonly(root: Path) -> None:
    for directory, names, files in os.walk(root, topdown=False, followlinks=False):
        for name in files:
            path = Path(directory) / name
            if stat.S_ISLNK(path.lstat().st_mode):
                raise RuntimeError(f"symlink rejected before chmod: {path}")
            path.chmod(0o555 if path.lstat().st_mode & 0o111 else 0o444)
        for name in names:
            path = Path(directory) / name
            if stat.S_ISLNK(path.lstat().st_mode):
                raise RuntimeError(f"symlink rejected before chmod: {path}")
            path.chmod(0o555)
    root.chmod(0o555)


def _validate_verification(candidate: Path, policy_path: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    evidence = json.loads((candidate / "verification.json").read_text())
    state = json.loads((candidate / "candidate.json").read_text())
    stage = candidate / "stage"
    expected_commands = _policy_commands(candidate / "source", candidate / "artifacts", candidate / "trx") + [
        [str(stage / component / executable), "--verify-release", str(stage / "manifest.json")]
        for component, executable in (("host", "LmStreaming.Sample"), ("daemon", "CodeReviewDaemon.Sample"))
    ]
    expected_trx = {f"trx/{Path(project).stem}.trx" for project in json.loads(policy_path.read_text())["testProjects"]}
    if (
        evidence.get("formatVersion") != VERIFICATION_FORMAT_VERSION
        or evidence.get("status") != "passed"
        or evidence.get("candidateId") != state.get("releaseId")
        or evidence.get("policyId") != POLICY
        or evidence.get("policySha256") != sha256(policy_path)
        or evidence.get("expectedCommands") != expected_commands
        or [row.get("args") for row in evidence.get("commands", [])] != expected_commands
        or any(row.get("exitCode") != 0 for row in evidence.get("commands", []))
        or {row.get("path") for row in evidence.get("trx", [])} != expected_trx
    ):
        raise RuntimeError("candidate verification record does not match the release contract")
    for row in evidence["trx"]:
        path = candidate / row["path"]
        if sha256(path) != row.get("sha256") or _trx_counts(path) != row.get("counts"):
            raise RuntimeError(f"TRX verification binding mismatch: {row.get('path')}")
    validate_tree(stage, writable_ok=False)
    if evidence.get("stageFiles") != _tree_rows(stage) or evidence.get("stageTreeSha256") != _tree_hash(stage):
        raise RuntimeError("verified stage was modified")
    return state, evidence


def publish_candidate(candidate: Path, root: Path, interrupt: bool = False) -> Path:
    policy_path = Path(__file__).with_name("verification-policy.json")
    state, evidence = _validate_verification(candidate, policy_path)
    stage = candidate / "stage"
    release_id = state["releaseId"]
    final = root / "releases" / release_id
    staging = root / "releases" / f".{release_id}.staging-{os.getpid()}"
    staging.parent.mkdir(parents=True, exist_ok=True)
    if final.exists():
        raise RuntimeError(f"release already exists: {release_id}")
    shutil.copytree(stage, staging, ignore=shutil.ignore_patterns("manifest.json"))
    staging.chmod(0o755)
    shutil.copy2(candidate / "verification.json", staging / "verification.json")
    shutil.copy2(candidate / "source" / "source-inventory.json", staging / "source-inventory.json")
    identity = _identity(state)
    manifest = {"formatVersion": MANIFEST_FORMAT_VERSION, "identity": identity, "artifacts": _artifact_rows(staging), "verifiedAtUtc": evidence["completedAtUtc"], "verificationPolicy": POLICY, "verificationSha256": sha256(staging / "verification.json"), "sourceInventorySha256": sha256(staging / "source-inventory.json")}
    atomic_write(staging / "manifest.json", _json(manifest), mode=0o644)
    digest = _release_hash(staging)
    seal = {"formatVersion": 1, "releaseId": release_id, "releaseRootSha256": digest, "redundantReleaseRootSha256": digest, "embeddedBuildIdentity": identity}
    atomic_write(staging / SEAL, _json(seal), mode=0o444)
    validate_tree(staging)
    if interrupt:
        raise RuntimeError("injected publish interruption")
    _make_readonly(staging)
    os.replace(staging, final)
    _fsync_dir(final.parent)
    read_verified(root, release_id)
    atomic_write(root / "pointers" / "latest-verified", release_id)
    return final


def read_verified(root: Path, release_id: str) -> dict[str, Any]:
    _canonical_relative(release_id)
    release = root / "releases" / release_id
    validate_tree(release, writable_ok=False)
    seal_path = physical_file(release, SEAL)
    seal = json.loads(seal_path.read_text())
    digest = _release_hash(release)
    if seal.get("releaseRootSha256") != digest or seal.get("redundantReleaseRootSha256") != digest:
        raise RuntimeError("release seal mismatch")
    verification = json.loads(physical_file(release, "verification.json").read_text())
    manifest = json.loads(physical_file(release, "manifest.json").read_text())
    validate_manifest_contract(manifest)
    if verification.get("status") != "passed" or verification.get("policyId") != POLICY:
        raise RuntimeError("release is not verified by current policy")
    if manifest.get("verificationSha256") != sha256(release / "verification.json") or manifest.get("sourceInventorySha256") != sha256(release / "source-inventory.json"):
        raise RuntimeError("release trust input mismatch")
    if manifest.get("identity") != seal.get("embeddedBuildIdentity"):
        raise RuntimeError("embedded identity mismatch")
    seen: set[str] = set()
    for item in manifest.get("artifacts", []):
        rel = _canonical_relative(item["path"]).as_posix()
        if rel in seen:
            raise RuntimeError(f"artifact path collision: {rel}")
        seen.add(rel)
        if sha256(physical_file(release, rel)) != item["sha256"]:
            raise RuntimeError(f"artifact hash mismatch: {rel}")
    return manifest


class ActivationAdapter:
    """Explicit activation boundary. Production callers must select a concrete adapter."""

    def _missing(self) -> None:
        raise NotImplementedError("a concrete activation adapter is required")

    def backup_database(self, release_id: str) -> str: self._missing(); return ""
    def migration_copy_gate(self, release_id: str) -> None: self._missing()
    def drain(self, previous: str) -> None: self._missing()
    def stop_incumbent(self, previous: str) -> None:
        """Take the listener ports from whoever owns them before the candidate binds them.

        The candidate reuses the incumbent's ports, so this must leave them free or the
        candidate start fails with EADDRINUSE.
        """
        self._missing()

    def start_host(self, release: Path) -> None: self._missing()
    def canary_host(self, identity: dict[str, Any]) -> None: self._missing()
    def start_daemon_held(self, release: Path) -> None: self._missing()
    def handshake(self, identity: dict[str, Any]) -> None: self._missing()
    def enable_admission(self, release_id: str) -> None: self._missing()
    def stabilize(self, release_id: str) -> None: self._missing()

    def rollback(self, previous: str, backup: str) -> None:
        """Put `previous` back in service: stop the candidate, give it the ports back, and
        undo the drain. Restoring the pointer alone leaves a live but non-admitting system.
        """
        self._missing()

    def health(self, component: str) -> dict[str, Any]: self._missing(); return {}


def _test_hard_kill(boundary: str) -> None:
    """Subprocess-only crash seam. It is inert unless a test names this exact boundary."""
    if os.environ.get("CODEREVIEW_RELEASE_TEST_SIGKILL") == boundary:
        os.kill(os.getpid(), 9)


def journal(root: Path, event: str, **fields: Any) -> None:
    path = root / "activation-journal.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    try:
        os.write(fd, _json({"at": _utc(), "event": event, **fields}))
        os.fsync(fd)
    finally:
        os.close(fd)
    _fsync_dir(path.parent)
    _test_hard_kill(f"journal:{event}")


def _events(root: Path) -> list[dict[str, Any]]:
    path = root / "activation-journal.jsonl"
    if not path.exists():
        return []
    data = path.read_bytes()
    lines = data.splitlines(keepends=True)
    events: list[dict[str, Any]] = []
    for index, raw in enumerate(lines):
        try:
            events.append(json.loads(raw))
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            if index == len(lines) - 1 and not raw.endswith(b"\n"):
                break
            raise RuntimeError(f"activation journal is corrupt at record {index + 1}") from exc
    return events


def _record_candidate(root: Path, component: str, pid: int, executable: str) -> None:
    """Persist a candidate's identity so a later run can take the ports back from it."""
    path = root / CANDIDATE_PROCESSES
    try:
        rows = json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        rows = {}
    if not isinstance(rows, dict):
        rows = {}
    rows[component] = {"pid": pid, "executable": executable}
    atomic_write(path, _json(rows))


def _recorded_candidates(root: Path) -> dict[str, dict[str, Any]]:
    try:
        rows = json.loads((root / CANDIDATE_PROCESSES).read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}
    return rows if isinstance(rows, dict) else {}


def _is_candidate_process(pid: int, executable: str) -> bool:
    """Guard against pid reuse: the live process must still be the recorded executable."""
    try:
        raw = Path(f"/proc/{pid}/cmdline").read_bytes()
    except OSError:
        return False
    return executable in raw.decode(errors="replace").split("\0")


@contextlib.contextmanager
def activation_lock(root: Path):
    root.mkdir(parents=True, exist_ok=True)
    lock_fd = os.open(root / "activation.lock", os.O_RDWR | os.O_CREAT, 0o600)
    try:
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc:
            raise RuntimeError("activation already in progress") from exc
        yield
    finally:
        os.close(lock_fd)


def _delete_pointer(path: Path) -> None:
    try:
        path.unlink()
    except FileNotFoundError:
        return
    _fsync_dir(path.parent)


def _recover_locked(root: Path, adapter: ActivationAdapter) -> str:
    events = _events(root)
    begins = [event for event in events if event["event"] == "begin"]
    if not begins:
        return "clean"
    begin = begins[-1]
    tail = events[events.index(begin):]
    if any(event["event"] in ("success", "rollback") for event in tail):
        return "clean"
    previous, intended = begin.get("previous", ""), begin["intended"]
    backup = next((event.get("backup", "") for event in tail if event["event"] == "database-backup-created"), "")
    if not backup:
        discovered = sorted(
            (root / "backups").glob(f"{intended}-*.sqlite") if (root / "backups").exists() else [],
            key=lambda path: path.stat().st_mtime_ns,
            reverse=True,
        )
        backup = str(discovered[0]) if discovered else ""
    if previous:
        atomic_write(root / "pointers" / "active", previous)
    else:
        _delete_pointer(root / "pointers" / "active")
    adapter.rollback(previous, backup)
    journal(root, "rollback", restored=previous, failed=intended, recovered=True)
    return "rolled_back"


def recover(root: Path, adapter: ActivationAdapter | None = None) -> str:
    if adapter is None:
        raise RuntimeError("recover requires a concrete activation adapter")
    with activation_lock(root):
        return _recover_locked(root, adapter)


def _run_adapter_boundary(name: str, action: Callable[[], Any]) -> Any:
    _test_hard_kill(f"adapter:{name}:before")
    result = action()
    _test_hard_kill(f"adapter:{name}:after")
    return result


def activate(root: Path, release_id: str, adapter: ActivationAdapter | None = None, fail_after_step: str | bool | None = None) -> None:
    if adapter is None:
        raise RuntimeError("activate requires a concrete activation adapter")
    with activation_lock(root):
        _recover_locked(root, adapter)
        manifest = read_verified(root, release_id)
        active = root / "pointers" / "active"
        old = active.read_text().strip() if active.exists() else ""
        journal(root, "begin", intended=release_id, previous=old)
        steps: list[tuple[str, Callable[[], Any]]] = [
            ("drained", lambda: _run_adapter_boundary("drain", lambda: adapter.drain(old))),
            (
                "incumbent-stopped",
                lambda: _run_adapter_boundary("stop-incumbent", lambda: adapter.stop_incumbent(old)),
            ),
            (
                "host-canary",
                lambda: (
                    _run_adapter_boundary(
                        "start-host", lambda: adapter.start_host(root / "releases" / release_id)
                    ),
                    _run_adapter_boundary("canary-host", lambda: adapter.canary_host(manifest["identity"])),
                ),
            ),
            (
                "daemon-held",
                lambda: _run_adapter_boundary(
                    "start-daemon-held", lambda: adapter.start_daemon_held(root / "releases" / release_id)
                ),
            ),
            (
                "handshake",
                lambda: _run_adapter_boundary("handshake", lambda: adapter.handshake(manifest["identity"])),
            ),
        ]
        backup = ""
        try:
            backup = _run_adapter_boundary("backup-database", lambda: adapter.backup_database(release_id))
            if fail_after_step == "database-backup-created-before-journal":
                raise RuntimeError("injected fault after backup before journal")
            journal(root, "database-backup-created", release=release_id, backup=backup)
            if fail_after_step == "database-backup-created":
                raise RuntimeError("injected fault after database-backup-created")
            _run_adapter_boundary("migration-copy-gate", lambda: adapter.migration_copy_gate(release_id))
            journal(root, "database-gated", release=release_id, backup=backup)
            if fail_after_step == "database-gated":
                raise RuntimeError("injected fault after database-gated")
            for name, action in steps:
                action()
                journal(root, name, release=release_id)
                if fail_after_step == name: raise RuntimeError(f"injected fault after {name}")
            if old:
                atomic_write(root / "pointers" / "previous", old)
                _test_hard_kill("pointer:previous")
            atomic_write(active, release_id)
            _test_hard_kill("pointer:active")
            journal(root, "pointers-updated", active=release_id, previous=old)
            if fail_after_step is True or fail_after_step == "pointers-updated": raise RuntimeError("injected fault after pointers-updated")
            _run_adapter_boundary("enable-admission", lambda: adapter.enable_admission(release_id))
            journal(root, "admission-active", release=release_id)
            if fail_after_step == "admission-active": raise RuntimeError("injected fault after admission-active")
            _run_adapter_boundary("stabilize", lambda: adapter.stabilize(release_id))
            journal(root, "stabilized", release=release_id)
            if fail_after_step == "stabilized": raise RuntimeError("injected fault after stabilized")
            journal(root, "success", active=release_id)
        except Exception:
            if old: atomic_write(active, old)
            else: _delete_pointer(active)
            adapter.rollback(old, backup)
            journal(root, "rollback", restored=old, failed=release_id)
            raise


class LocalActivationAdapter(ActivationAdapter):
    def __init__(
        self,
        root: Path,
        database: Path,
        host_url: str,
        daemon_url: str,
        control_socket: Path,
        profile: str,
        supervisor_unit: str = SUPERVISOR_UNIT,
        runner: CommandRunner | None = None,
    ):
        self.root = root
        self.database = database
        self.urls = {"host": host_url.rstrip("/"), "daemon": daemon_url.rstrip("/")}
        self.control_socket = control_socket
        self.profile = profile
        self.supervisor_unit = supervisor_unit
        self.runner = runner or CommandRunner()
        self.processes: dict[str, subprocess.Popen[bytes]] = {}

    def backup_database(self, release_id: str) -> str:
        backup = self.root / "backups" / f"{release_id}-{int(time.time_ns())}.sqlite"
        backup.parent.mkdir(parents=True, exist_ok=True)
        with sqlite3.connect(self.database) as source, sqlite3.connect(backup) as destination:
            source.backup(destination)
        with backup.open("rb") as stream:
            os.fsync(stream.fileno())
        _fsync_dir(backup.parent)
        return str(backup)

    def migration_copy_gate(self, release_id: str) -> None:
        check = self.root / "backups" / f".{release_id}-migration-check.sqlite"
        shutil.copy2(self.database, check)
        try:
            executable = self.root / "releases" / release_id / "daemon" / "CodeReviewDaemon.Sample"
            result = self.runner.run(
                [str(executable), "--verify-database-migration", str(check)],
                executable.parent,
                env={**os.environ, "ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production"},
                timeout=60,
            )
            with sqlite3.connect(check) as connection:
                integrity = connection.execute("PRAGMA integrity_check").fetchone()
                version = connection.execute("PRAGMA user_version").fetchone()
            if integrity != ("ok",):
                raise RuntimeError(f"SQLite migration-copy gate failed: {integrity}")
            journal(
                self.root,
                "migration-copy-verified",
                release=release_id,
                command=result,
                integrity=integrity[0],
                schemaVersion=version[0],
            )
        finally:
            check.unlink(missing_ok=True)

    def _control(self, command: str, wait: float = 0.0) -> None:
        deadline = time.monotonic() + wait
        while True:
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.settimeout(10)
                    client.connect(str(self.control_socket))
                    client.sendall((command + "\n").encode())
                    response = client.recv(128).decode().strip()
            except OSError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.1)
                continue
            if response != "ok":
                raise RuntimeError(f"daemon control command {command!r} failed: {response}")
            return

    def drain(self, previous: str) -> None:
        if previous and self.control_socket.exists():
            self._control("drain")

    def _systemctl(self, verb: str) -> None:
        self.runner.run(["systemctl", "--user", verb, self.supervisor_unit], self.root, timeout=120)

    def _address(self, component: str) -> tuple[str, int]:
        authority = self.urls[component].split("://", 1)[-1].split("/", 1)[0]
        host, separator, port = authority.rpartition(":")
        if not separator or not port.isdigit():
            raise RuntimeError(f"{component} url must name an explicit port: {self.urls[component]}")
        return host or "127.0.0.1", int(port)

    def _wait_port_bindable(self, component: str, timeout: float | None = None) -> None:
        """Prove the candidate can actually take the port, not merely that nothing answers on it.

        A refused connect is not proof of ownership transfer: a socket that is bound but has not
        called listen() refuses connections and still holds the address, so a connect-based gate
        passes and the candidate then dies on EADDRINUSE. Mirror the listener instead — same
        family, same SO_REUSEADDR, same bind and listen the candidate performs — and release it.
        """
        host, port = self._address(component)
        deadline = time.monotonic() + (PORT_HANDOVER_TIMEOUT_SECONDS if timeout is None else timeout)
        while True:
            probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                probe.bind((host, port))
                probe.listen(1)
                return
            except OSError as exc:
                if time.monotonic() >= deadline:
                    raise RuntimeError(
                        f"{component} port {port} is still owned after stopping {self.supervisor_unit}: {exc}"
                    ) from exc
            finally:
                # Closing a listener that never accepted a connection leaves no TIME_WAIT, so the
                # candidate can bind immediately after this returns.
                probe.close()
            time.sleep(0.1)

    def stop_incumbent(self, previous: str) -> None:
        # Two things can own the ports: the supervisor, and an unsupervised candidate left by an
        # earlier activation. Ownership transfer has to be deliberate about both.
        self._systemctl("stop")
        self._stop_candidates()
        for component in ("host", "daemon"):
            self._wait_port_bindable(component)
        journal(self.root, "incumbent-released", previous=previous, unit=self.supervisor_unit)

    def _start(self, component: str, executable: Path, args: list[str]) -> None:
        log_dir = self.root / "process-logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        log = (log_dir / f"{component}.log").open("ab", buffering=0)
        process = subprocess.Popen(
            [str(executable), *args],
            cwd=executable.parent,
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        self.processes[component] = process
        _record_candidate(self.root, component, process.pid, str(executable))

    def start_host(self, release: Path) -> None:
        self._start("host", release / "host" / "LmStreaming.Sample", ["--urls", self.urls["host"]])

    def _wait_health(self, component: str, identity: dict[str, Any], held: bool = False) -> None:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            process = self.processes.get(component)
            if process is not None and process.poll() is not None:
                raise RuntimeError(f"{component} exited before becoming ready: {process.returncode}")
            try:
                row = self.health(component)
                if row.get("releaseId") == identity["releaseId"] and row.get("ready", True):
                    if not held or row.get("admissionState") == "held":
                        return
            except (OSError, urllib.error.URLError, json.JSONDecodeError):
                pass
            time.sleep(0.1)
        raise RuntimeError(f"{component} health check timed out")

    def canary_host(self, identity: dict[str, Any]) -> None:
        self._wait_health("host", identity)

    def start_daemon_held(self, release: Path) -> None:
        self._start(
            "daemon",
            release / "daemon" / "CodeReviewDaemon.Sample",
            ["--review", self.profile, "--urls", self.urls["daemon"]],
        )

    def handshake(self, identity: dict[str, Any]) -> None:
        self._wait_health("daemon", identity, held=True)

    def enable_admission(self, release_id: str) -> None:
        self._control("activate")

    def stabilize(self, release_id: str) -> None:
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if all(process.poll() is None for process in self.processes.values()):
                time.sleep(0.1)
                continue
            raise RuntimeError("candidate process exited during stabilization")

    def _terminate(self, pid: int, process: subprocess.Popen[bytes] | None = None) -> None:
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.killpg(pid, signal.SIGTERM)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if process is not None:
                with contextlib.suppress(subprocess.TimeoutExpired):
                    process.wait(timeout=0.1)
                    return
                continue
            try:
                os.killpg(pid, 0)
            except (ProcessLookupError, PermissionError):
                return
            time.sleep(0.1)
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.killpg(pid, signal.SIGKILL)
        if process is not None:
            with contextlib.suppress(subprocess.TimeoutExpired):
                process.wait(timeout=5)

    def _stop_candidates(self) -> None:
        for process in self.processes.values():
            if process.poll() is None:
                self._terminate(process.pid, process)
        self.processes.clear()
        for row in _recorded_candidates(self.root).values():
            pid, executable = row.get("pid"), row.get("executable", "")
            if isinstance(pid, int) and _is_candidate_process(pid, executable):
                self._terminate(pid)
        (self.root / CANDIDATE_PROCESSES).unlink(missing_ok=True)

    def rollback(self, previous: str, backup: str) -> None:
        self._stop_candidates()
        if backup:
            shutil.copy2(backup, self.database)
        if not previous:
            return
        # The pointer already names `previous`, so a supervised start brings that exact release
        # back on the ports. Starting is idempotent when the incumbent was only drained, and the
        # control command is what undoes that drain — without it the system runs but admits nothing.
        self._systemctl("start")
        self._control("activate", wait=60)
        journal(self.root, "previous-readmitted", previous=previous, unit=self.supervisor_unit)

    def health(self, component: str) -> dict[str, Any]:
        suffix = "/api/system/release" if component == "host" else "/health/version"
        with urllib.request.urlopen(self.urls[component] + suffix, timeout=5) as response:
            return json.load(response)


def watchdog(root: Path, adapter: ActivationAdapter | None = None, auto_activate: bool = False) -> str:
    if adapter is None:
        raise RuntimeError("watchdog requires a concrete activation adapter")
    if auto_activate:
        if recover(root, adapter) == "rolled_back": return "rollback_required"
    else:
        with activation_lock(root):
            if _recover_locked(root, adapter) == "rolled_back": return "rollback_required"
            latest_path, active_path = root / "pointers" / "latest-verified", root / "pointers" / "active"
            latest = latest_path.read_text().strip() if latest_path.exists() else ""
            active = active_path.read_text().strip() if active_path.exists() else ""
            if not latest: return "no_verified_release"
            read_verified(root, latest)
            health = [adapter.health(component) for component in ("host", "daemon")]
            if any(item and (item.get("releaseId") != active or not item.get("ready", True)) for item in health):
                return "running_manifest_mismatch"
            if latest == active: return "current"
            return "newer_verified_available"
    latest_path = root / "pointers" / "latest-verified"
    latest = latest_path.read_text().strip() if latest_path.exists() else ""
    if not latest: return "no_verified_release"
    activate(root, latest, adapter)
    return "activated"


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="cmd", required=True)
    snap = sub.add_parser("snapshot"); snap.add_argument("--repo", type=Path, required=True); snap.add_argument("--output", type=Path, required=True); snap.add_argument("--include-dirty", action="store_true")
    prep = sub.add_parser("prepare"); prep.add_argument("--repo", type=Path, required=True); prep.add_argument("--candidate", type=Path, required=True); prep.add_argument("--include-dirty", action="store_true")
    ver = sub.add_parser("verify"); ver.add_argument("--candidate", type=Path, required=True); ver.add_argument("--policy", type=Path, default=Path(__file__).with_name("verification-policy.json"))
    pub = sub.add_parser("publish"); pub.add_argument("--candidate", type=Path, required=True); pub.add_argument("--root", type=Path, required=True)
    def add_activation_options(command: argparse.ArgumentParser) -> None:
        command.add_argument("--root", type=Path, required=True)
        command.add_argument("--database", type=Path, required=True)
        command.add_argument("--host-url", default="http://127.0.0.1:5080")
        command.add_argument("--daemon-url", default="http://127.0.0.1:5081")
        command.add_argument("--control-socket", type=Path, required=True)
        command.add_argument("--profile", default="achieveai")
        command.add_argument("--supervisor-unit", default=SUPERVISOR_UNIT)

    act = sub.add_parser("activate"); add_activation_options(act); act.add_argument("release_id")
    rec = sub.add_parser("recover"); add_activation_options(rec)
    watch = sub.add_parser("watchdog"); add_activation_options(watch); watch.add_argument("--activate", action="store_true")
    args = parser.parse_args()
    adapter = None
    if args.cmd in ("activate", "recover", "watchdog"):
        adapter = LocalActivationAdapter(
            args.root.resolve(),
            args.database.resolve(),
            args.host_url,
            args.daemon_url,
            args.control_socket.resolve(),
            args.profile,
            args.supervisor_unit,
        )
    if args.cmd == "snapshot": print(json.dumps(snapshot(args.repo.resolve(), args.output.resolve(), args.include_dirty), sort_keys=True))
    elif args.cmd == "prepare": print(json.dumps(prepare(args.repo.resolve(), args.candidate.resolve(), args.include_dirty), sort_keys=True))
    elif args.cmd == "verify": print(json.dumps(verify(args.candidate.resolve(), args.policy.resolve()), sort_keys=True))
    elif args.cmd == "publish": print(publish_candidate(args.candidate.resolve(), args.root.resolve()))
    elif args.cmd == "activate": activate(args.root.resolve(), args.release_id, adapter)
    elif args.cmd == "recover": print(recover(args.root.resolve(), adapter))
    else: print(watchdog(args.root.resolve(), adapter, args.activate))
    return 0


if __name__ == "__main__":
    try: raise SystemExit(main())
    except Exception as error:
        print(f"release tool refused: {error}", file=sys.stderr)
        raise SystemExit(1)
