#!/usr/bin/env python3
"""Archive or discard a script-owned local review; never push or clean unknown files."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from pathlib import Path
import sys

from review_common import (ReviewError, ReviewState, SourcePool, assert_no_git_operation,
                           check_outer_merge_ready, check_prepared_slot_conflict, checkpoint_pool_run, current_ref,
                           discover_root, ensure_repository, file_hash, git_status,
                           bind_operation_context, lock_session, log_event, operation_logging, preflight, read_submodules, recover_pool_checkpoint,
                           repo_slug, require_commit_identity, run_git, safe_path,
                           slot_writer_scope, utc_now, write_new_json, atomic_json,
                           inspect_checkout_preservation, preserve_checkout_generation,
                           preservation_generation, cleanup_worktree,
                           verify_preservation_archive)
from review_git_prepare import _prepare_nested, _prepare_outer, _verify_postconditions


def _restore_source(state: ReviewState, manifest: dict) -> None:
    slot = state.slot_path(manifest)
    source = safe_path(slot, manifest["submodule"]["path"])
    if not (source / ".git").exists():
        if manifest.get("source_baseline"):
            raise ReviewError("Previously initialized source checkout disappeared; manual recovery required.")
        return
    ensure_repository(source)
    if git_status(source):
        raise ReviewError("Source checkout changed during reset; leaving it untouched.")
    original = manifest.get("source_baseline")
    if original:
        target = original["ref"] or original["head"]
        if original["ref"]:
            if run_git(source, ["rev-parse", target]).stdout.strip() != original["head"]:
                raise ReviewError("Original source branch moved; refusing to restore a different commit.")
            run_git(source, ["checkout", target])
        else:
            run_git(source, ["checkout", "--detach", target])
    else:
        # Git deinit can delete ignored files even without --force. Inspect ignored
        # paths explicitly before allowing it to remove this created working copy.
        ignored = run_git(source, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]).stdout
        if ignored:
            raise ReviewError("Source checkout has ignored local data; refusing to deinitialize it.")
        index_sha = run_git(slot, ["rev-parse", f":{manifest['submodule']['path']}"]).stdout.strip()
        run_git(source, ["checkout", "--detach", index_sha])
        with _legacy_outer_mutation(state, phase="legacy-source-deinit"):
            run_git(slot, ["submodule", "deinit", "--", manifest["submodule"]["path"]])


def _require_commit_identity(slot: Path) -> None:
    require_commit_identity(slot, operation="Archive")


@contextmanager
def _legacy_outer_mutation(state: ReviewState, *, phase: str):
    """Revalidate root reservation/writer state under outer protection."""
    with state.outer_lock(phase=phase):
        check_outer_merge_ready(state)
        yield


def _archive_pool_branch(
    state: ReviewState, outer: Path, manifest: dict, archive: str
) -> None:
    """Rename one owned review branch under shared outer administration."""
    with state.outer_lock(phase="pooled-archive-branch"):
        check_outer_merge_ready(state)
        active = current_ref(outer)
        if active == manifest["branch"]:
            existing = run_git(
                state.root,
                ["show-ref", "--verify", "--quiet", f"refs/heads/{archive}"],
                check=False,
            )
            if existing.returncode == 0:
                raise ReviewError("Archive branch already exists; refusing overwrite.")
            run_git(outer, ["branch", "-m", archive])
            if current_ref(outer) != archive:
                raise ReviewError("Archive branch identity did not match after rename.")
        elif active != archive:
            raise ReviewError("Pooled branch changed while archiving.")
        archived = run_git(
            state.root,
            ["rev-parse", "--verify", f"refs/heads/{archive}"],
        ).stdout.strip()
        expected = manifest.get("checkpoint_sha") or manifest.get("branch_start")
        if archived != expected:
            raise ReviewError("Archive branch tip does not match its owned checkpoint.")


def _checkpoint(
    state: ReviewState,
    manifest: dict,
    files: list[str],
    *,
    slot: Path | None = None,
) -> None:
    slot = slot or state.slot_path(manifest)
    if manifest.get("schema_version") == 2:
        checkpoint_pool_run(
            state,
            manifest,
            files,
            slot=slot,
            message=f"Archive review {manifest['key']} ({manifest['run_id']})",
            trailer=f"Review-Setup-Run: {manifest['run_id']}",
            command=run_git,
        )
        return
    # Preserve schema-1 behavior while pooled callers share the stricter helper.
    paths = sorted(set(files + [manifest["submodule"]["path"]]))
    manifest["checkpoint_paths"] = paths
    manifest["status"] = "checkpointing"
    state.save(manifest)
    run_git(slot, ["add", "--", *paths])
    staged = run_git(slot, ["diff", "--cached", "--name-only", "-z"]).stdout.split("\0")
    allowed = set(paths)
    if any(path and path not in allowed for path in staged):
        raise ReviewError("Unexpected staged path appeared; checkpoint stopped without discarding anything.")
    if any(staged):
        message = (f"Archive review {manifest['key']} ({manifest['run_id']})\n\n"
                   f"Review-Setup-Run: {manifest['run_id']}")
        run_git(slot, ["commit", "-m", message])
    manifest["checkpoint_sha"] = run_git(slot, ["rev-parse", "HEAD"]).stdout.strip()
    manifest["status"] = "checkpointed"
    state.save(manifest)


def reset_run(state: ReviewState, slot: int, *, mode: str = "archive", confirm: str | None = None,
              dry_run: bool = False) -> dict:
    if mode not in ("archive", "discard"):
        raise ReviewError("Reset mode must be archive or discard.")
    if mode == "discard" and not _discard_confirmed(confirm):
        raise ReviewError("Discard requires --confirm YES (or yes/y).")
    if dry_run:
        manifest = state.load(slot)
        if manifest["status"] in ("archived", "discarded", "merged"):
            return manifest
        files = state.validate_run_changes(manifest, archive=mode == "archive")
        return {"dry_run": True, "mode": mode, "slot": slot, "branch": manifest["branch"],
                "paths": files + [manifest["submodule"]["path"]], "restore_commit": manifest["baseline_sha"]}
    with state.lock():
        check_outer_merge_ready(state)
        manifest = state.load(slot)
        if manifest["status"] in ("archived", "discarded", "merged"):
            return manifest
        if mode == "discard" and not _discard_confirmed(confirm):
            raise ReviewError("Discard requires --confirm YES (or yes/y).")
        if manifest.get("reset_mode") and manifest["reset_mode"] != mode:
            raise ReviewError("Reset already began in a different mode; retry that mode.")
        worktree = state.slot_path(manifest)
        ensure_repository(worktree)
        assert_no_git_operation(worktree)
        status = manifest["status"]
        if status in ("restoring", "detached"):
            actual_ref = current_ref(worktree)
            actual_head = run_git(worktree, ["rev-parse", "HEAD"]).stdout.strip()
            original_ref = manifest.get("archive_branch") if mode == "archive" else manifest["branch"]
            original_head = manifest.get("checkpoint_sha") or manifest["branch_start"]
            restored = actual_ref == manifest.get("baseline_ref") and actual_head == manifest["baseline_sha"]
            not_yet_restored = status == "restoring" and actual_ref == original_ref and actual_head == original_head
            if not (restored or not_yet_restored):
                raise ReviewError("Slot branch or HEAD changed during restoration; refusing to switch unrelated state.")
        if status == "checkpointing":
            # A commit may have completed before the journal write; recognize only
            # this run's trailer and exact parent/path scope, never arbitrary commits.
            tip = run_git(worktree, ["rev-parse", "HEAD"]).stdout.strip()
            if tip != manifest["branch_start"]:
                parent = run_git(worktree, ["rev-parse", "HEAD^"], check=False)
                message = run_git(worktree, ["log", "-1", "--format=%B"]).stdout
                changed = set(filter(None, run_git(worktree, ["diff-tree", "--no-commit-id", "--name-only", "-r", "-z", "HEAD"]).stdout.split("\0")))
                if parent.returncode or parent.stdout.strip() != manifest["branch_start"] or f"Review-Setup-Run: {manifest['run_id']}" not in message or not changed.issubset(set(manifest.get("checkpoint_paths", []))):
                    raise ReviewError("Unrecognized commit appeared during checkpoint; refusing recovery.")
                manifest["checkpoint_sha"] = tip
                manifest["status"] = status = "checkpointed"
                state.save(manifest)
        if status not in ("checkpointed", "renaming", "renamed", "restoring", "detached", "discarding"):
            files = state.validate_run_changes(manifest, archive=mode == "archive")
            if mode == "archive":
                _require_commit_identity(worktree)
            manifest["reset_mode"] = mode
            state.save(manifest)
            if mode == "archive":
                with _legacy_outer_mutation(state, phase="legacy-checkpoint"):
                    _checkpoint(state, manifest, files)
            else:
                manifest["discard_paths"] = files
                manifest["status"] = "discarding"
                state.save(manifest)
        if mode == "archive" and manifest["status"] in ("checkpointed", "renaming"):
            state.check_slot(manifest)
            if git_status(worktree):
                raise ReviewError("Slot changed after checkpoint; refusing archive transition.")
            archive = manifest.setdefault("archive_branch", f"archive/review/{manifest['key']}/{manifest['run_id']}")
            manifest["status"] = "renaming"
            state.save(manifest)
            active = current_ref(worktree)
            if active == manifest["branch"]:
                if run_git(worktree, ["show-ref", "--verify", "--quiet", f"refs/heads/{archive}"], check=False).returncode == 0:
                    raise ReviewError("Archive branch already exists; refusing overwrite.")
                with _legacy_outer_mutation(state, phase="legacy-archive-branch"):
                    run_git(worktree, ["branch", "-m", archive])
            elif active != archive:
                raise ReviewError("Branch changed while archiving.")
            manifest["status"] = "renamed"
            state.save(manifest)
        if mode == "discard" and manifest["status"] == "discarding":
            state.check_slot(manifest)
            for relative in manifest.get("discard_paths", []):
                path = safe_path(worktree, relative)
                if path.exists():
                    if not path.is_file() or file_hash(path) != manifest.get("artifacts", {}).get(relative):
                        raise ReviewError("Artifact changed during discard; stopping without removing it.")
                    path.unlink()
                    parent = path.parent
                    boundary = safe_path(worktree, manifest["pr_dir"])
                    while parent.is_relative_to(boundary):
                        try:
                            parent.rmdir()
                        except OSError:
                            break
                        parent = parent.parent
        if manifest["status"] != "detached":
            manifest["status"] = "restoring"
            state.save(manifest)
            with _legacy_outer_mutation(state, phase="legacy-restore"):
                _restore_source(state, manifest)
                if git_status(worktree):
                    # Source gitlink can differ transiently when restoring an initialized
                    # baseline; no other dirty path is permitted before checkout.
                    if any(path != manifest["submodule"]["path"] for _, path in git_status(worktree)):
                        raise ReviewError("Slot has new changes; restore stopped safely.")
                if manifest.get("baseline_ref"):
                    ref = manifest["baseline_ref"]
                    if run_git(worktree, ["rev-parse", ref]).stdout.strip() != manifest["baseline_sha"]:
                        raise ReviewError("Recorded baseline ref moved; refusing to switch it.")
                    run_git(worktree, ["checkout", ref])
                else:
                    run_git(worktree, ["checkout", "--detach", manifest["baseline_sha"]])
            manifest["status"] = "detached"
            state.save(manifest)
        if git_status(worktree):
            raise ReviewError("Restored slot is not clean; no branch was discarded.")
        if mode == "discard":
            expected = manifest["branch_start"]
            branch = manifest["branch"]
            exists = run_git(state.root, ["show-ref", "--verify", "--quiet", f"refs/heads/{branch}"], check=False).returncode == 0
            if exists:
                if run_git(state.root, ["rev-parse", f"refs/heads/{branch}"]).stdout.strip() != expected:
                    raise ReviewError("Review branch changed during discard; refusing deletion.")
                if branch in state.registered().values():
                    raise ReviewError("Review branch is checked out elsewhere; refusing deletion.")
                with _legacy_outer_mutation(state, phase="legacy-delete-ref"):
                    run_git(state.root, ["update-ref", "-d", f"refs/heads/{branch}", expected])
        manifest["status"] = "archived" if mode == "archive" else "discarded"
        state.save(manifest)
        history = state.directory / "history" / f"{manifest['run_id']}.json"
        if not history.exists():
            write_new_json(history, manifest)
        return manifest


def _pool_module(state: ReviewState, repo: str) -> tuple[dict, SourcePool]:
    matches = [
        module
        for module in read_submodules(state.root)
        if Path(module["path"]).name == repo
    ]
    if len(matches) != 1:
        raise ReviewError("Repository is not a unique canonical configured source name.")
    pool = SourcePool(state, matches[0])
    return matches[0], pool


def _pool_retained_commits(manifest: dict) -> dict[str, str]:
    if manifest.get("retained_commits"):
        return dict(manifest["retained_commits"])
    required = manifest.get("required_shas") or {}
    snapshot = manifest.get("snapshot") or {}
    return {
        "head": required.get("head") or snapshot.get("head_sha"),
        "base": required.get("base") or manifest.get("base_sha") or snapshot.get("base_sha"),
        "target": required.get("target") or snapshot.get("target_sha"),
        "baseline": manifest.get("baseline_sha"),
    }


def _pool_outer(state: ReviewState, pool: SourcePool, manifest: dict) -> Path:
    outer = safe_path(state.root, manifest["slot_path"])
    ensure_repository(outer)
    if outer not in state.registered():
        raise ReviewError("Pooled outer worktree is no longer registered.")
    common = run_git(
        outer, ["rev-parse", "--path-format=absolute", "--git-common-dir"]
    ).stdout.strip()
    if Path(common).resolve() != state.common:
        raise ReviewError("Pooled outer worktree belongs to a different repository.")
    assert_no_git_operation(outer)
    if outer.resolve() != safe_path(state.root, pool.slot_relative_path(manifest["slot_number"])):
        raise ReviewError("Pooled outer worktree path does not match its slot identity.")
    return outer


def _validate_pool_idle(outer: Path, manifest: dict) -> None:
    if current_ref(outer) is not None:
        raise ReviewError("Released pooled outer worktree must be detached.")
    if run_git(outer, ["rev-parse", "HEAD"]).stdout.strip() != manifest["root_head"]:
        raise ReviewError("Released pooled outer worktree is not at its recorded root baseline.")
    rows = git_status(outer)
    allowed = [(" M", manifest["submodule"]["path"])]
    if rows not in ([], allowed):
        raise ReviewError("Released pooled outer worktree has unexpected changes.")


def _recover_pool_checkpoint(state: ReviewState, manifest: dict, outer: Path) -> bool:
    return recover_pool_checkpoint(
        state,
        manifest,
        outer,
        trailer=f"Review-Setup-Run: {manifest['run_id']}",
    )


def reset_pool_run_locked(
    state: ReviewState,
    repo: str,
    slot: int,
    *,
    mode: str = "archive",
    confirm: str | None = None,
    dry_run: bool = False,
    run_maintenance: bool = True,
) -> dict:
    if mode not in ("archive", "discard"):
        raise ReviewError("Reset mode must be archive or discard.")
    if mode == "discard" and not _discard_confirmed(confirm):
        raise ReviewError("Discard requires --confirm YES (or yes/y).")
    _, pool = _pool_module(state, repo)
    manifest = state.load_pool_run(
        pool.repo_key,
        slot,
        requested_pr=_requested_pr_from_confirm(confirm),
        reap_stale=True,
        dry_run=dry_run,
    )
    if manifest is None:
        raise ReviewError("No managed pooled run exists for that repository slot.")
    if manifest.get("repo_name") != pool.repo_name or manifest.get("slot_id") != pool.slot_name(slot):
        raise ReviewError("Pooled run selector does not match its manifest identity.")
    bind_operation_context(
        run_id=manifest.get("run_id"),
        slot_id=manifest.get("slot_id"),
        repo_id=manifest.get("repo_key"),
        phase=manifest.get("reset_phase"),
    )
    log_event(
        "reset.phase",
        "Reset manifest loaded",
        phase=manifest.get("reset_phase") or "begin",
        mode=mode,
        outcome="validated",
    )
    if manifest["status"] in ("archived", "discarded", "merged"):
        return manifest
    if mode == "discard" and not _discard_confirmed(confirm):
        raise ReviewError("Discard requires --confirm YES (or yes/y).")
    if manifest.get("reset_mode") and manifest["reset_mode"] != mode:
        raise ReviewError("Reset already began in a different mode; retry that mode.")
    outer = _pool_outer(state, pool, manifest)
    phase = manifest.get("reset_phase")
    active_ref = current_ref(outer)
    detached_phases = (
        "outer-detach-intent",
        "outer-detached",
        "source-reset-intent",
        "source-reset",
        "source-verified",
        "release-intent",
        "released",
    )
    if phase in detached_phases:
        if active_ref is not None:
            expected_before_detach = (
                manifest.get("archive_branch")
                if mode == "archive"
                else manifest["branch"]
            )
            if phase != "outer-detach-intent" or active_ref != expected_before_detach:
                raise ReviewError("Pooled outer worktree reattached after reset began.")
    elif phase == "branch-archive-intent":
        if active_ref not in (manifest["branch"], manifest.get("archive_branch")):
            raise ReviewError("Pooled review branch changed outside this run.")
    else:
        expected = manifest.get("archive_branch") if phase == "branch-archived" else manifest["branch"]
        if active_ref != expected:
            raise ReviewError("Pooled review branch changed outside this run.")
    source = pool._source_for_slot(
        slot,
        expected_branch=None if current_ref(outer) is None else current_ref(outer),
    )
    if git_status(source):
        raise ReviewError("Source checkout changed during reset; leaving it untouched.")
    ignored = run_git(
        source, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]
    ).stdout
    if ignored:
        raise ReviewError("Source checkout has ignored local data; leaving it untouched.")
    retained = _pool_retained_commits(manifest)
    if any(not value for value in retained.values()):
        raise ReviewError("Pooled run lacks an exact commit required for retention.")
    if dry_run:
        if phase in detached_phases:
            _validate_pool_idle(outer, manifest)
            files = list(manifest.get("checkpoint_paths") or manifest.get("discard_paths") or [])
        else:
            files = state.validate_pool_run_changes(
                manifest, archive=mode == "archive"
            )
        return {
            "dry_run": True,
            "mode": mode,
            "slot": manifest["slot_id"],
            "branch": manifest["branch"],
            "paths": files + [manifest["submodule"]["path"]],
            "restore_commit": manifest["baseline_sha"],
            "retained_commits": retained,
            "reset_phase": phase,
            "next_action": "resume-reset" if phase else "begin-reset",
        }

    manifest["reset_mode"] = mode
    if phase is None:
        files = state.validate_pool_run_changes(manifest, archive=mode == "archive")
        manifest["reset_phase"] = "validated"
        state.save_pool_run(manifest)
        retained_values = pool.retain(manifest["run_id"], retained)
        manifest["retained_commits"] = retained_values
        manifest["retention_refs"] = {
            kind: pool.retention_ref(manifest["run_id"], kind)
            for kind in retained_values
        }
        manifest["reset_phase"] = "retained"
        state.save_pool_run(manifest)
        if mode == "archive":
            _require_commit_identity(outer)
            _checkpoint(state, manifest, files, slot=outer)
            manifest["reset_phase"] = "checkpointed"
        else:
            manifest["discard_paths"] = files
            for relative in files:
                path = safe_path(outer, relative)
                if path.exists():
                    if not path.is_file() or file_hash(path) != manifest["artifacts"].get(relative):
                        raise ReviewError("Artifact changed during discard; stopping without removing it.")
                    path.unlink()
            manifest["reset_phase"] = "artifacts-discarded"
        state.save_pool_run(manifest)
    elif manifest["reset_phase"] == "validated":
        retained_values = pool.retain(manifest["run_id"], retained)
        manifest["retained_commits"] = retained_values
        manifest["retention_refs"] = {
            kind: pool.retention_ref(manifest["run_id"], kind)
            for kind in retained_values
        }
        manifest["reset_phase"] = "retained"
        state.save_pool_run(manifest)
    elif manifest.get("status") == "checkpointing":
        recovered = _recover_pool_checkpoint(state, manifest, outer)
        if recovered:
            manifest["reset_phase"] = "checkpointed"
            state.save_pool_run(manifest)

    phase = manifest["reset_phase"]
    if mode == "archive" and phase == "retained":
        files = state.validate_pool_run_changes(manifest, archive=True)
        _require_commit_identity(outer)
        _checkpoint(state, manifest, files, slot=outer)
        manifest["reset_phase"] = "checkpointed"
        state.save_pool_run(manifest)
    if mode == "discard" and phase == "retained":
        files = state.validate_pool_run_changes(manifest, archive=False)
        manifest["discard_paths"] = files
        for relative in files:
            path = safe_path(outer, relative)
            if path.exists():
                if not path.is_file() or file_hash(path) != manifest["artifacts"].get(relative):
                    raise ReviewError("Artifact changed during discard; stopping without removing it.")
                path.unlink()
        manifest["reset_phase"] = "artifacts-discarded"
        state.save_pool_run(manifest)

    if mode == "archive" and manifest["reset_phase"] == "checkpointed":
        archive = manifest.setdefault(
            "archive_branch", f"archive/review/{manifest['key']}/{manifest['run_id']}"
        )
        manifest["reset_phase"] = "branch-archive-intent"
        state.save_pool_run(manifest)

    if mode == "archive" and manifest["reset_phase"] == "branch-archive-intent":
        archive = manifest["archive_branch"]
        _archive_pool_branch(state, outer, manifest, archive)
        manifest["reset_phase"] = "branch-archived"
        state.save_pool_run(manifest)
        bind_operation_context(phase=manifest["reset_phase"])
        log_event(
            "reset.phase",
            "Archive branch publication completed",
            phase=manifest["reset_phase"],
            mode=mode,
            outcome="success",
        )

    if manifest["reset_phase"] in ("branch-archived", "artifacts-discarded"):
        manifest["reset_phase"] = "outer-detach-intent"
        state.save_pool_run(manifest)

    if manifest["reset_phase"] == "outer-detach-intent":
        head = run_git(outer, ["rev-parse", "HEAD"]).stdout.strip()
        active = current_ref(outer)
        if active is not None:
            expected = manifest["archive_branch"] if mode == "archive" else manifest["branch"]
            if active != expected:
                raise ReviewError("Pooled branch changed before detaching it.")
            run_git(outer, ["checkout", "--detach", manifest["root_head"]])
        elif head != manifest["root_head"]:
            raise ReviewError("Pooled outer worktree detached at an unexpected commit.")
        manifest["reset_phase"] = "outer-detached"
        state.save_pool_run(manifest)

    if manifest["reset_phase"] == "outer-detached":
        manifest["reset_phase"] = "source-reset-intent"
        manifest["status"] = "resetting"
        state.save_pool_run(manifest)

    if manifest["reset_phase"] in ("source-reset-intent", "source-reset"):
        try:
            result = pool.reset_source(
                slot, manifest["run_id"], retained, refresh=True, expected_branch=None
            )
        except ReviewError as exc:
            manifest["status"] = "blocked"
            manifest["reset_phase"] = "source-reset"
            manifest["reset_error"] = str(exc)
            state.save_pool_run(manifest)
            raise
        manifest["baseline_sha"] = result["baseline_sha"]
        manifest["retained_commits"] = result["retained"]
        manifest["reset_warning"] = result["warning"]
        manifest["reset_phase"] = "source-verified"
        manifest["status"] = "resetting"
        manifest.pop("reset_error", None)
        state.save_pool_run(manifest)

    if manifest["reset_phase"] == "source-verified":
        rows = git_status(outer)
        allowed = [(
            " M",
            manifest["submodule"]["path"],
        )]
        unexpected = rows not in ([], allowed)
        if unexpected or manifest.get("reset_preservation_journal"):
            journal_path = Path(
                manifest.get("reset_preservation_journal")
                or safe_path(
                    state.directory,
                    f"cleanup/reset-{manifest['repo_key']}-{slot}-{manifest['run_id']}.json",
                )
            )
            journal = None
            if journal_path.exists():
                try:
                    import json

                    journal = json.loads(journal_path.read_text(encoding="utf-8"))
                except (OSError, ValueError):
                    raise ReviewError("Reset preservation journal is unreadable.") from None
            if journal is None:
                inspection = inspect_checkout_preservation(
                    outer,
                    protected=safe_path(outer, manifest["submodule"]["path"]),
                )
                binding = {
                    "schema_version": 1,
                    "workspace_identity": state.identity,
                    "outer": inspection,
                }
                journal = {
                    "schema_version": 1,
                    "identity": state.identity,
                    "repo_key": manifest["repo_key"],
                    "slot_number": slot,
                    "run_id": manifest["run_id"],
                    "reset_mode": mode,
                    "phase": "preserving",
                    "id": f"reset-{manifest['run_id']}-{slot}",
                    "generation": preservation_generation(binding),
                    "binding": binding,
                }
                atomic_json(journal_path, journal)
                manifest["reset_preservation_journal"] = str(journal_path)
                state.save_pool_run(manifest)
            required = {
                "identity": state.identity,
                "repo_key": manifest["repo_key"],
                "slot_number": slot,
                "run_id": manifest["run_id"],
                "reset_mode": mode,
            }
            if any(journal.get(key) != value for key, value in required.items()):
                raise ReviewError("Reset preservation journal belongs to another operation.")
            if journal.get("phase") == "preserving":
                fresh = inspect_checkout_preservation(
                    outer,
                    protected=safe_path(outer, manifest["submodule"]["path"]),
                )
                binding = {
                    "schema_version": 1,
                    "workspace_identity": state.identity,
                    "outer": fresh,
                }
                if (
                    preservation_generation(binding) != journal.get("generation")
                    or binding != journal.get("binding")
                ):
                    raise ReviewError(
                        "Reset preservation input changed before capture; slot remains untouched."
                    )
                preserved = preserve_checkout_generation(
                    state,
                    fresh,
                    operation_id=journal["id"],
                    repository=outer,
                )
                journal["preservation_path"] = preserved["path"]
                journal["manifest_sha256"] = preserved["manifest_sha256"]
                journal["phase"] = "preserved"
                atomic_json(journal_path, journal)
            if journal.get("phase") == "preserved":
                fresh = inspect_checkout_preservation(
                    outer,
                    protected=safe_path(outer, manifest["submodule"]["path"]),
                )
                binding = {
                    "schema_version": 1,
                    "workspace_identity": state.identity,
                    "outer": fresh,
                }
                if (
                    preservation_generation(binding) != journal.get("generation")
                    or binding != journal.get("binding")
                ):
                    raise ReviewError(
                        "Reset preservation input changed after capture; slot remains untouched."
                    )
                preservation_path = journal.get("preservation_path")
                expected_preservation_path = str(
                    state.directory / "preservations" / journal["id"]
                )
                if (
                    not isinstance(preservation_path, str)
                    or preservation_path != expected_preservation_path
                ):
                    raise ReviewError("Preservation archive journal binding is invalid.")
                verify_preservation_archive(
                    Path(preservation_path),
                    expected_manifest_sha256=journal.get("manifest_sha256"),
                    expected_generation=journal["generation"],
                    expected_binding=journal["binding"],
                )
                cleanup_worktree(
                    outer,
                    manifest["root_head"],
                    protected=manifest["submodule"]["path"],
                )
                journal["phase"] = "cleaned"
                atomic_json(journal_path, journal)
            if journal.get("phase") != "cleaned":
                raise ReviewError("Reset preservation journal has an unsupported phase.")
            manifest["reset_preservation_path"] = journal["preservation_path"]
            state.save_pool_run(manifest)
        _validate_pool_idle(outer, manifest)
        if mode == "discard":
            branch_ref = f"refs/heads/{manifest['branch']}"
            current = run_git(state.root, ["rev-parse", "--verify", branch_ref], check=False)
            if current.returncode == 0:
                expected = manifest["branch_start"]
                if current.stdout.strip() != expected:
                    raise ReviewError("Review branch changed during discard; refusing deletion.")
                run_git(state.root, ["update-ref", "-d", branch_ref, expected])
            pool.drop_retention(manifest["run_id"], expected=manifest["retained_commits"])
            manifest["retention_refs"] = {}
            manifest["status"] = "discarded"
            manifest["discarded_at"] = utc_now()
        else:
            manifest["status"] = "archived"
            manifest["archived_at"] = utc_now()
        manifest["reset_phase"] = "released"
        state.save_pool_run(manifest)
        bind_operation_context(phase="released")
        log_event(
            "reset.phase",
            "Reset reached durable released state",
            phase="released",
            mode=mode,
            outcome="success",
        )

    if run_maintenance and manifest["reset_phase"] == "released" and "maintenance" not in manifest:
        try:
            manifest["maintenance"] = pool.maintain()
        except ReviewError as exc:
            manifest["maintenance"] = {"status": "failed", "warning": str(exc)}
        state.save_pool_run(manifest)
    return manifest


def _discard_prepared_slot(
    state: ReviewState, pool: SourcePool, slot: int, *, confirm: str | None
) -> dict:
    """Real, non-dry counterpart to `reset_pool_run`'s `would-discard-prepared-slot`
    dry-run status.

    No legacy pool-run manifest exists for this pooled slot -- it was reaped
    as stale by `check_prepared_slot_conflict`'s `reap_stale=True` call, or
    never existed -- but Task 3's minimal prepared-slot manifest
    (`review_git_prepare.py`'s `prepare()`) still claims it. There is no
    pool-run lifecycle state for `reset_pool_run_locked` to resume, so this
    discards the prepared-slot manifest directly and resets the slot's
    outer+nested worktrees back to the exact idle baseline `prepare()`
    itself relies on (`_prepare_outer`/`_prepare_nested`), leaving the slot
    ready for a fresh `prepare()` call. No branch or checkpoint was ever
    created for a prepared-only slot, so only discard is meaningful here.

    Must run under the same locks `reset_pool_run` takes for any other
    pooled discard (`state.lock(shared=True)` + `state.slot_lock`). Re-derives
    `prepared` itself from disk under that lock rather than trusting the
    caller's pre-lock read -- exactly like `reset_pool_run_locked` re-loads
    the legacy manifest itself instead of trusting its own caller's copy --
    so a fresh, different-PR pool-run manifest that appeared between the
    pre-lock probe and this lock still blocks (falls through to a
    `ReviewError` from the conflict guard) rather than being discarded.
    """
    prepared = check_prepared_slot_conflict(
        state,
        pool,
        slot,
        requested_pr=_requested_pr_from_confirm(confirm),
        reap_stale=True,
        dry_run=False,
    )
    if prepared is None:
        raise ReviewError("No managed pooled run exists for that repository slot.")
    if not _discard_confirmed(confirm):
        raise ReviewError("Discard requires --confirm YES (or yes/y).")

    outer = safe_path(state.root, pool.slot_relative_path(slot))
    # Unlike `_pool_outer` (the legacy pool-run path, which has no repair
    # primitive downstream and so must refuse an invalid outer outright), this
    # discard path calls `_prepare_outer` immediately below -- the same
    # tolerant validate-or-repair primitive `prepare()` itself relies on.
    # `_prepare_outer` already re-derives validity itself (registered in
    # `worktree_rows` AND `_worktree_functional`'s own `--git-common-dir`
    # check) and, on anything less than that -- foreign, unregistered,
    # mismatched-repository, corrupt, or an empty/missing submodule
    # placeholder -- repairs it (`worktree remove --force` + prune + a
    # containment-checked `rmtree`, then a fresh `worktree add`) under
    # `state.outer_lock`, only ever touching this one slot's own path
    # (`_validate_contained`). A separate pre-check here (`ensure_repository`
    # plus its own registered/common-dir assertions, as `_pool_outer` does)
    # would just re-run a stricter, non-repairing version of the same test
    # and refuse exactly the foreign/corrupt slot this hard-reset path exists
    # to recover -- before ever reaching that repair. Nothing is lost for the
    # already-valid case: `_prepare_outer` still calls
    # `assert_no_git_operation(outer)` itself before resetting it.
    root_head = run_git(state.root, ["rev-parse", "HEAD"]).stdout.strip()
    pool._ensure_store()
    source_baseline = pool._verified_baseline()
    _prepare_outer(state, outer, root_head)
    nested = safe_path(outer, pool.module["path"])
    _prepare_nested(state, pool, nested, source_baseline)
    _verify_postconditions(state, pool, outer, nested, root_head, source_baseline)

    # Commit point: the slot is provably clean and idle before ownership is
    # released, so a failure above never leaves an unprepared slot silently
    # unowned.
    state.prepared_slot_path(pool.repo_key, slot).unlink(missing_ok=True)

    return {
        "dry_run": False,
        "mode": "discard",
        "status": "discarded-prepared-slot",
        "slot": pool.slot_name(slot),
        "slot_id": pool.slot_name(slot),
        "paths": [pool.slot_relative_path(slot)],
    }


def reset_pool_run(
    state: ReviewState,
    repo: str,
    slot: int,
    *,
    mode: str = "archive",
    confirm: str | None = None,
    dry_run: bool = False,
    run_maintenance: bool = True,
    lock_timeout: float = 1200,
) -> dict:
    if mode == "discard" and not _discard_confirmed(confirm):
        raise ReviewError("Discard requires --confirm YES (or yes/y).")
    # Fail-closed compatibility guard (rulings R2, R18): before this slot is
    # ever touched -- dry-run inspection included -- refuse if Task 3's
    # new-format prepared-slot manifest disagrees with the legacy manifest
    # about which PR currently occupies it. No other reset behavior changes.
    _, pool = _pool_module(state, repo)
    prepared = check_prepared_slot_conflict(
        state,
        pool,
        slot,
        requested_pr=_requested_pr_from_confirm(confirm),
        reap_stale=True,
        dry_run=dry_run,
    )
    if dry_run and prepared is not None:
        return {
            "dry_run": True,
            "mode": mode,
            "status": "would-discard-prepared-slot",
            "slot": slot,
            "slot_id": pool.slot_name(slot),
            "paths": [pool.slot_relative_path(slot)],
        }
    if dry_run:
        # Inspection must not create/open lifecycle lock state. The inner path
        # performs no writes and uses only an existing manifest/worktrees.
        return reset_pool_run_locked(
            state,
            repo,
            slot,
            mode=mode,
            confirm=confirm,
            dry_run=True,
            run_maintenance=False,
        )
    if prepared is not None:
        if mode != "discard":
            raise ReviewError(
                "A slot with only a prepared-slot manifest and no pool-run "
                "manifest has no review branch or checkpoint to archive; "
                "only --mode discard is supported."
            )
        with (
            lock_session(lock_timeout),
            state.lock(shared=True, phase="pooled-reset"),
            state.slot_lock(pool.repo_key, slot, phase="pooled-reset"),
            slot_writer_scope(
                state,
                pool.repo_key,
                slot,
                phase="pooled-reset",
                reap_stale=True,
                requested_pr=_requested_pr_from_confirm(confirm),
            ),
        ):
            return _discard_prepared_slot(state, pool, slot, confirm=confirm)
    with (
        lock_session(lock_timeout),
        state.lock(shared=True, phase="pooled-reset"),
        state.slot_lock(pool.repo_key, slot, phase="pooled-reset"),
        slot_writer_scope(
            state,
            pool.repo_key,
            slot,
            phase="pooled-reset",
            reap_stale=True,
            requested_pr=_requested_pr_from_confirm(confirm),
        ),
    ):
        return reset_pool_run_locked(
            state,
            repo,
            slot,
            mode=mode,
            confirm=confirm,
            dry_run=False,
            run_maintenance=run_maintenance,
        )


def _discard_confirmed(confirm: str | None) -> bool:
    """Return whether the operator explicitly confirmed destructive discard."""
    return isinstance(confirm, str) and confirm.casefold() in ("yes", "y")


def _requested_pr_from_confirm(confirm: str | None) -> int | None:
    """Best-effort PR number from an archive confirmation token.

    Discard uses a generic affirmative token; its requested PR comes from the
    slot's prepared manifest in `check_prepared_slot_conflict`. Archive keeps
    the existing `{repo-slug}-{pr_number}` token behavior.
    """
    if not confirm or _discard_confirmed(confirm):
        return None
    _, separator, raw_pr = confirm.rpartition("-")
    if not separator or not raw_pr.isascii() or not raw_pr.isdecimal():
        return None
    return int(raw_pr)


def _parse_pool_selector(value: str) -> tuple[str, int]:
    repo, separator, raw_slot = value.rpartition("-")
    if not separator or not repo or not raw_slot.isascii() or not raw_slot.isdecimal():
        raise ReviewError("Pooled slot must use the canonical repository-slot form, for example Nova-1.")
    slot = int(raw_slot)
    SourcePool._validate_slots([slot])
    return repo, slot


def _main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    selector = parser.add_mutually_exclusive_group(required=True)
    selector.add_argument(
        "--slot", help="Canonical pooled repository slot, for example Nova-1."
    )
    selector.add_argument(
        "--legacy-slot", type=int, help="Explicit schema-1 numeric slot."
    )
    parser.add_argument("--mode", choices=("archive", "discard"), default="archive")
    parser.add_argument("--confirm")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--lock-timeout", type=float, default=1200)
    parser.add_argument("--root", help="Canonical review store root (normally inferred from this script).")
    parser.add_argument("--verbose", action="store_true", help="Show safe lifecycle progress on stderr; stdout remains unchanged.")
    args = parser.parse_args(argv)
    try:
        preflight()
        import json
        state = ReviewState(discover_root(args.root))
        if args.legacy_slot is not None:
            if args.lock_timeout != 1200:
                raise ReviewError("--lock-timeout applies only to pooled reset.")
            result = reset_run(
                state, args.legacy_slot, mode=args.mode,
                confirm=args.confirm, dry_run=args.dry_run,
            )
        else:
            repo, slot = _parse_pool_selector(args.slot)
            result = reset_pool_run(
                state, repo, slot, mode=args.mode,
                confirm=args.confirm, dry_run=args.dry_run,
                lock_timeout=args.lock_timeout,
            )
        bind_operation_context(
            run_id=result.get("run_id"),
            slot_id=result.get("slot_id", result.get("slot")),
            repo_id=result.get("repo_key"),
            phase=result.get("reset_phase"),
        )
        print(json.dumps({key: result[key] for key in (
            "dry_run", "mode", "status", "slot", "slot_id", "branch",
            "archive_branch", "paths", "restore_commit", "reset_warning",
            "maintenance",
        ) if key in result}, indent=2))
        return 0
    except ReviewError as exc:
        print(f"Review reset stopped: {exc}", file=sys.stderr)
        return exc.code


def main(argv=None) -> int:
    raw = list(sys.argv[1:] if argv is None else argv)
    verbose = "--verbose" in raw
    root_value = None
    slot_id = None
    try:
        if "--root" in raw:
            root_value = Path(raw[raw.index("--root") + 1])
        else:
            root_value = discover_root(None)
    except (IndexError, ReviewError):
        root_value = None
    if "--slot" in raw:
        try:
            slot_id = raw[raw.index("--slot") + 1]
        except IndexError:
            pass
    with operation_logging(
        root_value,
        command="reset",
        verbose=verbose,
        slot_id=slot_id,
    ) as operation:
        code = _main(raw)
        operation.finish(code, None if code == 0 else "ReviewError")
        return code


if __name__ == "__main__":
    raise SystemExit(main())
