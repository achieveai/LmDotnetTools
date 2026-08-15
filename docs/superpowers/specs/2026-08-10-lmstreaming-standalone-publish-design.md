# LmStreaming.Sample Standalone Publish Launcher Design

## Goal

Modify the existing `samples/LmStreaming.Sample/publish-launch.ps1` so one script invocation creates and runs a standalone publish artifact. The running application must serve the prebuilt Vite JavaScript and CSS through ASP.NET Core. It must not run or depend on a Vite development server.

## Ownership

`publish-launch.ps1` owns the complete operation. A caller does not run a separate build first.

The script will:

1. Resolve repository and project paths.
2. Create a fresh temporary demonstration directory under `.claude/scratchpad`.
3. Run `npm ci` and `npm run build` directly from `ClientApp`.
4. Run `dotnet publish` into that temporary directory with `BuildClientApp=false`, avoiding a duplicate client build inside MSBuild.
5. Verify the publish output contains `wwwroot/dist/index.html` and the built JavaScript/CSS assets it references.
6. Set both `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production` for the launched process.
7. Start only the published `LmStreaming.Sample` executable while preserving the launcher's existing backend port, webhook public-base URL, workspace, and force/port-selection inputs where they remain applicable.
8. Report the publish directory and application URL.

## Existing Development Behavior

The launcher currently supports a development workflow involving Vite and ASP.NET Core. The standalone behavior will modify the existing script rather than create an enhanced or versioned replacement.

The standalone path must not:

- invoke `npm run dev`;
- call `UseViteDevelopmentServer` at runtime;
- set Vite runtime/proxy variables for the published process;
- rely on source-tree `wwwroot` after publication; or
- require a prior `dotnet build` or frontend build from the caller.

The default launcher becomes the standalone Production path. The existing Vite development-launch behavior is removed from this script rather than hidden behind an unspecified mode. Developers can still use the repository's ordinary frontend and backend development commands directly when they need hot reload.

## Artifact Layout

The demonstration artifact will be created beneath:

```text
.claude/scratchpad/lmstreaming-standalone-publish/run-<UTC-yyyyMMdd-HHmmss>-<process-id>/
```

Its publish subdirectory must include:

```text
LmStreaming.Sample.exe
wwwroot/dist/index.html
wwwroot/dist/assets/*.js
wwwroot/dist/assets/*.css
```

The directory is temporary demonstration output and remains untracked.

## Runtime Flow

1. The user invokes `publish-launch.ps1`.
2. The script builds the Vite production bundle.
3. The script publishes the server and copies all publish assets to the scratchpad run directory.
4. The script validates the artifact before launch and fails with a focused error if any required asset is absent.
5. The script starts the published executable with both `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production`.
6. The script waits up to a fixed timeout for `GET /api/providers` to return an HTTP success status, then checks `GET /dist/index.html` and a hashed JavaScript asset referenced by that HTML on the same ASP.NET Core port.
7. ASP.NET Core serves `/dist/index.html`, hashed assets, APIs, WebSockets, and the SPA fallback from the publish directory.

## Failure Handling

The script stops on:

- missing Node/npm or .NET SDK;
- failed dependency restore;
- failed Vite build;
- failed `dotnet publish`;
- absent published executable;
- absent `wwwroot/dist/index.html`;
- missing JavaScript or CSS referenced by the published HTML;
- launch failure; or
- the application failing its readiness check.

Errors identify the failed phase and preserve the demonstration directory for inspection. Successful run directories are also retained as the requested demonstration artifact; each invocation reports its exact path so the user can inspect or remove it later.

## Verification

The implementation will be demonstrated from a fresh scratchpad directory.

Verification must prove:

1. One launcher invocation performs every build and publish step.
2. The publish directory contains the executable and built frontend assets.
3. Within the configured startup timeout, `GET /api/providers` returns an HTTP success status from the ASP.NET Core port.
4. `GET /dist/index.html` returns the built page.
5. A hashed JavaScript asset referenced by that HTML returns an HTTP success status from the same ASP.NET Core port.
6. The launched process path points inside the scratchpad publish artifact rather than using `dotnet run` against the source project.
7. The script contains no `npm run dev` or Vite-process launch path, does not reserve or report a Vite port, and the demonstration succeeds without starting a Vite listener.

