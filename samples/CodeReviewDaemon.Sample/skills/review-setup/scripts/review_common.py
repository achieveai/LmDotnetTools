"""Shared, credential-free transport and recoverable local review lifecycle."""
from __future__ import annotations

from contextlib import ExitStack, contextmanager
from contextvars import ContextVar
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
from typing import Mapping
from urllib.parse import parse_qsl, quote, unquote, urlencode, urlsplit, urlunsplit
import uuid


class ReviewError(Exception):
    def __init__(self, message: str, code: int = 1):
        super().__init__(message)
        self.code = code


class CleanupNotProvenError(ReviewError):
    """An owned child process may still be running; no fallback may proceed."""


class OwnedTimeoutError(ReviewError):
    """The deadline elapsed and every executable owned group member terminated."""


class CommandNotStartedError(ReviewError):
    """Pre-spawn failure: this invocation definitely did not launch a command."""


class PullRequestMoved(ReviewError):
    """A provider's PR iteration or merge commit changed during context
    collection (Task 6): the Git content already fetched/prepared for this
    PR no longer corresponds to what the provider now reports. Callers must
    re-resolve and re-prepare rather than trust the partial/stale collection.
    Defaults to exit code 3, matching the router's exit-code contract."""

    def __init__(self, message: str, code: int = 3):
        super().__init__(message, code=code)


@dataclass(frozen=True)
class ResolvedPullRequest:
    """Provider-neutral identity of a resolved pull request (ruling R1).

    Produced by a provider's ``resolve()`` (e.g. a future ``AdoProvider``);
    consumed by the provider-neutral ``prepare()``. Every field is a plain,
    non-secret value -- there is nothing credential-shaped to redact here.
    """

    provider: str
    pr: int
    repo: str
    remote_url: str
    merge_ref: str
    merge_sha: str | None
    source_branch: str | None
    target_branch: str | None
    iteration: int | None

    def to_public_dict(self) -> dict:
        return {
            "provider": self.provider,
            "pr": self.pr,
            "repo": self.repo,
            "remote_url": self.remote_url,
            "merge_ref": self.merge_ref,
            "merge_sha": self.merge_sha,
            "source_branch": self.source_branch,
            "target_branch": self.target_branch,
            "iteration": self.iteration,
        }


def _normalized_remote_base(remote_url: str) -> str:
    """Derive the Git config scoping base from a remote URL: scheme + host
    (+ port) + path prefix only -- no credentials, no trailing slash.

    Rejects (hardened for Task 2's credential-binding interface): a malformed
    URL, a non-HTTPS scheme, a URL with no host, or a URL that embeds a
    username/password -- credentials must never be bound to an unvalidated
    or credential-bearing remote.
    """
    if not isinstance(remote_url, str) or not remote_url:
        raise ReviewError("Git remote URL is missing; refusing to bind credentials to it.")
    try:
        parts = urlsplit(remote_url)
        port = parts.port
    except (TypeError, ValueError):
        raise ReviewError("Git remote URL is malformed; refusing to bind credentials to it.") from None
    if parts.scheme != "https":
        raise ReviewError("Git remote URL must be HTTPS; refusing to bind credentials to it.")
    if not parts.hostname:
        raise ReviewError("Git remote URL has no host; refusing to bind credentials to it.")
    if parts.username or parts.password:
        raise ReviewError("Git remote URL contains embedded credentials; refusing to bind credentials to it.")
    netloc = parts.hostname
    if port:
        netloc = f"{netloc}:{port}"
    path = parts.path.rstrip("/")
    return f"{parts.scheme}://{netloc}{path}"


class GitTransportAuth:
    """Opaque, redacted, in-memory Git transport credential (rulings R1, R6).

    The only public surface is ``apply()`` and ``git_arguments()``.
    ``git_arguments()`` always returns ``[]``: no secret-bearing argv exists
    for this credential. ``apply()`` returns a copy of the given environment
    with one additional Git config entry appended (never overwriting any
    ``GIT_CONFIG_COUNT``/``GIT_CONFIG_KEY_*``/``GIT_CONFIG_VALUE_*`` entries
    the caller already carries) that sets an ``http.<base>.extraHeader``
    scoped to the normalized remote base URL -- never the bare, unscoped
    ``http.extraHeader`` -- so a redirect to a different origin cannot
    receive the same credential. The token is never exposed through
    ``repr()``/``str()`` and never placed in argv.

    ``apply()`` fails closed (raises ``ReviewError``) until bound to a
    specific remote via the internal, non-public ``_bound_to()`` (used by
    ``prepare()``); it never silently emits an unscoped or placeholder-scoped
    header.
    """

    def __init__(self, token: str, remote_base: str | None = None):
        self._token = token
        self._remote_base = remote_base

    @classmethod
    def bearer(cls, token: str) -> "GitTransportAuth":
        return cls(token)

    def _bound_to(self, remote_url: str) -> "GitTransportAuth":
        """Internal-only: return a copy of this credential scoped to
        ``remote_url``'s normalized base. Not part of the public contract;
        used by ``prepare()`` once both the auth and the resolved remote are
        available together."""
        return GitTransportAuth(self._token, _normalized_remote_base(remote_url))

    def apply(self, env: Mapping[str, str]) -> dict[str, str]:
        if not self._remote_base:
            raise ReviewError(
                "GitTransportAuth is not bound to a remote; call the "
                "internal _bound_to(remote_url) before apply() so the "
                "credential is scoped to a specific origin."
            )
        result = dict(env)
        count = int(result.get("GIT_CONFIG_COUNT", "0"))
        result[f"GIT_CONFIG_KEY_{count}"] = f"http.{self._remote_base}.extraHeader"
        result[f"GIT_CONFIG_VALUE_{count}"] = f"AUTHORIZATION: bearer {self._token}"
        result["GIT_CONFIG_COUNT"] = str(count + 1)
        return result

    def git_arguments(self) -> list[str]:
        return []

    def __repr__(self) -> str:
        return "GitTransportAuth(redacted)"

    def __str__(self) -> str:
        return "GitTransportAuth(redacted)"


@dataclass(frozen=True)
class PrepareResult:
    """In-memory return value of ``prepare()`` (Task 3).

    Not the same object as the on-disk prepared-state manifest (Task 3,
    ruling R2), which is a separate serialized projection of it.
    """

    repo: str
    slot: int
    pr: int
    merge_sha: str
    worktree: Path
    prepared_at: str


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def preflight() -> dict:
    if sys.version_info < (3, 10):
        raise ReviewError("Python 3.10 or newer is required; no pip packages are needed.")
    versions = {"python": sys.version.split()[0]}
    for command in ("git", "curl"):
        if not shutil.which(command):
            raise ReviewError(f"Required sandbox tool is not installed: {command}")
        result = subprocess.run([command, "--version"], text=True, capture_output=True, check=False)
        if result.returncode:
            raise ReviewError(f"Cannot execute required tool: {command}")
        versions[command] = result.stdout.splitlines()[0]
    return versions


class FetchProgress:
    """Private bounded log containing only generated events and numeric Git progress."""
    def __init__(self):
        descriptor, name = tempfile.mkstemp(prefix="review-fetch-", suffix=".log")
        self.path = Path(name)
        self.stream = os.fdopen(descriptor, "w", encoding="utf-8")
        self.started = time.monotonic()
        self.size = 0
        self.failed = False
        self.truncated = False
        self.phase = "network-acquisition"
        self.durable_events = 0
        self.last_progress_elapsed = None

    def event(self, message: str) -> None:
        if self.failed or self.truncated:
            return
        text = f"{time.monotonic() - self.started:.1f}s {message}\n"
        if self.size + len(text) > 2 * 1024 ** 2 - 64:
            text = "Progress log truncated at 2 MiB; command continues.\n"
            self.truncated = True
        try:
            self.stream.write(text)
            self.stream.flush()
            self.size += len(text)
        except OSError:
            self.failed = True

    def git_line(self, text: str) -> None:
        # Do not log arbitrary remote lines, paths, URLs, ref names, or errors.
        match = re.fullmatch(
            r"(?:remote: )?(Counting objects|Enumerating objects|Compressing objects|"
            r"Receiving objects|Resolving deltas|Updating files|Checking connectivity): "
            r"([0-9%(),. /|+\-]+(?:[KMGT]?i?B(?:/s)?[0-9%(),. /|+\-]*)*)(?:done\.)?", text.strip())
        if not match:
            return
        elapsed = time.monotonic() - self.started
        self.last_progress_elapsed = elapsed
        detail = match.group(2).strip()
        self.event(f"git {match.group(1)}: {detail}")
        if self.durable_events >= 256:
            return
        fields = {
            "phase": self.phase,
            "progress_kind": match.group(1),
            "elapsed_ms": max(0, round(elapsed * 1000, 3)),
        }
        percent = re.search(r"(?<![0-9])([0-9]{1,3})%", detail)
        counts = re.search(r"\(([0-9]+)/([0-9]+)\)", detail)
        size = re.search(r"([0-9]+(?:\.[0-9]+)?) ([KMGT]?i?B)(?: |$)", detail)
        if percent:
            fields["percent"] = min(100, int(percent.group(1)))
        if counts:
            fields.update(current_count=int(counts.group(1)), total_count=int(counts.group(2)))
        if size:
            units = {"B": 1, "KB": 1000, "MB": 1000 ** 2, "GB": 1000 ** 3,
                     "TB": 1000 ** 4, "KiB": 1024, "MiB": 1024 ** 2,
                     "GiB": 1024 ** 3, "TiB": 1024 ** 4}
            fields["received_bytes"] = round(float(size.group(1)) * units[size.group(2)])
        log_event("git.fetch.progress", "Numeric Git progress observed", level="Debug", **fields)
        self.durable_events += 1

    def close(self) -> None:
        try:
            self.stream.close()
        except OSError:
            self.failed = True


_FETCH_SESSION = ContextVar("review_fetch_session", default=None)


class FetchSession:
    def __init__(self, timeout: float, progress: FetchProgress | None):
        if not isinstance(timeout, (int, float)) or isinstance(timeout, bool) or not 0 < timeout <= 86400:
            raise ReviewError("History operation timeout must be between 0 and 86400 seconds.")
        self.timeout = timeout
        self.deadline = None
        self.excluded_lock_wait = 0.0
        self.progress = progress

    def remaining(self) -> float:
        if self.deadline is None:
            self.deadline = time.monotonic() + self.timeout
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise ReviewError("History acquisition exhausted its shared operation timeout.")
        return remaining

    def exclude_lock_wait(self, seconds: float) -> None:
        """Keep measured flock contention outside the history-operation budget."""
        if seconds <= 0:
            return
        self.excluded_lock_wait += seconds
        if self.deadline is not None:
            self.deadline += seconds

    def event(self, message: str) -> None:
        if self.progress:
            self.progress.event(message)


@contextmanager
def fetch_session(timeout: float = 300, *, log: bool = False):
    existing = _FETCH_SESSION.get()
    if existing is not None:
        yield existing
        return
    progress = FetchProgress() if log else None
    session = FetchSession(timeout, progress)
    token = _FETCH_SESSION.set(session)
    if progress:
        print(f"Fetch progress log: {progress.path}", flush=True)
        print(f"Effective aggregate history timeout: {timeout:g}s (--history-timeout); background tool timeout is separate.", flush=True)
        session.event(f"aggregate history budget={timeout:g}s")
    try:
        yield session
    except BaseException:
        session.event("operation failed")
        raise
    else:
        session.event("operation complete")
    finally:
        _FETCH_SESSION.reset(token)
        if progress:
            progress.close()
            if progress.failed:
                print("Fetch progress log could not be fully written.", file=sys.stderr, flush=True)


_OWNED_JOURNAL = ContextVar("review_owned_journal", default=())
_SLOT_WRITER = ContextVar("review_slot_writer", default=None)
_SHARED_WRITER = ContextVar("review_shared_writer", default=None)
_OUTER_WRITER = ContextVar("review_outer_writer", default=None)
_OUTER_MERGE_OWNER = ContextVar("review_outer_merge_owner", default=None)
_HELD_LOCKS = ContextVar("review_held_locks", default=())
_LOCK_SESSION = ContextVar("review_lock_session", default=None)
_OPERATION_LOG = ContextVar("review_operation_log", default=None)


_SENSITIVE_FIELD_PARTS = (
    "authorization",
    "credential",
    "environment",
    "header",
    "oauth",
    "password",
    "pat",
    "proxy",
    "secret",
    "token",
    "argv",
    "pr_content",
    "title",
    "description",
    "author",
)


