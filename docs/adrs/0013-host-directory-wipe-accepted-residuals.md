# ADR 0013: Accepted residuals and deferred work from PR #274's host workspace wipe

* Status: Accepted
* Date: 2026-08-25
* Related issues, PRs, or commits: `#283`, PR #274 (`517eb6d3`)

## Context

PR #274 (`fix(daemon): three ways a review run gets permanently stuck`) added
`HostDirectoryWipe.Delete` — the routine that recursively removes a review
daemon's host-backed workspace slot, guarding every path it touches against
escaping the pool root via `HostPathGuard`. Review of that PR raised four
concerns beyond what the PR itself fixed. Each was considered and
deliberately left alone rather than chased into the PR, for reasons specific
to that concern. This ADR is that reasoning, written down once so a later
reader does not have to re-derive it — or "fix" something that was left
alone on purpose.

Nothing recorded here is waiting on anyone. Each item below names the one
condition that would reopen it. Other findings from the same review that
**are** actionable were filed as separate, ordinary issues (#276, #279,
#280, #281, #282) and are out of scope for this record.

## Decision

Accept all four residuals as-is. No code change accompanies this ADR.

### 1. Containment is checked by path; the check-to-use gap is accepted

`HostDirectoryWipe.Delete` guards its root (`HostDirectoryWipe.cs:82`), then
walks the tree checking each entry with `HostPathGuard.Check`
(`HostDirectoryWipe.cs:99`) before acting on it. Every action after that
check takes a **path**, not a handle: `Unlink` at `:106`, the read-only
attribute clear at `:119`–`:122`, and the closing
`Directory.Delete(root, recursive: true)` at `:127`, which re-resolves every
path again from the root string. Each path is therefore resolved at least
twice, with no guarantee both resolutions reach the same filesystem object —
an unprivileged local race against the daemon's own workspace root, already
recorded in the source at `HostDirectoryWipe.cs:75`–`:77`.

A handle-based fix was designed and rejected. The shape considered: open
each entry with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`,
test the reparse attribute on the opened handle, delete through it. The
interop cost is not the blocker — .NET exposes no no-follow directory open
(`FileOptions` has no such flag; `File.OpenHandle`/`FileStream` cannot open
directories), so it would need `CreateFileW` plus
`GetFileInformationByHandleEx(FileAttributeTagInfo)` plus
`SetFileInformationByHandle(FileDispositionInfoEx)`. The real blocker: a
per-entry handle closes the leaf, not the path. Windows exposes no public
`openat`. Every child is still reached by a string re-resolved from the
root, so redirecting an *intermediate* directory redirects everything
beneath it regardless of whether the leaf was opened by handle. Closing that
would require relative opens via `NtCreateFile` with a `RootDirectory` in
`OBJECT_ATTRIBUTES`, and — because `Directory.Delete(root, recursive: true)`
also re-resolves — a hand-written handle-relative recursive delete in place
of it. That is a private reimplementation of recursive delete on
undocumented NT calls, for a race whose blast radius is bounded by
`HostPathGuard.Check` on every entry it does reach.

A partial fix was also rejected as worse than the documented residual: it
would read as fixed. Re-testing `FileAttributes.ReparsePoint` on the
`File.GetAttributes` call the wipe already makes at `:119` would narrow the
window nearly for free, but narrowing is not closing, and it leaves a second
containment test standing next to the real guard for a later reader to
mistake for the fix.

**Reopens if** the host-backed pool is retired in favor of a
container-backed store. There, the delete happens inside the sandbox and
this class does not exist at this layer.

### 2. Ancestors above the wipe root are not checked

`Delete` guards the root it is handed and everything below it, and nothing
above it. That is deliberate: the ancestors are the operator's own
configured workspace path, and refusing there would refuse every deployment
that intentionally places the pool behind a junction. Documented at
`HostDirectoryWipe.cs:73`–`:75`.

**Reopens if** the workspace root ever stops being operator-configured —
for example, if it becomes derived from repository or PR content.

### 3. The wipe's two "wasteful" costs are load-bearing

Flagged in review as a redundant traversal. Both costs are intentional and
documented at `HostDirectoryWipe.cs:62`–`:71`:

- `ChildrenOf` materializes each directory's entries instead of streaming
  them. This is correctness, not caching: the walk mutates the directory it
  is enumerating (`Unlink` removes an entry as the walk meets it), and a
  lazy enumeration is unspecified once the directory changes underneath it.
- The closing `Directory.Delete(root, recursive: true)` re-walks a tree
  already walked. Replacing it with a hand-rolled post-order delete would
  save the pass, but it would also buy back the very decision the guarded
  walk exists to remove — what is safe to recurse into, taken per entry, on
  the untrusted side of the store.

One redundant traversal of a tree being deleted anyway is the cheaper half
of that trade.

**Reopens if** the wipe shows up as a measured bottleneck. It has not.

### 4. S2S conversation lifetime vs. the worktree is deferred by owner decision

Raised in review: an S2S conversation outlives the worktree slot that
produced it, so a deep link into that conversation can outlive its backing
checkout. The owner decided to defer this, verbatim from the PR #274 thread:

> We do not care about S2S conversation lifetime wrt. the worktree. That we
> will deal with later. For now we're just building it. Also when we deal
> with that. We will just archive S2S conversation but not delete it.

Two constraints follow for whoever picks this up later:

- Archive, never delete. A conversation a deep link points at must stay
  resolvable. S2S has no archive capability today, so this is blocked on
  that feature existing, not on the daemon.
- Volume is the real design input. Scoped against a busy repository — on
  the order of 100 GB, roughly 150 PRs/day — retention has to be sized
  before a policy is chosen, not after.

**Reopens when** S2S gains an archive path. Not before.

## Consequences

- The four items above are closed as "considered and accepted," not as
  "fixed" or "ignored." A future reviewer who rediscovers any of them can
  find the reasoning here instead of re-deriving it, and can check the
  stated reopen condition before re-raising it.
- `HostDirectoryWipe` keeps its two-resolution path-based containment check
  and its ancestors-not-checked scope; no interop or NT-API surface is
  added to close item 1, and no config-driven ancestor guard is added for
  item 2.
- The wipe's traversal shape (materialized children, re-walking delete)
  stays as-is; a future performance investigation should re-read item 3
  before proposing a streaming or single-pass rewrite.
- S2S deep links into a conversation whose worktree has been reclaimed
  remain a known, owner-accepted gap until S2S ships an archive capability;
  no daemon-side mitigation (e.g., blocking the wipe on link references) is
  planned in the interim.