## Non-goals

- Changing frontend application behavior.
- Replacing Vite as the build tool.
- Making ordinary solution builds always run npm.
- Adding a second launcher script.
- Committing generated publish artifacts.

## Extension: `-DestinationDirectory` (Publish-Only, Atomic Sibling-Swap Deploy)

This section extends the design above with an optional parameter that redirects the script's
output from "launch a scratchpad demonstration" to "publish into a target deployment directory
without launching it" — whether that target is brand new, currently empty, or an already-running
standalone deployment. It does not change any behavior described above when the parameter is
absent, and it does not add a second script.

**Revision history:**

- Rev 1 required the destination to already exist as a prior deployment; a missing path was out
  of scope. Superseded — both new and existing deployments are supported.
- Rev 2 added dual-mode support but applied the *application* files directly in place for
  Recognized deployments (in-place overlay) and copied directly into Missing/Empty destinations.
  Both of those direct-write approaches are **superseded by this revision**. The required
  implementation is now an **atomic sibling-swap transaction** in every case that mutates the
  destination. Direct in-place overlay and direct full-copy-into-place are no longer part of this
  design; they are replaced below, not offered as an alternative.

### Parameter

```text
-DestinationDirectory <string>   # optional; no default
```

- **Omitted (default):** every behavior in this document above this extension is unchanged —
  fresh scratchpad staging, launch, readiness checks, retained run directory.
- **Provided:** the script becomes **publish-only** in every one of the four destination states
  below, and every state that results in a write to `-DestinationDirectory` does so through the
  atomic sibling-swap transaction defined in this section — never through a direct in-place copy
  or overlay. The script never starts the published executable when `-DestinationDirectory` is
  provided, regardless of state or outcome.

### Destination Classification

Before any build work starts, the script classifies the destination path into exactly one of
four states using cheap filesystem checks only:

| State | Definition | Outcome |
|---|---|---|
| **Missing** | The path does not exist. | Sibling-swap deploy (single rename). |
| **Empty** | The path exists, is a directory, and contains zero entries of any kind (including hidden/system entries such as `desktop.ini`). | Sibling-swap deploy (two-rename swap). |
| **Recognized deployment** | The path exists, is non-empty, and matches the deployment-recognition markers below. | Sibling-swap deploy with preserved-data carry-over (two-rename swap). |
| **Unrecognized non-empty** | The path exists, is non-empty, and does **not** match the recognition markers. | Fail before any build or candidate work. Nothing is touched, nothing is created. |

#### Deployment recognition markers

The specified baseline (stronger than the bare minimum of executable + any one other file) is:

```text
Recognized  <=>  LmStreaming.Sample.exe  AND  appsettings.json  AND  wwwroot/dist/index.html
                 all three present at the top level of the destination directory
```

A directory with only two of the three is treated as **Unrecognized non-empty** and rejected —
ambiguous or partial state is never a green light to build a candidate or touch anything.

### Atomic Sibling-Swap Transaction (required implementation)

This is the only way the script may write to a non-absent `-DestinationDirectory`. There is no
in-place overlay path and no direct full-copy-into-place path in this design; both were removed
because they could leave the live destination in a partially-written state if interrupted. Every
mutating branch below instead builds a complete, disposable **candidate** directory next to the
destination, validates it fully while it is still disposable, and only then makes it live with a
directory rename — an operation that is a single filesystem metadata update (not a data copy) on
the same volume, and is therefore effectively instantaneous and not subject to the same partial-
write failure modes as copying file contents.

**Hard requirement:** the candidate (and, where used, the backup) must be created as a **sibling
of the destination — same parent directory, same volume** as the destination. This is what makes
the rename atomic; it is enforced by construction (the script always creates the candidate at
`Join-Path (Split-Path -Parent $DestinationDirectory) "<name>.candidate-<UTC-timestamp>-<pid>"`),
not left to the operator to arrange. If the destination's parent directory does not yet exist
(only possible in the Missing state, for a multi-level new path), the script creates the parent
chain first, then creates the candidate inside it.

Naming convention used throughout:

```text
<DestinationName>.candidate-<UTC-timestamp>-<pid>    # disposable, being assembled/validated
<DestinationName>.backup-<UTC-timestamp>-<pid>       # the previous destination, moved aside
```