def _safe_log_value(key: str, value):
    """Return bounded diagnostics without preserving secret-bearing payloads."""
    lowered = key.casefold()
    if lowered == "command" or any(part in lowered for part in _SENSITIVE_FIELD_PARTS):
        return "[REDACTED]"
    if isinstance(value, BaseException):
        return {"category": type(value).__name__}
    if isinstance(value, dict):
        return {str(k): _safe_log_value(str(k), v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [_safe_log_value(key, item) for item in value]
    if isinstance(value, Path):
        return value.name
    if isinstance(value, str):
        if len(value) > 512:
            return value[:509] + "..."
        value = re.sub(r"(?i)(bearer|token|pat)[ =:]+[^\s,;]+", r"\1 [REDACTED]", value)
        try:
            parsed = urlsplit(value)
            if parsed.scheme and parsed.hostname and (parsed.username or parsed.password):
                host = parsed.hostname
                if parsed.port:
                    host += f":{parsed.port}"
                value = urlunsplit((parsed.scheme, host, parsed.path, parsed.query, parsed.fragment))
        except ValueError:
            return "[REDACTED]"
        return value
    if value is None or isinstance(value, (bool, int, float)):
        return value
    return type(value).__name__


class OperationLog:
    """Best-effort, per-invocation JSONL diagnostics; never lifecycle authority."""

    def __init__(self, path: Path | None, *, command: str, verbose: bool, context: dict):
        self.path = path
        self.command = command
        self.verbose = verbose
        self.context = dict(context)
        self.started = time.monotonic()
        self.available = path is not None
        self._warned = False
        self.actual_exit = 0
        self.failure_category = None

    def bind(self, **fields) -> None:
        self.context.update(
            {key: _safe_log_value(key, value) for key, value in fields.items()}
        )

    def _warn_failure(self) -> None:
        if not self._warned:
            print(
                "Structured diagnostics unavailable; lifecycle safety remains authoritative.",
                file=sys.stderr,
                flush=True,
            )
            self._warned = True

    def _write(self, payload: dict) -> None:
        if self.path is None:
            raise OSError("log path unavailable")
        descriptor = os.open(
            self.path,
            os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW,
        )
        try:
            os.write(
                descriptor,
                (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode(
                    "utf-8"
                ),
            )
            if payload.get("event") == "operation.end":
                os.fsync(descriptor)
        finally:
            os.close(descriptor)

    def event(self, event: str, message: str, *, level: str = "Information", **fields) -> None:
        safe = {key: _safe_log_value(key, value) for key, value in fields.items()}
        payload = {
            "@t": utc_now(),
            "@l": level,
            "@m": _safe_log_value("message", message),
            "@mt": event,
            "@logger": "review_common",
            "application": "review-setup",
            "event": event,
            **self.context,
            **safe,
        }
        if self.available:
            try:
                self._write(payload)
            except OSError:
                self.available = False
                self._warn_failure()
        if self.verbose:
            details = []
            for key in (
                "phase",
                "lock_class",
                "mode",
                "outcome",
                "failure_category",
                "actual_exit",
                "wait_ms",
                "hold_ms",
            ):
                if key in payload and payload[key] is not None:
                    details.append(f"{key}={payload[key]}")
            suffix = " " + " ".join(details) if details else ""
            print(f"[{payload['invocation_id'][:8]}] {event}{suffix}", file=sys.stderr, flush=True)

    def finish(self, actual_exit: int, failure_category: str | None = None) -> None:
        self.actual_exit = actual_exit
        self.failure_category = failure_category


@contextmanager
def operation_logging(
    root: Path | None,
    *,
    command: str,
    verbose: bool = False,
    invocation_id: str | None = None,
    **context,
):
    invocation_id = invocation_id or uuid.uuid4().hex
    attempt_id = uuid.uuid4().hex
    path = None
    process_start_ticks = None
    try:
        process_start_ticks = process_identity(os.getpid())["start_ticks"]
        if root is not None:
            root = Path(root).resolve()
            common = Path(
                subprocess.run(
                    ["git", "-C", str(root), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                    check=True,
                    capture_output=True,
                    text=True,
                ).stdout.strip()
            ).resolve()
            directory = common / "review-setup" / "logs" / datetime.now(timezone.utc).date().isoformat()
            if directory.is_symlink():
                raise OSError("symlink log directory")
            directory.mkdir(mode=0o700, parents=True, exist_ok=True)
            path = directory / f"{invocation_id}.log.jsonl"
            descriptor = os.open(
                path,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
            )
            os.close(descriptor)
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        path = None
    operation = OperationLog(
        path,
        command=command,
        verbose=verbose,
        context={
            "invocation_id": invocation_id,
            "attempt_id": attempt_id,
            "operation_id": None,
            "run_id": None,
            "pr_number": context.pop("pr_number", None),
            "slot_id": context.pop("slot_id", None),
            "repo_id": context.pop("repo_id", None),
            "store_id": context.pop("store_id", None),
            "process_id": os.getpid(),
            "process_start_ticks": process_start_ticks,
            "command_class": command,
            **{key: _safe_log_value(key, value) for key, value in context.items()},
        },
    )
    token = _OPERATION_LOG.set(operation)
    operation.event("operation.begin", "Operation began", mode=command)
    try:
        yield operation
    except BaseException as exc:
        operation.finish(getattr(exc, "code", 1), type(exc).__name__)
        raise
    finally:
        operation.event(
            "operation.end",
            "Operation ended",
            actual_exit=operation.actual_exit,
            failure_category=operation.failure_category,
            duration_ms=max(0, round((time.monotonic() - operation.started) * 1000, 3)),
            outcome="success" if operation.actual_exit == 0 else "failed",
        )
        _OPERATION_LOG.reset(token)


def log_event(event: str, message: str, *, level: str = "Information", **fields) -> None:
    operation = _OPERATION_LOG.get()
    if operation is not None:
        operation.event(event, message, level=level, **fields)


def bind_operation_context(**fields) -> None:
    operation = _OPERATION_LOG.get()
    if operation is not None:
        operation.bind(**fields)


class LockSession:
    """One aggregate budget charged only for time actually blocked on flocks."""

    def __init__(self, timeout: float):
        if (
            not isinstance(timeout, (int, float))
            or isinstance(timeout, bool)
            or not 0 <= timeout <= 86400
        ):
            raise ReviewError("Lock timeout must be between 0 and 86400 seconds.")
        self.timeout = float(timeout)
        self.waited = 0.0

    def remaining(self) -> float:
        return max(0.0, self.timeout - self.waited)

    def charge(self, seconds: float) -> float:
        if seconds <= 0:
            return 0.0
        charged = min(seconds, self.remaining())
        self.waited += charged
        fetch = _FETCH_SESSION.get()
        if fetch is not None:
            fetch.exclude_lock_wait(charged)
        return charged


@contextmanager
def lock_session(timeout: float = 1200):
    """Share one lock-wait budget across an invocation without touching fetch time."""
    existing = _LOCK_SESSION.get()
    if existing is not None:
        yield existing
        return
    session = LockSession(timeout)
    token = _LOCK_SESSION.set(session)
    try:
        yield session
    finally:
        _LOCK_SESSION.reset(token)


def _lock_owner_rows(path: Path) -> list[dict]:
    directory = path.parent / ".lock-owners" / hashlib.sha256(path.name.encode()).hexdigest()
    if directory.is_symlink() or not directory.is_dir():
        return []
    rows = []
    for candidate in directory.glob("*.json"):
        try:
            value = json.loads(candidate.read_text(encoding="utf-8"))
            if isinstance(value, dict):
                rows.append(value)
        except (OSError, ValueError):
            continue
    return rows


def _lock_timeout_message(resource: str, scope: str, session: LockSession, path: Path) -> str:
    owners = _lock_owner_rows(path)
    details = ""
    if owners:
        owner = owners[0]
        details = (
            f"; best-effort owner pid={owner.get('pid', 'unknown')} "
            f"phase={owner.get('phase', 'unknown')} scope={owner.get('scope', 'unknown')}"
        )
    return (
        f"Timed out waiting for {resource} {scope}; waited {session.waited:.2f}s of "
        f"the {session.timeout:.2f}s lock budget{details}. Diagnostics are not ownership proof."
    )


def _lock_rank(kind: str) -> int:
    return {
        "compatibility": 10,
        "registry": 20,
        "slot": 30,
        "acquisition": 40,
        "outer": 40,
        "source": 50,
    }[kind]


@contextmanager
def _scoped_lock(
    path: Path,
    *,
    kind: str,
    resource: str,
    scope: str,
    shared: bool = False,
    phase: str = "unspecified",
    wait: bool = True,
):
    """Acquire a real flock and record its actual held scope in this context."""
    held = _HELD_LOCKS.get()
    rank = _lock_rank(kind)
    kinds = {entry["kind"] for entry in held}
    if kind == "registry" and "slot" in kinds:
        raise ReviewError("Registry lock cannot be reacquired while a slot lock is held.")
    if kind == "outer" and kinds.intersection({"acquisition", "source"}):
        raise ReviewError("Outer administration cannot nest with acquisition/source locks.")
    if kind in {"acquisition", "source"} and "outer" in kinds:
        raise ReviewError("Acquisition/source locks cannot nest with outer administration.")
    if kind == "acquisition" and any(
        entry["kind"] == "source" and entry["mode"] == "shared" for entry in held
    ):
        raise ReviewError("Acquisition cannot begin under a source-reader lock.")
    if held and rank < max(entry["rank"] for entry in held):
        raise ReviewError(f"Lock order violation while acquiring {resource} {scope}.")
    if kind != "compatibility" and "compatibility" not in kinds:
        raise ReviewError(f"{resource} {scope} requires the compatibility lock.")
    for entry in held:
        if entry["path"] == path:
            if entry["mode"] == "exclusive" or shared:
                yield entry
                return
            raise ReviewError(f"Cannot upgrade held shared {resource} {scope} lock.")

    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError(f"Refusing symlink {resource} lock path.")
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    owner_path = None
    mode = fcntl.LOCK_SH if shared else fcntl.LOCK_EX
    session = _LOCK_SESSION.get()
    if session is None:
        session = LockSession(0 if not wait else 1200)
    try:
        started_wait = time.monotonic()
        blocked_since = None
        charged_through = started_wait
        last_heartbeat = started_wait
        retry_ceiling = 0.05
        jitter = secrets.SystemRandom()
        while True:
            if blocked_since is not None:
                now = time.monotonic()
                session.charge(now - charged_through)
                charged_through = now
                if session.remaining() <= 0:
                    raise ReviewError(
                        _lock_timeout_message(resource, scope, session, path)
                    )
            try:
                fcntl.flock(fd, mode | fcntl.LOCK_NB)
                acquired_at = time.monotonic()
                if blocked_since is not None:
                    session.charge(acquired_at - charged_through)
                    if session.remaining() <= 0:
                        fcntl.flock(fd, fcntl.LOCK_UN)
                        raise ReviewError(
                            _lock_timeout_message(resource, scope, session, path)
                        )
                break
            except BlockingIOError:
                now = time.monotonic()
                if blocked_since is None:
                    blocked_since = started_wait
                    last_heartbeat = now
                    log_event(
                        "lock.wait.begin",
                        "Waiting for lifecycle lock",
                        level="Debug",
                        lock_class=kind,
                        lock_scope=scope,
                        mode="shared" if shared else "exclusive",
                        phase=phase,
                    )
                session.charge(now - charged_through)
                charged_through = now
                if now - last_heartbeat >= 10:
                    log_event(
                        "lock.wait.heartbeat",
                        "Still waiting for lifecycle lock",
                        level="Debug",
                        lock_class=kind,
                        lock_scope=scope,
                        mode="shared" if shared else "exclusive",
                        phase=phase,
                        wait_ms=round((now - blocked_since) * 1000, 3),
                    )
                    last_heartbeat = now
                if not wait or session.remaining() <= 0:
                    raise ReviewError(
                        _lock_timeout_message(resource, scope, session, path)
                    ) from None
                # Jitter prevents synchronized contenders from retrying in
                # lock-step. Start near the prior 50 ms cadence, then
                # exponentially widen the window to at most one second.
                delay = min(
                    session.remaining(),
                    jitter.uniform(retry_ceiling * 0.8, retry_ceiling),
                )
                time.sleep(delay)
                retry_ceiling = min(retry_ceiling * 2, 1.0)
        log_event(
            "lock.acquired",
            "Lifecycle lock acquired",
            level="Debug",
            lock_class=kind,
            lock_scope=scope,
            mode="shared" if shared else "exclusive",
            phase=phase,
            wait_ms=round((acquired_at - started_wait) * 1000, 3),
        )
        owner_dir = path.parent / ".lock-owners" / hashlib.sha256(path.name.encode()).hexdigest()
        if owner_dir.is_symlink():
            raise ReviewError("Refusing symlink lock-owner directory.")
        owner_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
        owner_path = owner_dir / f"{os.getpid()}-{uuid.uuid4().hex}.json"
        write_new_json(
            owner_path,
            {
                "pid": os.getpid(),
                "kind": kind,
                "mode": "shared" if shared else "exclusive",
                "resource": resource,
                "scope": scope,
                "phase": phase,
                "acquired_at": utc_now(),
            },
        )
        entry = {
            "path": path,
            "kind": kind,
            "rank": rank,
            "mode": "shared" if shared else "exclusive",
            "resource": resource,
            "scope": scope,
            "owner_path": owner_path,
        }
        token = _HELD_LOCKS.set((*held, entry))
        try:
            yield entry
        finally:
            log_event(
                "lock.released",
                "Lifecycle lock released",
                level="Debug",
                lock_class=kind,
                lock_scope=scope,
                mode="shared" if shared else "exclusive",
                phase=phase,
                hold_ms=max(0, round((time.monotonic() - acquired_at) * 1000, 3)),
            )
            _HELD_LOCKS.reset(token)
    finally:
        if owner_path is not None:
            try:
                owner_path.unlink()
            except FileNotFoundError:
                pass
        # Closing, rather than LOCK_UN, preserves inherited open-file-description
        # semantics if a positively-owned subprocess still holds this descriptor.
        os.close(fd)


def _held_lock(path: Path, *, exclusive: bool = False) -> bool:
    for entry in _HELD_LOCKS.get():
        if entry["path"] == path and (not exclusive or entry["mode"] == "exclusive"):
            return True
    return False


def _slot_writer_path(state, repo_key: str, slot: int) -> Path:
    return state.directory / "slot-writers" / f"{repo_key}-{slot}.json"


def _slot_writer_current_scope(state, repo_key: str, slot: int) -> bool:
    scope = _SLOT_WRITER.get()
    return bool(
        scope
        and scope["state"] is state
        and scope["repo_key"] == repo_key
        and scope["slot"] == slot
    )


def _read_slot_writer(state, repo_key: str, slot: int) -> tuple[Path, bytes | None, dict | None]:
    path = _slot_writer_path(state, repo_key, slot)
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError("Refusing symlink slot-writer journal.")
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return path, None, None
    except OSError:
        raise ReviewError(
            "Slot-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    try:
        record = json.loads(raw)
    except ValueError:
        raise ReviewError(
            "Slot-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    if (
        not isinstance(record, dict)
        or record.get("schema_version") != 1
        or record.get("workspace_identity") != state.identity
        or record.get("repo_key") != repo_key
        or record.get("slot_number") != slot
    ):
        raise ReviewError(
            "Slot-writer journal identity is incompatible; operator-scoped recovery is required."
        )
    operation = record.get("operation")
    if operation is not None and (
        not isinstance(operation, dict)
        or not isinstance(operation.get("id"), str)
        or not re.fullmatch(r"[0-9a-f]{32}", operation["id"])
        or not isinstance(operation.get("phase"), str)
        or not operation["phase"]
        or type(operation.get("spawn_pending")) is not bool
        or not isinstance(operation.get("coordinator"), dict)
        or operation.get("process") is not None
        and not isinstance(operation.get("process"), dict)
    ):
        raise ReviewError(
            "Slot-writer journal operation is incompatible; operator-scoped recovery is required."
        )
    if operation is not None:
        for label in ("coordinator", "process"):
            identity = operation.get(label)
            if identity is None and label == "process":
                continue
            if (
                not isinstance(identity.get("pid"), int)
                or isinstance(identity.get("pid"), bool)
                or identity["pid"] < 1
                or not isinstance(identity.get("start_ticks"), int)
                or isinstance(identity.get("start_ticks"), bool)
                or identity["start_ticks"] < 0
                or label == "process"
                and (
                    not isinstance(identity.get("pgid"), int)
                    or isinstance(identity.get("pgid"), bool)
                    or identity["pgid"] < 1
                    or not isinstance(identity.get("session"), int)
                    or isinstance(identity.get("session"), bool)
                    or identity["session"] < 1
                )
                or not isinstance(identity.get("boot_id"), str)
                or not identity["boot_id"]
                or not isinstance(identity.get("pid_namespace"), str)
                or not identity["pid_namespace"]
            ):
                raise ReviewError(
                    "Slot-writer process identity is incompatible; operator-scoped recovery is required."
                )
        if operation.get("spawn_pending") and operation.get("process") is not None:
            raise ReviewError(
                "Slot-writer pending identity is ambiguous; operator-scoped recovery is required."
            )
    return path, raw, record


def _slot_writer_journal_stale_reason(
    operation: dict, *, requested_pr: int | None, now: datetime
) -> str | None:
    """Classify a not-yet-provably-quiescent slot-writer operation as stale
    (forward-only: safe to reap and continue) under the same policy already
    applied to pool-run manifests (`_pool_run_stale_reason`): unconditionally
    stale when it already belongs to the exact PR the caller is requesting
    (a retry of that same request, not someone else's live review), or once
    it has gone untouched past `POOL_RUN_ABANDONED_AFTER_SECONDS`. A journal
    proven to belong to a still-executing process group is never classified
    here -- that liveness proof always blocks, matching "retain blocking for
    fresh, different-PR state."
    """
    if requested_pr is not None:
        return "same-pr"
    raw = operation.get("started_at")
    if not isinstance(raw, str):
        return "expired"
    try:
        started_at = datetime.fromisoformat(raw)
    except ValueError:
        return "expired"
    if started_at.tzinfo is None:
        return "expired"
    if (now - started_at) > timedelta(seconds=POOL_RUN_ABANDONED_AFTER_SECONDS):
        return "expired"
    return None


def _slot_writer_assert_quiescent(
    state,
    repo_key: str,
    slot: int,
    *,
    reap_stale: bool = False,
    requested_pr: int | None = None,
    dry_run: bool = False,
) -> str | None:
    """Validate a prior SLOT writer before any selected-slot mutation.

    By default (`reap_stale=False`), behavior is unchanged from before: an
    unreadable, schema-incompatible, or not-yet-provably-quiescent journal
    always raises, and this returns `None` on success.

    Pass `reap_stale=True` (the pooled-reset flow only) to opt a caller into
    the same forward-only stale-journal policy already used for pool-run
    manifests: a journal that fails to read/parse, or whose schema or
    workspace identity does not match, is unconditionally stale -- exactly
    as an unparseable or identity-incompatible pool-run manifest already is
    (see `_pool_run_stale_reason`). A journal recording a still-pending or
    unconfirmed launch, or one from another boot/PID namespace, is stale
    once `requested_pr` names the exact PR this reset already targets, or
    once it is older than `POOL_RUN_ABANDONED_AFTER_SECONDS` (30 minutes).
    A journal proven to belong to a still-executing process group is never
    stale -- that liveness check always blocks, regardless of `reap_stale`,
    `requested_pr`, or age; only manual operator-scoped recovery clears it.

    Pass `dry_run=True` to classify without reaping, so an inspection
    caller can report what would happen without mutating anything.

    Returns the stale reason when a stale journal was reaped (or would be,
    under `dry_run`), or `None` when the slot was already quiescent (no
    journal, or no pending operation) with nothing to reap.
    """
    path = _slot_writer_path(state, repo_key, slot)
    try:
        _, _, record = _read_slot_writer(state, repo_key, slot)
    except ReviewError:
        if not reap_stale:
            raise
        if not dry_run:
            path.unlink(missing_ok=True)
        return "unreadable-or-incompatible"
    if record is None:
        return None
    operation = record.get("operation")
    if operation is None:
        return None
    now = datetime.now(timezone.utc)
    if operation.get("spawn_pending") or not operation.get("process"):
        if reap_stale:
            reason = _slot_writer_journal_stale_reason(
                operation, requested_pr=requested_pr, now=now
            )
            if reason is not None:
                if not dry_run:
                    path.unlink(missing_ok=True)
                return reason
        raise ReviewError(
            "Slot writer has pending or missing launch identity; operator-scoped recovery is required."
        )
    identity = operation["process"]
    current = process_identity(os.getpid())
    if any(
        identity.get(key) != current.get(key)
        for key in ("boot_id", "pid_namespace")
    ):
        if reap_stale:
            reason = _slot_writer_journal_stale_reason(
                operation, requested_pr=requested_pr, now=now
            )
            if reason is not None:
                if not dry_run:
                    path.unlink(missing_ok=True)
                return reason
        raise ReviewError(
            "Recorded slot writer is in another boot/PID namespace; retirement is unproven and operator-scoped recovery is required."
        )
    rows = _process_census()
    members = [
        row
        for row in rows
        if row["pgid"] == identity.get("pgid")
        or row["session"] == identity.get("session")
    ]
    if any(row["state"] != "Z" for row in members):
        raise ReviewError(
            "Recorded slot writer process group is still executable; selected-slot mutation is refused."
        )
    if dry_run:
        return None
    record["operation"] = None
    record["last_settled_at"] = utc_now()
    record["last_settled_operation"] = operation
    atomic_json(path, record)
    return None


def _optional_file_binding(path: Path, label: str) -> tuple[str | None, bytes | None]:
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError(f"Refusing symlink {label}.")
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return None, None
    except OSError:
        raise ReviewError(f"{label.capitalize()} is unreadable.") from None
    return hashlib.sha256(raw).hexdigest(), raw


def _slot_recovery_binding(state, pool, slot: int, operation: dict) -> dict:
    run_path = state.pool_run_path(pool.repo_key, slot)
    run_hash, run_raw = _optional_file_binding(run_path, "pool-run state")
    manifest = None
    if run_raw is not None:
        manifest = state.load_pool_run(pool.repo_key, slot)
        if (
            manifest.get("repo_name") != pool.repo_name
            or manifest.get("slot_id") != pool.slot_name(slot)
            or manifest.get("slot_path") != pool.slot_relative_path(slot)
            or parse_ado_url(manifest.get("submodule", {}).get("url", ""))
            != parse_ado_url(pool.module["url"])
            or manifest.get("submodule", {}).get("path") != pool.module["path"]
            or not isinstance(manifest.get("run_id"), str)
            or not manifest["run_id"]
        ):
            raise ReviewError("Pool-run state does not match the selected slot topology.")

    pool_hash, pool_raw = _optional_file_binding(pool.state_path, "source-pool state")
    if pool_raw is not None:
        pool._read_state()

    reservations = []
    reservation_dir = state.directory / "setup-reservations"
    if reservation_dir.is_symlink():
        raise ReviewError("Refusing symlink setup reservation registry.")
    if reservation_dir.exists():
        for candidate in sorted(reservation_dir.glob(f"{pool.repo_key}-*.json")):
            digest, raw = _optional_file_binding(candidate, "setup reservation")
            try:
                value = json.loads(raw)
            except (TypeError, ValueError):
                raise ReviewError("Setup reservation is unreadable.") from None
            if (
                not isinstance(value, dict)
                or value.get("schema_version") != 1
                or value.get("identity") != state.identity
                or value.get("repo_key") != pool.repo_key
            ):
                raise ReviewError("Setup reservation has incompatible identity or schema.")
            if value.get("slot_number") == slot:
                reservations.append(
                    {
                        "path": candidate.name,
                        "sha256": digest,
                        "id": value.get("id"),
                        "key": value.get("key"),
                        "snapshot_fingerprint": value.get("snapshot_fingerprint"),
                    }
                )

    phase = operation.get("phase")
    if phase not in ("warm-slot", "setup-slot", "pooled-reset"):
        raise ReviewError("Slot-writer phase is not supported for explicit recovery.")
    if phase == "pooled-reset" and manifest is None:
        raise ReviewError("Pooled-reset journal no longer has a bound pool run.")
    if phase == "setup-slot" and manifest is None and not reservations:
        raise ReviewError("Setup journal no longer has a bound run or setup reservation.")

    outer = safe_path(state.root, pool.slot_relative_path(slot))
    nested = safe_path(
        state.root, f"{pool.slot_relative_path(slot)}/{pool.module['path']}"
    )
    topology = {
        "outer": "absent",
        "nested": "absent",
        "outer_head": None,
        "outer_branch": None,
        "source_head": None,
        "run_status": manifest.get("status") if manifest else None,
        "run_phase": manifest.get("phase") if manifest else None,
        "reset_phase": manifest.get("reset_phase") if manifest else None,
    }
    allowed_setup_source_paths = set()
    if phase == "setup-slot":
        cleanup_path = state.directory / "cleanup" / f"{pool.repo_key}-{slot}.json"
        cleanup_hash, cleanup_raw = _optional_file_binding(
            cleanup_path, "setup cleanup journal"
        )
        topology["cleanup_sha256"] = cleanup_hash
        topology["cleanup_phase"] = None
        if cleanup_raw is not None:
            try:
                cleanup = json.loads(cleanup_raw)
            except ValueError:
                raise ReviewError("Setup cleanup journal is unreadable.") from None
            if (
                not isinstance(cleanup, dict)
                or cleanup.get("identity") != state.identity
                or cleanup.get("phase") not in (
                    "cleaning",
                    "normalizing",
                    "switching",
                    "cleaned",
                    "done",
                )
                or not isinstance(cleanup.get("inventory"), dict)
                or not isinstance(cleanup["inventory"].get("source"), list)
            ):
                raise ReviewError("Setup cleanup journal does not match the selected slot.")
            allowed_setup_source_paths = set(cleanup["inventory"]["source"])
            topology["cleanup_phase"] = cleanup["phase"]

    with state.outer_lock(phase="slot-recovery-outer-validation"):
        if outer.exists():
            ensure_repository(outer, read_only=True)
            if outer.resolve() not in state.registered(read_only=True):
                raise ReviewError("Selected recovery slot is not an owned registered worktree.")
            common = run_git_read(
                outer,
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            ).stdout.strip()
            if Path(common).resolve() != state.common:
                raise ReviewError("Selected recovery slot belongs to another outer store.")
            assert_no_git_operation(outer)
            branch = current_ref(outer, read_only=True)
            topology.update(
                outer="registered",
                outer_head=validate_sha(
                    run_git_read(outer, ["rev-parse", "HEAD"]).stdout.strip()
                ),
                outer_branch=branch,
            )
            if manifest is None:
                if branch is not None:
                    raise ReviewError("Unbound recovery slot is attached to a branch.")
            else:
                reset_detached = manifest.get("reset_phase") in (
                    "outer-detach-intent",
                    "outer-detached",
                    "source-reset-intent",
                    "source-reset",
                    "source-verified",
                    "release-intent",
                    "released",
                )
                allowed_branches = {manifest.get("branch")}
                if manifest.get("archive_branch"):
                    allowed_branches.add(manifest["archive_branch"])
                if reset_detached:
                    allowed_branches.add(None)
                if branch not in allowed_branches:
                    raise ReviewError("Selected recovery slot branch changed outside its run.")
        elif manifest is not None and manifest.get("phase") not in (
            "reservation-intent",
            "warm-intent",
        ):
            raise ReviewError("Selected recovery slot disappeared after its run advanced.")

    with pool.acquisition_lock(phase="slot-recovery-source-validation"):
        with pool.store_lock(
            phase="slot-recovery-source-validation", check_ready=False
        ):
            if pool.store.exists():
                if pool.store.is_symlink():
                    raise ReviewError("Refusing symlink source store during slot recovery.")
                bare = run_git_read(
                    pool.store, ["rev-parse", "--is-bare-repository"], check=False
                )
                if bare.returncode or bare.stdout.strip() != "true":
                    raise ReviewError("Selected slot source store is not an owned bare repository.")
                origin = run_git_read(
                    pool.store, ["config", "--get", "remote.origin.url"]
                ).stdout.strip()
                if parse_ado_url(origin) != parse_ado_url(pool.module["url"]):
                    raise ReviewError("Selected slot source-store origin changed.")
                for name in (
                    "MERGE_HEAD",
                    "CHERRY_PICK_HEAD",
                    "REVERT_HEAD",
                    "rebase-merge",
                    "rebase-apply",
                    "index.lock",
                    "packed-refs.lock",
                    "shallow.lock",
                    "config.lock",
                ):
                    if (pool.store / name).exists():
                        raise ReviewError(
                            "Selected slot source store has an active Git operation or lock."
                        )
            elif nested.exists() and any(nested.iterdir()):
                raise ReviewError("Selected slot has source content without its owned store.")

            if (nested / ".git").exists():
                ensure_repository(nested, read_only=True)
                source_common = run_git_read(
                    nested,
                    ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                ).stdout.strip()
                if Path(source_common).resolve() != pool.store.resolve():
                    raise ReviewError("Selected slot source belongs to another store.")
                if nested.resolve() not in {
                    row["worktree"]
                    for row in worktree_rows(pool.store, read_only=True)
                }:
                    raise ReviewError("Selected slot source is not an owned registered worktree.")
                assert_no_git_operation(nested)
                if current_ref(nested, read_only=True) is not None:
                    raise ReviewError("Selected slot source is attached.")
                source_status = git_status(nested, read_only=True)
                if source_status:
                    observed = {path for _, path in source_status}
                    if phase != "setup-slot" or not allowed_setup_source_paths or not observed.issubset(
                        allowed_setup_source_paths
                    ):
                        raise ReviewError("Selected slot source is dirty outside its cleanup journal.")
                topology.update(
                    nested="registered",
                    source_head=validate_sha(
                        run_git_read(nested, ["rev-parse", "HEAD"]).stdout.strip()
                    ),
                )
            elif nested.exists():
                if any(nested.iterdir()):
                    raise ReviewError("Selected slot source placeholder contains foreign content.")
                topology["nested"] = "empty-placeholder"
            elif outer.exists():
                raise ReviewError("Selected outer slot has no source placeholder.")

    shared_path, shared_raw, shared_record = _read_shared_writer(
        state, pool.repo_key
    )
    shared_operation = shared_record.get("operation") if shared_record else None
    if shared_operation is not None and (
        shared_operation.get("slot_number") != slot
        or shared_operation.get("id") != operation["id"]
    ):
        raise ReviewError(
            "Shared source writer belongs to another operation; recover its owning slot."
        )

    binding = {
        "workspace_identity": state.identity,
        "repo_key": pool.repo_key,
        "repo_name": pool.repo_name,
        "slot_number": slot,
        "slot_id": pool.slot_name(slot),
        "operation_id": operation["id"],
        "operation_phase": phase,
        "shared_writer_sha256": (
            hashlib.sha256(shared_raw).hexdigest()
            if shared_raw is not None
            else None
        ),
        "shared_operation_id": (
            shared_operation.get("id") if shared_operation else None
        ),
        "pool_state_sha256": pool_hash,
        "pool_run_sha256": run_hash,
        "run_id": manifest.get("run_id") if manifest else None,
        "setup_reservations": reservations,
        "topology": topology,
    }
    binding["topology_sha256"] = hashlib.sha256(
        json.dumps(topology, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return binding


def recover_slot_writer(
    state,
    pool,
    slot: int,
    *,
    assertion: dict | None = None,
    inspect: bool = False,
) -> dict:
    """Inspect or explicitly settle one unobservable selected-slot writer."""
    slot = pool._validate_slots([slot])[0]
    compatibility = any(
        entry["kind"] == "compatibility" for entry in _HELD_LOCKS.get()
    )
    slot_held = any(
        entry["kind"] == "slot"
        and entry["mode"] == "exclusive"
        and entry["resource"] == pool.repo_key
        and entry["scope"] == f"slot {slot}"
        for entry in _HELD_LOCKS.get()
    )
    if not compatibility or not slot_held:
        raise ReviewError(
            "Slot recovery requires the actual compatibility and selected-slot exclusive locks."
        )

    path, slot_raw, slot_record = _read_slot_writer(
        state, pool.repo_key, slot
    )
    shared_path, shared_raw, shared_record = _read_shared_writer(
        state, pool.repo_key
    )
    slot_operation = slot_record.get("operation") if slot_record else None
    shared_operation = shared_record.get("operation") if shared_record else None
    operation = slot_operation or shared_operation
    if slot_operation is None and shared_operation is not None:
        if shared_operation.get("slot_number") != slot:
            raise ReviewError(
                f"Shared source writer belongs to slot {shared_operation.get('slot_number')}; recover that slot."
            )
        last_slot_operation = (
            slot_record.get("last_settled_operation") if slot_record else None
        )
        if (
            last_slot_operation is not None
            and last_slot_operation.get("id") != shared_operation.get("id")
        ):
            raise ReviewError(
                "Selected slot was settled for a different operation; recovery is refused."
            )
    slot_digest = (
        hashlib.sha256(slot_raw).hexdigest()
        if slot_raw is not None
        else None
    )
    shared_digest = (
        hashlib.sha256(shared_raw).hexdigest()
        if shared_raw is not None
        else None
    )
    if operation is None:
        return {
            "workspace_identity": state.identity,
            "repo_key": pool.repo_key,
            "repo_name": pool.repo_name,
            "slot_number": slot,
            "slot_id": pool.slot_name(slot),
            "slot_writer_sha256": slot_digest,
            "shared_writer_sha256": shared_digest,
            "pending": False,
            "recovery_required": False,
        }

    binding = _slot_recovery_binding(state, pool, slot, operation)
    current = process_identity(os.getpid())
    details = {
        **binding,
        "slot_writer_sha256": slot_digest,
        "current_pid_namespace": current["pid_namespace"],
        "pending": True,
        "recovery_required": True,
    }
    identity = operation.get("process")
    identities = []
    if isinstance(operation.get("coordinator"), dict):
        identities.append(("coordinator", operation["coordinator"]))
    if isinstance(identity, dict):
        identities.append(("process group", identity))

    def assert_no_observable_recorded_writer():
        current_rows = _process_census()
        for label, prior in identities:
            if all(
                prior.get(key) == current.get(key)
                for key in ("boot_id", "pid_namespace")
            ):
                if label == "coordinator":
                    active = [
                        row
                        for row in current_rows
                        if row["pid"] == prior.get("pid")
                        and row["start_ticks"] == prior.get("start_ticks")
                    ]
                else:
                    active = [
                        row
                        for row in current_rows
                        if row["pgid"] == prior.get("pgid")
                        or row["session"] == prior.get("session")
                    ]
                if any(row["state"] != "Z" for row in active):
                    raise ReviewError(
                        f"Recorded slot writer {label} is still executable; recovery is refused."
                    )

    assert_no_observable_recorded_writer()
    if inspect:
        return details

    required = {
        key: details[key]
        for key in (
            "workspace_identity",
            "repo_key",
            "repo_name",
            "slot_number",
            "slot_id",
            "slot_writer_sha256",
            "operation_id",
            "operation_phase",
            "shared_writer_sha256",
            "shared_operation_id",
            "pool_state_sha256",
            "pool_run_sha256",
            "run_id",
            "setup_reservations",
            "topology_sha256",
            "current_pid_namespace",
        )
    }
    required["prior_writers_retired"] = True
    if (
        not isinstance(assertion, dict)
        or any(assertion.get(key) != value for key, value in required.items())
        or not isinstance(assertion.get("operator"), str)
        or not assertion["operator"].strip()
        or not isinstance(assertion.get("basis"), str)
        or not assertion["basis"].strip()
    ):
        raise ReviewError(
            "Operator assertion must bind this exact workspace/state/slot/run/topology and describe verified broader-scope prior-writer retirement."
        )

    # Repeat every dynamic check and bind the physical slot/shared bytes separately
    # from the logical operation. Either journal may already be settled or absent
    # after a crash between the durable recovery publications.
    assert_no_observable_recorded_writer()
    path2, slot_raw2, slot_record2 = _read_slot_writer(
        state, pool.repo_key, slot
    )
    shared_path2, shared_raw2, shared_record2 = _read_shared_writer(
        state, pool.repo_key
    )
    if path2 != path or slot_raw2 != slot_raw or slot_record2 != slot_record:
        raise ReviewError("Slot-writer journal changed during recovery.")
    if (
        shared_path2 != shared_path
        or shared_raw2 != shared_raw
        or shared_record2 != shared_record
    ):
        raise ReviewError("Shared-writer journal changed during recovery.")
    if _slot_recovery_binding(state, pool, slot, operation) != binding:
        raise ReviewError("Selected slot state or topology changed during recovery.")

    recovered_at = utc_now()
    audit = {
        "schema_version": 1,
        "recovered_at": recovered_at,
        "proof": "operator-asserted prior-writer retirement",
        "binding": details,
        "operator_assertion": assertion,
        "previous_slot_journal": slot_record,
        "previous_shared_journal": shared_record,
    }
    audit_path = (
        state.directory
        / "slot-writer-recoveries"
        / f"{pool.repo_key}-{slot}-{uuid.uuid4().hex}.json"
    )
    write_new_json(audit_path, audit)

    current_path, current_slot_raw, current_slot = _read_slot_writer(
        state, pool.repo_key, slot
    )
    if current_path != path or current_slot_raw != slot_raw:
        raise ReviewError("Slot-writer journal changed before recovery publication.")
    current_slot_operation = (
        current_slot.get("operation") if current_slot else None
    )
    if current_slot_operation is not None and current_slot_operation != operation:
        raise ReviewError("A different slot-writer operation replaced the recovered operation.")
    if current_slot is None:
        settled = {
            "schema_version": 1,
            "workspace_identity": state.identity,
            "repo_key": pool.repo_key,
            "slot_number": slot,
            "operation": None,
        }
    else:
        settled = dict(current_slot)
    settled["operation"] = None
    settled["last_settled_at"] = recovered_at
    settled["last_settled_operation"] = operation
    settled["last_settlement_proof"] = "operator-asserted prior-writer retirement"
    settled["last_recovery_audit"] = audit_path.name
    atomic_json(path, settled)

    if binding["shared_operation_id"] is not None:
        current_shared_path, current_shared_raw, current_shared = _read_shared_writer(
            state, pool.repo_key
        )
        if (
            current_shared_path != shared_path
            or current_shared_raw != shared_raw
            or (current_shared.get("operation") or {}).get("id")
            != binding["shared_operation_id"]
        ):
            raise ReviewError("Shared-writer journal changed during recovery.")
        shared_settled = dict(current_shared)
        shared_settled["operation"] = None
        shared_settled["last_settled_at"] = recovered_at
        shared_settled["last_settled_operation"] = current_shared["operation"]
        shared_settled["last_settlement_proof"] = (
            "operator-asserted prior-writer retirement"
        )
        shared_settled["last_recovery_audit"] = audit_path.name
        atomic_json(shared_path, shared_settled)
    return {
        **details,
        "pending": False,
        "recovery_required": False,
        "proof": "operator-asserted prior-writer retirement",
        "recovery_audit": str(audit_path),
    }


def _outer_transaction_path(state) -> Path:
    return state.directory / "outer-merge-transaction.json"


def _outer_writer_path(state) -> Path:
    return state.directory / "outer-writer.json"


def load_outer_transaction(state) -> dict | None:
    """Load the one durable canonical-root merge reservation, if present."""
    path = _outer_transaction_path(state)
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError("Refusing symlink outer-merge transaction state.")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None
    except (OSError, ValueError):
        raise ReviewError(
            "Outer-merge transaction is unreadable; operator-scoped recovery is required."
        ) from None
    required_strings = (
        "id",
        "workspace_identity",
        "run_id",
        "repo_key",
        "slot_id",
        "branch",
        "review_tip",
        "root_start",
        "main_branch",
        "baseline_branch",
        "baseline_sha",
        "module_path",
        "phase",
    )
    if (
        not isinstance(value, dict)
        or value.get("schema_version") != 1
        or value.get("workspace_identity") != state.identity
        or any(not isinstance(value.get(key), str) or not value[key] for key in required_strings)
        or not re.fullmatch(r"[0-9a-f]{32}", value["id"])
        or not isinstance(value.get("slot_number"), int)
        or isinstance(value.get("slot_number"), bool)
        or value["slot_number"] not in range(SourcePool.SLOT_COUNT)
        or not isinstance(value.get("allowed_paths"), list)
        or any(not isinstance(path_value, str) for path_value in value["allowed_paths"])
    ):
        raise ReviewError(
            "Outer-merge transaction has incompatible identity or schema."
        )
    for key in ("review_tip", "root_start", "baseline_sha"):
        validate_sha(value[key])
    if value.get("expected_merge_head") is not None:
        validate_sha(value["expected_merge_head"])
    if value.get("result_sha") is not None:
        validate_sha(value["result_sha"])
    for relative in value["allowed_paths"]:
        safe_path(state.root, relative)
    return value


def save_outer_transaction(state, transaction: dict) -> None:
    value = dict(transaction)
    value.update(
        {
            "schema_version": 1,
            "workspace_identity": state.identity,
            "updated_at": utc_now(),
        }
    )
    atomic_json(_outer_transaction_path(state), value)
    transaction.update(value)


def clear_outer_transaction(state, transaction_id: str) -> None:
    path = _outer_transaction_path(state)
    current = load_outer_transaction(state)
    if current is None:
        return
    if current.get("id") != transaction_id:
        raise ReviewError("Outer-merge transaction ownership changed before release.")
    path.unlink()
    directory = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def _read_outer_writer(state) -> tuple[Path, bytes | None, dict | None]:
    path = _outer_writer_path(state)
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError("Refusing symlink outer-writer journal.")
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return path, None, None
    except OSError:
        raise ReviewError(
            "Outer-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    try:
        record = json.loads(raw)
    except ValueError:
        raise ReviewError(
            "Outer-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    operation = record.get("operation") if isinstance(record, dict) else None
    if (
        not isinstance(record, dict)
        or record.get("schema_version") != 1
        or record.get("workspace_identity") != state.identity
        or operation is not None
        and (
            not isinstance(operation, dict)
            or not isinstance(operation.get("id"), str)
            or not re.fullmatch(r"[0-9a-f]{32}", operation["id"])
            or not isinstance(operation.get("transaction_id"), str)
            or not re.fullmatch(r"[0-9a-f]{32}", operation["transaction_id"])
            or not isinstance(operation.get("phase"), str)
            or not operation["phase"]
            or type(operation.get("spawn_pending")) is not bool
            or not isinstance(operation.get("coordinator"), dict)
            or operation.get("process") is not None
            and not isinstance(operation.get("process"), dict)
        )
    ):
        raise ReviewError(
            "Outer-writer journal has incompatible identity or schema."
        )
    return path, raw, record


def _recorded_identity_is_active(identity: dict, *, group: bool) -> bool | None:
    """Return True/False when observable, None across boot/PID namespaces."""
    current = process_identity(os.getpid())
    if any(identity.get(key) != current.get(key) for key in ("boot_id", "pid_namespace")):
        return None
    rows = _process_census()
    if group:
        selected = [
            row
            for row in rows
            if row["pgid"] == identity.get("pgid")
            or row["session"] == identity.get("session")
        ]
    else:
        selected = [
            row
            for row in rows
            if row["pid"] == identity.get("pid")
            and row["start_ticks"] == identity.get("start_ticks")
        ]
    return any(row["state"] != "Z" for row in selected)


def check_outer_writer_ready(state) -> None:
    scope = _OUTER_WRITER.get()
    if scope is not None and scope["state"].identity == state.identity:
        return
    _, _, record = _read_outer_writer(state)
    if record is None or record.get("operation") is None:
        return
    operation = record["operation"]
    process = operation.get("process")
    if process and _recorded_identity_is_active(process, group=True):
        raise ReviewError("Recorded canonical-root writer is still executable.")
    raise ReviewError(
        "A canonical-root writer journal remains pending; inspect and explicitly recover it before mutation."
    )


def check_outer_merge_ready(state, *, owner_id: str | None = None) -> dict | None:
    """Gate every canonical-root mutation against the durable merge reservation."""
    contextual_owner = _OUTER_MERGE_OWNER.get()
    owner_id = owner_id or contextual_owner
    check_outer_writer_ready(state)
    transaction = load_outer_transaction(state)
    if transaction is not None and transaction["id"] != owner_id:
        raise ReviewError(
            f"Canonical root is reserved by merge run {transaction['run_id']} "
            f"at phase {transaction['phase']}."
        )
    return transaction


@contextmanager
def outer_merge_owner(transaction_id: str):
    if not isinstance(transaction_id, str) or not re.fullmatch(r"[0-9a-f]{32}", transaction_id):
        raise ReviewError("Invalid outer-merge owner token.")
    existing = _OUTER_MERGE_OWNER.get()
    if existing is not None and existing != transaction_id:
        raise ReviewError("Nested outer-merge ownership does not match.")
    token = _OUTER_MERGE_OWNER.set(transaction_id)
    try:
        yield
    finally:
        _OUTER_MERGE_OWNER.reset(token)


@contextmanager
def outer_writer_scope(state, transaction_id: str, *, phase: str):
    """Journal one canonical-root mutating child while the outer lock is held."""
    outer_path = state.directory / "outer-administration.lock"
    if not _held_lock(outer_path, exclusive=True):
        raise ReviewError("Outer-writer protection requires the outer administration lock.")
    transaction = check_outer_merge_ready(state, owner_id=transaction_id)
    if transaction is None or transaction["id"] != transaction_id:
        raise ReviewError("Outer-writer scope has no matching durable transaction.")
    existing = _OUTER_WRITER.get()
    if existing is not None:
        if existing["state"] is not state or existing["transaction_id"] != transaction_id:
            raise ReviewError("Nested outer-writer scope identifies another transaction.")
        yield existing
        return
    path = _outer_writer_path(state)
    record = {
        "schema_version": 1,
        "workspace_identity": state.identity,
        "operation": None,
    }
    if path.exists():
        record.update(json.loads(path.read_text(encoding="utf-8")))
    else:
        atomic_json(path, record)
    scope = {
        "state": state,
        "transaction_id": transaction_id,
        "phase": phase,
        "path": path,
    }
    token = _OUTER_WRITER.set(scope)

    def started(identity):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if not operation or not operation.get("spawn_pending"):
            raise CleanupNotProvenError(
                "Outer-writer spawn intent changed before identity publication."
            )
        operation["process"] = identity
        operation["spawn_pending"] = False
        operation["exec_ready_at"] = utc_now()
        atomic_json(path, current)

    def arm(operation_id=None):
        current = json.loads(path.read_text(encoding="utf-8"))
        if current.get("operation") is not None:
            raise ReviewError("A canonical-root writer operation is already pending.")
        operation_id = operation_id or uuid.uuid4().hex
        current["operation"] = {
            "id": operation_id,
            "transaction_id": transaction_id,
            "phase": phase,
            "spawn_pending": True,
            "process": None,
            "coordinator": process_identity(os.getpid()),
            "started_at": utc_now(),
        }
        atomic_json(path, current)
        return operation_id

    def not_started(operation_id):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if (
            not operation
            or operation.get("id") != operation_id
            or operation.get("spawn_pending") is not True
            or operation.get("process") is not None
        ):
            raise CleanupNotProvenError(
                "Outer-writer launch intent changed before typed prelaunch settlement."
            )
        current["operation"] = None
        current["last_settled_at"] = utc_now()
        current["last_settled_operation"] = operation
        current["last_settlement_proof"] = "typed-command-not-started"
        atomic_json(path, current)

    try:
        scope.update(arm=arm, started=started, not_started=not_started)
        yield scope
    finally:
        _OUTER_WRITER.reset(token)


def outer_recovery_binding(state) -> dict:
    """Capture immutable transaction and canonical Git topology for recovery review."""
    transaction_path = _outer_transaction_path(state)
    transaction_raw = transaction_path.read_bytes()
    transaction = load_outer_transaction(state)
    if transaction is None:
        raise ReviewError("Outer-writer recovery has no active merge transaction.")
    writer_path, writer_raw, writer = _read_outer_writer(state)
    operation = writer.get("operation") if writer else None
    if operation is None:
        return {
            "pending": False,
            "workspace_identity": state.identity,
            "transaction_id": transaction["id"],
        }
    merge_head_path = Path(
        run_git_read(state.root, ["rev-parse", "--git-path", "MERGE_HEAD"]).stdout.strip()
    )
    if not merge_head_path.is_absolute():
        merge_head_path = state.root / merge_head_path
    merge_head = merge_head_path.read_text(encoding="ascii").strip() if merge_head_path.exists() else None
    topology = {
        "head": validate_sha(run_git_read(state.root, ["rev-parse", "HEAD"]).stdout.strip()),
        "branch": current_ref(state.root, read_only=True),
        "merge_head": validate_sha(merge_head) if merge_head else None,
        "index_sha256": hashlib.sha256(
            run_git_read(state.root, ["ls-files", "--stage", "-z"]).stdout.encode(
                "utf-8", "surrogateescape"
            )
        ).hexdigest(),
        "tracked_status": run_git_read(
            state.root, ["status", "--porcelain=v1", "-z", "--untracked-files=no"]
        ).stdout,
    }
    return {
        "pending": True,
        "workspace_identity": state.identity,
        "transaction_id": transaction["id"],
        "transaction_sha256": hashlib.sha256(transaction_raw).hexdigest(),
        "writer_sha256": hashlib.sha256(writer_raw).hexdigest(),
        "operation_id": operation["id"],
        "operation_phase": operation["phase"],
        "topology": topology,
        "topology_sha256": hashlib.sha256(
            json.dumps(topology, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest(),
    }


def recover_outer_writer(state, *, assertion: dict | None = None, inspect: bool = False) -> dict:
    """Explicitly settle a retired root writer bound to unchanged Git topology."""
    outer_path = state.directory / "outer-administration.lock"
    if not _held_lock(outer_path, exclusive=True):
        raise ReviewError("Outer-writer recovery requires the outer administration lock.")
    writer_path, writer_raw, writer = _read_outer_writer(state)
    operation = writer.get("operation") if writer else None
    details = outer_recovery_binding(state)
    if operation is None or not details["pending"]:
        return details
    identities = [(operation.get("coordinator"), False), (operation.get("process"), True)]
    visibility = []
    for identity, group in identities:
        if not isinstance(identity, dict):
            continue
        active = _recorded_identity_is_active(identity, group=group)
        visibility.append(active)
        if active is True:
            raise ReviewError("A recorded canonical-root writer remains executable.")
    details["writer_visibility"] = (
        "unobservable-namespace" if None in visibility else "retired-observable"
    )
    if inspect:
        details["recovery_required"] = True
        return details
    required = {
        key: details[key]
        for key in (
            "workspace_identity",
            "transaction_id",
            "transaction_sha256",
            "writer_sha256",
            "operation_id",
            "operation_phase",
            "topology_sha256",
            "writer_visibility",
        )
    }
    required["prior_writer_retired"] = True
    if (
        not isinstance(assertion, dict)
        or any(assertion.get(key) != value for key, value in required.items())
        or not isinstance(assertion.get("operator"), str)
        or not assertion["operator"].strip()
        or not isinstance(assertion.get("basis"), str)
        or not assertion["basis"].strip()
    ):
        raise ReviewError(
            "Operator assertion must bind the exact transaction, writer, and topology and attest prior-writer retirement."
        )
    current_path, current_raw, current = _read_outer_writer(state)
    current_details = outer_recovery_binding(state)
    current_details["writer_visibility"] = details["writer_visibility"]
    if current_path != writer_path or current_raw != writer_raw or current_details != details:
        raise ReviewError("Canonical-root state changed during writer recovery.")
    recovered_at = utc_now()
    audit_path = (
        state.directory
        / "outer-writer-recoveries"
        / f"{operation['id']}-{uuid.uuid4().hex}.json"
    )
    write_new_json(
        audit_path,
        {
            "schema_version": 1,
            "recovered_at": recovered_at,
            "binding": details,
            "operator_assertion": assertion,
            "previous_writer_journal": current,
        },
    )
    settled = dict(current)
    settled["operation"] = None
    settled["last_settled_at"] = recovered_at
    settled["last_settled_operation"] = operation
    settled["last_settlement_proof"] = "operator-asserted prior-writer retirement"
    settled["last_recovery_audit"] = audit_path.name
    atomic_json(writer_path, settled)
    return {**details, "pending": False, "recovery_required": False, "recovery_audit": str(audit_path)}


def _shared_writer_path(state, repo_key: str) -> Path:
    return state.directory / "shared-writers" / f"{repo_key}.json"


def _read_shared_writer(state, repo_key: str) -> tuple[Path, bytes | None, dict | None]:
    path = _shared_writer_path(state, repo_key)
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError("Refusing symlink shared-writer journal.")
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return path, None, None
    except OSError:
        raise ReviewError(
            "Shared-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    try:
        record = json.loads(raw)
    except ValueError:
        raise ReviewError(
            "Shared-writer journal is unreadable; operator-scoped recovery is required."
        ) from None
    if (
        not isinstance(record, dict)
        or record.get("schema_version") != 1
        or record.get("workspace_identity") != state.identity
        or record.get("repo_key") != repo_key
    ):
        raise ReviewError(
            "Shared-writer journal identity is incompatible; operator-scoped recovery is required."
        )
    operation = record.get("operation")
    if operation is not None and (
        not isinstance(operation, dict)
        or not isinstance(operation.get("id"), str)
        or not re.fullmatch(r"[0-9a-f]{32}", operation["id"])
        or not isinstance(operation.get("phase"), str)
        or not operation["phase"]
        or not isinstance(operation.get("slot_number"), int)
        or isinstance(operation.get("slot_number"), bool)
        or operation["slot_number"] < 0
        or operation["slot_number"] >= SourcePool.SLOT_COUNT
        or type(operation.get("spawn_pending")) is not bool
        or not isinstance(operation.get("coordinator"), dict)
        or operation.get("process") is not None
        and not isinstance(operation.get("process"), dict)
    ):
        raise ReviewError(
            "Shared-writer journal operation is incompatible; operator-scoped recovery is required."
        )
    if operation is not None:
        for label in ("coordinator", "process"):
            identity = operation.get(label)
            if identity is None and label == "process":
                continue
            if (
                not isinstance(identity.get("pid"), int)
                or isinstance(identity.get("pid"), bool)
                or identity["pid"] < 1
                or not isinstance(identity.get("start_ticks"), int)
                or isinstance(identity.get("start_ticks"), bool)
                or identity["start_ticks"] < 0
                or label == "process"
                and (
                    not isinstance(identity.get("pgid"), int)
                    or isinstance(identity.get("pgid"), bool)
                    or identity["pgid"] < 1
                    or not isinstance(identity.get("session"), int)
                    or isinstance(identity.get("session"), bool)
                    or identity["session"] < 1
                )
                or not isinstance(identity.get("boot_id"), str)
                or not identity["boot_id"]
                or not isinstance(identity.get("pid_namespace"), str)
                or not identity["pid_namespace"]
            ):
                raise ReviewError(
                    "Shared-writer process identity is incompatible; operator-scoped recovery is required."
                )
        if operation.get("spawn_pending") and operation.get("process") is not None:
            raise ReviewError(
                "Shared-writer pending identity is ambiguous; operator-scoped recovery is required."
            )
    return path, raw, record


def _settle_shared_writer_if_retired(state, repo_key: str) -> None:
    path, _, record = _read_shared_writer(state, repo_key)
    if record is None or record.get("operation") is None:
        return
    operation = record["operation"]
    if operation.get("spawn_pending") or not operation.get("process"):
        raise ReviewError(
            "Shared source writer has pending or missing launch identity; operator-scoped recovery is required."
        )
    identity = operation["process"]
    current = process_identity(os.getpid())
    if any(
        identity.get(key) != current.get(key)
        for key in ("boot_id", "pid_namespace")
    ):
        raise ReviewError(
            "Recorded shared source writer is in another boot/PID namespace; retirement is unproven and operator-scoped recovery is required."
        )
    members = [
        row
        for row in _process_census()
        if row["pgid"] == identity.get("pgid")
        or row["session"] == identity.get("session")
    ]
    if any(row["state"] != "Z" for row in members):
        raise ReviewError(
            "Recorded shared source writer process group is still executable; shared-source use is refused."
        )
    record["operation"] = None
    record["last_settled_at"] = utc_now()
    record["last_settled_operation"] = operation
    atomic_json(path, record)


def check_shared_writer_ready(state, repo_key: str) -> None:
    """Refuse every shared-source user while an unretired writer is durable."""
    scope = _SHARED_WRITER.get()
    if scope is not None and scope["state"].identity == state.identity and scope["repo_key"] == repo_key:
        return
    _settle_shared_writer_if_retired(state, repo_key)


def _shared_writer_state_for_common(common: Path):
    common = Path(common).resolve()
    directory = common.parent.parent
    if common.parent.name != "source-stores" or directory.name != "review-setup":
        return None
    return type(
        "SharedWriterState",
        (),
        {
            "directory": directory,
            "identity": hashlib.sha256(str(directory.parent.resolve()).encode()).hexdigest(),
        },
    )()


@contextmanager
def shared_writer_scope(state, repo_key: str, slot: int, *, phase: str):
    """Publish source-mutating Git children in one repo-scoped durable journal."""
    source_path = _source_lock_path(
        state.directory / "source-stores" / f"{repo_key}.git", "source"
    )
    if not _held_lock(source_path, exclusive=True):
        raise ReviewError("Shared-writer protection requires the source-write lock.")
    existing = _SHARED_WRITER.get()
    if existing is not None:
        if existing["state"] is not state or existing["repo_key"] != repo_key:
            raise ReviewError("Nested shared-writer scopes must identify the same source store.")
        yield existing
        return
    check_shared_writer_ready(state, repo_key)
    path = _shared_writer_path(state, repo_key)
    record = {
        "schema_version": 1,
        "workspace_identity": state.identity,
        "repo_key": repo_key,
        "operation": None,
    }
    if path.exists():
        record.update(json.loads(path.read_text(encoding="utf-8")))
    else:
        atomic_json(path, record)
    scope = {
        "state": state,
        "repo_key": repo_key,
        "slot": slot,
        "phase": phase,
        "path": path,
    }
    token = _SHARED_WRITER.set(scope)

    def started(identity):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if not operation or not operation.get("spawn_pending"):
            raise CleanupNotProvenError(
                "Shared-writer spawn intent changed before identity publication."
            )
        operation["process"] = identity
        operation["spawn_pending"] = False
        operation["exec_ready_at"] = utc_now()
        atomic_json(path, current)

    def arm(operation_id=None):
        current = json.loads(path.read_text(encoding="utf-8"))
        if current.get("operation") is not None:
            raise ReviewError("A shared-writer operation is already pending.")
        operation_id = operation_id or uuid.uuid4().hex
        current["operation"] = {
            "id": operation_id,
            "phase": phase,
            "slot_number": slot,
            "spawn_pending": True,
            "process": None,
            "coordinator": process_identity(os.getpid()),
            "started_at": utc_now(),
        }
        atomic_json(path, current)
        return operation_id

    def not_started(operation_id):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if (
            not operation
            or operation.get("id") != operation_id
            or operation.get("spawn_pending") is not True
            or operation.get("process") is not None
        ):
            raise CleanupNotProvenError(
                "Shared-writer launch intent changed before typed prelaunch settlement."
            )
        current["operation"] = None
        current["last_settled_at"] = utc_now()
        current["last_settled_operation"] = operation
        current["last_settlement_proof"] = "typed-command-not-started"
        atomic_json(path, current)

    try:
        scope["arm"] = arm
        scope["started"] = started
        scope["not_started"] = not_started
        yield scope
    finally:
        _SHARED_WRITER.reset(token)


@contextmanager
def slot_writer_scope(
    state,
    repo_key: str,
    slot: int,
    *,
    phase: str,
    reap_stale: bool = False,
    requested_pr: int | None = None,
):
    """Journal every owned subprocess while one selected slot is mutable.

    `reap_stale` and `requested_pr` are inert unless a caller opts in (the
    pooled-reset flow only): see `_slot_writer_assert_quiescent` for the
    stale-journal policy they enable. Every other caller is unaffected.
    """
    if not any(
        entry["kind"] == "slot"
        and entry["mode"] == "exclusive"
        and entry["scope"] == f"slot {slot}"
        and entry["resource"] == repo_key
        for entry in _HELD_LOCKS.get()
    ):
        raise ReviewError("Slot-writer protection requires the actual selected-slot lock.")
    transaction = load_outer_transaction(state)
    if transaction is not None and transaction.get("id") != _OUTER_MERGE_OWNER.get():
        if (
            transaction.get("repo_key") == repo_key
            and transaction.get("slot_number") == slot
        ):
            raise ReviewError(
                f"Selected slot is reserved by merge run {transaction['run_id']} at phase {transaction['phase']}."
            )
        if phase in ("setup-slot", "pooled-reset", "warm-slot"):
            raise ReviewError(
                f"Canonical outer mutation is reserved by merge run {transaction['run_id']} at phase {transaction['phase']}."
            )
    existing = _SLOT_WRITER.get()
    if existing is not None:
        if not _slot_writer_current_scope(state, repo_key, slot):
            raise ReviewError("Nested slot-writer scopes must identify the same selected slot.")
        yield existing
        return
    _slot_writer_assert_quiescent(
        state,
        repo_key,
        slot,
        reap_stale=reap_stale,
        requested_pr=requested_pr,
    )
    path = _slot_writer_path(state, repo_key, slot)
    record = {
        "schema_version": 1,
        "workspace_identity": state.identity,
        "repo_key": repo_key,
        "slot_number": slot,
        "operation": None,
    }
    if path.exists():
        record.update(json.loads(path.read_text(encoding="utf-8")))
    else:
        atomic_json(path, record)
    scope = {
        "state": state,
        "repo_key": repo_key,
        "slot": slot,
        "phase": phase,
        "path": path,
    }
    token = _SLOT_WRITER.set(scope)

    def started(identity):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if not operation or not operation.get("spawn_pending"):
            raise CleanupNotProvenError(
                "Slot-writer spawn intent changed before identity publication."
            )
        operation["process"] = identity
        operation["spawn_pending"] = False
        operation["exec_ready_at"] = utc_now()
        atomic_json(path, current)

    def arm(operation_id=None):
        current = json.loads(path.read_text(encoding="utf-8"))
        if current.get("operation") is not None:
            raise ReviewError("A slot-writer operation is already pending.")
        operation_id = operation_id or uuid.uuid4().hex
        current["operation"] = {
            "id": operation_id,
            "phase": phase,
            "spawn_pending": True,
            "process": None,
            "coordinator": process_identity(os.getpid()),
            "started_at": utc_now(),
        }
        atomic_json(path, current)
        return operation_id

    def not_started(operation_id):
        current = json.loads(path.read_text(encoding="utf-8"))
        operation = current.get("operation")
        if (
            not operation
            or operation.get("id") != operation_id
            or operation.get("spawn_pending") is not True
            or operation.get("process") is not None
        ):
            raise CleanupNotProvenError(
                "Slot-writer launch intent changed before typed prelaunch settlement."
            )
        current["operation"] = None
        current["last_settled_at"] = utc_now()
        current["last_settled_operation"] = operation
        current["last_settlement_proof"] = "typed-command-not-started"
        atomic_json(path, current)

    try:
        scope["arm"] = arm
        scope["started"] = started
        scope["not_started"] = not_started
        yield scope
    finally:
        _SLOT_WRITER.reset(token)


@contextmanager
def owned_journal(callback):
    """Compose durable launch observers; nested scopes never replace one another."""
    callbacks = _OWNED_JOURNAL.get()
    token = _OWNED_JOURNAL.set((*callbacks, callback))
    try:
        yield
    finally:
        _OWNED_JOURNAL.reset(token)


def process_identity(pid: int) -> dict:
    fields = Path(f"/proc/{pid}/stat").read_text().rsplit(") ", 1)[1].split()
    return {"pid": pid, "state": fields[0], "pgid": int(fields[2]),
            "session": int(fields[3]), "start_ticks": int(fields[19]),
            "boot_id": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
            "pid_namespace": os.readlink(f"/proc/{pid}/ns/pid")}


def _process_census() -> list[dict]:
    """Require a complete procfs view of this PID namespace; fail closed."""
    try:
        if int(Path('/proc/self/stat').read_text().split(' ', 1)[0]) != os.getpid():
            raise ValueError("procfs PID mapping mismatch")
        nspid = next(line.split()[1:] for line in Path('/proc/self/status').read_text().splitlines()
                     if line.startswith('NSpid:'))
        if nspid != [str(os.getpid())]:
            raise ValueError("procfs namespace mismatch")
        for line in Path('/proc/self/mountinfo').read_text().splitlines():
            fields = line.split()
            if fields[4] == '/proc' and (' - proc ' not in line or re.search(r'hidepid=[^0, ]', line)):
                raise ValueError("restricted procfs")
        rows = []
        for path in Path('/proc').iterdir():
            if not path.name.isdigit():
                continue
            try:
                # A zombie leader may still have executable threads.
                for task in (path / 'task').iterdir():
                    try:
                        fields = (task / 'stat').read_text().rsplit(') ', 1)[1].split()
                        rows.append({'pid': int(task.name), 'tgid':int(path.name), 'state': fields[0],
                                     'pgid': int(fields[2]), 'session': int(fields[3]), 'start_ticks': int(fields[19])})
                    except FileNotFoundError:
                        continue
            except FileNotFoundError:
                continue  # Process exited during the census.
        return rows
    except (OSError, ValueError, IndexError, StopIteration):
        raise CleanupNotProvenError("Complete same-namespace process visibility is unavailable; cleanup remains unproven.") from None


def run_owned(args: list[str], *, timeout: float, term_grace: float = 2.0,
              kill_grace: float = 2.0, env: dict[str, str] | None = None,
              input_data: bytes | None = None, progress: FetchProgress | None = None,
              mutates: bool = False) -> subprocess.CompletedProcess:
    """Run a command as the sole owner of a fresh POSIX session/process group.

    On timeout, only the process group this call created is ever signalled:
    first TERM, then (if the group is not independently proven empty) KILL.
    "Terminated" is never inferred from pipe EOF or the direct child being
    reaped alone -- a descendant that closes/redirects its inherited pipe is
    invisible to those checks, so every "terminated" outcome is confirmed
    with a killpg(pgid, 0) probe before being reported as such. Pipes are
    drained and the direct child is reaped before this function returns or
    raises. Every exit path funnels through a single reap/close step so a
    caller never leaks the child, its pipes, or an unreaped zombie, even if
    escalation itself raises partway through.

    If cleanup cannot be proven complete within the bounded windows, this
    raises ReviewError describing a blocked/error state -- it never claims
    the command was cleanly cancelled when that isn't verified.
    """
    callbacks = _OWNED_JOURNAL.get()
    child_started = time.monotonic()
    executable = Path(args[0]).name if args else "unknown"
    command_class = (
        "git"
        if executable == "git"
        else "curl"
        if executable == "curl"
        else "python"
        if executable.startswith("python")
        else "process"
    )
    log_event(
        "owned-child.spawn-intent",
        "Owned child launch intent recorded",
        level="Debug",
        command_class=command_class,
    )
    slot_scope = _SLOT_WRITER.get() if mutates else None
    shared_scope = _SHARED_WRITER.get() if mutates else None
    outer_scope = _OUTER_WRITER.get() if mutates else None
    writer_operations = []
    operation_id = (
        uuid.uuid4().hex
        if slot_scope is not None or shared_scope is not None or outer_scope is not None
        else None
    )
    for scope in (slot_scope, shared_scope, outer_scope):
        if scope is not None:
            writer_operations.append((scope, scope["arm"](operation_id)))
            callbacks = (*callbacks, scope["started"])
    gate_read = gate_write = None
    launch = args
    if callbacks:
        gate_read, gate_write = os.pipe()
        # The wrapper cannot exec Git until its immutable identity is durable.
        launch = [sys.executable, '-c',
                  'import os,sys; fd=int(sys.argv[1]); go=os.read(fd,1); os.close(fd); '
                  'os.execvpe(sys.argv[2],sys.argv[2:],os.environ) if go==b"1" else sys.exit(125)',
                  str(gate_read), *args]
    try:
        process = subprocess.Popen(
            launch,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            stdin=subprocess.PIPE if input_data is not None else subprocess.DEVNULL,
            start_new_session=True,
            env=env,
            pass_fds=(gate_read,) if callbacks else (),
        )
    except OSError as exc:
        if gate_read is not None:
            os.close(gate_read)
            os.close(gate_write)
        for scope, operation_id in writer_operations:
            scope["not_started"](operation_id)
        raise CommandNotStartedError(
            f"Could not start owned process ({type(exc).__name__})."
        ) from None
    if callbacks:
        os.close(gate_read)
        try:
            identity = process_identity(process.pid)
            if identity['pgid'] != process.pid or identity['session'] != process.pid:
                raise CleanupNotProvenError("Owned gated process identity could not be confirmed.")
            for callback in callbacks:
                callback(identity)
            log_event(
                "owned-child.started",
                "Owned child started",
                level="Debug",
                command_class=command_class,
                child_pid=identity["pid"],
                child_start_ticks=identity["start_ticks"],
                child_pgid=identity["pgid"],
                child_session=identity["session"],
            )
            os.write(gate_write, b'1')
        except BaseException:
            os.close(gate_write)
            process.kill(); process.wait()
            for stream in (process.stdout, process.stderr, process.stdin):
                if stream is not None: stream.close()
            raise
        os.close(gate_write)
    else:
        identity = process_identity(process.pid)
        log_event(
            "owned-child.started",
            "Owned child started",
            level="Debug",
            command_class=command_class,
            child_pid=identity["pid"],
            child_start_ticks=identity["start_ticks"],
            child_pgid=identity["pgid"],
            child_session=identity["session"],
        )

    result = None
    failure_category = None
    try:
        result = _run_owned_body(
            process, timeout, term_grace, kill_grace, input_data, progress
        )
        return result
    except ReviewError as exc:
        failure_category = type(exc).__name__
        raise
    except OSError as exc:
        failure_category = type(exc).__name__
        # Any other OS-level failure across signalling/probing/draining/
        # reaping still becomes a clean ReviewError, matching the guarantee
        # a single subprocess.run(...) call used to give run_git's callers
        # (this operation now spans Popen, signalling/probing/reaping instead
        # of one call, so the guard has to span all of it too).
        raise ReviewError(f"Owned process operation could not complete ({type(exc).__name__}).") from None
    finally:
        for scope, _ in writer_operations:
            path = scope["path"]
            current = json.loads(path.read_text(encoding="utf-8"))
            operation = current.get("operation")
            if operation and operation.get("process"):
                identity = operation["process"]
                me = process_identity(os.getpid())
                if any(identity.get(key) != me.get(key) for key in ("boot_id", "pid_namespace")):
                    raise CleanupNotProvenError("Writer retirement crossed an unobservable namespace.")
                if not _group_confirmed_empty(identity["pgid"], time.monotonic() + kill_grace):
                    raise CleanupNotProvenError("Writer process group retirement remains unproven.")
                current["operation"] = None
                current["last_settled_at"] = utc_now()
                current["last_settled_operation"] = operation
                atomic_json(path, current)
        log_event(
            "owned-child.ended",
            "Owned child ended",
            level="Error" if failure_category else "Debug",
            command_class=command_class,
            child_pid=identity["pid"],
            child_start_ticks=identity["start_ticks"],
            actual_exit=result.returncode if result is not None else None,
            failure_category=failure_category,
            duration_ms=max(0, round((time.monotonic() - child_started) * 1000, 3)),
        )


def _run_owned_body(process: subprocess.Popen, timeout: float, term_grace: float,
                    kill_grace: float, input_data: bytes | None = None,
                    progress: FetchProgress | None = None) -> subprocess.CompletedProcess:
    # start_new_session calls setsid() in the child, so its process group id
    # is expected to equal its own pid. Confirm this with a real syscall
    # instead of assuming it. The resulting pgid is the *only* value ever
    # used for a signal or a probe below -- it is captured once, here, and
    # never re-derived mid-flight, so escalation can never drift onto a
    # different (possibly pid-reused) group than the one we confirmed.
    try:
        pgid = os.getpgid(process.pid)
    except ProcessLookupError:
        process.wait()
        raise ReviewError(
            "Owned process exited before its process group could be confirmed."
        ) from None
    if pgid != process.pid:
        process.kill()
        process.wait()
        raise ReviewError(
            "Owned process group id did not match its own pid after setsid(); "
            "refusing to signal an unexpected group."
        ) from None
    stdout = stderr = b""
    timed_out = False
    try:
        try:
            if progress is None:
                stdout, stderr = process.communicate(input=input_data, timeout=timeout)
            else:
                # communicate remains the sole pipe owner. TimeoutExpired carries
                # all bytes received so far; repeated calls safely resume draining.
                deadline = time.monotonic() + timeout
                observed = 0
                pending = b""
                first = True
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise subprocess.TimeoutExpired(process.args, timeout)
                    try:
                        stdout, stderr = process.communicate(
                            input=input_data if first else None, timeout=min(0.2, remaining))
                        finished = True
                    except subprocess.TimeoutExpired as exc:
                        stderr = exc.stderr or b""
                        finished = False
                    first = False
                    pending += stderr[observed:]
                    observed = len(stderr)
                    lines = re.split(b"[\r\n]", pending)
                    pending = lines.pop()[-4096:]
                    for line in lines:
                        if len(line) <= 4096:
                            progress.git_line(line.decode("utf-8", "replace"))
                    if finished:
                        if pending:
                            progress.git_line(pending.decode("utf-8", "replace"))
                        break
        except subprocess.TimeoutExpired:
            timed_out = True
            stdout, stderr = _escalate_owned_group(process, pgid, term_grace, kill_grace)
    finally:
        # Regardless of how we got here (including a blocked ReviewError
        # raised by escalation), make sure the direct child is reaped and
        # its pipe fds are closed; never leave a zombie or leaked FD.
        if process.poll() is None:
            try:
                process.kill()
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=kill_grace)
            except subprocess.TimeoutExpired:
                pass
        for stream in (process.stdout, process.stderr, process.stdin):
            if stream is not None:
                stream.close()

    if timed_out:
        raise OwnedTimeoutError(f"Command timed out after {timeout:.1f}s and its process group was terminated; zombie records may await their parent reaper.")
    return subprocess.CompletedProcess(process.args, process.returncode,
                                       stdout.decode("utf-8", "surrogateescape"),
                                       stderr.decode("utf-8", "surrogateescape"))


def _signal_owned_group(pgid: int, sig: int) -> None:
    try:
        os.killpg(pgid, sig)
    except ProcessLookupError:
        pass  # The group is already gone; nothing left to signal.
    except PermissionError:
        # EPERM here means this pgid can no longer be safely proven ours to
        # signal (e.g. a narrow pid/pgid-reuse window) -- never treat that as
        # a successful signal or silently continue; surface it distinctly.
        raise CleanupNotProvenError(
            "Permission denied signalling the owned process group; treating "
            "as blocked rather than cancelled."
        ) from None



def _group_confirmed_empty(pgid: int, deadline: float) -> bool:
    """Prove no executable group members: ESRCH, or a complete zombie-only census.

    Zombie tasks have terminated but await their parent's wait(). They cannot
    write and cannot be reaped by sending KILL again. Neither pipe EOF nor the
    direct child's exit proves this; include every visible thread in the group.
    Unknown visibility and permission failures remain blocked.
    """
    while True:
        try:
            os.killpg(pgid, 0)
        except ProcessLookupError:
            return True
        except PermissionError:
            raise CleanupNotProvenError(
                "Permission denied probing the owned process group for "
                "emptiness; treating as blocked rather than cancelled."
            ) from None
        members = [row for row in _process_census() if row['pgid'] == pgid]
        if members and all(row['state'] == 'Z' for row in members):
            return True  # Terminated, not literally empty: only parent-reap records remain.
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.01)



def _drain(process: subprocess.Popen, grace: float) -> tuple[bytes, bytes, bool]:
    try:
        stdout, stderr = process.communicate(timeout=grace)
        return stdout, stderr, True
    except subprocess.TimeoutExpired:
        return b"", b"", False


def _escalate_owned_group(process: subprocess.Popen, pgid: int, term_grace: float,
                          kill_grace: float) -> tuple[bytes, bytes]:
    """Signal only this call's own process group; never anything unrelated.

    Never declares the group terminated on pipe EOF / direct-child reap
    alone: after TERM (and again after KILL), the direct child being reaped
    is necessary but not sufficient -- group emptiness is independently
    confirmed with `_group_confirmed_empty` before returning success.
    """
    _signal_owned_group(pgid, signal.SIGTERM)
    stdout, stderr, reaped = _drain(process, term_grace)
    if reaped and _group_confirmed_empty(pgid, time.monotonic() + term_grace):
        return stdout, stderr

    # Either the direct child (and/or pipe) didn't settle within term_grace,
    # or it did but the group isn't provably empty (e.g. a TERM-resistant
    # descendant that closed/redirected the inherited pipe) -- escalate to
    # KILL rather than trust pipe EOF alone.
    _signal_owned_group(pgid, signal.SIGKILL)
    if not reaped:
        stdout, stderr, reaped = _drain(process, kill_grace)
    if reaped and _group_confirmed_empty(pgid, time.monotonic() + kill_grace):
        return stdout, stderr

    raise CleanupNotProvenError(
        "Owned process group required SIGKILL and could not be independently "
        "confirmed empty within the bounded window; treating as blocked "
        "rather than cancelled."
    )



def _git_command_mutates(args: list[str]) -> bool:
    """Conservatively classify Git commands that can mutate owned slot topology/state."""
    if any(command in args for command in ("init", "fetch", "restore", "clean", "checkout", "switch", "add", "commit", "merge", "reset", "read-tree", "update-index", "update-ref", "repack", "prune")):
        return True
    if "branch" in args and any(option in args for option in ("-m", "-M", "-d", "-D", "-f")):
        return True
    if "worktree" in args and any(command in args for command in ("add", "remove", "move", "prune", "repair")):
        return True
    if "remote" in args and any(command in args for command in ("add", "remove", "rename", "set-url", "set-head")):
        return True
    if "submodule" in args and any(command in args for command in ("update", "deinit", "absorbgitdirs")):
        return True
    return False


def _gate_outer_mutation(repo: Path) -> None:
    """Refuse canonical outer-repository writes outside the active merge owner."""
    path = Path(repo).resolve()
    # Bare source/acquisition stores live beneath the outer common directory but
    # are independent repositories. Do not mistake their writes for outer-root
    # administration merely because an ancestor happens to be `.git`.
    if (
        (path / "HEAD").is_file()
        and (path / "objects").is_dir()
        and (path / "refs").is_dir()
        and not (path / ".git").exists()
    ):
        return
    common = None
    cursor = path
    while True:
        dot_git = cursor / ".git"
        if dot_git.is_dir():
            common = dot_git.resolve()
            break
        if dot_git.is_file():
            try:
                line = dot_git.read_text(encoding="utf-8").strip()
            except OSError:
                return
            prefix = "gitdir: "
            if not line.startswith(prefix):
                return
            gitdir = Path(line[len(prefix) :])
            gitdir = gitdir if gitdir.is_absolute() else (cursor / gitdir)
            gitdir = gitdir.resolve()
            commondir = gitdir / "commondir"
            if commondir.is_file():
                value = Path(commondir.read_text(encoding="utf-8").strip())
                common = (gitdir / value).resolve()
            else:
                common = gitdir
            break
        if cursor.parent == cursor:
            return
        cursor = cursor.parent
    if common is None:
        return
    directory = common / "review-setup"
    transaction_path = directory / "outer-merge-transaction.json"
    writer_path = directory / "outer-writer.json"
    if not transaction_path.exists() and not writer_path.exists():
        return
    state = type(
        "OuterMutationState",
        (),
        {
            "root": repo,
            "common": common,
            "directory": directory,
            "identity": hashlib.sha256(str(common).encode()).hexdigest(),
        },
    )()
    check_outer_merge_ready(state)


def _gate_unscoped_push(args: list[str]) -> None:
    """Fail closed on a Git push that no writer scope journals.

    This is deliberately independent of ``_gate_outer_mutation``'s
    canonical-root transaction singleton: a slot review-ref publish must
    never be serialized behind root ``main`` integration, so an active
    ``slot_writer_scope`` alone is sufficient here, exactly as an active
    ``outer_writer_scope`` alone is sufficient for a parent ``main`` push.
    A push with neither scope active bypasses journal ownership entirely
    and is refused.
    """
    if "push" not in args:
        return
    if _SLOT_WRITER.get() is None and _OUTER_WRITER.get() is None:
        raise ReviewError(
            "Unscoped Git push is not permitted; parent-origin pushes must run "
            "inside a slot-writer or outer-writer scope."
        )


def run_git(repo: Path, args: list[str], check: bool = True, *,
            read_only: bool = False, input_data: bytes | None = None,
            timeout: float = 300, progress: FetchProgress | None = None,
            git_binary: str = "git",
            extra_env: Mapping[str, str] | None = None) -> subprocess.CompletedProcess:
    # Do not echo stderr: remote helpers can include credential-bearing URLs.
    # Disable optional index refreshes for observations, including legacy reset
    # dry-run helpers. Explicit Git mutations still take their mandatory locks.
    #
    # `git_binary`/`extra_env` let a caller select which Git executable runs
    # this specific invocation (e.g. a provider-neutral fetch that must apply
    # transport auth or a scoped, per-command credential helper) without
    # touching any other caller: both default to the prior behavior exactly
    # ("git", no extra environment).
    gate_mutates = not read_only and _git_command_mutates(args)
    if gate_mutates and _OUTER_WRITER.get() is None:
        _gate_outer_mutation(repo)
    if not read_only:
        _gate_unscoped_push(args)
    # `push` is not in `_git_command_mutates` (it must never be entangled with
    # the canonical-root transaction singleton above), but it still must not
    # bypass the per-scope typed-launch journal (`arm`/`started`) that
    # `run_owned` applies automatically whenever a slot/outer writer scope is
    # active -- so it counts toward that journal's `mutates` flag here.
    journal_mutates = gate_mutates or (not read_only and "push" in args)
    environment = os.environ.copy()
    environment["GIT_OPTIONAL_LOCKS"] = "0"
    if extra_env:
        environment.update(extra_env)
    session = _FETCH_SESSION.get()
    if session is not None and session.deadline is not None:
        try:
            timeout = min(timeout, session.remaining())
        except ReviewError as exc:
            raise CommandNotStartedError(str(exc)) from None
    result = run_owned([git_binary, "--literal-pathspecs", "-c", "diff.external=", "-C", str(repo), *args],
                       timeout=timeout, env=environment, input_data=input_data, progress=progress,
                       mutates=journal_mutates)
    if check and result.returncode:
        operation = next((arg for arg in args if not arg.startswith("-")), "operation")
        raise ReviewError(f"Git {operation} failed (exit {result.returncode}); no state was forcibly cleared.")
    return result


def run_git_read(repo: Path, args: list[str], check: bool = True, *,
                  git_binary: str = "git",
                  extra_env: Mapping[str, str] | None = None) -> subprocess.CompletedProcess:
    """Run a Git observation with optional lock/index refreshes disabled."""
    return run_git(repo, args, check=check, read_only=True, git_binary=git_binary, extra_env=extra_env)


# ---------------------------------------------------------------------------
# Guarded parent-origin remote primitives.
#
# These narrowly-scoped helpers read, create/update, and delete refs on the
# parent repository's own `origin` remote only. They are never valid for the
# nested per-slot Azure DevOps source pool, which remains a detached,
# read-only checkout forever: `validate_parent_origin_remote` refuses a
# remote that itself parses as a recognized ADO URL. No live push or ref
# deletion is exercised by this change; `--publish`/`--complete`/`--abandon`
# are implemented separately and are the only callers that will actually
# invoke `create_or_update_parent_remote_ref`/`delete_parent_remote_ref`.
# ---------------------------------------------------------------------------


def validate_parent_origin_remote(root: Path, *, remote: str = "origin") -> str:
    """Validate ``remote`` on ``root`` as the canonical parent-repository remote.

    Requires HTTPS with no embedded credentials (enforced by
    ``_normalized_remote_base``) and refuses a URL that itself parses as a
    recognized Azure DevOps remote -- the parent repository's own remote must
    never be confused with a nested, read-only ADO source. Returns the raw
    configured URL; callers must not print it verbatim.
    """
    result = run_git_read(root, ["config", "--get", f"remote.{remote}.url"], check=False)
    if result.returncode:
        raise ReviewError("Parent-origin remote is not configured.")
    url = result.stdout.strip()
    _normalized_remote_base(url)
    try:
        parse_ado_url(url)
    except ReviewError:
        pass
    else:
        raise ReviewError("Parent-origin remote must not be a nested Azure DevOps remote.")
    return url


def validate_parent_ref(root: Path, value: str) -> str:
    """Accept only a full, well-formed ``refs/heads/...`` refname."""
    if not isinstance(value, str) or not value.startswith("refs/heads/"):
        raise ReviewError("Parent-origin operations require a full refs/heads/... refname.")
    if run_git_read(root, ["check-ref-format", value], check=False).returncode:
        raise ReviewError("Parent-origin refname is malformed.")
    return value


def read_parent_remote_ref(root: Path, ref: str, *, remote: str = "origin") -> str | None:
    """Read exactly one remote ref with ``ls-remote``.

    Returns the validated SHA, or ``None`` if the ref is absent. Refuses
    ambiguous or malformed remote output rather than guessing.
    """
    validate_parent_ref(root, ref)
    validate_parent_origin_remote(root, remote=remote)
    result = run_git_read(root, ["ls-remote", "--exit-code", "--", remote, ref], check=False)
    if result.returncode == 2:
        return None
    if result.returncode:
        raise ReviewError("Parent-origin remote ref lookup failed.")
    matches = []
    for line in result.stdout.splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) != 2 or parts[1] != ref:
            raise ReviewError("Parent-origin remote returned an unexpected ref listing.")
        matches.append(parts[0])
    if len(matches) != 1:
        raise ReviewError("Parent-origin remote ref lookup was ambiguous.")
    return validate_sha(matches[0])


def _assert_ref_writer_scope(kind: str) -> None:
    """Fail closed unless the matching writer scope journals this ref push.

    ``kind="slot"`` requires an active ``slot_writer_scope`` -- independent of
    any canonical-root transaction, so different slots can publish/reconcile
    concurrently without contending on the singleton root transaction.
    ``kind="outer"`` requires an active ``outer_writer_scope`` (the durable
    canonical-root transaction), used only for the parent ``main`` push.
    """
    if kind == "slot":
        if _SLOT_WRITER.get() is None:
            raise ReviewError("Parent-origin review-ref push requires an active slot-writer scope.")
    elif kind == "outer":
        if _OUTER_WRITER.get() is None:
            raise ReviewError("Parent-origin main-ref push requires an active outer-writer scope.")
    else:
        raise ReviewError("Unrecognized parent-origin writer-scope kind.")


def create_or_update_parent_remote_ref(
    root: Path,
    ref: str,
    new_sha: str,
    *,
    expected_old: str | None,
    writer_scope: str,
    remote: str = "origin",
) -> str:
    """Create or fast-forward one parent-origin ref with an explicit lease.

    ``expected_old=None`` means the ref must not already exist remotely (a
    create); any other value is the exact SHA this push must replace. That is
    proven twice: once by ``--force-with-lease``, and once by an explicit
    ``merge-base --is-ancestor`` check, because a lease alone proves the
    remote tip did not move -- it does not prove the new value is a
    fast-forward of it. The push is verified by a fresh post-push read before
    returning.
    """
    _assert_ref_writer_scope(writer_scope)
    validate_parent_ref(root, ref)
    validate_parent_origin_remote(root, remote=remote)
    new_sha = validate_sha(new_sha)
    if expected_old is not None:
        expected_old = validate_sha(expected_old)
        if run_git_read(
            root, ["merge-base", "--is-ancestor", expected_old, new_sha], check=False
        ).returncode:
            raise ReviewError(
                "Parent-origin ref update is not a fast-forward from its expected prior tip."
            )
    lease = f"{ref}:{expected_old or ''}"
    run_git(
        root,
        ["push", f"--force-with-lease={lease}", "--", remote, f"{new_sha}:{ref}"],
    )
    actual = read_parent_remote_ref(root, ref, remote=remote)
    if actual != new_sha:
        raise ReviewError("Parent-origin ref push did not verify at its exact expected result.")
    return actual


def delete_parent_remote_ref(
    root: Path,
    ref: str,
    *,
    expected_tip: str,
    writer_scope: str,
    remote: str = "origin",
) -> None:
    """Delete one parent-origin ref only after a fresh exact-SHA comparison,
    then verify its absence.

    A ref already absent is treated as an already-satisfied deletion (safe to
    call after an interrupted retry), never as an error.
    """
    _assert_ref_writer_scope(writer_scope)
    validate_parent_ref(root, ref)
    validate_parent_origin_remote(root, remote=remote)
    expected_tip = validate_sha(expected_tip)
    current = read_parent_remote_ref(root, ref, remote=remote)
    if current is None:
        return
    if current != expected_tip:
        raise ReviewError("Parent-origin ref moved; refusing to delete an unexpected tip.")
    lease = f"{ref}:{expected_tip}"
    run_git(root, ["push", f"--force-with-lease={lease}", "--", remote, f":{ref}"])
    if read_parent_remote_ref(root, ref, remote=remote) is not None:
        raise ReviewError("Parent-origin ref deletion did not verify as absent.")


def acquisition_cache_path(common: Path) -> Path:
    return common.parent / "acquisition-caches" / common.name


def _source_repo_key(common: Path) -> str:
    key = Path(common).name.removesuffix(".git")
    if not re.fullmatch(r"[a-z0-9._-]+", key) or key in (".", ".."):
        raise ReviewError("Source common directory has no safe repository lock key.")
    return key


def _source_lock_path(common: Path, name: str) -> Path:
    common = Path(common).resolve()
    return common.parent.parent / "source-locks" / f"{_source_repo_key(common)}.{name}.lock"


@contextmanager
def acquisition_lock(common: Path, *, phase: str = "network-acquisition", wait: bool = True):
    common = Path(common).resolve()
    with _scoped_lock(
        _source_lock_path(common, "acquisition"),
        kind="acquisition",
        resource=_source_repo_key(common),
        scope="acquisition cache",
        phase=phase,
        wait=wait,
    ):
        yield


@contextmanager
def source_lock(
    common: Path,
    *,
    shared: bool = False,
    phase: str = "source-store",
    wait: bool = True,
):
    common = Path(common).resolve()
    with _scoped_lock(
        _source_lock_path(common, "source"),
        kind="source",
        resource=_source_repo_key(common),
        scope="source store",
        shared=shared,
        phase=phase,
        wait=wait,
    ):
        yield


def _assert_cache_quiescent(common: Path, cache: Path, operation: dict | None, *, asserted=False) -> None:
    current = process_identity(os.getpid())
    rows = _process_census()
    if not asserted:
        if not operation or operation.get('spawn_pending') or not operation.get('owner'):
            raise ReviewError("Acquisition cleanup lacks historical process identity; explicit operator recovery is required.")
        for index, identity in enumerate([operation['owner'], *operation.get('processes', [])]):
            if any(identity.get(key) != current[key] for key in ('boot_id', 'pid_namespace')):
                raise ReviewError("Recorded operation is in another boot/PID namespace; its retirement is unproven. Use operator-scoped recovery.")
            if index == 0 and identity['pid'] == os.getpid() and identity['start_ticks'] == current['start_ticks']:
                continue  # This invocation is settling its own handled failure.
            if index == 0:
                active = [row for row in rows if row['pid'] == identity['pid'] and row['start_ticks'] == identity['start_ticks']]
            else:
                active = [row for row in rows if row['pgid'] == identity['pgid'] or row['session'] == identity['session']]
            if any(row['state'] != 'Z' for row in active):
                raise ReviewError("Recorded acquisition process/group is still active; cleanup remains blocked.")
    if not asserted:
        return  # Cooperative writers are serialized; every recorded group was checked.
    # A census in this exec cannot see sibling PID namespaces. An explicit
    # assertion covers those; this check still refuses observable local users.
    for row in rows:
        if row['pid'] == os.getpid() or row['state'] == 'Z':
            continue
        proc = Path('/proc') / str(row['pid'])
        try:
            targets = [os.readlink(proc / 'cwd')]
            targets += [os.readlink(fd) for fd in (proc / 'fd').iterdir()]
            if any(target == str(base) or target.startswith(str(base) + '/')
                   for target in targets for base in (common, cache)):
                raise ReviewError("An observable process still has the source/cache open; cleanup remains blocked.")
        except FileNotFoundError:
            continue
        except PermissionError:
            raise ReviewError("Process handle visibility is incomplete; operator recovery cannot bypass current-state checks.") from None


def _cache_administration(common: Path, cache: Path) -> None:
    for base in (common, cache):
        for path in [base, *base.parents]:
            if path.is_symlink():
                raise ReviewError("Refusing symlink source/cache administration.")
        if not base.is_dir():
            raise ReviewError("Source/cache is missing.")
        for parent, dirs, names in os.walk(base, followlinks=False):
            for name in dirs + names:
                path = Path(parent) / name
                if path.is_symlink():
                    raise ReviewError("Refusing symlink source/cache administration.")
                if name.endswith('.lock'):
                    raise ReviewError("A source/cache Git lock remains; no lock was removed.")
        if (base / 'objects/info/alternates').exists():
            raise ReviewError("Source/cache alternates are not supported for recovery.")


def check_acquisition_ready(
    common: Path,
    *,
    source_reader: bool = False,
    owned_operation_id: str | None = None,
) -> None:
    """Gate cache mutation while allowing proven network-only source readers."""
    writer_state = _shared_writer_state_for_common(common)
    if writer_state is not None:
        check_shared_writer_ready(writer_state, _source_repo_key(common))
    cache = acquisition_cache_path(common)
    if not cache.exists() and not cache.is_symlink():
        return
    path = cache / "review-acquisition.json"
    if path.is_symlink():
        raise ReviewError("Acquisition cache has no safe owned state.")
    if not path.is_file():
        # Cache creation publishes its state while acquisition EX is held. An
        # unlocked follower may observe the directory in that narrow window;
        # defer to bounded_fetch's acquisition wait. Once acquisition EX and
        # source EX are held, a still-missing state is a real safety failure.
        if not _held_lock(_source_lock_path(common, "acquisition"), exclusive=True):
            return
        raise ReviewError("Acquisition cache has no safe owned state.")
    try:
        state = json.loads(path.read_text())
    except (OSError, ValueError):
        raise ReviewError("Acquisition cache state is unreadable.") from None
    if not isinstance(state, dict):
        raise ReviewError("Acquisition cache state is invalid.")
    operation = state.get("operation")
    if operation:
        phase = operation.get("phase")
        source_untouched = operation.get("source_untouched") is True
        if owned_operation_id is not None:
            current = process_identity(os.getpid())
            owner = operation.get("owner")
            if (
                not state.get("blocked")
                and phase in ("network-acquisition", "cache-finalize")
                and source_untouched
                and operation.get("id") == owned_operation_id
                and isinstance(owner, dict)
                and all(
                    owner.get(key) == current[key]
                    for key in ("pid", "start_ticks", "boot_id", "pid_namespace")
                )
                and _held_lock(
                    _source_lock_path(common, "acquisition"), exclusive=True
                )
                and _held_lock(_source_lock_path(common, "source"), exclusive=True)
            ):
                return
            raise ReviewError(
                "Current acquisition operation ownership or required locks changed."
            )
        if (
            not state.get("blocked")
            and phase in ("network-acquisition", "cache-finalize")
            and source_untouched
        ):
            # A legitimate cache-only writer cannot affect authoritative source
            # readers. Unlocked followers must continue into bounded_fetch(),
            # where acquisition EX provides the wait and locked recheck.
            return
    elif owned_operation_id is not None:
        raise ReviewError("Current acquisition operation disappeared before source import.")
    if state.get("blocked") or operation:
        if source_reader or not (
            _held_lock(_source_lock_path(common, "acquisition"), exclusive=True)
            and _held_lock(_source_lock_path(common, "source"), exclusive=True)
        ):
            raise ReviewError(
                "Pending source import or unknown acquisition state blocks authoritative source reads."
            )
        recover_acquisition_cache(common)


def recover_acquisition_cache(common: Path, *, assertion: dict | None = None, inspect: bool = False) -> dict:
    """Resolve only verified owned cleanup; never fetch, reset, or delete objects."""
    common = Path(common)
    acquisition_path = _source_lock_path(common, "acquisition")
    source_path = _source_lock_path(common, "source")
    if not _held_lock(acquisition_path, exclusive=True) or not _held_lock(
        source_path, exclusive=True
    ):
        raise ReviewError(
            "Acquisition recovery requires actual held acquisition and source-write locks."
        )
    cache = acquisition_cache_path(common)
    path = cache / 'review-acquisition.json'
    _cache_administration(common, cache)
    try:
        raw = path.read_bytes(); state = json.loads(raw)
    except (OSError, ValueError):
        raise ReviewError("Acquisition cache state is unreadable.") from None
    digest = hashlib.sha256(raw).hexdigest()
    origin = run_git_read(common, ['config', '--get', 'remote.origin.url']).stdout.strip()
    expected = {'schema_version':1, 'source_common':str(common), 'remote_hash':hashlib.sha256(origin.encode()).hexdigest()}
    if not isinstance(state, dict) or any(state.get(k) != v for k,v in expected.items()):
        raise ReviewError("Acquisition cache identity does not match its source store.")
    if run_git_read(cache, ['rev-parse', '--is-bare-repository']).stdout.strip() != 'true' or run_git_read(cache, ['config', '--get', 'remote.origin.url']).stdout.strip() != origin:
        raise ReviewError("Acquisition cache repository/origin identity changed.")
    details = {'source_common':str(common), 'cache':str(cache), 'state_sha256':digest,
               'blocked':bool(state.get('blocked')), 'pending':bool(state.get('operation')),
               'current_process':process_identity(os.getpid())}
    if inspect or not (state.get('blocked') or state.get('operation')):
        return details
    if assertion is not None:
        required = {'source_common':str(common), 'cache':str(cache), 'state_sha256':digest,
                    'current_pid_namespace':details['current_process']['pid_namespace'],
                    'prior_writers_retired':True}
        if (not isinstance(assertion, dict) or any(assertion.get(k) != v for k,v in required.items())
                or not isinstance(assertion.get('operator'), str) or not assertion['operator'].strip()
                or not isinstance(assertion.get('basis'), str) or not assertion['basis'].strip()):
            raise ReviewError("Operator assertion must bind this exact state/cache/namespace and describe verified broader-scope prior-writer retirement.")
    operation = state.get('operation')
    _assert_cache_quiescent(common, cache, operation, asserted=assertion is not None)
    before = {label:_commit_edges(run_git_read, base) for label,base in [('source',common),('cache',cache)]}
    for base in (common, cache):
        run_git_read(base, ['fsck', '--connectivity-only', '--no-dangling'])
    if operation:
        checkpoint_name = operation.get('checkpoint', '')
        if not re.fullmatch(r'review-checkpoint-[a-zA-Z0-9-]+\.json', checkpoint_name):
            raise ReviewError("Acquisition retained-history checkpoint is missing.")
        try:
            checkpoint = json.loads((cache / checkpoint_name).read_text())
            for label in ('source', 'cache'):
                _verify_edges({sha:set(parents) for sha,parents in checkpoint[label].items()}, before[label])
        except (OSError, ValueError, KeyError, TypeError):
            raise ReviewError("Acquisition retained-history checkpoint is unreadable.") from None
    _assert_cache_quiescent(common, cache, operation, asserted=assertion is not None)
    _cache_administration(common, cache)
    for label,base in [('source',common),('cache',cache)]:
        _verify_edges(before[label], _commit_edges(run_git_read, base))
    if path.read_bytes() != raw:
        raise ReviewError("Acquisition state changed during recovery.")
    record = {'recovered_at':utc_now(), 'state_sha256':digest, 'previous_state':state,
              'proof':'operator-asserted prior-writer retirement' if assertion else 'same-namespace owned-process termination',
              'operator_assertion':assertion, 'historical_edges_verified':bool(operation)}
    atomic_json(cache / f'review-recovery-{uuid.uuid4().hex}.json', record)
    state = dict(state)
    if operation:
        state['depth_highwater'] = max(state.get('depth_highwater',0), operation['depth'])
    state.pop('operation', None); state['blocked'] = False
    state['last_recovered_at'] = record['recovered_at']
    atomic_json(path, state)
    return dict(details, blocked=False, pending=False, proof=record['proof'])


def _commit_edges(command, repo: Path) -> dict[str, set[str]]:
    heads = [validate_sha(row["head"]) for row in worktree_rows(repo) if row.get("head") and set(row["head"]) != {"0"}]
    return {parts[0]: set(parts[1:]) for parts in
            (line.split() for line in command(repo, ["rev-list", "--all", "--parents", *heads]).stdout.splitlines())}


def _verify_edges(before: dict, after: dict) -> None:
    if any(commit not in after or not parents.issubset(after[commit]) for commit, parents in before.items()):
        raise ReviewError("Acquisition cache failed retained-history preservation verification.")


def history_coverage(repo: Path, sha: str, *, command=run_git) -> dict:
    """Shortest distance to a shallow boundary, or complete reachable ancestry."""
    sha = validate_sha(sha)
    lines = command(repo, ["rev-list", "--parents", sha]).stdout.splitlines()
    parents = {parts[0]: parts[1:] for parts in (line.split() for line in lines)}
    raw = command(repo, ["rev-parse", "--git-path", "shallow"]).stdout.strip()
    path = Path(raw) if Path(raw).is_absolute() else repo / raw
    boundaries = set(path.read_text(encoding="ascii").split()) if path.exists() else set()
    queue = deque([(sha, 1)])
    seen = set()
    while queue:
        commit, distance = queue.popleft()
        if commit in seen:
            continue
        seen.add(commit)
        if commit in boundaries:
            return {"complete": False, "covered_depth": distance}
        queue.extend((parent, distance + 1) for parent in parents.get(commit, []))
    return {"complete": True, "covered_depth": len(parents)}


def _bounded_fetch_impl(repo: Path, remote: str, refs: list[str], *, depth: int,
                        max_depth: int = 1000000, deepen: bool = False,
                        prime: bool = False) -> subprocess.CompletedProcess:
    """Acquire remotely in the persistent cache, then import under source EX."""
    if not isinstance(depth, int) or isinstance(depth, bool) or not 0 < depth <= max_depth:
        raise ReviewError("Bounded fetch depth is outside the configured history budget.")
    if not refs:
        raise ReviewError("Bounded fetch requires at least one source ref.")
    if _FETCH_SESSION.get() is None:
        with fetch_session():
            return _bounded_fetch_impl(
                repo,
                remote,
                refs,
                depth=depth,
                max_depth=max_depth,
                deepen=deepen,
                prime=prime,
            )
    session = _FETCH_SESSION.get()
    common = Path(run_git_read(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"]).stdout.strip()).resolve()
    with source_lock(common, shared=True, phase="fetch-cache-check"):
        check_acquisition_ready(common, source_reader=True)
        if not deepen and not prime and all(re.fullmatch(r"[0-9a-f]{40}", ref) for ref in refs):
            if all(run_git_read(common, ["cat-file", "-e", f"{ref}^{{commit}}"], check=False).returncode == 0 for ref in refs):
                session.event("exact-object cache hit")
                result = subprocess.CompletedProcess([], 0, "", "")
                result.requested_tips = tuple(
                    validate_sha(
                        run_git_read(common, ["rev-parse", f"{ref}^{{commit}}"]).stdout.strip()
                    )
                    for ref in dict.fromkeys(refs)
                )
                return result
    with acquisition_lock(common, phase="network-acquisition"):
        # The cache writer is serialized, but that alone is never proof that an
        # interrupted writer's descendants retired. Recovery still applies the
        # existing journal/process checks while both relevant real locks are held.
        with source_lock(common, phase="acquisition-recovery"):
            check_acquisition_ready(common)
        # A follower rechecks after waiting so two same-repository misses do not
        # perform redundant network work.
        with source_lock(common, shared=True, phase="post-acquisition-recheck"):
            if not deepen and not prime and all(re.fullmatch(r"[0-9a-f]{40}", ref) for ref in refs):
                if all(run_git_read(common, ["cat-file", "-e", f"{ref}^{{commit}}"], check=False).returncode == 0 for ref in refs):
                    session.event("exact-object cache hit after acquisition wait")
                    result = subprocess.CompletedProcess([], 0, "", "")
                    result.requested_tips = tuple(
                        validate_sha(
                            run_git_read(
                                common, ["rev-parse", f"{ref}^{{commit}}"]
                            ).stdout.strip()
                        )
                        for ref in dict.fromkeys(refs)
                    )
                    return result
        return _fetch_via_bounded_store(
            repo,
            remote,
            refs,
            depth=depth,
            max_depth=max_depth,
            prime=prime,
            deepen=deepen,
        )


def bounded_fetch(repo: Path, remote: str, refs: list[str], *, depth: int,
                  max_depth: int = 1000000, deepen: bool = False,
                  prime: bool = False) -> subprocess.CompletedProcess:
    started = time.monotonic()
    log_event(
        "git.fetch.begin",
        "Bounded Git acquisition began",
        phase="network-acquisition",
        ref_count=len(refs),
        depth=depth,
    )
    try:
        result = _bounded_fetch_impl(
            repo,
            remote,
            refs,
            depth=depth,
            max_depth=max_depth,
            deepen=deepen,
            prime=prime,
        )
    except BaseException as exc:
        log_event(
            "git.fetch.end",
            "Bounded Git acquisition failed",
            level="Error",
            phase="network-acquisition",
            outcome="failed",
            failure_category=type(exc).__name__,
            duration_ms=max(0, round((time.monotonic() - started) * 1000, 3)),
        )
        raise
    log_event(
        "git.fetch.end",
        "Bounded Git acquisition ended",
        phase="network-acquisition",
        outcome="success" if result.returncode == 0 else "failed",
        actual_exit=result.returncode,
        duration_ms=max(0, round((time.monotonic() - started) * 1000, 3)),
    )
    return result


def _fetch_via_bounded_store(
    repo: Path,
    remote: str,
    refs: list[str],
    *,
    depth: int,
    max_depth: int,
    prime: bool = False,
    deepen: bool = False,
) -> subprocess.CompletedProcess:
    session = _FETCH_SESSION.get()

    operation = None
    source_import = False

    def command(path: Path, args: list[str], *, check: bool = True):
        timeout = session.remaining()
        if operation is None:
            return run_git(path, args, check=check, timeout=timeout,
                           progress=session.progress if "fetch" in args else None)
        operation.update(
            spawn_pending=True,
            phase="source-import" if source_import else "network-acquisition",
            source_untouched=not source_import,
            remaining_seconds=round(timeout, 3),
        )
        atomic_json(state_path, state)
        def started(identity):
            operation['processes'].append(identity)
            operation['spawn_pending'] = False
            atomic_json(state_path, state)
        try:
            with owned_journal(started):
                return run_git(path, args, check=check, timeout=timeout,
                               progress=session.progress if "fetch" in args else None)
        except CommandNotStartedError:
            operation['spawn_pending'] = False  # Typed proof: no child was launched.
            atomic_json(state_path, state)
            raise

    common = Path(command(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"]).stdout.strip())
    remote_url = remote if remote != "origin" else command(common, ["config", "--get", "remote.origin.url"]).stdout.strip()
    cache = acquisition_cache_path(common)
    for path in [cache, *cache.parents]:
        if path.is_symlink():
            raise ReviewError("Refusing symlink acquisition cache path.")
    cache.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    identity = {"schema_version": 1, "source_common": str(common),
                "remote_hash": hashlib.sha256(remote_url.encode()).hexdigest()}
    state_path = cache / "review-acquisition.json"
    if cache.exists():
        if not state_path.is_file() or state_path.is_symlink():
            raise ReviewError("Existing acquisition cache has no owned identity; refusing adoption.")
        try:
            state = json.loads(state_path.read_text())
        except (OSError, ValueError):
            raise ReviewError("Acquisition cache state is unreadable.") from None
        if not isinstance(state, dict) or any(state.get(key) != value for key, value in identity.items()):
            raise ReviewError("Acquisition cache identity does not match its source store.")
        if state.get("blocked") or state.get("operation"):
            with source_lock(common, phase="acquisition-recovery"):
                recover_acquisition_cache(common)
            state = json.loads(state_path.read_text())
        if command(cache, ["rev-parse", "--is-bare-repository"]).stdout.strip() != "true":
            raise ReviewError("Owned acquisition cache is not bare.")
        origin = command(cache, ["config", "--get", "remote.origin.url"]).stdout.strip()
        if origin != remote_url:
            raise ReviewError("Acquisition cache origin changed.")
    else:
        cache.mkdir(mode=0o700)
        command(cache, ["init", "--bare", "-q"])
        command(cache, ["remote", "add", "origin", remote_url])
        state = {**identity, "depth_highwater": 0}
        atomic_json(state_path, state)
    assert_no_git_operation(cache)
    for path in (cache / "objects", cache / "objects" / "pack", cache / "refs"):
        if path.is_symlink():
            raise ReviewError("Refusing symlink acquisition cache administration.")
    if (cache / "objects" / "info" / "alternates").exists():
        raise ReviewError("Acquisition cache must not use alternate object stores.")
    with source_lock(common, shared=True, phase="snapshot-retained-roots"):
        check_acquisition_ready(common, source_reader=True)
        edges_before = _commit_edges(run_git_read, common)
        roots = run_git_read(common, ["for-each-ref", "--format=%(objectname)"]).stdout.split()
        roots += [row["head"] for row in worktree_rows(common) if row.get("head")]
    cache_before = _commit_edges(command, cache)
    roots += command(cache, ["for-each-ref", "--format=%(objectname)"]).stdout.split()
    roots = list(dict.fromkeys(validate_sha(sha) for sha in roots))
    requested = list(dict.fromkeys(refs))
    cold_cache = not bool(cache_before)
    warm_cache = bool(cache_before) and not prime
    specifiers = (
        list(dict.fromkeys([*requested, *roots]))
        if cold_cache or deepen
        else requested
    )
    if len(specifiers) > 256:
        raise ReviewError("Bounded compatibility fetch exceeds the 256-root limit.")
    highwater = state.get("depth_highwater", 0)
    if not isinstance(highwater, int) or highwater < 0:
        raise ReviewError("Invalid acquisition depth high-water state.")
    absolute_depth = max(highwater, depth if prime else len(edges_before) + depth,
                         len(cache_before) + 1, len(edges_before) + 1)
    effective_depth = absolute_depth if cold_cache or prime or deepen else depth
    if effective_depth > max_depth:
        raise ReviewError("Absolute acquisition exceeds the configured history budget.")
    object_bytes = sum(p.stat().st_size for base in (common, cache) for p in (base / "objects" / "pack").glob("*.pack"))
    if min(shutil.disk_usage(path).free for path in (common, cache)) < max(2 * 1024 ** 3, object_bytes * 2):
        raise ReviewError("Insufficient temporary-pack headroom for bounded compatibility fetch.")
    mappings = [f"{ref}:refs/heads/acquire-{hashlib.sha256(ref.encode()).hexdigest()}" for ref in specifiers]
    # Stable per-SHA refs preserve completed downloads and negotiation roots.
    # A warm cache fetches only the missing requested tips at their requested
    # depth. Reasserting the cache-wide high-water would redownload every old
    # root; retained refs and shallow boundaries are verified unchanged below.
    session.event(
        f"acquisition cache reuse={bool(cache_before)} depth={effective_depth} "
        f"highwater={highwater} roots={len(specifiers)}"
    )
    operation = {
        "id": uuid.uuid4().hex,
        "owner": process_identity(os.getpid()),
        "processes": [],
        "started_at": utc_now(),
        "phase": "network-acquisition",
        "source_untouched": True,
        "spawn_pending": False,
        "depth": effective_depth,
    }
    operation['checkpoint'] = f"review-checkpoint-{operation['id']}.json"
    atomic_json(cache / operation['checkpoint'], {
        'source':{sha:sorted(parents) for sha,parents in edges_before.items()},
        'cache':{sha:sorted(parents) for sha,parents in cache_before.items()}})
    state['operation'] = operation
    atomic_json(state_path, state)
    try:
        fetch_args = [
            "-c", "gc.auto=0", "-c", "maintenance.auto=false", "fetch", "--progress",
            "--no-tags",
            *([] if warm_cache else [f"--depth={effective_depth}"]),
            "--", remote_url,
            *["+" + mapping for mapping in mappings],
        ]
        snapshot = tuple(requested)
        acquisition = None
        for attempt in (1, 2):
            operation["attempt"] = attempt
            atomic_json(state_path, state)
            session.event(f"acquisition attempt={attempt}")
            log_event(
                "git.fetch.attempt",
                "Git transfer attempt began",
                phase="network-acquisition",
                attempt=attempt,
                effective_depth=effective_depth,
                depth_highwater=highwater,
            )
            acquisition = command(cache, fetch_args, check=False)
            session.event(f"acquisition exit={acquisition.returncode}")
            if not acquisition.returncode:
                break
            text = acquisition.stderr.casefold()
            transient = any(
                token in text
                for token in (
                    "connection reset",
                    "connection timed out",
                    "operation timed out",
                    "the remote end hung up unexpectedly",
                    "early eof",
                    "rpc failed",
                    "http 408",
                    "http 429",
                    "http 500",
                    "http 502",
                    "http 503",
                    "http 504",
                )
            )
            forbidden = any(
                token in text
                for token in (
                    "authentication failed",
                    "could not read username",
                    "authorization",
                    "401",
                    "403",
                    "repository not found",
                    "not our ref",
                    "couldn't find remote ref",
                    "cannot lock ref",
                    "unable to create",
                    "shallow file has changed",
                )
            )
            if attempt == 2 or not transient or forbidden:
                # The owned runner returned, so every launched identity is known
                # retired. Avoid treating test doubles that never published an
                # identity as historical process proof.
                if operation.get("processes"):
                    token = _FETCH_SESSION.set(None)
                    try:
                        with source_lock(common, phase="failed-acquisition-settlement"):
                            recover_acquisition_cache(common)
                    finally:
                        _FETCH_SESSION.reset(token)
                else:
                    state.pop("operation", None)
                    state["blocked"] = False
                    atomic_json(state_path, state)
                operation = None
                return acquisition
            # The child has returned, but retirement and retained-edge settlement
            # still use the existing recovery proof before another network attempt.
            token = _FETCH_SESSION.set(None)
            try:
                with source_lock(common, phase="retry-acquisition-settlement"):
                    recover_acquisition_cache(common)
            finally:
                _FETCH_SESSION.reset(token)
            operation = None
            if tuple(requested) != snapshot:
                raise ReviewError("Acquisition snapshot changed before transient retry.")
            with source_lock(common, phase="retry-snapshot-verification"):
                check_acquisition_ready(common)
                _verify_edges(edges_before, _commit_edges(run_git_read, common))
            _verify_edges(cache_before, _commit_edges(command, cache))
            session.remaining()
            operation = {
                "id": uuid.uuid4().hex,
                "owner": process_identity(os.getpid()),
                "processes": [],
                "started_at": utc_now(),
                "spawn_pending": False,
                "source_untouched": True,
                "phase": "network-acquisition",
                "depth": effective_depth,
                "attempt": attempt + 1,
            }
            operation['checkpoint'] = f"review-checkpoint-{operation['id']}.json"
            atomic_json(cache / operation['checkpoint'], {
                'source':{sha:sorted(parents) for sha,parents in edges_before.items()},
                'cache':{sha:sorted(parents) for sha,parents in cache_before.items()}})
            state["operation"] = operation
            atomic_json(state_path, state)
        assert acquisition is not None
        if deepen and warm_cache:
            deepen_args = [
                "-c",
                "gc.auto=0",
                "-c",
                "maintenance.auto=false",
                "fetch",
                "--progress",
                "--no-tags",
                f"--deepen={depth}",
                "--",
                remote_url,
                *["+" + mapping for mapping in mappings],
            ]
            deepened = command(cache, deepen_args, check=False)
            if deepened.returncode:
                return deepened
        tips = [validate_sha(command(cache, ["rev-parse", mapping.split(":", 1)[1] + "^{commit}"]).stdout.strip()) for mapping in mappings]
        for tip in dict.fromkeys(tips):
            command(cache, ["update-ref", f"refs/review-setup/acquired/{tip}", tip])
        _verify_edges(cache_before, _commit_edges(command, cache))
        state.update({"depth_highwater": max(highwater, effective_depth), "acquired_at": utc_now()})
        atomic_json(state_path, state)
        acquired_bytes = sum(p.stat().st_size for p in (cache / "objects" / "pack").glob("*.pack"))
        if shutil.disk_usage(common).free < max(2 * 1024 ** 3, acquired_bytes * 2):
            raise ReviewError("Insufficient headroom to import the acquisition cache.")
        tips = list(dict.fromkeys(tips))
        local_args = ["-c", "protocol.file.allow=always", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
                      "fetch", "--progress", "--no-tags", "--update-shallow"]
        session.event("local history import")
        with source_lock(common, phase="source-import"):
            # Re-snapshot after the network phase. This captures retention roots
            # and edges published by another slot while the download ran. The
            # exact current operation may cross this gate only while this
            # invocation still owns both acquisition EX and source EX; readers
            # remain barred as soon as source_untouched becomes false.
            check_acquisition_ready(
                common, owned_operation_id=operation["id"]
            )
            import_edges = _commit_edges(run_git_read, common)
            import_roots = run_git_read(
                common, ["for-each-ref", "--format=%(objectname)"]
            ).stdout.split()
            import_roots += [
                row["head"] for row in worktree_rows(common) if row.get("head")
            ]
            operation.update(
                phase="source-import", source_untouched=False, spawn_pending=False
            )
            atomic_json(state_path, state)
            # Any concurrent root absent from the original cache snapshot is
            # imported locally before authoritative mutation, never fetched remotely.
            for root in dict.fromkeys(validate_sha(value) for value in import_roots):
                if run_git_read(
                    cache, ["cat-file", "-e", f"{root}^{{commit}}"], check=False
                ).returncode:
                    local_copy = command(
                        cache,
                        [*local_args, "--", common.as_uri(), root],
                        check=False,
                    )
                    if local_copy.returncode:
                        raise ReviewError(
                            "Concurrent retained root could not be preserved before source import."
                        )
            source_import = True
            imported = command(common, [*local_args, "--", cache.as_uri(), *tips], check=False)
            if imported.returncode:
                return imported
            expanded = command(common, [*local_args, f"--deepen={depth}", "--", cache.as_uri(), *tips], check=False)
            if expanded.returncode:
                return expanded
            _verify_edges(edges_before, _commit_edges(run_git_read, common))
            _verify_edges(import_edges, _commit_edges(run_git_read, common))
            expanded.requested_tips = tuple(tips[: len(requested)])
            persisted = json.loads(state_path.read_text())
            if persisted.get("blocked") or persisted.get("operation") != operation:
                raise CleanupNotProvenError(
                    "Successful source import journal changed before settlement."
                )
            token = _FETCH_SESSION.set(None)
            try:
                recover_acquisition_cache(common)
            finally:
                _FETCH_SESSION.reset(token)
            operation = None
        session.event("local history import complete")
        return expanded
    except CleanupNotProvenError:
        state['blocked'] = True
        atomic_json(state_path, state)
        raise
    finally:
        # A killed caller leaves the durable operation intact. A handled failure
        # has already stopped the group: verify integrity outside an exhausted
        # transfer budget, but do not settle twice after an in-loop retry.
        error = sys.exc_info()[1]
        if operation is not None and (
            error is None
            or isinstance(error, ReviewError)
            and not isinstance(error, CleanupNotProvenError)
        ):
            token = _FETCH_SESSION.set(None)
            try:
                with source_lock(common, phase="settle-acquisition-journal"):
                    recover_acquisition_cache(common)
            finally:
                _FETCH_SESSION.reset(token)


def ensure_repository(path: Path, *, read_only: bool = False) -> Path:
    if path.is_symlink() or not path.is_dir():
        raise ReviewError("Expected a real Git checkout directory.")
    runner = run_git_read if read_only else run_git
    result = runner(path, ["rev-parse", "--show-toplevel"], check=False)
    if result.returncode or Path(result.stdout.strip()).resolve() != path.resolve():
        raise ReviewError("Git root mismatch; refusing an empty submodule or parent-repository fallback.")
    return path.resolve()


def discover_root(explicit: str | None = None) -> Path:
    root = Path(explicit).absolute() if explicit else Path(__file__).resolve().parents[4]
    ensure_repository(root)
    if not (root / ".gitmodules").is_file() or not (root / "PRs").is_dir():
        raise ReviewError("Expected the canonical NOVA_reviews root with .gitmodules and PRs/.")
    return root.resolve()


def repo_slug(name: str) -> str:
    slug = re.sub(r"[^a-z0-9._]", "-", name.lower()).strip("-")
    if not slug or slug in (".", ".."):
        raise ReviewError("Repository name cannot form a safe review slug.")
    return slug


def canonical_repo_name(module: dict) -> str:
    path = module.get("path")
    if not isinstance(path, str) or not path.startswith("repos/"):
        raise ReviewError("Source module path must contain a safe canonical repository name.")
    parts = PurePosixPath(path).parts
    if len(parts) != 2:
        raise ReviewError("Source module path must contain a safe canonical repository name.")
    name = parts[1]
    if (
        not name
        or name in (".", "..", ".git")
        or "\\" in name
        or any(ord(character) < 32 for character in name)
    ):
        raise ReviewError("Source module path must contain a safe canonical repository name.")
    return name


def validate_sha(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", value):
        raise ReviewError("Provider did not supply a full, valid commit ID.")
    return value.lower()


def parse_ado_url(url: str) -> dict:
    try:
        parsed = urlsplit(url)
        port = parsed.port
    except (TypeError, ValueError):
        raise ReviewError("Invalid Azure DevOps repository URL.") from None
    if parsed.scheme != "https" or parsed.username or parsed.password or parsed.query or parsed.fragment or port not in (None, 443):
        raise ReviewError("ADO repository URL must be HTTPS without credentials, query, or custom port.")
    parts = [unquote(part) for part in parsed.path.strip("/").split("/")]
    if any(part in ("", ".", "..") or "/" in part or "\\" in part or any(ord(c) < 32 for c in part) for part in parts):
        raise ReviewError("Unsafe Azure DevOps URL path.")
    host = (parsed.hostname or "").lower()
    if host == "dev.azure.com" and len(parts) == 4 and parts[2] == "_git":
        org, project, _, repository = parts
    elif host.endswith(".visualstudio.com"):
        org = host.removesuffix(".visualstudio.com")
        if parts and parts[0].lower() == "defaultcollection":
            parts = parts[1:]
        if len(parts) != 3 or parts[1] != "_git":
            raise ReviewError("Unsupported legacy Azure DevOps repository URL.")
        project, _, repository = parts
    else:
        raise ReviewError("Repository is not a recognized Azure DevOps HTTPS URL.")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", org):
        raise ReviewError("Unsafe Azure DevOps organization name.")
    return {"org_url": f"https://dev.azure.com/{org.lower()}", "project": project, "repository": repository}


def canonical_ado_metadata_url(url: str, org_url: str) -> str:
    """Remove ADO's organization-name decoration from provider metadata only.

    Actual configured Git remotes still use strict parse_ado_url. Passwords,
    arbitrary usernames and cross-organization URLs are never accepted.
    """
    try:
        parsed = urlsplit(url)
        port = parsed.port
    except (TypeError, ValueError):
        raise ReviewError("Invalid provider repository URL.") from None
    if parsed.scheme != "https" or parsed.password is not None or parsed.query or parsed.fragment or port not in (None, 443):
        raise ReviewError("Provider repository URL contains unsupported credentials or URL components.")
    host = (parsed.hostname or "").lower()
    if host != "dev.azure.com" and not host.endswith(".visualstudio.com"):
        raise ReviewError("Provider repository URL has an unexpected host.")
    clean = urlunsplit(("https", host, parsed.path, "", ""))
    coordinates = parse_ado_url(clean)
    if coordinates["org_url"].casefold() != org_url.rstrip("/").casefold():
        raise ReviewError("Provider repository URL points to a different organization.")
    organization = coordinates["org_url"].rsplit("/", 1)[1]
    if parsed.username is not None and unquote(parsed.username).casefold() != organization.casefold():
        raise ReviewError("Provider repository URL contains an unsupported username.")
    return (coordinates["org_url"] + "/" + quote(coordinates["project"], safe="")
            + "/_git/" + quote(coordinates["repository"], safe=""))


def safe_path(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or "\\" in relative or any(ord(c) < 32 for c in relative):
        raise ReviewError("Unsafe managed path.")
    rel = PurePosixPath(relative)
    if rel.is_absolute() or not rel.parts or any(p in (".", "..", ".git") for p in rel.parts):
        raise ReviewError("Managed paths must remain beneath their recorded root.")
    path = root
    for part in rel.parts:
        path = path / part
        if path.is_symlink():
            raise ReviewError("Symlink in managed path; refusing to follow it.")
    if not path.resolve().is_relative_to(root.resolve()):
        raise ReviewError("Managed path escapes its root.")
    return path


def file_hash(path: Path) -> str:
    if path.is_symlink() or not path.is_file():
        raise ReviewError("Expected a regular artifact file.")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def atomic_json(path: Path, value: dict) -> None:
    if path.is_symlink() or path.parent.is_symlink():
        raise ReviewError("Refusing symlink state file.")
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd, temporary = tempfile.mkstemp(prefix=".state-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, sort_keys=True, ensure_ascii=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def write_new_text(path: Path, text: str) -> None:
    if path.is_symlink():
        raise ReviewError("Refusing symlink artifact.")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".artifact-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)  # Atomic publication, refusing existing paths.
        except FileExistsError:
            raise ReviewError("Artifact already exists; previous runs are never overwritten.") from None
    finally:
        os.unlink(temporary)


def write_new_json(path: Path, value: dict) -> None:
    write_new_text(path, json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n")


class AdoClient:
    """Read-only ADO queries through curl's inherited sandbox proxy/CA environment."""
    def __init__(self, org_url: str, *, timeout: int = 30, retries: int = 2):
        parsed = urlsplit(org_url)
        if parsed.scheme != "https" or parsed.hostname != "dev.azure.com" or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ReviewError("ADO organization must be https://dev.azure.com/<organization>.")
        org = parsed.path.strip("/")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", org) or parsed.port not in (None, 443):
            raise ReviewError("Invalid Azure DevOps organization.")
        self.org_url = f"https://dev.azure.com/{org.lower()}"
        self.timeout, self.retries = timeout, retries
        self.last_headers: dict[str, str] = {}

    def request(self, method: str, path: str, body=None):
        method = method.upper()
        parsed = urlsplit(path)
        if parsed.scheme or parsed.netloc or parsed.fragment or path.startswith("//") or "\\" in path:
            raise ReviewError("ADO request must use an organization-relative path.")
        if any(unquote(part) in (".", "..") or "/" in unquote(part) or "\\" in unquote(part)
               or any(ord(c) < 32 for c in unquote(part)) for part in parsed.path.split("/")):
            raise ReviewError("Unsafe ADO request path.")
        if method not in ("GET", "POST") or (method == "POST" and not parsed.path.lower().rstrip("/").endswith(("/workitemsbatch", "/pullrequestquery", "/wiql"))):
            raise ReviewError("Only read-only ADO query operations are allowed.")
        query = dict(parse_qsl(parsed.query, keep_blank_values=True))
        query.setdefault("api-version", "7.1")
        url = self.org_url + "/" + parsed.path.lstrip("/") + "?" + urlencode(query)
        payload = json.dumps(body).encode() if body is not None else None
        for attempt in range(self.retries + 1):
            with tempfile.TemporaryDirectory(prefix="review-ado-") as directory:
                headers = Path(directory) / "headers"
                output = Path(directory) / "body"
                args = ["curl", "--disable", "--silent", "--show-error", "--proto", "=https",
                        "--max-time", str(self.timeout), "--request", method, "--dump-header", str(headers),
                        "--output", str(output), "--write-out", "%{http_code}", "--header", "Accept: application/json"]
                if payload is not None:
                    args += ["--header", "Content-Type: application/json", "--data-binary", "@-"]
                args += [url]
                try:
                    result = subprocess.run(args, input=payload, capture_output=True, timeout=self.timeout + 5)
                except (OSError, subprocess.TimeoutExpired):
                    raise ReviewError("ADO transport failed; check sandbox proxy connectivity.") from None
                if result.returncode:
                    raise ReviewError(f"ADO transport failed (curl exit {result.returncode}); no response or credentials logged.")
                try:
                    status = int(result.stdout)
                except ValueError:
                    raise ReviewError("ADO transport returned an invalid HTTP status.") from None
                self.last_headers = {}
                if headers.exists():
                    for line in headers.read_text(encoding="iso-8859-1").splitlines():
                        key, sep, value = line.partition(":")
                        if sep and key.lower() in ("x-ms-continuationtoken", "retry-after"):
                            self.last_headers[key.lower()] = value.strip()
                if status == 302:
                    raise ReviewError(
                        "ADO authentication required (received an HTTP 302 sign-in "
                        "redirect); this is not the same as the resource being unavailable."
                    )
                if status in (429, 500, 502, 503, 504) and attempt < self.retries:
                    delay = self.last_headers.get("retry-after", "")
                    time.sleep(min(30, int(delay)) if delay.isdigit() else min(2 ** attempt, 8))
                    continue
                if not 200 <= status < 300:
                    raise ReviewError(f"ADO query failed (HTTP {status}); the resource may be inaccessible or unavailable.")
                try:
                    return json.loads(output.read_bytes())
                except (ValueError, OSError):
                    raise ReviewError("ADO returned a non-JSON response.") from None
        raise ReviewError("ADO request retry limit reached.")

    def get_pages(self, path: str) -> list:
        values, seen = [], set()
        for _ in range(1000):
            result = self.request("GET", path)
            if isinstance(result, list):
                return values + result
            if not isinstance(result, dict) or not isinstance(result.get("value"), list):
                raise ReviewError("ADO list response is missing its value array.")
            values.extend(result["value"])
            token = self.last_headers.get("x-ms-continuationtoken")
            if not token:
                return values
            if token in seen:
                raise ReviewError("ADO repeated a continuation token; refusing an incomplete list.")
            seen.add(token)
            parsed = urlsplit(path)
            query = dict(parse_qsl(parsed.query, keep_blank_values=True))
            query["continuationToken"] = token
            path = urlunsplit(("", "", parsed.path, urlencode(query), ""))
        raise ReviewError("ADO pagination exceeded the safety limit.")


def read_submodules(root: Path, *, read_only: bool = False) -> list[dict]:
    config = root / ".gitmodules"
    if config.is_symlink():
        raise ReviewError("Refusing symlink .gitmodules.")
    runner = run_git_read if read_only else run_git
    result = runner(root, ["config", "--file", str(config), "--get-regexp", r"^submodule\..*\.path$"])
    modules = []
    for line in result.stdout.splitlines():
        key, _, relative = line.partition(" ")
        safe_path(root, relative)
        if not relative.startswith("repos/") or len(PurePosixPath(relative).parts) != 2:
            raise ReviewError("Source submodules must be direct children of repos/.")
        url = runner(root, ["config", "--file", str(config), "--get", key[:-4] + "url"]).stdout.strip()
        coordinates = parse_ado_url(url)
        modules.append({"name": key[len("submodule."):-len(".path")], "path": relative, "url": url, **coordinates})
    if not modules:
        raise ReviewError("No configured Azure DevOps source submodules found.")
    return modules


def git_status(repo: Path, *, read_only: bool = False) -> list[tuple[str, str]]:
    runner = run_git_read if read_only else run_git
    raw = runner(repo, ["status", "--porcelain=v1", "-z", "--untracked-files=all"]).stdout
    records, entries, index = raw.split("\0"), [], 0
    while index < len(records) and records[index]:
        row = records[index]
        code, path = row[:2], row[3:]
        entries.append((code, path))
        index += 1
        if "R" in code or "C" in code:
            if index < len(records) and records[index]:
                entries.append((code, records[index]))
                index += 1
    return entries


def git_ignored_paths(
    repo: Path, *, protected: str | None = None, read_only: bool = False
) -> list[str]:
    """List ignored untracked files without reporting a protected managed source."""
    runner = run_git_read if read_only else run_git
    args = ["ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--", "."]
    if protected:
        safe_path(repo, protected)
    paths = filter(None, runner(repo, args).stdout.split("\0"))
    if protected:
        paths = (
            path
            for path in paths
            if path != protected and not path.startswith(protected + "/")
        )
    return sorted(paths)


def current_ref(repo: Path, *, read_only: bool = False) -> str | None:
    runner = run_git_read if read_only else run_git
    result = runner(repo, ["symbolic-ref", "--quiet", "--short", "HEAD"], check=False)
    return result.stdout.strip() if result.returncode == 0 else None


def worktree_rows(repo: Path, *, read_only: bool = False) -> list[dict]:
    """Parse `git worktree list --porcelain -z` into structured rows.

    Each row carries at least a resolved absolute "worktree" path and a
    "detached" bool; "head" (sha) and "branch" (short name, when checked out)
    are included when Git reports them. Shared by `ReviewState.registered()`
    (the NOVA root's own worktree registry) and `SourcePool` (each owned bare
    source store's separate worktree registry) -- these are two distinct
    registries against two different repositories, not the same list twice.
    """
    runner = run_git_read if read_only else run_git
    raw = runner(repo, ["worktree", "list", "--porcelain", "-z"]).stdout
    rows: list[dict] = []
    current: dict | None = None
    for record in raw.split("\0"):
        if record.startswith("worktree "):
            if current is not None:
                rows.append(current)
            current = {"worktree": Path(record[9:]).resolve(), "detached": False}
        elif current is None:
            continue
        elif record.startswith("HEAD "):
            current["head"] = record[5:]
        elif record == "detached":
            current["detached"] = True
        elif record.startswith("branch "):
            current["branch"] = record[7:].removeprefix("refs/heads/")
    if current is not None:
        rows.append(current)
    return rows


def validate_relative_worktree(root: Path, path: Path, common: Path, *, require_relative: bool = True) -> bool:
    """Check all three native links without changing metadata; return whether all are relative."""
    root = root.resolve()
    def owned(candidate, *, file=False):
        if not candidate.is_relative_to(root):
            raise ReviewError("Worktree metadata escapes the workspace.")
        for part in (candidate, *candidate.parents):
            if part.is_symlink():
                raise ReviewError("Worktree metadata has a symlink ancestor.")
            if part == root:
                break
        if file and not candidate.is_file():
            raise ReviewError("Worktree linking metadata is not a regular file.")
        resolved = candidate.resolve()
        if not resolved.is_relative_to(root):
            raise ReviewError("Worktree metadata escapes the workspace.")
        return resolved
    path, common = owned(path), owned(common)
    gitfile = owned(path / ".git", file=True)
    text = gitfile.read_text().strip()
    if not text.startswith("gitdir: ") or "\n" in text:
        raise ReviewError("Invalid worktree Git pointer.")
    forward = Path(text[len("gitdir: "):])
    directory = owned(path / forward)
    if directory.parent != common / "worktrees":
        raise ReviewError("Worktree metadata belongs to a foreign store.")
    backfile = owned(directory / "gitdir", file=True)
    commonfile = owned(directory / "commondir", file=True)
    backward, shared = Path(backfile.read_text().strip()), Path(commonfile.read_text().strip())
    if owned(directory / backward) != gitfile or owned(directory / shared) != common:
        raise ReviewError("Worktree linking metadata does not resolve to its owned paths.")
    actual = run_git_read(path, ["rev-parse", "--absolute-git-dir"]).stdout.strip()
    if Path(actual).resolve() != directory:
        raise ReviewError("Worktree Git directory differs from its linking metadata.")
    relative = not any(link.is_absolute() for link in (forward, backward, shared))
    if require_relative and not relative:
        raise ReviewError("Worktree links are absolute; run workspace setup to normalize them.")
    return relative


def ensure_relative_worktrees(state, pools) -> None:
    """Normalize only preflighted root/pool registrations under the caller's lifecycle EX barrier."""
    if not _held_lock(state.directory / "allocation.lock", exclusive=True):
        raise ReviewError("Relative worktree normalization requires the lifecycle writer lock.")
    root = state.root
    # Check support even for an empty workspace, before any network fetch creates a store.
    for verb in ("add", "repair"):
        help_result = run_git_read(root, ["worktree", verb, "-h"], check=False)
        if "relative-paths" not in help_result.stdout + help_result.stderr:
            raise ReviewError("Workspace setup requires Git with native relative worktree support (2.48+).")
    with ExitStack() as locks:
        for pool in pools:
            for number in range(6):
                if (root / pool.slot_relative_path(number)).exists():
                    locks.enter_context(state.slot_lock(pool.repo_key, number, phase="relative-links"))
        existing = [pool for pool in pools if pool.store.exists()]
        for pool in existing:
            check_shared_writer_ready(state, pool.repo_key)
            check_acquisition_ready(pool.store)
        outer_paths = {root / pool.slot_relative_path(number) for pool in pools for number in range(6)}
        groups = [(root, state.common, {root, *outer_paths})]
        groups += [(pool.store, pool.store, {pool.store, root / pool.module["path"],
                    *(root / pool.slot_relative_path(number) / pool.module["path"] for number in range(6))})
                   for pool in existing]
        plans = []
        seen_outer = set()
        # Every group is inspected before the first repair, including currently relative links.
        for repo, common, allowed in groups:
            rows = worktree_rows(repo, read_only=True)
            registered = {row["worktree"] for row in rows}
            if len(registered) != len(rows) or not registered.issubset(allowed):
                raise ReviewError("Unexpected worktree registration; refusing link repair.")
            for expected in allowed - {repo}:
                if (expected / ".git").exists() and expected not in registered:
                    raise ReviewError("Unregistered worktree; refusing link repair.")
            if repo == root:
                seen_outer = registered
            assert_no_git_operation(repo, read_only=True)
            snapshots, needs_repair = [], False
            for row in rows:
                path = row["worktree"]
                if path == repo:
                    continue
                if path.parent.name == "repos" and path.parent.parent != root and path.parent.parent not in seen_outer:
                    raise ReviewError("Source worktree has no owned outer registration.")
                relative = validate_relative_worktree(root, path, common, require_relative=False)
                directory = Path(run_git_read(path, ["rev-parse", "--absolute-git-dir"]).stdout.strip())
                # Native repair rewrites the forward/back pair, not commondir.
                if Path((directory / "commondir").read_text().strip()).is_absolute():
                    raise ReviewError("Absolute commondir is not native-repairable; refusing before mutation.")
                assert_no_git_operation(path, read_only=True)
                if (directory / "locked").exists():
                    raise ReviewError("Locked worktree; refusing link repair.")
                preserved = []
                for name in ("HEAD", "index"):
                    item = directory / name
                    if item.is_symlink() or not item.is_file():
                        raise ReviewError("Worktree HEAD/index is not a regular file.")
                    preserved.append(hashlib.sha256(item.read_bytes()).hexdigest())
                snapshots.append((path, directory, preserved))
                needs_repair |= not relative
            plans.append((repo, common, snapshots, needs_repair))
        for repo, common, snapshots, needs_repair in plans:
            # Outer and source administration may not nest. Lifecycle EX spans both phases.
            guard = state.outer_lock(phase="relative-links") if repo == root else source_lock(common, phase="relative-links")
            with guard:
                for path, directory, before in snapshots:
                    validate_relative_worktree(root, path, common, require_relative=False)
                    assert_no_git_operation(path, read_only=True)
                    if [hashlib.sha256((directory / name).read_bytes()).hexdigest() for name in ("HEAD", "index")] != before:
                        raise ReviewError("Worktree HEAD/index changed after link preflight.")
                if needs_repair:
                    run_git(repo, ["worktree", "repair", "--relative-paths"])
                for path, directory, before in snapshots:
                    validate_relative_worktree(root, path, common)
                    after = [hashlib.sha256((directory / name).read_bytes()).hexdigest() for name in ("HEAD", "index")]
                    if after != before:
                        raise ReviewError("Worktree HEAD/index changed during link normalization.")


def require_commit_identity(repo: Path, *, operation: str = "Operation") -> None:
    """Require Git's effective author and committer identities without guessing."""
    for variable in ("GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT"):
        result = run_git_read(repo, ["var", variable], check=False)
        if result.returncode or not re.fullmatch(
            r"[^<>\r\n]+ <[^<>\s]+> [0-9]+ [+-][0-9]{4}\n?", result.stdout
        ):
            raise ReviewError(
                f"{operation} requires an existing Git author and committer identity; no identity was configured or guessed."
            )


@dataclass(frozen=True)
class ReviewOwnedAllowlist:
    """A reusable review-owned content allowlist.

    Not yet wired into any legacy call site: existing setup/reset/archive/
    discard/local-merge code keeps its narrower, hardcoded `pr_dir`-plus-
    gitlink check unchanged. This representation exists for the future
    publish/reconcile/complete flow, which needs the wider surface
    (`pr_dir/**`, `KnowledgeBase/**`, `DeveloperLearnings/**`, plus the
    selected gitlink) without silently broadening what legacy actions accept.
    """

    pr_dir: str
    gitlink_path: str
    extra_prefixes: tuple[str, ...] = ()

    def prefixes(self) -> tuple[str, ...]:
        return (self.pr_dir + "/", *self.extra_prefixes)

    def allows(self, relative: str) -> bool:
        if relative == self.gitlink_path:
            return True
        return any(relative.startswith(prefix) for prefix in self.prefixes())


def checkpoint_pool_run(
    state: ReviewState,
    manifest: dict,
    files: list[str],
    *,
    slot: Path,
    message: str,
    trailer: str,
    command=None,
    extra_prefixes: tuple[str, ...] = (),
) -> str:
    """Commit only a validated PR directory and its selected gitlink.

    ``extra_prefixes`` optionally allows additional top-level review-owned
    prefixes (e.g. ``KnowledgeBase/``, ``DeveloperLearnings/``) to be staged
    alongside the manifest's ``pr_dir`` and selected gitlink. It defaults to
    empty, so existing callers keep committing only the narrower legacy set.
    """
    command = command or run_git
    for relative in files:
        if not relative.startswith(manifest["pr_dir"] + "/") and not any(
            relative.startswith(prefix) for prefix in extra_prefixes
        ):
            raise ReviewError(
                "Checkpoint input path is outside the allowed review-owned prefixes."
            )
    paths = sorted(set([*files, manifest["submodule"]["path"]]))
    for relative in paths:
        safe_path(slot, relative)
    manifest["checkpoint_paths"] = paths
    manifest["status"] = "checkpointing"
    state.save_pool_run(manifest)
    command(slot, ["add", "--", *paths])
    staged = set(
        filter(
            None,
            run_git_read(slot, ["diff", "--cached", "--name-only", "-z"]).stdout.split("\0"),
        )
    )
    if not staged.issubset(set(paths)):
        raise ReviewError(
            "Unexpected staged path appeared; checkpoint stopped without discarding anything."
        )
    if staged:
        command(slot, ["commit", "-m", f"{message}\n\n{trailer}"])
    tip = validate_sha(run_git_read(slot, ["rev-parse", "HEAD"]).stdout.strip())
    manifest["checkpoint_sha"] = tip
    manifest["status"] = "checkpointed"
    state.save_pool_run(manifest)
    return tip


def recover_pool_checkpoint(
    state: ReviewState,
    manifest: dict,
    slot: Path,
    *,
    trailer: str,
    expected_parent: str | None = None,
) -> bool:
    """Recognize only the exact single-parent, scoped checkpoint this run intended."""
    tip = validate_sha(run_git_read(slot, ["rev-parse", "HEAD"]).stdout.strip())
    parent = validate_sha(expected_parent or manifest["branch_start"])
    if tip == parent:
        return False
    parents = run_git_read(slot, ["rev-list", "--parents", "-n", "1", tip]).stdout.split()
    message = run_git_read(slot, ["log", "-1", "--format=%B"]).stdout
    changed = set(
        filter(
            None,
            run_git_read(
                slot,
                ["diff-tree", "--no-commit-id", "--name-only", "-r", "-z", tip],
            ).stdout.split("\0"),
        )
    )
    if (
        parents != [tip, parent]
        or trailer not in message
        or not changed.issubset(set(manifest.get("checkpoint_paths", [])))
    ):
        raise ReviewError("Unrecognized commit appeared during pooled checkpoint recovery.")
    manifest["checkpoint_sha"] = tip
    manifest["status"] = "checkpointed"
    state.save_pool_run(manifest)
    return True


def assert_no_git_operation(repo: Path, *, read_only: bool = False) -> None:
    runner = run_git_read if read_only else run_git
    for name in ("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "index.lock"):
        resolved = runner(repo, ["rev-parse", "--git-path", name]).stdout.strip()
        path = Path(resolved)
        if not path.is_absolute():
            path = repo / path
        if path.exists():
            raise ReviewError("An existing Git operation or index lock must be resolved first.")


PRESERVATION_MAX_BYTES = 4 * 1024 ** 3
PRESERVATION_HEADROOM_BYTES = 64 * 1024 ** 2


def _hash_preservation_file(path: Path, *, changed_message: str) -> dict:
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    digest = hashlib.sha256()
    size = 0
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode):
            raise ReviewError("Unsupported special file in preservation input.")
        for block in iter(lambda: os.read(descriptor, 65536), b""):
            digest.update(block)
            size += len(block)
        after = os.fstat(descriptor)
    finally:
        os.close(descriptor)
    identity = lambda item: (
        item.st_dev,
        item.st_ino,
        item.st_mode,
        item.st_size,
        item.st_mtime_ns,
        item.st_ctime_ns,
    )
    if identity(before) != identity(after) or size != after.st_size:
        raise ReviewError(changed_message)
    return {
        "mode": stat.S_IMODE(after.st_mode),
        "size": size,
        "sha256": digest.hexdigest(),
    }


def _index_entry(repo: Path, relative: str) -> dict | None:
    result = run_git_read(repo, ["show", f":{relative}"], check=False)
    if result.returncode:
        return None
    mode_result = run_git_read(repo, ["ls-files", "-s", "--", relative])
    fields = mode_result.stdout.split(None, 3)
    if len(fields) < 4:
        raise ReviewError("Preservation index entry is unreadable.")
    payload = result.stdout.encode("utf-8", "surrogateescape")
    return {
        "mode": int(fields[0], 8) & 0o777,
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "data": payload,
    }


def _preservation_entry(repo: Path, relative: str) -> dict:
    """Describe one cleanup input without following it or exposing its content."""
    relative_path = PurePosixPath(relative)
    if relative_path.is_absolute() or any(part in ("", ".", "..") for part in relative_path.parts):
        raise ReviewError("Preservation path escapes its checkout.")
    path = repo.joinpath(*relative_path.parts)
    parent = path.parent
    while parent != repo:
        if parent.is_symlink():
            raise ReviewError("Symlink parent in preservation path; refusing capture.")
        parent = parent.parent
    index = _index_entry(repo, relative)
    record = {"path": relative}
    if index is not None:
        record.update(
            index_mode=index["mode"],
            index_size=index["size"],
            index_sha256=index["sha256"],
        )
    try:
        value = path.lstat()
    except FileNotFoundError:
        record["type"] = "absent"
        return record
    mode = stat.S_IMODE(value.st_mode)
    if stat.S_ISLNK(value.st_mode):
        target = os.readlink(path)
        record.update(
            type="symlink",
            mode=mode,
            target=target,
            symlink_target=target,
        )
        return record
    if stat.S_ISDIR(value.st_mode):
        record.update(type="directory", mode=mode)
        return record
    if not stat.S_ISREG(value.st_mode):
        raise ReviewError("Unsupported special file in preservation input.")
    record.update(
        type="file",
        **_hash_preservation_file(
            path, changed_message="Preservation input changed during inspection."
        ),
    )
    return record


def _preservation_paths(repo: Path, *, protected: str | None = None) -> tuple[list, list, list]:
    status = git_status(repo, read_only=True)
    ignored = git_ignored_paths(repo, protected=protected, read_only=True)
    paths = {path for _, path in status} | set(ignored)
    protected_prefix = f"{protected}/" if protected else None
    if protected_prefix:
        paths = {
            path
            for path in paths
            if path != protected and not path.startswith(protected_prefix)
        }
    return status, ignored, sorted(paths)


def _preservation_special_files(repo: Path, *, protected: Path | None = None) -> None:
    for parent, dirs, files in os.walk(repo, followlinks=False):
        parent = Path(parent)
        for name in list(dirs) + files:
            path = parent / name
            if path == repo / ".git" or path == protected:
                if name in dirs:
                    dirs.remove(name)
                continue
            value = path.lstat()
            if stat.S_ISDIR(value.st_mode) or stat.S_ISREG(value.st_mode) or stat.S_ISLNK(value.st_mode):
                if stat.S_ISLNK(value.st_mode) and name in dirs:
                    dirs.remove(name)
                continue
            raise ReviewError("Unsupported special file in preservation input.")


def _index_stage_identity(repo: Path) -> str:
    """Hash Git's canonical, NUL-delimited staged-entry stream without mutation."""
    staged = run_git_read(repo, ["ls-files", "--stage", "-z"]).stdout.encode(
        "utf-8", "surrogateescape"
    )
    return hashlib.sha256(staged).hexdigest()


def inspect_checkout_preservation(repo: Path, *, protected: Path | None = None) -> dict:
    inventory = inspect_cleanup_tree(repo, protected=protected, read_only=True)
    protected_relative = protected.relative_to(repo).as_posix() if protected else None
    _preservation_special_files(repo, protected=protected)
    status, ignored, paths = _preservation_paths(repo, protected=protected_relative)
    entries = [_preservation_entry(repo, path) for path in paths]
    index = _index_binding(repo)
    return {
        "head": validate_sha(run_git_read(repo, ["rev-parse", "HEAD"]).stdout.strip()),
        "ref": current_ref(repo, read_only=True),
        # Kept under the schema's established key. This is now the SHA-256 of
        # the read-only `git ls-files --stage -z` canonical entry stream.
        "index_tree": _index_stage_identity(repo),
        "index": index,
        "status": [[code, path] for code, path in status],
        "ignored": ignored,
        "inventory_count": len(inventory),
        "entries": entries,
    }


def _index_binding(repo: Path) -> dict:
    raw = run_git_read(repo, ["rev-parse", "--path-format=absolute", "--git-path", "index"]).stdout.strip()
    index = Path(raw)
    if not index.is_absolute():
        index = repo / index
    if index.is_symlink() or not index.is_file():
        raise ReviewError("Preservation requires a regular Git index file.")
    return {
        "path": str(index.resolve()),
        "sha256": file_hash(index),
        "size": index.stat().st_size,
    }


def inspect_preservation_generation(
    state,
    repo_key: str,
    slot: int,
    old_run_id: str | None,
    outer: Path,
    source: Path,
) -> dict:
    """Bind one exact slot generation and every input cleanup would remove."""
    roots = {
        "outer": inspect_checkout_preservation(outer, protected=source),
        "source": inspect_checkout_preservation(source),
    }
    binding = {
        "schema_version": 1,
        "workspace_identity": state.identity,
        "repo_key": repo_key,
        "slot_number": slot,
        "old_run_id": old_run_id,
        "outer": roots["outer"],
        "source": roots["source"],
    }
    generation = hashlib.sha256(
        json.dumps(binding, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    return {
        "reclaim_generation": generation,
        "outer_head": roots["outer"]["head"],
        "outer_ref": roots["outer"]["ref"],
        "outer_index_sha256": roots["outer"]["index"]["sha256"],
        "outer_index_tree": roots["outer"]["index_tree"],
        "source_head": roots["source"]["head"],
        "source_ref": roots["source"]["ref"],
        "source_index_sha256": roots["source"]["index"]["sha256"],
        "source_index_tree": roots["source"]["index_tree"],
        "binding": binding,
    }


def _copy_preservation_file(source: Path, destination: Path, expected: dict) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    input_fd = os.open(source, os.O_RDONLY | os.O_NOFOLLOW)
    output_fd = os.open(
        destination,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
        0o600,
    )
    digest = hashlib.sha256()
    size = 0
    try:
        before = os.fstat(input_fd)
        while True:
            block = os.read(input_fd, 65536)
            if not block:
                break
            digest.update(block)
            size += len(block)
            os.write(output_fd, block)
        os.fsync(output_fd)
        after = os.fstat(input_fd)
    finally:
        os.close(input_fd)
        os.close(output_fd)
    identity = lambda item: (
        item.st_dev,
        item.st_ino,
        item.st_mode,
        item.st_size,
        item.st_mtime_ns,
        item.st_ctime_ns,
    )
    if (
        identity(before) != identity(after)
        or size != expected["size"]
        or digest.hexdigest() != expected["sha256"]
    ):
        raise ReviewError("Preservation input changed during capture.")


def _write_preservation_payload(destination: Path, payload: bytes, expected: dict) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(
        destination,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
        0o600,
    )
    try:
        os.write(descriptor, payload)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    if len(payload) != expected["size"] or hashlib.sha256(payload).hexdigest() != expected["sha256"]:
        raise ReviewError("Preservation payload verification failed.")


def _archive_checkout_preservation(
    repository: Path,
    temporary: Path,
    label: str,
    checkout: dict,
) -> dict:
    root_record = {
        "head": checkout["head"],
        "ref": checkout["ref"],
        "index_sha256": checkout["index"]["sha256"],
        "index_tree": checkout["index_tree"],
        "entries": [],
    }
    _copy_preservation_file(
        Path(checkout["index"]["path"]),
        temporary / label / "index",
        checkout["index"],
    )
    blobs = temporary / label / "blobs"
    for entry in checkout["entries"]:
        archived = dict(entry)
        if entry.get("index_sha256"):
            expected = {
                "size": entry["index_size"],
                "sha256": entry["index_sha256"],
            }
            payload_path = blobs / f"index-{entry['index_sha256']}"
            if not payload_path.exists():
                index = _index_entry(repository, entry["path"])
                if index is None:
                    raise ReviewError("Preservation index input changed during capture.")
                _write_preservation_payload(payload_path, index["data"], expected)
            archived["index_payload"] = payload_path.relative_to(temporary).as_posix()
        if entry["type"] == "file":
            payload_path = blobs / f"worktree-{entry['sha256']}"
            if not payload_path.exists():
                _copy_preservation_file(repository / entry["path"], payload_path, entry)
            elif file_hash(payload_path) != entry["sha256"]:
                raise ReviewError("Preservation blob verification failed.")
            archived["worktree_sha256"] = entry["sha256"]
            archived["worktree_payload"] = payload_path.relative_to(temporary).as_posix()
        root_record["entries"].append(archived)
    return root_record


def preserve_cleanup_generation(
    state,
    inspection: dict,
    *,
    operation_id: str,
    outer: Path,
    source: Path,
) -> dict:
    """Durably capture one inspected generation before cleanup; retries verify it."""
    if not re.fullmatch(r"[0-9a-f]{32}", operation_id):
        raise ReviewError("Preservation operation identity is invalid.")
    expected = inspection["reclaim_generation"]
    binding = inspection["binding"]
    root = Path(inspection.get("preservation_root") or state.directory / "preservations")
    final = root / operation_id
    manifest_path = final / "manifest.json"
    if final.is_symlink() or final.parent.is_symlink():
        raise ReviewError("Refusing symlink preservation archive.")
    if final.exists():
        if not manifest_path.is_file() or manifest_path.is_symlink():
            raise ReviewError("Preservation archive is incomplete.")
        try:
            saved = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            raise ReviewError("Preservation archive manifest is unreadable.") from None
        if saved.get("reclaim_generation") != expected or saved.get("binding") != binding:
            raise ReviewError("Preservation archive belongs to another slot generation.")
        return {
            "path": str(final),
            "reclaim_generation": expected,
            "manifest_sha256": file_hash(manifest_path),
        }

    size = sum(
        entry.get("size", 0) + entry.get("index_size", 0)
        for label in ("outer", "source")
        for entry in binding[label]["entries"]
    ) + sum(binding[label]["index"]["size"] for label in ("outer", "source"))
    if size > PRESERVATION_MAX_BYTES:
        raise ReviewError("Preservation input exceeds the bounded archive size.")
    usage = shutil.disk_usage(state.directory)
    if usage.free < size * 2 + PRESERVATION_HEADROOM_BYTES:
        raise ReviewError("Insufficient disk headroom for verified preservation.")

    root.mkdir(parents=True, mode=0o700, exist_ok=True)
    temporary = root / f".{operation_id}.capturing"
    if temporary.exists() or temporary.is_symlink():
        raise ReviewError("Incomplete preservation staging directory requires exact retry cleanup.")
    temporary.mkdir(parents=True, mode=0o700)
    try:
        archive = {
            "schema_version": 1,
            "reclaim_generation": expected,
            "binding": binding,
            "captured_at": utc_now(),
            "roots": {},
            "checkouts": {},
        }
        for label, repository in (("outer", outer), ("source", source)):
            record = _archive_checkout_preservation(
                repository,
                temporary,
                label,
                binding[label],
            )
            archive["roots"][label] = record
            archive["checkouts"][label] = record
        write_new_json(temporary / "manifest.json", archive)
        os.replace(temporary, final)
        directory = os.open(final.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return {
        "path": str(final),
        "reclaim_generation": expected,
        "manifest_sha256": file_hash(manifest_path),
    }


def preservation_generation(binding: dict) -> str:
    return hashlib.sha256(
        json.dumps(binding, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


def _read_verified_preservation_file(
    path: Path, *, expected_size: int, expected_sha256: str
) -> bytes:
    if (
        not isinstance(expected_size, int)
        or isinstance(expected_size, bool)
        or expected_size < 0
        or not isinstance(expected_sha256, str)
        or not re.fullmatch(r"[0-9a-f]{64}", expected_sha256)
    ):
        raise ReviewError("Preservation archive metadata is invalid.")
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    except OSError:
        raise ReviewError("Preservation archive payload is missing or unsafe.") from None
    digest = hashlib.sha256()
    chunks = []
    size = 0
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode):
            raise ReviewError("Preservation archive payload is not a regular file.")
        for block in iter(lambda: os.read(descriptor, 65536), b""):
            chunks.append(block)
            digest.update(block)
            size += len(block)
        after = os.fstat(descriptor)
    finally:
        os.close(descriptor)
    identity = lambda item: (
        item.st_dev,
        item.st_ino,
        item.st_mode,
        item.st_size,
        item.st_mtime_ns,
        item.st_ctime_ns,
    )
    if (
        identity(before) != identity(after)
        or size != after.st_size
        or size != expected_size
        or digest.hexdigest() != expected_sha256
    ):
        raise ReviewError("Preservation archive payload size or SHA-256 changed.")
    return b"".join(chunks)


def _preservation_archive_relative(value, *, expected: str | None = None) -> str:
    if not isinstance(value, str):
        raise ReviewError("Preservation archive contains an invalid payload path.")
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(
        part in ("", ".", "..") for part in relative.parts
    ):
        raise ReviewError("Preservation archive payload path is unsafe.")
    normalized = relative.as_posix()
    if expected is not None and normalized != expected:
        raise ReviewError("Preservation archive payload binding changed.")
    return normalized


def verify_preservation_archive(
    archive: Path,
    *,
    expected_manifest_sha256: str,
    expected_generation: str,
    expected_binding: dict,
) -> dict:
    """Revalidate one journal-bound archive immediately before destructive cleanup."""
    if (
        not isinstance(expected_manifest_sha256, str)
        or not re.fullmatch(r"[0-9a-f]{64}", expected_manifest_sha256)
        or not isinstance(expected_generation, str)
        or not re.fullmatch(r"[0-9a-f]{64}", expected_generation)
        or not isinstance(expected_binding, dict)
        or preservation_generation(expected_binding) != expected_generation
    ):
        raise ReviewError("Preservation archive journal binding is invalid.")
    if archive.is_symlink() or not archive.is_dir():
        raise ReviewError("Preservation archive is missing or unsafe.")
    actual_files = set()
    for parent, directories, files in os.walk(archive, followlinks=False):
        parent_path = Path(parent)
        for name in list(directories):
            path = parent_path / name
            try:
                value = path.lstat()
            except OSError:
                raise ReviewError("Preservation archive directory changed.") from None
            if not stat.S_ISDIR(value.st_mode) or stat.S_ISLNK(value.st_mode):
                raise ReviewError("Preservation archive contains an unsafe directory.")
        for name in files:
            path = parent_path / name
            try:
                value = path.lstat()
            except OSError:
                raise ReviewError("Preservation archive payload changed.") from None
            if not stat.S_ISREG(value.st_mode) or stat.S_ISLNK(value.st_mode):
                raise ReviewError("Preservation archive contains an unsafe payload.")
            actual_files.add(path.relative_to(archive).as_posix())
    manifest_path = archive / "manifest.json"
    try:
        manifest_size = manifest_path.stat(follow_symlinks=False).st_size
    except OSError:
        raise ReviewError("Preservation archive manifest is missing or unsafe.") from None
    manifest_bytes = _read_verified_preservation_file(
        manifest_path,
        expected_size=manifest_size,
        expected_sha256=expected_manifest_sha256,
    )
    try:
        saved = json.loads(manifest_bytes.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        raise ReviewError("Preservation archive manifest is unreadable.") from None
    if (
        not isinstance(saved, dict)
        or saved.get("schema_version") != 1
        or saved.get("reclaim_generation") != expected_generation
        or saved.get("binding") != expected_binding
    ):
        raise ReviewError("Preservation archive manifest binding changed.")
    checkouts = saved.get("checkouts")
    labels = [
        label
        for label in ("outer", "source")
        if isinstance(expected_binding.get(label), dict)
    ]
    if not isinstance(checkouts, dict) or set(checkouts) != set(labels):
        raise ReviewError("Preservation archive checkout inventory is incomplete.")
    roots = saved.get("roots")
    if roots is not None and roots != checkouts:
        raise ReviewError("Preservation archive root binding changed.")

    expected_files = {"manifest.json"}
    for label in labels:
        checkout = expected_binding[label]
        archived = checkouts[label]
        expected_root = {
            "head": checkout.get("head"),
            "ref": checkout.get("ref"),
            "index_sha256": checkout.get("index", {}).get("sha256"),
            "index_tree": checkout.get("index_tree"),
        }
        if not isinstance(archived, dict) or any(
            archived.get(key) != value for key, value in expected_root.items()
        ):
            raise ReviewError("Preservation archive checkout binding changed.")
        entries = archived.get("entries")
        inspected_entries = checkout.get("entries")
        if not isinstance(entries, list) or not isinstance(inspected_entries, list):
            raise ReviewError("Preservation archive entry inventory is invalid.")
        if len(entries) != len(inspected_entries):
            raise ReviewError("Preservation archive entry inventory is incomplete.")

        index = checkout.get("index")
        if not isinstance(index, dict):
            raise ReviewError("Preservation archive index binding is invalid.")
        index_relative = f"{label}/index"
        _read_verified_preservation_file(
            archive / index_relative,
            expected_size=index.get("size"),
            expected_sha256=index.get("sha256"),
        )
        expected_files.add(index_relative)

        seen_paths = set()
        for inspected, entry in zip(inspected_entries, entries):
            if not isinstance(inspected, dict) or not isinstance(entry, dict):
                raise ReviewError("Preservation archive entry is invalid.")
            relative = _preservation_archive_relative(inspected.get("path"))
            if relative in seen_paths or entry.get("path") != relative:
                raise ReviewError("Preservation archive entry path binding changed.")
            seen_paths.add(relative)
            entry_type = inspected.get("type")
            if entry_type not in ("absent", "directory", "file", "symlink"):
                raise ReviewError("Preservation archive entry type is unsafe.")
            expected_entry = dict(inspected)
            index_sha256 = inspected.get("index_sha256")
            if index_sha256 is not None:
                payload_relative = f"{label}/blobs/index-{index_sha256}"
                if entry.get("index_payload") != payload_relative:
                    raise ReviewError("Preservation archive index payload binding changed.")
                _read_verified_preservation_file(
                    archive / payload_relative,
                    expected_size=inspected.get("index_size"),
                    expected_sha256=index_sha256,
                )
                expected_entry["index_payload"] = payload_relative
                expected_files.add(payload_relative)
            if entry_type == "file":
                worktree_sha256 = inspected.get("sha256")
                payload_relative = f"{label}/blobs/worktree-{worktree_sha256}"
                if (
                    entry.get("worktree_payload") != payload_relative
                    or entry.get("worktree_sha256") != worktree_sha256
                ):
                    raise ReviewError(
                        "Preservation archive worktree payload binding changed."
                    )
                _read_verified_preservation_file(
                    archive / payload_relative,
                    expected_size=inspected.get("size"),
                    expected_sha256=worktree_sha256,
                )
                expected_entry["worktree_payload"] = payload_relative
                expected_entry["worktree_sha256"] = worktree_sha256
                expected_files.add(payload_relative)
            elif "worktree_payload" in entry or "worktree_sha256" in entry:
                raise ReviewError("Preservation archive non-file entry has a payload.")
            if entry_type == "symlink" and (
                not isinstance(entry.get("target"), str)
                or entry.get("symlink_target") != entry.get("target")
            ):
                raise ReviewError("Preservation archive symlink record is invalid.")
            if entry != expected_entry:
                raise ReviewError("Preservation archive entry metadata changed.")

    if actual_files != expected_files:
        raise ReviewError("Preservation archive file inventory is incomplete or changed.")
    return saved


def preserve_checkout_generation(
    state,
    checkout: dict,
    *,
    operation_id: str,
    repository: Path,
    label: str = "outer",
) -> dict:
    """Durably archive one checkout inspection for reset continuation."""
    binding = {
        "schema_version": 1,
        "workspace_identity": state.identity,
        label: checkout,
    }
    generation = preservation_generation(binding)
    final = state.directory / "preservations" / operation_id
    manifest_path = final / "manifest.json"
    if final.is_symlink() or final.parent.is_symlink():
        raise ReviewError("Refusing symlink preservation archive.")
    if final.exists():
        try:
            saved = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            raise ReviewError("Preservation archive is incomplete or unreadable.") from None
        if saved.get("reclaim_generation") != generation or saved.get("binding") != binding:
            raise ReviewError("Preservation archive belongs to another checkout generation.")
        return {
            "path": str(final),
            "reclaim_generation": generation,
            "manifest_sha256": file_hash(manifest_path),
        }
    size = checkout["index"]["size"] + sum(
        entry.get("size", 0) + entry.get("index_size", 0)
        for entry in checkout["entries"]
    )
    if size > PRESERVATION_MAX_BYTES:
        raise ReviewError("Preservation input exceeds the bounded archive size.")
    if shutil.disk_usage(state.directory).free < size * 2 + PRESERVATION_HEADROOM_BYTES:
        raise ReviewError("Insufficient disk headroom for verified preservation.")
    temporary = final.parent / f".{operation_id}.capturing"
    if temporary.exists() or temporary.is_symlink():
        raise ReviewError("Incomplete preservation staging directory requires exact retry cleanup.")
    temporary.mkdir(parents=True, mode=0o700)
    try:
        archive = {
            "schema_version": 1,
            "reclaim_generation": generation,
            "binding": binding,
            "captured_at": utc_now(),
            "checkouts": {
                label: _archive_checkout_preservation(
                    repository,
                    temporary,
                    label,
                    checkout,
                )
            },
        }
        write_new_json(temporary / "manifest.json", archive)
        os.replace(temporary, final)
        descriptor = os.open(final.parent, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return {
        "path": str(final),
        "reclaim_generation": generation,
        "manifest_sha256": file_hash(manifest_path),
    }


def inspect_cleanup_tree(
    repo: Path,
    *,
    protected: Path | None = None,
    read_only: bool = False,
) -> list[str]:
    """Inventory without following links; nested repositories are never cleanup targets."""
    assert_no_git_operation(repo, read_only=read_only)
    paths = []
    for parent, dirs, files in os.walk(repo, followlinks=False):
        parent = Path(parent)
        for name in list(dirs) + files:
            path = parent / name
            if path == repo / ".git" or path == protected:
                if name in dirs:
                    dirs.remove(name)
                continue
            if name == ".git" or (path.is_dir() and not path.is_symlink()
                                  and (path / "HEAD").is_file() and (path / "objects").is_dir()):
                raise ReviewError("Unexpected nested Git repository; refusing slot cleanup.")
            if path.is_symlink() and name in dirs:
                dirs.remove(name)
            paths.append(path.relative_to(repo).as_posix())
    return paths


def cleanup_worktree(repo: Path, expected_head: str, *, protected: str | None = None) -> None:
    """Discard only one prevalidated checkout; never recurse into a source gitlink."""
    started = time.monotonic()
    log_event("cleanup.begin", "Checkout cleanup began", phase="inspect")
    if run_git_read(repo, ["rev-parse", "HEAD"]).stdout.strip() != expected_head:
        raise ReviewError("Checkout HEAD changed after cleanup intent.")
    paths = inspect_cleanup_tree(
        repo, protected=safe_path(repo, protected) if protected else None
    )
    status = git_status(repo, read_only=True)
    ignored = run_git_read(
        repo, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]
    ).stdout.split("\0")
    log_event(
        "cleanup.phase.begin",
        "Checkout content cleanup began",
        phase="content-clean",
        staged_count=sum(code[0] != " " for code, _ in status),
        unstaged_count=sum(code[1] != " " for code, _ in status),
        untracked_count=sum(code == "??" for code, _ in status),
        ignored_count=sum(bool(path) for path in ignored),
        inventory_count=len(paths),
    )
    run_git(repo, ["-c", "submodule.recurse=false", "restore", "--source=HEAD",
                   "--staged", "--worktree", "--", "."])
    args = ["clean", "-f", "-d", "-x"]
    if protected:
        args += ["-e", f"/{protected}/"]
    run_git(repo, [*args, "--", "."])
    log_event(
        "cleanup.end",
        "Checkout cleanup ended",
        phase="content-clean",
        outcome="success",
        duration_ms=max(0, round((time.monotonic() - started) * 1000, 3)),
    )


def subtree_files(root: Path, relative: str) -> dict[str, str]:
    directory = safe_path(root, relative)
    files = {}
    if not directory.exists():
        return files
    for parent, dirs, names in os.walk(directory, followlinks=False):
        for name in dirs + names:
            path = Path(parent) / name
            rel = path.relative_to(root).as_posix()
            safe_path(root, rel)
            if path.is_file():
                files[rel] = file_hash(path)
    return files


# A pool-run manifest that has gone untouched this long is treated as
# abandoned by `ReviewState.load_pool_run(reap_stale=True)` -- forward-only
# staleness reason "expired" below. `save_pool_run()` bumps `updated_at` on
# every phase transition, so any run still actively progressing never ages
# past this window.
POOL_RUN_ABANDONED_AFTER_SECONDS = 30 * 60


def _pool_run_pr_number(value: dict) -> int | None:
    snapshot = value.get("snapshot")
    pr_number = snapshot.get("pr_number") if isinstance(snapshot, dict) else None
    return pr_number if isinstance(pr_number, int) and not isinstance(pr_number, bool) else None


def _pool_run_is_expired(value: dict, *, now: datetime) -> bool:
    """Malformed, missing, or timezone-naive `updated_at` counts as expired
    (unparseable-old-state policy) rather than raising or assuming a zone."""
    raw = value.get("updated_at")
    if not isinstance(raw, str):
        return True
    try:
        updated_at = datetime.fromisoformat(raw)
    except ValueError:
        return True
    if updated_at.tzinfo is None:
        return True
    return (now - updated_at) > timedelta(seconds=POOL_RUN_ABANDONED_AFTER_SECONDS)


def _pool_run_stale_reason(
    value: dict | None,
    *,
    identity: str,
    repo_key: str,
    slot: int,
    requested_pr: int | None,
    check_abandonment: bool,
    now: datetime,
) -> str | None:
    """Classify a loaded pool-run manifest as stale (forward-only: safe to
    reap and continue), or return None when it is still live and must block.

    Stale when: (a) the file failed to read/parse (`value is None`), (b) its
    schema or workspace identity does not match, (c) `check_abandonment` is
    requested and it has gone untouched past
    `POOL_RUN_ABANDONED_AFTER_SECONDS`, or (d) `check_abandonment` is
    requested and it already belongs to the exact PR the caller is
    requesting -- a retry of the same request, not someone else's live
    review. A schema/identity-valid, fresh manifest for a *different* PR is
    never stale here; callers must keep blocking on it.
    """
    if value is None:
        return "unreadable"
    if (
        value.get("schema_version") != 2
        or value.get("identity") != identity
        or value.get("repo_key") != repo_key
        or value.get("slot_number") != slot
    ):
        return "identity"
    if not check_abandonment:
        return None
    if requested_pr is not None and _pool_run_pr_number(value) == requested_pr:
        return "same-pr"
    if _pool_run_is_expired(value, now=now):
        return "expired"
    return None


class ReviewState:
    """A script-owned slot registry. Root worktree and unknown paths are never reset."""
    def __init__(self, root: Path, *, read_only: bool = False):
        self.read_only = read_only
        self.root = ensure_repository(root, read_only=read_only)
        runner = run_git_read if read_only else run_git
        common = runner(root, ["rev-parse", "--git-common-dir"]).stdout.strip()
        self.common = (root / common).resolve() if not Path(common).is_absolute() else Path(common).resolve()
        self.directory = self.common / "review-setup"
        if self.directory.is_symlink():
            raise ReviewError("Refusing symlink review state directory.")
        self.identity = hashlib.sha256(str(self.common).encode()).hexdigest()

    @contextmanager
    def lock(
        self,
        *,
        shared: bool = False,
        phase: str = "lifecycle",
        wait: bool = True,
    ):
        """Compatibility barrier: pooled callers use SH; legacy/admin callers use EX."""
        self.directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        for name in ("slots", "history", "pending"):
            if (self.directory / name).is_symlink():
                raise ReviewError("Refusing symlink review state subdirectory.")
        with _scoped_lock(
            self.directory / "allocation.lock",
            kind="compatibility",
            resource="workspace",
            scope="compatibility barrier",
            shared=shared,
            phase=phase,
            wait=wait,
        ):
            yield

    @contextmanager
    def registry_lock(self, *, phase: str = "reservation", wait: bool = True):
        with _scoped_lock(
            self.directory / "registry.lock",
            kind="registry",
            resource="workspace",
            scope="registry",
            phase=phase,
            wait=wait,
        ):
            yield

    @contextmanager
    def slot_lock(
        self,
        repo_key: str,
        slot: int,
        *,
        phase: str = "slot-lifecycle",
        wait: bool = True,
    ):
        if not re.fullmatch(r"[a-z0-9._-]+", repo_key) or repo_key in (".", ".."):
            raise ReviewError("Invalid pool repository key.")
        SourcePool._validate_slots([slot])
        with _scoped_lock(
            self.directory / "slot-locks" / f"{repo_key}-{slot}.lock",
            kind="slot",
            resource=repo_key,
            scope=f"slot {slot}",
            phase=phase,
            wait=wait,
        ):
            yield

    @contextmanager
    def outer_lock(self, *, phase: str = "outer-administration", wait: bool = True):
        with _scoped_lock(
            self.directory / "outer-administration.lock",
            kind="outer",
            resource="workspace",
            scope="outer Git administration",
            phase=phase,
            wait=wait,
        ):
            yield

    def manifest_path(self, slot: int) -> Path:
        if not isinstance(slot, int) or slot < 0 or slot > 9999:
            raise ReviewError("Slot must be an integer from 0 through 9999.")
        return self.directory / "slots" / f"{slot}.json"

    def pool_run_path(self, repo_key: str, slot: int) -> Path:
        if not re.fullmatch(r"[a-z0-9._-]+", repo_key) or repo_key in (".", ".."):
            raise ReviewError("Invalid pool repository key.")
        SourcePool._validate_slots([slot])
        return self.directory / "pool-runs" / f"{repo_key}-{slot}.json"

    def prepared_slot_path(self, repo_key: str, slot: int) -> Path:
        """Path for Task 3's minimal `prepare()` manifest -- a distinct,
        additive namespace from `pool_run_path()`'s legacy `pool-runs/`
        schema. Never the same file; never read/written by the other."""
        if not re.fullmatch(r"[a-z0-9._-]+", repo_key) or repo_key in (".", ".."):
            raise ReviewError("Invalid pool repository key.")
        SourcePool._validate_slots([slot])
        return self.directory / "prepared-slots" / f"{repo_key}-{slot}.json"

    def setup_reservation_path(self, repo_key: str, key: str) -> Path:
        if not re.fullmatch(r"[a-z0-9._-]+", repo_key) or repo_key in (".", ".."):
            raise ReviewError("Invalid pool repository key.")
        if not re.fullmatch(r"[a-z0-9._-]+", key) or key in (".", ".."):
            raise ReviewError("Invalid review reservation key.")
        return self.directory / "setup-reservations" / f"{repo_key}-{key}.json"

    def save_setup_reservation(self, reservation: dict) -> None:
        path = self.setup_reservation_path(
            reservation.get("repo_key"), reservation.get("key")
        )
        value = dict(reservation)
        value.update(
            {
                "schema_version": 1,
                "identity": self.identity,
                "updated_at": utc_now(),
            }
        )
        atomic_json(path, value)

    def load_setup_reservation(self, repo_key: str, key: str) -> dict | None:
        path = self.setup_reservation_path(repo_key, key)
        if path.is_symlink() or path.parent.is_symlink():
            raise ReviewError("Refusing symlink setup reservation.")
        if not path.exists():
            return None
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            raise ReviewError("Setup reservation is unreadable.") from None
        if (
            value.get("schema_version") != 1
            or value.get("identity") != self.identity
            or value.get("repo_key") != repo_key
            or value.get("key") != key
        ):
            raise ReviewError("Setup reservation has incompatible identity or schema.")
        SourcePool._validate_slots([value.get("slot_number")])
        if not isinstance(value.get("id"), str) or not re.fullmatch(
            r"[0-9a-f]{32}", value["id"]
        ):
            raise ReviewError("Setup reservation token is invalid.")
        return value

    def all_setup_reservations(self, repo_key: str) -> list[dict]:
        if not re.fullmatch(r"[a-z0-9._-]+", repo_key) or repo_key in (".", ".."):
            raise ReviewError("Invalid pool repository key.")
        directory = self.directory / "setup-reservations"
        if directory.is_symlink():
            raise ReviewError("Refusing symlink setup reservation registry.")
        if not directory.exists():
            return []
        rows = []
        for path in sorted(directory.glob(f"{repo_key}-*.json")):
            try:
                value = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                raise ReviewError("Setup reservation is unreadable.") from None
            if value.get("repo_key") != repo_key or not isinstance(value.get("key"), str):
                raise ReviewError("Setup reservation has incompatible identity or schema.")
            rows.append(self.load_setup_reservation(repo_key, value["key"]))
        return rows

    def save_pool_run(self, manifest: dict) -> None:
        repo_key = manifest.get("repo_key")
        slot = manifest.get("slot_number")
        path = self.pool_run_path(repo_key, slot)
        manifest["updated_at"] = utc_now()
        atomic_json(path, manifest)
        event = {
            "time": manifest["updated_at"],
            "event": "pool-run-state",
            "slot": manifest["slot_id"],
            "run_id": manifest["run_id"],
            "status": manifest["status"],
        }
        log = self.directory / "events.jsonl"
        fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "a", encoding="utf-8") as stream:
            stream.write(json.dumps(event) + "\n")

    def load_pool_run(
        self,
        repo_key: str,
        slot: int,
        *,
        requested_pr: int | None = None,
        reap_stale: bool = False,
        dry_run: bool = False,
    ) -> dict | None:
        """Load the legacy pool-run manifest for `{repo_key, slot}`.

        By default (`reap_stale=False`), behavior is unchanged from before:
        an unreadable file or a schema/workspace-identity mismatch raises.
        `requested_pr` and `dry_run` are inert unless `reap_stale=True`.

        Pass `reap_stale=True` to opt a caller into forward-only handling of
        an abandoned/incompatible manifest instead of hard-failing on it: a
        manifest classified stale (see `_pool_run_stale_reason`) is deleted
        -- exactly this validated path, nothing broader -- and this returns
        `None`, as if no run existed, so the caller's normal "nothing here"
        handling takes over. Callers should hold the same slot/outer lock
        they already take for any other pool-run mutation; this performs no
        locking of its own. Pass `requested_pr` to enable staleness reason
        (d) (same requested PR, regardless of age); a fresh, identity-valid
        manifest for any other PR is never stale and still blocks. Pass
        `dry_run=True` to get the same stale-vs-live classification and
        `None`/raise outcome without deleting anything, e.g. for inspection
        commands that must never mutate state.
        """
        path = self.pool_run_path(repo_key, slot)
        if path.is_symlink() or path.parent.is_symlink():
            raise ReviewError("Refusing symlink pool-run state file.")
        if not path.exists():
            return None
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            value = None
        reason = _pool_run_stale_reason(
            value,
            identity=self.identity,
            repo_key=repo_key,
            slot=slot,
            requested_pr=requested_pr,
            check_abandonment=reap_stale,
            now=datetime.now(timezone.utc),
        )
        if reason is None:
            return value
        if not reap_stale:
            if reason == "unreadable":
                raise ReviewError("Pool-run manifest is unreadable.")
            raise ReviewError("Pool-run manifest has incompatible identity or schema.")
        if not dry_run:
            path.unlink(missing_ok=True)
        return None

    def release_setup_reservation(
        self, repo_key: str, key: str, reservation_id: str
    ) -> None:
        value = self.load_setup_reservation(repo_key, key)
        if value is None:
            return
        if value.get("id") != reservation_id:
            raise ReviewError("Setup reservation ownership changed.")
        self.setup_reservation_path(repo_key, key).unlink()

    def all_pool_runs(self) -> list[dict]:
        directory = self.directory / "pool-runs"
        if directory.is_symlink():
            raise ReviewError("Refusing symlink pool-run registry.")
        if not directory.exists():
            return []
        rows = []
        for path in sorted(directory.glob("*.json")):
            try:
                value = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                raise ReviewError("Pool-run manifest is unreadable.") from None
            if value.get("schema_version") != 2 or value.get("identity") != self.identity:
                raise ReviewError("Pool-run manifest has incompatible identity or schema.")
            rows.append(value)
        return rows

    def load(self, slot: int) -> dict:
        path = self.manifest_path(slot)
        if path.is_symlink() or path.parent.is_symlink():
            raise ReviewError("Refusing symlink state file.")
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            raise ReviewError("No readable managed run exists for that slot.") from None
        if value.get("identity") != self.identity or value.get("slot") != slot or value.get("schema_version") != 1:
            raise ReviewError("Run manifest belongs to a different Git root or has an unsupported schema.")
        expected = f".worktrees/review-slot-{slot}"
        if value.get("slot_path") != expected:
            raise ReviewError("Run manifest contains an unexpected slot path.")
        safe_path(self.root, expected)
        return value

    def save(self, manifest: dict) -> None:
        if manifest.get("schema_version") == 2:
            self.save_pool_run(manifest)
            return
        manifest["updated_at"] = utc_now()
        atomic_json(self.manifest_path(manifest["slot"]), manifest)
        self.directory.mkdir(parents=True, exist_ok=True)
        event = {"time": manifest["updated_at"], "event": "state", "slot": manifest["slot"],
                 "run_id": manifest["run_id"], "status": manifest["status"]}
        log = self.directory / "events.jsonl"
        fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "a", encoding="utf-8") as stream:
            stream.write(json.dumps(event) + "\n")

    def slot_path(self, manifest: dict) -> Path:
        return safe_path(self.root, manifest["slot_path"])

    def all_runs(self) -> list[dict]:
        directory = self.directory / "slots"
        if directory.is_symlink():
            raise ReviewError("Refusing symlink slot registry.")
        return [self.load(int(path.stem)) for path in sorted(directory.glob("*.json"))]

    def registered(self, *, read_only: bool = False) -> dict[Path, str | None]:
        return {
            row["worktree"]: row.get("branch")
            for row in worktree_rows(self.root, read_only=read_only)
        }

    def check_slot(self, manifest: dict, *, allow_detached: bool = False) -> Path:
        slot = self.slot_path(manifest)
        ensure_repository(slot)
        if slot not in self.registered():
            raise ReviewError("Slot is not registered in the canonical repository.")
        common = run_git(slot, ["rev-parse", "--git-common-dir"]).stdout.strip()
        if (slot / common).resolve() != self.common:
            raise ReviewError("Slot belongs to a different repository or mount namespace.")
        assert_no_git_operation(slot)
        expected = manifest.get("archive_branch") if manifest.get("status") in ("renamed", "restoring") and manifest.get("archive_branch") else manifest["branch"]
        active = current_ref(slot)
        rename_recovery = (manifest.get("status") == "renaming" and active == manifest.get("archive_branch")
                           and run_git(slot, ["rev-parse", "HEAD"]).stdout.strip() == manifest.get("checkpoint_sha"))
        if active != expected and not rename_recovery and not (allow_detached and active is None):
            raise ReviewError("Slot branch changed outside this run; refusing to alter it.")
        return slot

    def validate_run_changes(self, manifest: dict, *, archive: bool = False) -> list[str]:
        slot = self.check_slot(manifest)
        return self._validate_run_changes_in_slot(slot, manifest, archive=archive)


    def validate_pool_run_changes(
        self,
        manifest: dict,
        *,
        archive: bool = False,
        expected_source_sha: str | None = None,
        expected_tip: str | None = None,
        extra_prefixes: tuple[str, ...] = (),
    ) -> list[str]:
        if manifest.get("schema_version") != 2:
            raise ReviewError("Expected a schema-2 pooled run manifest.")
        slot = safe_path(self.root, manifest["slot_path"])
        ensure_repository(slot)
        if slot not in self.registered():
            raise ReviewError("Pooled slot is not registered in the canonical repository.")
        common = run_git(
            slot, ["rev-parse", "--path-format=absolute", "--git-common-dir"]
        ).stdout.strip()
        if Path(common).resolve() != self.common:
            raise ReviewError("Pooled slot belongs to a different repository or mount namespace.")
        assert_no_git_operation(slot)
        expected_ref = (
            manifest.get("archive_branch")
            if manifest.get("reset_phase") == "branch-archived"
            else manifest["branch"]
        )
        if current_ref(slot) != expected_ref:
            raise ReviewError("Pooled slot branch changed outside this run.")
        return self._validate_run_changes_in_slot(
            slot,
            manifest,
            archive=archive,
            expected_source_sha=expected_source_sha,
            expected_tip=expected_tip,
            extra_prefixes=extra_prefixes,
        )


    def _validate_run_changes_in_slot(
        self,
        slot: Path,
        manifest: dict,
        *,
        archive: bool,
        expected_source_sha: str | None = None,
        expected_tip: str | None = None,
        extra_prefixes: tuple[str, ...] = (),
    ) -> list[str]:
        expected_tip = expected_tip or manifest.get("checkpoint_sha") or manifest["branch_start"]
        if run_git(slot, ["rev-parse", "HEAD"]).stdout.strip() != expected_tip:
            raise ReviewError("Review branch has new commits outside the recorded run; preserve them manually before reset.")
        files = []
        for code, relative in git_status(slot):
            safe_path(slot, relative)
            if relative == manifest["submodule"]["path"]:
                continue
            if not relative.startswith(manifest["pr_dir"] + "/") and not any(
                relative.startswith(prefix) for prefix in extra_prefixes
            ):
                raise ReviewError("Unrelated slot changes exist; nothing was discarded or staged.")
            if relative in manifest.get("baseline_artifacts", {}):
                raise ReviewError("An artifact from a prior round was modified; refusing to overwrite its history.")
            known_artifacts = dict(manifest.get("artifacts", {}))
            if manifest.get("status") == "publishing":
                known_artifacts.update(manifest.get("planned_artifacts", {}))
            if not archive and (relative not in known_artifacts or not safe_path(slot, relative).is_file() or file_hash(safe_path(slot, relative)) != known_artifacts[relative]):
                raise ReviewError("A generated artifact changed or new user output exists; archive it instead of discarding.")
            if code[0] not in (" ", "?") and not archive:
                raise ReviewError("Staged review output requires archive mode.")
            files.append(relative)
        source = safe_path(slot, manifest["submodule"]["path"])
        if (source / ".git").exists():
            ensure_repository(source)
            if git_status(source):
                raise ReviewError("Source submodule contains changes; no source code is automatically committed or reset.")
            if not manifest.get("source_baseline") and run_git(source, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]).stdout:
                raise ReviewError("Source checkout contains ignored local data; reset must preserve it.")
            expected_source = expected_source_sha or manifest["snapshot"]["head_sha"]
            if manifest.get("source_prepared") and run_git(source, ["rev-parse", "HEAD"]).stdout.strip() != expected_source:
                raise ReviewError("Source HEAD changed after setup; refusing reset.")
        return sorted(set(files))


    def reserve(self, snapshot: dict, module: dict, *, slot: int | None = None) -> dict:
        key = f"{repo_slug(snapshot['repository']['name'])}-{int(snapshot['pr_number'])}"
        branch = f"review/{key}"
        if run_git(self.root, ["check-ref-format", "--branch", branch], check=False).returncode:
            raise ReviewError("Invalid review branch name.")
        if slot is not None:
            self.manifest_path(slot)
        runs = self.all_runs()
        for old in runs:
            if old["key"] == key and old["status"] not in ("archived", "discarded"):
                if slot is not None and old["slot"] != slot:
                    raise ReviewError(f"PR already owns slot {old['slot']}; reset it before choosing another slot.")
                if old["snapshot"].get("snapshot_fingerprint") != snapshot.get("snapshot_fingerprint"):
                    raise ReviewError("PR snapshot changed; archive/reset the prior run before preparing a new snapshot.", 3)
                if old["status"] == "reserving":
                    return self._finish_reserve(old)
                self.validate_run_changes(old)
                return old
        active = {r["slot"] for r in runs if r["status"] not in ("archived", "discarded")}
        if slot is None:
            slot = next((n for n in range(10000) if n not in active), None)
            if slot is None:
                raise ReviewError("No free review slot.")
        if slot in active:
            raise ReviewError("Requested slot is reserved for another review.")
        path = safe_path(self.root, f".worktrees/review-slot-{slot}")
        previous = next((r for r in runs if r["slot"] == slot), None)
        if path.exists() and previous is None:
            raise ReviewError("Existing slot directory is not owned by these scripts.")
        registered = self.registered()
        owners = [p for p, name in registered.items() if name == branch]
        if owners and owners != [path]:
            raise ReviewError("Review branch is checked out in another worktree.")
        baseline = run_git(self.root, ["rev-parse", "HEAD"]).stdout.strip()
        start_ref = None
        if previous:
            ensure_repository(path)
            if path not in registered or git_status(path):
                raise ReviewError("Previously released slot is not clean and registered.")
            baseline = run_git(path, ["rev-parse", "HEAD"]).stdout.strip()
            start_ref = current_ref(path)
        branch_result = run_git(self.root, ["rev-parse", "--verify", f"refs/heads/{branch}"], check=False)
        branch_start = branch_result.stdout.strip() if branch_result.returncode == 0 else baseline
        manifest = {"schema_version": 1, "identity": self.identity, "slot": slot,
                    "slot_path": path.relative_to(self.root).as_posix(), "key": key, "branch": branch,
                    "branch_existed": branch_result.returncode == 0, "branch_start": branch_start,
                    "baseline_sha": baseline, "baseline_ref": start_ref, "submodule": module,
                    "snapshot": snapshot,
                    "pr_dir": f"PRs/{canonical_repo_name(module)}-{int(snapshot['pr_number'])}",
                    "status": "reserving",
                    "run_id": datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:8],
                    "created_at": utc_now(), "artifacts": {}, "baseline_artifacts": {},
                    "source_prepared": False, "source_baseline": None}
        self.save(manifest)
        return self._finish_reserve(manifest)

    def _finish_reserve(self, manifest: dict) -> dict:
        path = self.slot_path(manifest)
        if not path.exists():
            path.parent.mkdir(parents=True, exist_ok=True)
            exclude = self.common / "info/exclude"
            if exclude.is_symlink() or exclude.parent.is_symlink():
                raise ReviewError("Refusing symlink Git exclusion file.")
            existing = exclude.read_text(encoding="utf-8") if exclude.exists() else ""
            if "/.worktrees/" not in existing.splitlines():
                exclude.parent.mkdir(exist_ok=True)
                with exclude.open("a", encoding="utf-8") as stream:
                    stream.write(("\n" if existing and not existing.endswith("\n") else "") + "/.worktrees/\n")
            run_git(self.root, ["worktree", "add", "--relative-paths", "--detach", str(path), manifest["baseline_sha"]])
        ensure_repository(path)
        if path not in self.registered() or git_status(path):
            raise ReviewError("Interrupted slot reservation is not clean and registered.")
        branch = manifest["branch"]
        exists = run_git(self.root, ["show-ref", "--verify", "--quiet", f"refs/heads/{branch}"], check=False).returncode == 0
        if exists:
            actual = run_git(self.root, ["rev-parse", f"refs/heads/{branch}"]).stdout.strip()
            if actual != manifest["branch_start"]:
                raise ReviewError("Review ref changed during preparation.")
            run_git(path, ["checkout", branch])
        else:
            run_git(path, ["checkout", "-b", branch, manifest["branch_start"]])
        manifest["baseline_artifacts"] = subtree_files(path, manifest["pr_dir"])
        source = safe_path(path, manifest["submodule"]["path"])
        if (source / ".git").exists():
            ensure_repository(source)
            if git_status(source):
                raise ReviewError("Source submodule is not clean.")
            manifest["source_baseline"] = {"head": run_git(source, ["rev-parse", "HEAD"]).stdout.strip(), "ref": current_ref(source)}
        manifest["status"] = "reserved"
        self.save(manifest)
        return manifest


def check_prepared_slot_conflict(
    state: "ReviewState",
    pool: "SourcePool",
    slot: int,
    *,
    requested_pr: int | None = None,
    reap_stale: bool = False,
    dry_run: bool = False,
) -> dict | None:
    """Fail-closed compatibility guard (rulings R2, R18).

    Before `review_reset.py` or `merge_review.py` operate on a pooled
    `{repo, slot}`, compare Task 3's new-format prepared-slot manifest
    (`review_git_prepare.py`'s `prepare()`, at
    `.git/review-setup/prepared-slots/{repo-key}-{slot}.json`) against the
    legacy `pool-runs`/`SourcePool` manifest for the same slot. Raises
    `ReviewError` if both exist and disagree on which PR currently occupies
    the slot -- refusing to reset/merge what may be the wrong PR.

    A missing new-format manifest is not a conflict: the legacy manifest
    remains fully authoritative, exactly as before this guard existed
    (ruling R18 -- no other reset/merge behavior is changed). Likewise, if
    the legacy manifest itself is absent, there is nothing recorded to
    disagree with; reset/merge's own pre-existing checks are responsible for
    rejecting an unprepared slot.

    The legacy `pool-runs` schema (schema_version 2) never recorded a merge
    commit -- only the PR's linked source/target/base SHAs (it checks out
    `snapshot["head_sha"]`, not a merge commit) -- so there is no legacy
    field directly comparable to the new manifest's `merge_sha`. The one
    safety-critical identity this guard can and does check on both sides is
    the PR number itself, which is exactly the scenario ruling R2's own
    rationale describes: "a slot re-tasked by the new preparer could be
    reset or merged as if it still held the old PR."

    Returns the parsed prepared-slot manifest when no legacy manifest is
    present to compare against (either none was ever written, or it was
    stale and `reap_stale=True` reaped it) -- callers that need to know
    "a prepared-slot manifest exists but there is nothing legacy left to
    check it against" (e.g. `reset_pool_run`'s dry-run reporting) can use
    this. Returns `None` once a legacy manifest is confirmed present and
    consistent; raises on any detected conflict, exactly as before.
    """
    new_path = state.prepared_slot_path(pool.repo_key, slot)
    if new_path.is_symlink():
        raise ReviewError("Refusing symlink prepared-slot manifest.")
    if not new_path.exists():
        return
    try:
        new_manifest = json.loads(new_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        raise ReviewError("Prepared-slot manifest is unreadable.") from None
    if (
        not isinstance(new_manifest, dict)
        or new_manifest.get("schema") != 1
        or new_manifest.get("repo") != pool.repo_name
        or new_manifest.get("slot") != slot
    ):
        raise ReviewError("Prepared-slot manifest has an unexpected shape for this slot.")
    legacy = state.load_pool_run(
        pool.repo_key,
        slot,
        requested_pr=(
            requested_pr
            if requested_pr is not None
            else new_manifest.get("pr")
            if reap_stale
            else None
        ),
        reap_stale=reap_stale,
        dry_run=dry_run,
    )
    if legacy is None:
        return new_manifest
    legacy_snapshot = legacy.get("snapshot")
    legacy_pr = legacy_snapshot.get("pr_number") if isinstance(legacy_snapshot, dict) else None
    if legacy_pr is not None and new_manifest.get("pr") != legacy_pr:
        raise ReviewError(
            f"Refusing to operate on {pool.repo_name} slot {slot}: the prepared-slot manifest "
            f"records PR {new_manifest.get('pr')} but the legacy pool-run manifest for this slot "
            f"records PR {legacy_pr}. Slot mapping is ambiguous; resolve manually before retrying."
        )


class SourcePool:
    """A shared, repo-scoped warm-slot pool of nested source checkouts.

    Six numbered outer worktrees per source repo (`.worktrees/{Repo}-{slot}`,
    slots 0..5) each host a detached nested source worktree at
    `repos/{Repo}`, all backed by exactly one bare object/common directory
    the pool owns: `review-setup/source-stores/{repo-key}.git`, under the
    NOVA store's own Git common directory (`state.common`). This is a
    separate, additive namespace from `ReviewState`'s per-PR
    `slots/<n>.json` schema-1 manifests -- `SourcePool` never reads or
    writes under `review-setup/slots/`, and `ReviewState` never reads or
    writes the pool's own state (`review-setup/pool/{repo-key}.json`).

    The module identity is resolved only from a `read_submodules()`-
    validated `.gitmodules` entry; a caller-supplied dict that does not
    exactly match a real entry is rejected before any Git call is made.

    Callers must hold `ReviewState.lock()` for the duration of any mutating
    call (`warm`, `refresh`), exactly like `ReviewState.reserve()` --
    `SourcePool` does not re-acquire or nest that lock itself.

    No `git submodule update`/`deinit` is ever used, no alternates file is
    ever created, no existing worktree is ever moved, and no forced
    cleanup (`--force`, `clean -f`, `reset --hard`) is ever issued against a
    slot this call did not just create. Unknown, foreign, legacy, occupied,
    dirty, staged, untracked, and ignored state is preserved: incompatible
    paths are refused with `ReviewError`, never deleted or overwritten.
    """

    SLOT_COUNT = 6

    def __init__(self, state: ReviewState, module: dict, *, reserve_bytes: int = 0):
        self.state = state
        known = {
            entry["path"]: entry
            for entry in read_submodules(state.root, read_only=state.read_only)
        }
        entry = known.get(module.get("path"))
        if not entry or entry["name"] != module.get("name") or entry["url"] != module.get("url"):
            raise ReviewError("Source module is not a validated .gitmodules submodule entry.")
        self.module = entry
        self.reserve_bytes = int(reserve_bytes)
        self.repo_key = repo_slug(self.module["name"])
        self.repo_name = canonical_repo_name(self.module)
        self.directory = state.common / "review-setup" / "pool"
        self.store = state.common / "review-setup" / "source-stores" / f"{self.repo_key}.git"
        self.state_path = self.directory / f"{self.repo_key}.json"
        self._baseline_cache: str | None = None

    # -- slot naming -----------------------------------------------------

    def slot_name(self, slot: int) -> str:
        return f"{self.repo_name}-{slot}"

    def slot_relative_path(self, slot: int) -> str:
        return f".worktrees/{self.slot_name(slot)}"

    @staticmethod
    def _validate_slots(slots) -> list[int]:
        # Validate the complete requested slot set before any mutation: a
        # mixed request such as [0, 6] must be fully rejected, not partially
        # applied. `bool` is an `int` subclass in Python, so it is excluded
        # explicitly rather than accepted as 0/1.
        numbers = []
        for value in slots:
            if isinstance(value, bool) or not isinstance(value, int):
                raise ReviewError("Pool slot numbers must be plain integers.")
            if value < 0 or value >= SourcePool.SLOT_COUNT:
                raise ReviewError(
                    f"Pool slot {value} is outside the supported range 0-{SourcePool.SLOT_COUNT - 1}."
                )
            numbers.append(value)
        seen: set[int] = set()
        ordered = []
        for value in numbers:
            if value not in seen:
                seen.add(value)
                ordered.append(value)
        return sorted(ordered)

    # -- owned bare store --------------------------------------------------

    def _ensure_store_locked(self) -> None:
        parent = self.store.parent
        if parent.is_symlink():
            raise ReviewError("Refusing symlink source-store parent directory.")
        parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.store.is_symlink():
            raise ReviewError("Refusing symlink source store.")
        if self.store.exists():
            result = run_git(self.store, ["rev-parse", "--is-bare-repository"], check=False)
            if result.returncode or result.stdout.strip() != "true":
                raise ReviewError("Existing source-store path is not a compatible bare repository.")
        else:
            run_git(parent, ["init", "--bare", "-q", str(self.store)])
        origin = run_git(self.store, ["config", "--get", "remote.origin.url"], check=False)
        if origin.returncode:
            run_git(self.store, ["remote", "add", "origin", self.module["url"]])
            origin = run_git(self.store, ["config", "--get", "remote.origin.url"], check=False)
        if origin.returncode or parse_ado_url(origin.stdout.strip()) != parse_ado_url(self.module["url"]):
            raise ReviewError("Source-store origin does not match the configured repository.")

    def _ensure_store(self) -> None:
        """Create/verify the store under acquisition then source EX, exactly once."""
        acquisition_path = _source_lock_path(self.store, "acquisition")
        source_path = _source_lock_path(self.store, "source")
        if _held_lock(acquisition_path, exclusive=True) and _held_lock(source_path, exclusive=True):
            with self.shared_writer(phase="source-store-initialization"):
                self._ensure_store_locked()
            return
        if _held_lock(acquisition_path) or _held_lock(source_path):
            raise ReviewError("Store initialization cannot upgrade or partially reenter repository locks.")
        with self.acquisition_lock(phase="source-store-initialization"):
            with self.store_lock(
            phase="source-store-initialization", check_ready=False
        ), self.shared_writer(
                phase="source-store-initialization"
            ):
                self._ensure_store_locked()

    def _read_state(self) -> dict:
        if self.state_path.is_symlink() or self.state_path.parent.is_symlink():
            raise ReviewError("Refusing symlink pool state file.")
        try:
            value = json.loads(self.state_path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return {}
        except (OSError, ValueError):
            raise ReviewError("Existing pool state is unreadable; refusing to replace it.") from None
        if not isinstance(value, dict) or value.get("repo_key") != self.repo_key:
            raise ReviewError("Pool state belongs to a different source repository.")
        return value


    def _state_value(self, value: dict, updates: dict) -> dict:
        result = dict(value)
        result.update(updates)
        result.update(
            {
                "schema_version": 1,
                "repo_key": self.repo_key,
                "repo_name": self.repo_name,
                "module_path": self.module["path"],
                "baseline_ref": f"refs/review-setup/baselines/{self.repo_key}",
            }
        )
        return result

    def _write_state(self, updates: dict) -> dict:
        value = self._state_value(self._read_state(), updates)
        atomic_json(self.state_path, value)
        return value

    def publication_intent_path(self) -> Path:
        return self.directory / "baseline-publications" / f"{self.repo_key}.json"


    def baseline_ref(self) -> str:
        return f"refs/review-setup/baselines/{self.repo_key}"

    def generation_namespace(self) -> str:
        return f"refs/review-setup/baseline-generations/{self.repo_key}"

    def generation_ref(self, sha: str) -> str:
        sha = validate_sha(sha)
        return f"{self.generation_namespace()}/{sha}"

    def _resolve_ref(self, ref: str, *, read_only: bool = False) -> str | None:
        runner = run_git_read if read_only else run_git
        actual = runner(self.store, ["rev-parse", "--verify", ref], check=False)
        if actual.returncode:
            return None
        return validate_sha(actual.stdout.strip())

    def cached_baseline_sha(self, *, read_only: bool = False) -> str | None:
        """The last independently-verified baseline, without fetching or recovery."""
        intent_path = self.publication_intent_path()
        if intent_path.is_symlink() or intent_path.parent.is_symlink():
            raise ReviewError("Refusing symlink baseline publication intent.")
        if intent_path.exists():
            return None
        value = self._read_state()
        sha = value.get("baseline_sha")
        if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
            return None
        if value.get("repo_key") != self.repo_key:
            return None
        if not self.store.exists() or self.store.is_symlink():
            return None
        if value.get("baseline_ref") != self.baseline_ref():
            return None
        if self._resolve_ref(self.baseline_ref(), read_only=read_only) != sha:
            return None
        return sha

    def _verify_generation_ref(self, sha: str, *, read_only: bool = False) -> bool:
        return self._resolve_ref(self.generation_ref(sha), read_only=read_only) == sha

    def _publication_binding(self) -> dict:
        return {
            "workspace_identity": self.state.identity,
            "repo_key": self.repo_key,
            "repo_name": self.repo_name,
            "module_path": self.module["path"],
            "origin_url": self.module["url"],
            "store_path": str(self.store.resolve()),
            "state_path": str(self.state_path.resolve()),
            "baseline_ref": self.baseline_ref(),
        }

    def _load_publication_intent(self) -> dict | None:
        path = self.publication_intent_path()
        if path.is_symlink() or path.parent.is_symlink():
            raise ReviewError("Refusing symlink baseline publication intent.")
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return None
        except (OSError, ValueError):
            raise ReviewError("Baseline publication intent is unreadable.") from None
        if not isinstance(value, dict) or value.get("schema_version") != 1:
            raise ReviewError("Baseline publication intent has an unsupported schema.")
        if any(value.get(key) != expected for key, expected in self._publication_binding().items()):
            raise ReviewError("Baseline publication intent has incompatible repository identity.")
        if value.get("phase") not in ("prepared", "refs-applied", "state-applied"):
            raise ReviewError("Baseline publication intent has an invalid phase.")
        expected_state = value.get("expected_state")
        target_state = value.get("target_state")
        if not isinstance(expected_state, dict) or not isinstance(target_state, dict):
            raise ReviewError("Baseline publication intent has invalid state bindings.")
        expected_old = value.get("expected_old_sha")
        if expected_old is not None:
            expected_old = validate_sha(expected_old)
        target = validate_sha(value.get("target_sha"))
        if expected_state.get("baseline_sha") != expected_old:
            raise ReviewError("Baseline publication intent old state is inconsistent.")
        if target_state.get("baseline_sha") != target:
            raise ReviewError("Baseline publication intent target state is inconsistent.")
        if self._state_value(target_state, {}) != target_state:
            raise ReviewError("Baseline publication intent target identity is inconsistent.")
        for key in ("repo_key", "repo_name", "module_path", "baseline_ref"):
            if key in expected_state and expected_state[key] != target_state[key]:
                raise ReviewError("Baseline publication intent old identity is inconsistent.")
        expected_generations = value.get("expected_generation_refs")
        if not isinstance(expected_generations, dict):
            raise ReviewError("Baseline publication intent generation bindings are invalid.")
        required = {
            self.generation_ref(sha): sha
            for sha in dict.fromkeys(
                value for value in (expected_old, target) if value is not None
            )
        }
        if set(expected_generations) != set(required):
            raise ReviewError("Baseline publication intent generation set is inconsistent.")
        for ref, old in expected_generations.items():
            if old is not None:
                validate_sha(old)
            if old not in (None, required[ref]):
                raise ReviewError("Baseline publication intent generation old value is invalid.")
        return value

    def _write_publication_intent(self, intent: dict, phase: str) -> dict:
        value = dict(intent)
        value["phase"] = phase
        atomic_json(self.publication_intent_path(), value)
        return value

    def _publication_refs_state(self, intent: dict) -> str:
        expected_old = intent["expected_old_sha"]
        target = intent["target_sha"]
        baseline = self._resolve_ref(self.baseline_ref())
        before = baseline == expected_old
        after = baseline == target
        for ref, expected in intent["expected_generation_refs"].items():
            actual = self._resolve_ref(ref)
            before = before and actual == expected
            after = after and actual == ref.rsplit("/", 1)[1]
        if after:
            return "after"
        if before:
            return "before"
        raise ReviewError("Baseline publication refs do not match the durable intent.")

    def _apply_publication_refs(self, intent: dict, *, verify_unchanged: bool = False) -> None:
        if self._publication_refs_state(intent) == "after" and not verify_unchanged:
            return
        commands = ["start\n"]
        for ref, old in intent["expected_generation_refs"].items():
            target = ref.rsplit("/", 1)[1]
            commands.append(f"update {ref} {target} {old or '0' * 40}\n")
        commands.append(
            f"update {self.baseline_ref()} {intent['target_sha']} "
            f"{intent['expected_old_sha'] or '0' * 40}\n"
        )
        commands.extend(("prepare\n", "commit\n"))
        run_git(
            self.store,
            ["update-ref", "--stdin"],
            input_data="".join(commands).encode("ascii"),
        )
        if self._publication_refs_state(intent) != "after":
            raise ReviewError("Source-pool baseline ref verification failed.")

    def _retire_publication_intent(self, intent: dict) -> None:
        path = self.publication_intent_path()
        current = self._load_publication_intent()
        if current != intent:
            raise ReviewError("Baseline publication intent changed before retirement.")
        path.unlink()
        directory = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)

    def _finish_publication_intent(self, intent: dict) -> None:
        phase = intent["phase"]
        refs_state = self._publication_refs_state(intent)
        state = self._read_state()
        state_is_old = state == intent["expected_state"]
        state_is_target = state == intent["target_state"]
        if phase == "prepared":
            if not state_is_old:
                raise ReviewError("Baseline publication state does not match prepared intent.")
            if refs_state == "before" or intent["expected_old_sha"] == intent["target_sha"]:
                self._apply_publication_refs(
                    intent,
                    verify_unchanged=(
                        refs_state == "after"
                        and intent["expected_old_sha"] == intent["target_sha"]
                        and all(
                            old == sha
                            for old, sha in zip(
                                intent["expected_generation_refs"].values(),
                                (
                                    ref.rsplit("/", 1)[1]
                                    for ref in intent["expected_generation_refs"]
                                ),
                            )
                        )
                    ),
                )
            intent = self._write_publication_intent(intent, "refs-applied")
            phase = "refs-applied"
            refs_state = "after"
        if phase == "refs-applied":
            if refs_state != "after" or not (state_is_old or state_is_target):
                raise ReviewError("Baseline publication does not match refs-applied intent.")
            if state_is_old:
                written = self._write_state(intent["target_state"])
                if written != intent["target_state"]:
                    raise ReviewError("Baseline publication wrote an unexpected target state.")
            intent = self._write_publication_intent(intent, "state-applied")
            phase = "state-applied"
        if phase == "state-applied":
            if self._publication_refs_state(intent) != "after" or self._read_state() != intent[
                "target_state"
            ]:
                raise ReviewError("Baseline publication final state does not match its intent.")
            self._retire_publication_intent(intent)

    def _new_publication_intent(self, sha: str, depth: int) -> dict:
        state = self._read_state()
        recorded = state.get("baseline_sha")
        if recorded is not None:
            recorded = validate_sha(recorded)
        if self._resolve_ref(self.baseline_ref()) != recorded:
            raise ReviewError("Baseline ref/state disagree without an owned publication intent.")
        updates = {"baseline_sha": sha, "verified_at": utc_now(), "fetch_depth": depth}
        target_state = self._state_value(state, updates)
        generations = {}
        for value in dict.fromkeys(value for value in (recorded, sha) if value):
            ref = self.generation_ref(value)
            existing = self._resolve_ref(ref)
            if existing is not None and existing != value:
                raise ReviewError("A baseline generation ref points to a different commit.")
            generations[ref] = existing
        intent = {
            "schema_version": 1,
            **self._publication_binding(),
            "expected_old_sha": recorded,
            "target_sha": sha,
            "expected_state": state,
            "target_state": target_state,
            "expected_generation_refs": generations,
            "phase": "prepared",
        }
        atomic_json(self.publication_intent_path(), intent)
        return intent

    def _publish_generation_transaction(self, sha: str, *, depth: int = 1000) -> None:
        """Finish any owned publication, then durably publish the requested target."""
        intent = self._load_publication_intent()
        if intent is not None:
            self._finish_publication_intent(intent)
        state = self._read_state()
        self._finish_publication_intent(self._new_publication_intent(sha, depth))

    def publish_baseline(self, sha: str, *, depth: int = 1000) -> None:
        sha = validate_sha(sha)
        with self.store_lock(phase="publish-baseline", check_ready=False), self.shared_writer(
            phase="publish-baseline"
        ):
            if run_git(self.store, ["cat-file", "-e", f"{sha}^{{commit}}"], check=False).returncode:
                raise ReviewError("Cannot publish a missing source-pool baseline commit.")
            self._publish_generation_transaction(sha, depth=depth)
            if self._load_publication_intent() is not None:
                raise ReviewError("Source-pool baseline publication intent did not retire.")
            if self.cached_baseline_sha() != sha or not self._verify_generation_ref(sha):
                raise ReviewError("Source-pool baseline publication verification failed.")
            self._baseline_cache = sha

    def _historical_baseline_refs(self, *, read_only: bool = False) -> dict[str, set[str]]:
        runner = run_git_read if read_only else run_git
        result = {"generation": set(), "legacy-run": set()}
        for namespace, kind in (
            (self.generation_namespace(), "generation"),
            (f"refs/review-setup/retain/{self.repo_key}/", "legacy-run"),
        ):
            output = runner(
                self.store,
                ["for-each-ref", "--format=%(refname) %(objectname)", namespace],
            ).stdout.splitlines()
            for line in output:
                ref, separator, raw_sha = line.partition(" ")
                if not separator:
                    raise ReviewError("Managed baseline provenance ref is malformed.")
                value = validate_sha(raw_sha)
                if kind == "generation":
                    if ref != self.generation_ref(value):
                        raise ReviewError("Baseline generation ref name and target disagree.")
                    result[kind].add(value)
                else:
                    prefix = f"refs/review-setup/retain/{self.repo_key}/"
                    relative = ref.removeprefix(prefix)
                    run_id, separator, role = relative.partition("/")
                    if (
                        separator
                        and role == "baseline"
                        and ref == self.retention_ref(run_id, "baseline")
                    ):
                        result[kind].add(value)
        return result

    def classify_source_baseline(
        self,
        source: Path,
        *,
        manifest: dict | None = None,
        read_only: bool = False,
    ) -> dict:
        """Classify one owned detached source by exact managed provenance."""
        runner = run_git_read if read_only else run_git
        ensure_repository(source, read_only=read_only)
        common = runner(
            source, ["rev-parse", "--path-format=absolute", "--git-common-dir"]
        ).stdout.strip()
        if Path(common).resolve() != self.store.resolve():
            raise ReviewError("Nested source worktree is not owned by this source pool.")
        if source.resolve() not in {
            row["worktree"] for row in worktree_rows(self.store, read_only=read_only)
        }:
            raise ReviewError("Nested source worktree is not registered in the owned source store.")
        assert_no_git_operation(source, read_only=read_only)
        if current_ref(source, read_only=read_only) is not None:
            raise ReviewError("Nested source worktree must remain detached.")
        head = validate_sha(runner(source, ["rev-parse", "HEAD"]).stdout.strip())
        baseline = self.cached_baseline_sha(read_only=read_only)
        if baseline is None:
            raise ReviewError("No consistent verified source-pool baseline is available.")
        if head == baseline:
            return {"status": "warm", "head_sha": head, "baseline_sha": baseline}
        provenance = self._historical_baseline_refs(read_only=read_only)
        if head in provenance["generation"] or head in provenance["legacy-run"]:
            return {
                "status": "stale",
                "head_sha": head,
                "baseline_sha": baseline,
                "provenance": "generation" if head in provenance["generation"] else "legacy-run",
            }
        if manifest and manifest.get("status") == "merged":
            merge_sha = manifest.get("merge_baseline_sha")
            run_id = manifest.get("run_id")
            retained_ref = (manifest.get("retention_refs") or {}).get("merge-baseline")
            expected_ref = self.retention_ref(run_id, "merge-baseline") if run_id else None
            if (
                isinstance(merge_sha, str)
                and validate_sha(merge_sha) == head
                and retained_ref == expected_ref
                and expected_ref is not None
                and self._resolve_ref(expected_ref, read_only=read_only) == head
            ):
                return {
                    "status": "stale",
                    "head_sha": head,
                    "baseline_sha": baseline,
                    "provenance": "terminal-merge",
                }
        return {"status": "unsafe", "head_sha": head, "baseline_sha": baseline}

    def normalize_stale_source(
        self,
        source: Path,
        target: str | None = None,
        *,
        manifest: dict | None = None,
        expected_head: str | None = None,
    ) -> dict:
        """Normalize only an exact managed stale generation under writer protection."""
        requested = validate_sha(target) if target is not None else None
        with self.store_lock(phase="normalize-stale-baseline"), self.shared_writer(
            phase="normalize-stale-baseline"
        ):
            classification = self.classify_source_baseline(source, manifest=manifest)
            current = classification["baseline_sha"]
            target = requested or current
            if requested is not None:
                if self._resolve_ref(self.baseline_ref()) == requested:
                    pass
                elif not self._verify_generation_ref(requested):
                    raise ReviewError(
                        "Journal-pinned baseline is no longer exact retained provenance."
                    )
            if expected_head is not None and classification["head_sha"] not in (
                validate_sha(expected_head),
                target,
            ):
                raise ReviewError("Source HEAD changed before stale-slot normalization.")
            if git_status(source) or git_ignored_paths(source):
                raise ReviewError("Dirty source checkout cannot be normalized by warm.")
            if classification["status"] == "unsafe":
                raise ReviewError("Source HEAD has no exact managed baseline provenance.")
            if classification["head_sha"] != target:
                old = classification["head_sha"]
                generation = self.generation_ref(old)
                existing = self._resolve_ref(generation)
                if existing is None:
                    if classification.get("provenance") != "legacy-run":
                        # Terminal merge refs are deliberately contextual and never globalized.
                        if classification.get("provenance") != "terminal-merge":
                            raise ReviewError("Stale baseline generation evidence disappeared.")
                    elif old not in self._historical_baseline_refs()["legacy-run"]:
                        raise ReviewError("Legacy baseline evidence disappeared before promotion.")
                    if classification.get("provenance") == "legacy-run":
                        run_git(self.store, ["update-ref", generation, old, "0" * 40])
                        if not self._verify_generation_ref(old):
                            raise ReviewError("Historical baseline generation promotion failed.")
                run_git(source, ["checkout", "--detach", target])
            final_head = validate_sha(run_git(source, ["rev-parse", "HEAD"]).stdout.strip())
            if (
                final_head != target
                or current_ref(source) is not None
                or git_status(source)
                or git_ignored_paths(source)
                or source.resolve()
                not in {row["worktree"] for row in worktree_rows(self.store)}
            ):
                raise ReviewError("Stale source normalization did not verify cleanly.")
            return {
                "status": "warm" if target == current else "stale",
                "head_sha": target,
                "baseline_sha": current,
            }


    def cached_baseline_sha_read_only(self) -> str | None:
        """Read and verify the cached baseline without changing state."""
        return self.cached_baseline_sha(read_only=True)

    def refresh(self, *, depth: int = 1000, persist: bool = True) -> str:
        """Fetch a bounded default-branch snapshot without shrinking history."""
        if not isinstance(depth, int) or isinstance(depth, bool) or depth < 1:
            raise ReviewError("Source-pool fetch depth must be a positive integer.")
        self._ensure_store()
        result = bounded_fetch(self.store, self.module["url"], ["HEAD"], depth=depth)
        if result.returncode:
            raise ReviewError(f"Bounded source-pool baseline fetch failed (exit {result.returncode}).")
        tips = getattr(result, "requested_tips", ())
        if len(tips) != 1:
            raise ReviewError("Bounded source-pool baseline fetch returned no exact result identity.")
        sha = validate_sha(tips[0])
        if persist:
            self.publish_baseline(sha, depth=depth)
        return sha


    def prime_history(self, *, depth: int = 100000, timeout: float = 3600,
                      dry_run: bool = False) -> dict:
        """Prime baseline ancestry only. Caller holds the lifecycle lock; no slot moves."""
        if not isinstance(depth, int) or isinstance(depth, bool) or not 0 < depth <= 1000000:
            raise ReviewError("Baseline depth must be between 1 and 1000000.")
        FetchSession(timeout, None)
        sha = self.cached_baseline_sha()
        coverage = history_coverage(self.store, sha) if sha else {"complete": False, "covered_depth": 0}
        recorded = self._read_state().get("history_priming", {})
        satisfied = bool(sha and (coverage["complete"] or coverage["covered_depth"] >= depth))
        row = {"repository": self.repo_name, "target_depth": depth, "baseline_sha": sha,
               "cache_hit": satisfied and recorded.get("baseline_sha") == sha,
               "status": "planned" if dry_run else "primed", **coverage}
        if dry_run:
            row["network_required"] = not satisfied
            return row
        with fetch_session(timeout, log=True) as session:
            session.remaining()
            self._ensure_store()
            if not satisfied:
                # Pin the already verified configured default baseline when present;
                # otherwise HEAD resolves the configured remote's default branch.
                result = bounded_fetch(self.store, self.module["url"], [sha or "HEAD"],
                                       depth=depth, max_depth=1000000, prime=True)
                if result.returncode:
                    raise ReviewError(f"Baseline history priming fetch failed (exit {result.returncode}).")
                if sha is None:
                    tips = getattr(result, "requested_tips", ())
                    if len(tips) != 1:
                        raise ReviewError(
                            "Baseline history fetch returned no exact result identity."
                        )
                    sha = validate_sha(tips[0])
                self.publish_baseline(sha, depth=depth)
                coverage = history_coverage(self.store, sha, command=lambda path, args:
                                            run_git(path, args, timeout=session.remaining()))
                if not coverage["complete"] and coverage["covered_depth"] < depth:
                    raise ReviewError("Fetched baseline history did not meet the requested coverage.")
            record = {"baseline_sha": sha, "target_depth": depth, "verified_at": utc_now(), **coverage}
            self._write_state({"history_priming": record})
            row.update({"baseline_sha": sha, **coverage})
            session.event(f"baseline coverage={coverage['covered_depth']} complete={coverage['complete']}")
        return row


    def _verified_baseline(self) -> str:
        # Repeated warm() calls against an already-verified cached baseline
        # reuse it and never re-fetch: first this pool instance's own
        # in-memory cache, then the persisted cache -- but only if the store
        # actually already has that exact object (never trust a cache entry
        # for a store that could have been recreated since).
        if self._baseline_cache is not None:
            return self._baseline_cache
        cached = self.cached_baseline_sha()
        if cached is not None and self.store.exists():
            check = run_git(self.store, ["cat-file", "-e", cached], check=False)
            if check.returncode == 0:
                self._baseline_cache = cached
                return cached
        return self.refresh()

    # -- warming -----------------------------------------------------------

    def warm(self, slots) -> list[dict]:
        numbers = self._validate_slots(slots)  # raises before any mutation
        if self.reserve_bytes:
            usage = shutil.disk_usage(self.state.common)
            if usage.free < self.reserve_bytes:
                raise ReviewError("Not enough free disk space reserved for the warm-pool operation.")
        root_head = validate_sha(run_git_read(self.state.root, ["rev-parse", "HEAD"]).stdout.strip())
        source_baseline = None
        rows = []
        for number in numbers:
            slot_path = self.state.directory / "slot-locks" / f"{self.repo_key}-{number}.lock"
            if _held_lock(slot_path, exclusive=True):
                with slot_writer_scope(
                    self.state, self.repo_key, number, phase="warm-slot"
                ):
                    self._ensure_store()
                    source_baseline = source_baseline or self._verified_baseline()
                    rows.append(self._warm_one(number, root_head, source_baseline))
            else:
                with self.state.slot_lock(
                    self.repo_key, number, phase="warm-slot"
                ):
                    with slot_writer_scope(
                        self.state, self.repo_key, number, phase="warm-slot"
                    ):
                        self._ensure_store()
                        source_baseline = source_baseline or self._verified_baseline()
                        rows.append(self._warm_one(number, root_head, source_baseline))
        return rows

    def _warm_one(self, slot: int, root_head: str, source_baseline: str) -> dict:
        outer = safe_path(self.state.root, self.slot_relative_path(slot))
        nested = safe_path(self.state.root, f"{self.slot_relative_path(slot)}/{self.module['path']}")
        self._ensure_outer(outer, root_head)
        self._ensure_nested(nested, source_baseline)
        if current_ref(nested) is not None:
            raise ReviewError("Nested source worktree must remain detached.")
        if git_status(nested) or git_ignored_paths(nested):
            raise ReviewError("Nested source worktree is not clean.")
        outer_status = git_status(outer)
        if git_ignored_paths(outer, protected=self.module["path"]) or any(
            path != self.module["path"] or code[0] != " "
            for code, path in outer_status
        ):
            raise ReviewError(
                "Pool outer worktree contains changes beyond its expected gitlink delta."
            )
        classification = self.normalize_stale_source(
            nested,
            manifest=self.state.load_pool_run(self.repo_key, slot),
        )
        head = classification["head_sha"]
        source_baseline = classification["baseline_sha"]
        return {
            "slot": self.slot_name(slot),
            "slot_path": outer.relative_to(self.state.root).as_posix(),
            "baseline_sha": source_baseline,
            "root_head": root_head,
            "head_sha": head,
        }

    def _ensure_outer(self, outer: Path, root_head: str) -> None:
        with self.state.outer_lock(phase="outer-worktree-registration"):
            self._ensure_outer_locked(outer, root_head)

    def _ensure_outer_locked(self, outer: Path, root_head: str) -> None:
        if outer.is_symlink():
            raise ReviewError("Refusing symlink pool worktree target.")
        registered = self.state.registered()
        if outer.exists():
            ensure_repository(outer)
            if outer not in registered:
                raise ReviewError(
                    "Pool slot directory exists but is not a registered worktree; "
                    "refusing to adopt foreign or unmanaged state."
                )
            common = run_git(outer, ["rev-parse", "--path-format=absolute", "--git-common-dir"]).stdout.strip()
            if Path(common).resolve() != self.state.common:
                raise ReviewError("Pool slot belongs to a different repository or mount namespace.")
            assert_no_git_operation(outer)
            if current_ref(outer) is not None:
                raise ReviewError(
                    "Pool slot outer worktree is on a branch; refusing to adopt foreign state."
                )
            return
        if outer.parent.is_symlink():
            raise ReviewError("Refusing symlink pool worktree parent directory.")
        outer.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        run_git(self.state.root, ["worktree", "add", "--relative-paths", "--detach", str(outer), root_head])

    def _ensure_nested(self, nested: Path, source_baseline: str) -> None:
        with self.store_lock(
            phase="source-worktree-registration", check_ready=False
        ), self.shared_writer(
            phase="source-worktree-registration"
        ):
            if nested.is_symlink():
                raise ReviewError("Refusing symlink nested source worktree target.")
            if (nested / ".git").exists():
                ensure_repository(nested)
                common = run_git(nested, ["rev-parse", "--path-format=absolute", "--git-common-dir"]).stdout.strip()
                if Path(common).resolve() != self.store.resolve():
                    raise ReviewError(
                        "Nested source worktree is bound to a different store; refusing to adopt it."
                    )
                owned = {row["worktree"] for row in worktree_rows(self.store)}
                if nested.resolve() not in owned:
                    raise ReviewError(
                        "Nested source path is a Git checkout but not a registered worktree of the owned store."
                    )
                assert_no_git_operation(nested)
                if current_ref(nested) is not None:
                    run_git(nested, ["checkout", "--detach", "-q", "HEAD"])
                run_git(nested, ["reset", "--hard", source_baseline])
                run_git(nested, ["clean", "-ffdx"])
                return
            if not nested.exists():
                raise ReviewError(
                    "Expected the gitlink placeholder directory created by the outer worktree checkout; "
                    "the outer worktree may be foreign or damaged."
                )
            if any(nested.iterdir()):
                raise ReviewError("Nested source path is occupied by unmanaged content; refusing to overwrite it.")
            run_git(self.store, ["worktree", "add", "--relative-paths", "--detach", str(nested), source_baseline])


    @staticmethod
    def _validate_run_id(run_id: str) -> str:
        if not isinstance(run_id, str) or not re.fullmatch(
            r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", run_id
        ):
            raise ReviewError("Run id cannot form a safe owned retention ref.")
        return run_id


    def retention_namespace(self, run_id: str) -> str:
        return f"refs/review-setup/retain/{self.repo_key}/{self._validate_run_id(run_id)}"


    def retention_ref(self, run_id: str, kind: str) -> str:
        if kind not in (
            "head",
            "base",
            "target",
            "baseline",
            "archive",
            "merge-baseline",
        ):
            raise ReviewError("Unsupported retention-ref kind.")
        return f"{self.retention_namespace(run_id)}/{kind}"


    def retain(self, run_id: str, commits: dict[str, str]) -> dict[str, str]:
        """Pin exact owned commits before any checkout can move away from them."""
        with self.store_lock(phase="retain-snapshot", check_ready=False), self.shared_writer(
            phase="retain-snapshot"
        ):
            if not self.store.exists() or self.store.is_symlink():
                raise ReviewError("Owned source store is absent or unsafe.")
            bare = run_git(self.store, ["rev-parse", "--is-bare-repository"], check=False)
            if bare.returncode or bare.stdout.strip() != "true":
                raise ReviewError("Owned source store is not a compatible bare repository.")
            required = ("head", "base", "target", "baseline")
            if any(kind not in commits for kind in required):
                raise ReviewError("Retention requires exact head, base, target, and baseline commits.")
            values = {kind: validate_sha(sha) for kind, sha in commits.items()}
            for kind, sha in values.items():
                if run_git(
                    self.store, ["cat-file", "-e", f"{sha}^{{commit}}"], check=False
                ).returncode:
                    raise ReviewError(f"Cannot retain missing {kind} commit in the owned source store.")
            commands = ["start\n"]
            for kind, sha in values.items():
                ref = self.retention_ref(run_id, kind)
                current = run_git(self.store, ["rev-parse", "--verify", ref], check=False)
                if current.returncode == 0 and current.stdout.strip() != sha:
                    raise ReviewError("An owned retention ref already points to a different commit.")
                previous = current.stdout.strip() if current.returncode == 0 else "0" * 40
                commands.append(f"update {ref} {sha} {previous}\n")
            commands.extend(("prepare\n", "commit\n"))
            run_git(self.store, ["update-ref", "--stdin"], input_data="".join(commands).encode("ascii"))
            for kind, sha in values.items():
                if run_git(self.store, ["rev-parse", self.retention_ref(run_id, kind)]).stdout.strip() != sha:
                    raise ReviewError("Retention-ref verification failed.")
            return values


    def drop_retention(self, run_id: str, *, expected: dict[str, str]) -> None:
        """Delete only this discarded run's refs, guarded by exact old SHAs."""
        with self.store_lock(phase="drop-retention", check_ready=False), self.shared_writer(
            phase="drop-retention"
        ):
            commands = ["start\n"]
            for kind, raw_sha in expected.items():
                sha = validate_sha(raw_sha)
                ref = self.retention_ref(run_id, kind)
                current = run_git(self.store, ["rev-parse", "--verify", ref], check=False)
                if current.returncode:
                    continue
                if current.stdout.strip() != sha:
                    raise ReviewError("Retention ref moved; refusing to delete a different object owner.")
                commands.append(f"delete {ref} {sha}\n")
            commands.extend(("prepare\n", "commit\n"))
            run_git(self.store, ["update-ref", "--stdin"], input_data="".join(commands).encode("ascii"))


    def _source_for_slot(self, slot: int, *, expected_branch: str | None = None) -> Path:
        number = self._validate_slots([slot])[0]
        outer = safe_path(self.state.root, self.slot_relative_path(number))
        nested = safe_path(
            self.state.root, f"{self.slot_relative_path(number)}/{self.module['path']}"
        )
        ensure_repository(outer)
        if outer not in self.state.registered():
            raise ReviewError("Managed pool slot is not a registered outer worktree.")
        active = current_ref(outer)
        if active is not None and active != expected_branch:
            raise ReviewError("Managed pool slot is on an unexpected branch.")
        ensure_repository(nested)
        common = run_git(
            nested, ["rev-parse", "--path-format=absolute", "--git-common-dir"]
        ).stdout.strip()
        if Path(common).resolve() != self.store.resolve():
            raise ReviewError("Nested source worktree is not owned by this source pool.")
        if nested.resolve() not in {row["worktree"] for row in worktree_rows(self.store)}:
            raise ReviewError("Nested source worktree is not registered in the owned source store.")
        assert_no_git_operation(nested)
        return nested


    def reset_source(
        self,
        slot: int,
        run_id: str,
        retained: dict[str, str],
        *,
        refresh: bool = True,
        expected_branch: str | None = None,
    ) -> dict:
        """Return a clean pooled source to a verified warm baseline without deinit."""
        source = self._source_for_slot(slot, expected_branch=expected_branch)
        if git_status(source):
            raise ReviewError("Source checkout changed during reset; leaving it untouched.")
        ignored = run_git(
            source, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]
        ).stdout
        if ignored:
            raise ReviewError("Source checkout has ignored local data; leaving it untouched.")
        retained_values = self.retain(run_id, retained)
        warning = None
        baseline = None
        if refresh:
            try:
                baseline = self.refresh(persist=False)
            except CleanupNotProvenError:
                raise
            except ReviewError:
                baseline = self.cached_baseline_sha()
                if baseline is None or not self.store.exists() or run_git(
                    self.store, ["cat-file", "-e", f"{baseline}^{{commit}}"], check=False
                ).returncode:
                    raise ReviewError(
                        "Baseline refresh failed and no verified cached baseline is available; "
                        "the pooled reset remains recoverable and blocked."
                    ) from None
                warning = "Baseline refresh failed; reused the verified cached baseline."
        else:
            baseline = self.cached_baseline_sha()
            if baseline is None or run_git(
                self.store, ["cat-file", "-e", f"{baseline}^{{commit}}"], check=False
            ).returncode:
                raise ReviewError("No verified cached warm baseline is available.")
        run_git(source, ["checkout", "--detach", validate_sha(baseline)])
        if git_status(source) or current_ref(source) is not None:
            raise ReviewError("Pooled source did not return to a clean detached baseline.")
        if run_git(source, ["rev-parse", "HEAD"]).stdout.strip() != baseline:
            raise ReviewError("Pooled source baseline verification failed.")
        self.publish_baseline(baseline)
        return {
            "baseline_sha": baseline,
            "warning": warning,
            "retained": retained_values,
        }


    @contextmanager
    def acquisition_lock(self, *, phase: str = "network-acquisition", wait: bool = True):
        """Serialize one repository's persistent acquisition cache writers."""
        with acquisition_lock(self.store, phase=phase, wait=wait):
            yield

    @contextmanager
    def store_lock(
        self,
        *,
        shared: bool = False,
        phase: str = "source-store",
        wait: bool = True,
        check_ready: bool = True,
    ):
        """Coordinate authoritative source Git readers and writers."""
        with source_lock(self.store, shared=shared, phase=phase, wait=wait):
            if check_ready:
                check_shared_writer_ready(self.state, self.repo_key)
            yield

    @contextmanager
    def shared_writer(self, *, phase: str):
        """Journal source-store mutations for a selected pooled slot."""
        scope = _SLOT_WRITER.get()
        if scope is None:
            check_shared_writer_ready(self.state, self.repo_key)
            yield
            return
        if scope["state"] is not self.state or scope["repo_key"] != self.repo_key:
            raise ReviewError(
                "Shared source mutation uses a mismatched selected-slot scope."
            )
        with shared_writer_scope(
            self.state,
            self.repo_key,
            scope["slot"],
            phase=phase,
        ):
            yield


    def _retained_commits(self) -> list[str]:
        if not self.store.exists():
            return []
        prefix = f"refs/review-setup/retain/{self.repo_key}/"
        output = run_git(
            self.store, ["for-each-ref", "--format=%(objectname)", prefix]
        ).stdout.splitlines()
        return [validate_sha(value) for value in output if value]


    def maintain(self, *, now: str | None = None, reserve_bytes: int = 5 * 1024**3) -> dict:
        """Run safe foreground maintenance on an owned idle store at most daily."""
        if not self.store.exists() or self.store.is_symlink():
            return {"status": "skipped-absent"}
        with ExitStack() as slot_locks:
            # Maintenance never waits behind a live review. Canonical ordering and
            # nonblocking acquisition prevent inversion across multiple slots.
            for slot in range(self.SLOT_COUNT):
                try:
                    slot_locks.enter_context(
                        self.state.slot_lock(
                            self.repo_key,
                            slot,
                            phase="maintenance-scan",
                            wait=False,
                        )
                    )
                except ReviewError:
                    return {"status": "skipped-busy-slot", "slot": self.slot_name(slot)}
            return self._maintain_locked(now=now, reserve_bytes=reserve_bytes)

    def _maintain_locked(
        self, *, now: str | None = None, reserve_bytes: int = 5 * 1024**3
    ) -> dict:
        timestamp = datetime.fromisoformat(now) if now else datetime.now(timezone.utc)
        if timestamp.tzinfo is None:
            raise ReviewError("Maintenance time must include a timezone.")
        value = self._read_state()
        last_raw = value.get("maintenance_at")
        if last_raw:
            try:
                last = datetime.fromisoformat(last_raw)
            except ValueError:
                raise ReviewError("Pool maintenance timestamp is invalid.") from None
            if last.tzinfo is None:
                raise ReviewError("Pool maintenance timestamp must include a timezone.")
            if (timestamp - last).total_seconds() < 24 * 60 * 60:
                return {"status": "skipped-cadence", "maintenance_at": last_raw}
        for path in (self.store, self.state.root):
            if shutil.disk_usage(path).free < reserve_bytes:
                return {"status": "skipped-headroom", "path": str(path)}
        retained = self._retained_commits()
        with self.store_lock():
            # Revalidate cadence and headroom under the per-store lock. The
            # caller already owns ReviewState.lock(), establishing the fixed
            # global-then-store lock order used by every maintenance path.
            locked_value = self._read_state()
            locked_last_raw = locked_value.get("maintenance_at")
            if locked_last_raw:
                try:
                    locked_last = datetime.fromisoformat(locked_last_raw)
                except ValueError:
                    raise ReviewError("Pool maintenance timestamp is invalid.") from None
                if locked_last.tzinfo is None:
                    raise ReviewError("Pool maintenance timestamp must include a timezone.")
                if (timestamp - locked_last).total_seconds() < 24 * 60 * 60:
                    return {
                        "status": "skipped-cadence",
                        "maintenance_at": locked_last_raw,
                    }
            bare = run_git(self.store, ["rev-parse", "--is-bare-repository"], check=False)
            origin = run_git(self.store, ["config", "--get", "remote.origin.url"], check=False)
            if bare.returncode or bare.stdout.strip() != "true" or origin.returncode or origin.stdout.strip() != self.module["url"]:
                return {"status": "skipped-unowned"}
            if any(run.get("repo_key") == self.repo_key and run.get("status") not in ("archived", "discarded", "merged")
                   for run in self.state.all_pool_runs()):
                return {"status": "skipped-active-review"}
            checkouts = []
            expected_sources = {
                safe_path(self.state.root, f"{self.slot_relative_path(slot)}/{self.module['path']}"): slot
                for slot in range(self.SLOT_COUNT)
            }
            for row in worktree_rows(self.store):
                path = row["worktree"]
                if path == self.store.resolve():
                    continue
                if path not in expected_sources:
                    return {"status": "skipped-unowned-checkout"}
                number = expected_sources[path]
                source = self._source_for_slot(number)
                outer = safe_path(self.state.root, self.slot_relative_path(number))
                if current_ref(source) is not None or git_status(source) or run_git(source, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]).stdout:
                    return {"status": "skipped-busy-checkout"}
                if any(relative != self.module["path"] or code[0] != " " for code, relative in git_status(outer)):
                    return {"status": "skipped-busy-checkout"}
                assert_no_git_operation(outer)
                checkouts.extend((outer, source))
            objects = self.store / "objects"
            temporary_bytes = sum(path.stat().st_size for path in objects.rglob("*") if path.is_file())
            for path in (self.store, self.state.root, *checkouts):
                if shutil.disk_usage(path).free < reserve_bytes + temporary_bytes:
                    return {"status": "skipped-headroom", "path": str(path)}
            retained = self._retained_commits()
            generations = self._historical_baseline_refs()["generation"]
            baseline = self.cached_baseline_sha()
            if baseline is None:
                return {"status": "skipped-unverified-baseline"}
            retained.extend(generations)
            retained.append(baseline)
            retained = list(dict.fromkeys(retained))
            for sha in retained:
                run_git(self.store, ["cat-file", "-e", f"{sha}^{{commit}}"])
            # Deliberately avoid `git gc`: it can expire reflogs and prune
            # worktree metadata as implicit side effects. Repack reachable
            # objects, then prune only loose objects older than the normal
            # two-week grace period.
            run_git(self.store, ["repack", "-d", "-l"])
            run_git(self.store, ["prune", "--expire=2.weeks.ago"])
            for sha in retained:
                if run_git(
                    self.store, ["cat-file", "-e", f"{sha}^{{commit}}"], check=False
                ).returncode:
                    raise ReviewError("Maintenance could not verify a retained source commit.")
            completed = timestamp.astimezone(timezone.utc).isoformat()
            self._write_state({"maintenance_at": completed})
        return {"status": "completed", "maintenance_at": completed}
