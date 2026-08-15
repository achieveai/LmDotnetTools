# LmStreaming.Sample Destination-Directory Publish Plan

## Global Constraints

- No npm/dotnet in tests: C# invokes PS helpers with mocked staging
- Dot-sourceable script: main() function + guard, no auto-exec on dot-source
- Deterministic failure injection: -MoveDelegate scriptblock param for rollback tests
- Correct move semantics: Move-Item -Path -Destination, never Rename-Item -NewName with full path
- Process-check robustness: Returns PID/path, tolerates access-denied, -ProcessEnumerationDelegate seam
- Candidate assembly order: staged -> exclude preserve -> copy existing preserve -> validate
- SQLite coherence: sidecars as "coherent best-effort snapshot" (stopped-app precondition)
- Backup removal non-fatal: warning after success, not rollback trigger
- Non-vacuous tests: all 9 verification scenarios with real filesystem behavior
- No commits: plan structure only, exact commands included

## Overview

Implements -DestinationDirectory extension to publish-launch.ps1.

Transforms script:
- Default (no param): unchanged launch pipeline
- With param: publish-only via atomic sibling-swap

Uses atomic directory moves (not renames) on same volume. Three running-instance checkpoints (Recognized only). Rollback automatic on second-move failure.

## Files to Modify

1. samples/LmStreaming.Sample/publish-launch.ps1
   - Add -DestinationDirectory parameter
   - Wrap main logic in function main { }
   - Add dot-source guard
   - Add 6 helper functions

2. tests/LmStreaming.Sample.Tests/PublishLaunchScriptTests.cs
   - Add 8 structure tests

3. tests/LmStreaming.Sample.Tests/PublishLaunchDestinationTests.cs (new)
   - Real filesystem tests, 9 scenarios

## Core Functions

1. Test-DestinationState: Classify path (Missing/Empty/Recognized/Unrecognized)
2. Test-ProcessHoldsPath: Check running process, returns {Holding, PID, Path}
3. New-CandidateDirectory: Create sibling candidate, returns path
4. Copy-ReplaceSet: Copy staged excluding preserve-list
5. Copy-PreserveSet: Copy preserve items from existing
6. Invoke-AtomicMove: Single/double move with rollback, accepts -MoveDelegate

## Preserve-List

conversations, notify-waits.db, notify-waits.db-wal, notify-waits.db-shm,
notify-waits.db-journal, oauth-tokens, workspaces, chat-modes,
workflow-index, logs, recordings, .env

## Test Phases

Phase 1: Script structure (parameter, dot-sourceable, no launch)
Phase 2: Destination classification (Missing/Empty/Recognized/Unrecognized)
Phase 3: Process check (nonexistent, access-denied, exact match)
Phase 4: Atomic move (single, double, rollback, backup removal)
Phase 5: Candidate assembly (exclude preserve, .env from existing, SQLite trio)

## Commands

Run tests:
  cd B:\sources\LmDotnetTools
  dotnet test tests/LmStreaming.Sample.Tests -p:Configuration=Debug --filter PublishLaunchDestinationTests

Dot-source:
  cd samples/LmStreaming.Sample
  . .\publish-launch.ps1
  Test-DestinationState -DestinationPath "C:\path"

## Self-Review

[x] Four states mutually exclusive
[x] Three checkpoints (Recognized only: A,B,C)
[x] Rollback on second-move failure
[x] .env from existing only
[x] SQLite sidecars as snapshot
[x] Backup removal non-fatal
[x] All 9 verification scenarios
[x] Dot-sourceable with guard
[x] Injected delegates for testing
[x] No npm/dotnet per test
[x] Default mode unchanged
[x] No launch in destination
[x] Candidates as siblings
[x] Unrecognized pre-build rejection

Status: Plan complete, ready for implementation
Generated: 2026-08-11