#### Missing destination

1. Create the destination's parent directory chain if absent.
2. Build the candidate: copy the entire validated staged publish output into it verbatim. There
   is no preserve-set and no existing data of any kind — nothing to carry over, and `.env` is
   never present (staging never contains one).
3. Validate the candidate (executable present, `wwwroot/dist/index.html` present, every asset it
   references resolves).
4. **Rename the candidate to the destination path.** Since nothing exists there, this is a single
   rename with nothing to back up. On success, the destination now exists and the transaction is
   complete. On failure (e.g., a race where something now occupies that path), the candidate is
   left in place for inspection and the destination is reported as still Missing.

#### Empty existing directory

1. Build the candidate exactly as in the Missing case (full copy of staged output; no
   preserve-set; no `.env`).
2. Validate the candidate.
3. **Rename the existing empty destination directory aside to the backup name.** This step is
   required even though the directory is empty, because the destination path is occupied — a
   directory cannot be renamed onto an already-existing path, so the incumbent (empty) directory
   must be moved out of the way first.
4. **Rename the candidate to the destination path.**
   - On success: remove the backup (an empty directory — trivial to remove) and the transaction
     is complete.
   - On failure: **restore the backup** by renaming it back to the destination path, so the
     destination ends exactly as it was before the run (an empty directory). Report the failure.
     The candidate is retained for inspection, not deleted, so the operator can see what
     prevented the swap.

#### Recognized existing deployment

1. Run the running-instance precondition check (Checkpoint A — see below). If the destination's
   executable is currently running, stop now, before any build step.
2. Stage exactly as today (scratchpad build/publish/validate). Nothing about staging touches the
   destination or the candidate.
3. Create the candidate sibling directory (empty, freshly created).
4. Copy the staged **replace-set** (everything except the preserve-list) into the candidate. This
   never includes `.env`, by the same rule as the base design — `.env` is never sourced from
   staging, even if a staged output somehow contained one.
5. Re-run the running-instance precondition check (**Checkpoint B**), immediately before reading
   any file from the live existing destination. This is the point that matters most for
   correctness: the preserve-list includes `notify-waits.db` and its `-wal`/`-shm`/`-journal`
   sidecars, and reading those while the owning process might still hold them open risks copying
   a torn, mid-write snapshot. Requiring the process to be stopped here — not merely "not running
   at classification time" — is what makes the copy in the next step coherent.
6. Copy the preserve-list **byte-for-byte** from the existing destination into the candidate:
   `conversations/`, `notify-waits.db` together with whichever of `-wal`/`-shm`/`-journal`
   currently exist next to it (copied as one pass — a main database file copied without its
   present sidecars is the classic source of SQLite-level incoherence, not a timing detail),
   `oauth-tokens/`, `workspaces/`, `chat-modes/`, `workflow-index/`, `logs/`, `recordings/`, and
   `.env` if present. This is the **only** place `.env` can come from; it is never taken from
   staging. If a preserve-list path does not exist at the destination (e.g., no `.env` was ever
   created), it is simply absent from the candidate too — nothing is fabricated.
   - **Note on size:** `logs/` and `recordings/` can be large in a long-running deployment. This
     copy has no arbitrary timeout; it is expected to be the longest part of a Recognized-
     deployment run when historical logs/recordings have accumulated. This is disclosed here as
     expected behavior, not treated as a bug or truncated for expedience.
7. Validate the fully-assembled candidate (same static checks as always) while it is still
   disposable and the live destination has not yet been touched.
8. Re-run the running-instance precondition check once more (**Checkpoint C**), immediately
   before the swap, since Steps 5–7 (in particular the large preserve-list copy in Step 6) can
   take a meaningful amount of time during which something could have started the app.
9. **Rename the existing destination aside to the backup name.**
10. **Rename the candidate to the destination path.**
    - On success: remove the backup. The transaction is complete; the destination is now the new
      deployment, and every preserved path in it is byte-identical to what was read from the old
      deployment in Step 6.
    - On failure: **restore the backup** by renaming it back to the destination path. The
      destination ends exactly as it was before the run. The candidate is retained (not deleted)
      for inspection. Report the failure clearly, distinguishing "swap failed and was rolled
      back — your deployment is unchanged" from any earlier failure mode.

#### Unrecognized non-empty directory

No candidate is built and nothing is touched. This is unchanged from the prior revision: fail
immediately, before any build step, naming which recognition markers were present or absent.

### Running-instance check — three checkpoints (Recognized deployment only)

Missing and Empty destinations never run this check — no executable can exist at a path with no
prior deployment. For a Recognized deployment, the check runs at three points, each guarding a
different risk:

- **Checkpoint A** (before staging begins): fail fast without wasting a build if the destination
  is obviously in use.
- **Checkpoint B** (immediately before copying preserve-list files out of the live destination):
  guards the coherence of the byte-for-byte copy itself — this is the checkpoint that actually
  matters for the SQLite-sidecar coherence guarantee.
- **Checkpoint C** (immediately before the final rename pair): guards against the process having
  been started during the (potentially long, per the logs/recordings note) candidate-assembly
  work between Checkpoint B and the swap.

Each checkpoint is a discrete check-then-act gate, not a continuously held lock. This is stated
plainly rather than assumed: between any checkpoint and the next action, a sufficiently
adversarial or unlucky concurrent start of the destination executable is not detected until the
following checkpoint. Three checkpoints narrow this window substantially compared to one, but do
not eliminate it. Building a true exclusive lock (e.g., an OS-level file lock held across the
whole transaction) is not undertaken here; it is named as a possible further hardening under
"Non-goals Additions" rather than assumed to already exist.

### Preserve / Replace Classification (Recognized-deployment candidate assembly only)

The preserve-list and replace-set definitions are unchanged from prior revisions:

```text
conversations/
notify-waits.db
notify-waits.db-wal
notify-waits.db-shm
notify-waits.db-journal      (defensive: present only if WAL mode ever falls back to rollback journal)
oauth-tokens/
workspaces/
chat-modes/
workflow-index/
logs/
recordings/
.env
```

Everything else in the staged output (executable, managed DLLs/PDBs/XML docs,
`.deps.json`/`.runtimeconfig.json`, `appsettings*.json`, `sandbox-skills/`, `wwwroot/` including
`wwwroot/dist/`) is the replace-set copied into the candidate. What changed in this revision is
*where* these copies land: both sets are assembled into the disposable candidate (Steps 4 and 6
above), never written directly into the live destination. The allow-list is still top-level and
deny-by-default: a future publish adding a new top-level folder is replaced by default, and only
the fixed list above is exempt.

### Stale Asset Cleanup — no longer a separate step

Prior revisions described a post-overlay diff-and-delete pass over the destination's
`wwwroot/dist` to remove old hashed Vite assets that a plain overlay copy would otherwise leave
behind forever. **This step no longer exists**, and its removal is a direct benefit of the
sibling-swap transaction: the candidate's `wwwroot/dist` is built solely from the freshly staged
output (Step 4), so it never contains a stale hashed asset in the first place. When the candidate
becomes the destination via rename, the old `wwwroot/dist` — stale assets and all — goes with the
old directory to the backup name and is removed entirely once the swap succeeds. There is nothing
to diff and nothing to selectively delete.

### Failure Handling Additions

- **Unrecognized non-empty destination** → stop before any build step; nothing created, nothing
  touched.
- **Destination executable currently running** (Recognized-deployment state; any of Checkpoints
  A/B/C) → stop at that checkpoint; no destination mutation has occurred yet in any case, because
  the only destination-mutating actions (the two renames) happen strictly after Checkpoint C.
- **Staging failure** (missing tooling, failed restore, failed Vite build, failed `dotnet
  publish`, failed staged-artifact validation) → no candidate has been created yet in the
  Recognized-deployment branch (staging happens before candidate creation); for Missing/Empty,
  the candidate may exist but the destination path itself is never touched by a staging failure,
  since the rename step is not reached. The destination is untouched in every classification.
- **Candidate assembly or candidate validation failure** (copy error, disk full, a preserve-list
  read failure, a failed static validation of the assembled candidate) → the destination has not
  been touched yet in any state; only the (invalid or partial) candidate exists, alongside the
  destination in whatever state it started in. The candidate is retained for inspection rather
  than silently deleted, so the operator can see what went wrong.
- **First rename failure** (existing → backup, or the single candidate → destination rename in
  the Missing case) → the destination remains exactly as it was (still the original directory,
  under its original name); the candidate is retained. Nothing was renamed, so there is nothing
  to roll back.
- **Second rename failure** (candidate → destination, after existing → backup already succeeded)
  → **rollback**: rename the backup back to the destination path. The destination ends exactly as
  it was before the run. The candidate is retained for inspection. This is the specific rollback
  path the design requires, and it is exercised directly by a dedicated test (see Verification).
- **Process crash between the two renames** (existing → backup succeeded, candidate → destination
  not yet attempted) — an inherent residual of a two-step swap; addressed explicitly in "Known
  Limitations" below rather than assumed away.

### Known Limitations

The two limitations disclosed in the prior revision are **resolved** by this design, and this
subsection explains why, rather than merely asserting it:

- **"Overlay is not transactional" — resolved.** There is no more in-place overlay. Every
  Recognized-deployment write happens inside a disposable candidate; the live destination is
  mutated only by the two renames in Steps 9–10, and a failure at either rename is handled by the
  rollback path above. The destination is never observed in a state that mixes old and new
  application files.
- **"A failed fresh publish can trap a retry behind the recognition check" — resolved.** A
  failed candidate build in the Missing/Empty branches never touches the destination path at all
  (the rename is the last step, and it either fully succeeds or the destination is untouched on
  failure per the rules above). A retry always finds the destination in the same classification
  it started in (still Missing, still Empty), so there is no degraded, unrecognized, partially-
  populated state for a retry to run into.

New, narrower residuals disclosed by this revision (not swept away by the word "atomic"):

- **The inter-rename crash window (Recognized and Empty branches).** Between "rename existing/old
  → backup" succeeding and "rename candidate → destination" being attempted, a hard process
  crash (not a caught exception — an actual kill, power loss, etc.) leaves the destination path
  literally absent, with the old contents sitting under the backup name and the new contents
  sitting under the candidate name, both as siblings. No data is lost (the backup is a complete,
  untouched copy of what was there) and no file is ever partially written at the destination path
  itself, but the destination is briefly (rename is near-instantaneous, not a copy, so this window
  is very small) not present at all if a crash lands exactly there. Recovery: for the Empty
  branch, the backup was empty anyway, so nothing of value needs restoring, and the next run
  simply sees Missing and proceeds normally, ignoring the orphaned backup/candidate siblings
  (which the operator can remove manually — they do not confuse destination classification, since
  classification only inspects the exact destination path). For the Recognized branch, the
  operator must notice the destination is gone and manually rename the backup back into place (or
  the candidate, if it looks complete) before running again; this manual-recovery step is a
  disclosed non-goal of automatic handling below, not a silent gap.
- **Same-volume/same-parent is a hard requirement, not merely a preference.** The candidate and
  backup are always created as literal siblings of the destination, so this requirement is met by
  construction on any local filesystem. It is not met, and atomicity is not guaranteed, if the
  destination's parent is a network share or another filesystem whose "rename" is not a single
  metadata operation (e.g., some remote/SMB configurations). This design assumes a local,
  NTFS-equivalent-semantics volume; that assumption is stated here rather than silently relied on.
- **Transient disk usage.** During a Recognized-deployment swap, the old destination (as backup),
  the new candidate, and (briefly, until backup removal) both coexist — roughly double the
  deployment's disk footprint, dominated by whatever `logs/`/`recordings/` history exists. This is
  a direct, expected consequence of atomicity, not an oversight.
- **Checkpoints are discrete, not a held lock.** As stated under "Running-instance check," the
  three checkpoints narrow but do not close every race with something starting the destination
  executable mid-transaction. This is named explicitly rather than claimed away by the word
  "atomic," which here describes the rename step, not the entire multi-second transaction.

### Verification Additions (Destination Mode)

1. **Fresh deploy into a missing directory.** Assert the destination is created via the
   candidate-then-rename path (observable indirectly: a candidate sibling briefly exists during
   the run, e.g. by racing a directory listing, or by instrumentation/log assertion), the final
   destination's contents are byte-for-byte the staged output, post-swap validation passes, and
   no launch occurs.
2. **Fresh deploy into an empty existing directory.** Same as (1), and additionally assert the
   two-rename sequence occurred (old empty dir briefly renamed to a backup name, then removed) —
   not a direct copy into the pre-existing empty directory.
3. **Recognized-deployment swap — byte-identical preserved stores.** SHA-256-hash every
   preserve-list path (including `.env` and the `notify-waits.db` + sidecar triad) at the source
   destination before the run. After a successful run, hash the same paths at the new destination.
   All hashes must match exactly. Additionally, open the post-swap `notify-waits.db` with its
   `-wal`/`-shm` companions through the real SQLite store class used in production and confirm all
   previously-committed rows are readable — a functional coherence proof, not only a byte-hash
   proof.
4. **`.env` sourced only from the existing destination, never from staging.** Seed the staged
   scratchpad output with a decoy `.env` (simulating a hypothetical future staging leak) and a
   different, real `.env` at the existing destination. After the run, assert the destination's
   `.env` is the pre-existing one, byte-for-byte, and the decoy never appears anywhere in the
   final destination.
5. **No stale `wwwroot/dist` assets — proven as a property of the candidate build, not a cleanup
   pass.** Run once, capture hashed asset names. Change the frontend source, run again. Assert
   the first run's hashed files are absent from the new destination and were never present in the
   candidate in the first place (i.e., prove there was nothing to clean, not merely that cleanup
   ran).
6. **No process launch, in every state.** For Missing, Empty, and Recognized-deployment, assert
   the script exits without starting `LmStreaming.Sample.exe`, verified by a process-tree scan of
   the script's own PID.
7. **Unrecognized non-empty directory rejected untouched.** Cover at least one fully-foreign
   directory and one partial-marker directory (e.g., executable + `appsettings.json` but no
   `wwwroot/dist/index.html`). Assert immediate failure before any build step, and the directory's
   contents byte-identical to their pre-attempt state. No candidate sibling is ever created.
8. **Recognition conjunction enforced.** A directory with exactly two of the three markers (any
   pairing) is rejected as Unrecognized non-empty.
9. **Rollback simulation for a second-rename failure.** Arrange for "rename existing → backup" to
   succeed and "rename candidate → destination" to fail deterministically (e.g., pre-create a
   blocking file/handle at the destination path immediately after the first rename, or deny
   permission on the parent directory transiently). Assert: the backup is renamed back to the
   destination path; the destination's contents (including preserve-list paths) are byte-identical
   to their pre-run state; the candidate is retained on disk (not deleted) and reported; the script
   exits with a clear "swap failed and was rolled back" error distinct from other failure messages.
10. **Retry trap is resolved (positive proof, replacing the old proof that it existed).** Force a
    candidate-assembly failure (not a rename failure) in the Missing and Empty branches. Assert the
    destination is untouched (still Missing, still Empty) afterward, and that a subsequent,
    unmodified retry succeeds normally — proving there is no degraded state a retry could get stuck
    behind.
11. **Running-instance checkpoints, individually observable.** For a Recognized deployment,
    instrument or log each of Checkpoints A/B/C and assert all three actually execute in a
    successful run, in order, before their respective guarded actions (staging, preserve-list copy,
    final rename). Separately, start a process holding the destination executable open at each of
    the three checkpoints' guarded moments in turn (three separate test runs) and assert the script
    fails at that checkpoint specifically, before the guarded action executes, with the destination
    untouched. For Missing/Empty, assert no running-instance check is attempted at all.
12. **Large preserved-data handling.** Seed a destination's `logs/`/`recordings/` with a
    non-trivial synthetic volume of data (many files/bytes, not just one placeholder file). Assert
    the preserve-list copy completes without truncation or premature timeout and every byte is
    present and correct in the new destination, and that the run's total duration reflects the
    actual copy cost rather than a hidden cap.
13. **Destination validation, all states.** After a successful run in each of Missing, Empty, and
    Recognized-deployment, independently re-verify the static checks (executable present,
    `wwwroot/dist/index.html` present, referenced assets resolve) against the destination from a
    separate process.

### Non-goals Additions

- Making the apply step a general-purpose deployment/rollback tool for arbitrary destinations.
- Guaranteeing swap atomicity on a non-local filesystem or any volume whose rename is not a single
  metadata operation (e.g., certain network shares) — the design assumes a local,
  NTFS-equivalent-semantics volume for the destination's parent directory.
- Closing the discrete-checkpoint race entirely (e.g., via an OS-level exclusive lock held across
  the whole transaction) — three checkpoints are specified; a continuously-held lock is a possible
  further hardening, not part of this design.
- Automatically recovering from the inter-rename crash window (i.e., automatically detecting and
  restoring an orphaned backup/candidate pair left by a hard crash mid-swap) — this is a manual
  recovery step for the operator, not an automatic behavior.
- Reconciling a destination whose non-empty contents were never produced by this script and do not
  match its recognition markers — left untouched and reported, not repaired.
- Pruning or rotating `logs/`/`recordings/` history to keep preserve-list copies fast — out of
  scope; the design accepts that this copy scales with however much history exists.

### Self-Review — Resolved Ambiguities and Open Questions

Resolved in this revision, superseding prior passes where noted:

- **Sibling-swap is now the required implementation, not an optional hardening.** Prior revisions
  offered it as a recommended-if-feasible alternative to direct in-place overlay / direct
  full-copy. This revision removes the direct-write paths entirely; every destination-mutating
  branch goes through candidate-build → validate → rename, per the coordinator's explicit
  decision.
- **"No partial retry trap or in-place overlay limitation remains"** — verified true for the two
  *specific* limitations named in that decision (see "Known Limitations," first two bullets, with
  the reasoning for why each is actually resolved rather than just asserted resolved). This
  revision does **not** claim zero limitations of any kind remain; it discloses four new, narrower
  residuals (inter-rename crash window, non-local-filesystem caveat, transient disk usage,
  discrete-vs-continuous checkpoint gating) rather than overclaiming atomicity covers everything.
  This distinction is treated as load-bearing: "atomic" describes the rename step and the
  candidate's disposability, not an unconditional guarantee over the whole multi-second
  transaction or over every possible filesystem.
- **Running-instance check widened from two checkpoints to three.** Rev 2 had "before staging" and
  "immediately before the overlay copy." With no more overlay copy, the equivalent risk moved to
  two places: reading preserve-list files out of the live destination (now Checkpoint B) and the
  final rename (now Checkpoint C). Both are named and tested individually rather than collapsed
  into one "before applying" checkpoint, because they guard different failure modes (read
  coherence vs. a late-started process).
- **`.env` provenance clarified and made testable.** "Preserved but never taken from staging" is
  now expressed as a structural fact (it is only ever copied in Step 6 of the Recognized-deployment
  candidate assembly, which reads from the existing destination, never from the staged replace-set
  copied in Step 4) and backed by a dedicated decoy test (Verification #4), rather than only a
  prose promise.
- **Stale-asset cleanup as a separate step is removed, not merely "scoped."** Prior revisions
  scoped a diff-and-delete pass to `wwwroot/dist`. This revision recognizes that sibling-swap makes
  that pass unnecessary — the candidate's `wwwroot/dist` is never stale to begin with, since it is
  built fresh from staging every time and the old one is discarded wholesale with the rest of the
  backup. This is a simplification, not merely a re-scoping, and is called out as such so a reader
  comparing revisions doesn't wonder where the cleanup step went.
- **Large preserve-list data is now explicit design guidance, not an unstated assumption.** The
  coordinator's note that logs/runtime data can be large is reflected as: no arbitrary timeout on
  the preserve-copy step, an explicit disk-usage limitation, and a dedicated large-data test
  (Verification #12), rather than left implicit.

Left genuinely open (not resolved here, flagged rather than assumed):

- Whether to build the continuously-held exclusive lock mentioned under "Non-goals Additions" as a
  follow-up hardening beyond the three checkpoints — deliberately deferred, since it changes the
  concurrency model (requiring the script to hold a lock the running application would also need
  to respect) rather than being a small addition.
- Whether an automatic "detect and offer to restore an orphaned backup/candidate pair" recovery
  tool is worth building for the inter-rename crash window, versus leaving it fully manual as
  specified. Not attempted here; flagged as a candidate follow-up, not assumed necessary.
- Whether the design should impose a soft warning (not a hard failure) when a Recognized
  deployment's `logs/`/`recordings/` exceeds some size threshold, to alert an operator before a
  long preserve-copy starts, rather than only after the fact via total run duration. Left as a
  possible UX improvement, not specified as required.
