# ADR 0013: A background transcript flush has no caller, so it is resolved without a provenance comparison

* Status: Accepted
* Date: 2026-08-25
* Related issues, PRs, or commits: [#253](https://github.com/achieveai/LmDotnetTools/issues/253)
* Supersedes: gap 3 of [ADR 0011](0011-workspace-transcript-files.md) ("Service-to-service conversations
  get no transcript at all in v1")

## Context

ADR 0011 shipped the workspace transcript mirror and recorded five accepted gaps. The third was that
service-to-service conversations get no transcript, justified like this:

> A background flush has no inbound HTTP request and can only present a null credential, which
> conflicts permanently with an S2S-owned session binding.

That sentence is accurate about the mechanism and wrong about what follows from it. It reads as though
the conflict is a property of the situation — a background flush simply *has* no credential, therefore
it *must* lose the comparison. In fact the conflict came from one line in
`ConversationTranscriptWriter.ResolveSessionAsync`, which passed `requestCredential: null` into
`SandboxSessionRegistry.ResolveThreadWorkspaceSessionAsync`.

The registry compares **provenance**, raw and nullable, where `null` means *the interactive UI*:

```csharp
var ownerAppId = binding.CallerCredential?.AppId;
var callerAppId = requestCredential?.AppId;
if (!string.Equals(ownerAppId, callerAppId, StringComparison.Ordinal)) { /* CredentialConflict */ }
```

`null` is therefore **a provenance, not an absence** — which is deliberate and load-bearing elsewhere:
it is exactly why an interactive caller is not conflated with an explicit S2S caller that happens to
reuse the default app id. So passing `null` from the mirror did not say *"there is no caller"*. It said
*"the caller is the interactive UI"*, which is a false claim, and against an S2S-owned binding it is a
claim by the wrong actor. `CredentialConflict`, on every flush, forever, with no input that could ever
change and therefore no retry that could ever win.

Two further findings shaped the fix, both verified against the code rather than assumed:

- **The attach path was never the problem.** Issue #253 and ADR 0011 both point at the sample's
  per-thread agent factory, on the theory that the UI path attaches and the S2S path does not.
  `Program.cs` wraps the *entire* pooled agent factory in `AttachingToMirror`, so an S2S conversation
  has always been subscribed and has always scheduled flushes. Every one of them died at step 1.
- **The suite could not have caught this.** `FakeFileBrowser` echoed a settable `Resolution` regardless
  of the credential it was handed, so an S2S-owned thread and a UI-owned thread were literally the same
  fixture. A double that answers identically for both cannot fail when production confuses them.

## Decision

**Resolution for in-process background work is a separate seam that performs no provenance comparison,
because there is no actor to compare.**

`IWorkspaceFileBrowser` gains `ResolveThreadWorkspaceSessionForBackgroundAsync(threadId,
persistedWorkspaceId, ct)`. It shares one implementation body with the caller-bearing method and differs
in exactly one respect: the cross-actor check is **skipped**, not satisfied.

Three properties are decided deliberately.

**1. Skipped, not passed.** No credential would satisfy the check honestly. A background drain has no
inbound request to borrow one from, and the binding's owning app id is precisely the thing this path
must not require the caller to already know. Synthesising the owner's credential and handing it back to
the comparison would be the same code with a lie in the middle, and it would read to the next person as
though a real identity had been checked. The honest statement is that provenance is *not applicable*
here, so the code says that.

**2. A separate method, not a flag on the existing one.** A `bool compareProvenance` parameter on the
public method would sit on the request-handling path, one bad default or one careless call site away
from disabling the cross-actor guard for real callers. A separate method with one consumer cannot be
reached by accident, and its name states the precondition that makes it safe.

**3. The guard is unchanged for everything that has a caller.** The condition for using this seam is not
"trusted code" but "no caller exists". Every request-handling path has one — including the interactive
UI, whose `null` is a provenance. `FileBrowserController` continues to use the caller-bearing method and
continues to answer `409` on conflict.

This grants no reach the owner did not already have. The gateway call underneath uses
`binding.Credential` — the session's own stored credential — in both methods, so no new privilege is
minted; the workspace-id match against the persisted conversation still applies, so a stale binding
after a workspace switch is still refused; and a thread with no binding still resolves to `NoSession`.
The single behavioural difference is that an absent caller is no longer mistaken for a mismatched one.

**The untitled filename question #253 raises is answered by changing nothing.** An S2S conversation is
normally never titled by a human, and `WorkspaceTranscriptLine.MainFileLeaf` already falls back to the
bare `shortThreadId` when the slug is empty — no leading separator, no invented `untitled-` literal.
Minting one would collide with a real conversation someone titles "Untitled", and
`IsThisConversation` already accepts the bare stem as this conversation's own, so retitle adoption works
with no extra case. The fallback is now pinned by a test so it reads as a decision.

## Consequences

Service-to-service conversations get transcripts, including the sub-agent fan-out —
`ConversationDescendantScanner` reads only persisted thread metadata and never knew about credentials,
so it was never itself broken; it simply never ran, because the root's step-1 failure returned before
the fan-out was reached.

This is the case the mirror was most wanted for. ADR 0011 recorded that the owner *"was told plainly
that S2S — a daemon run nobody watches live — is where transcripts would be most useful, and chose
UI-only for v1"*. A remote caller that hands work to this host now has a record it can read with
ordinary file tools, which was the whole premise of putting the transcript in the workspace.

Every other property of ADR 0011 is untouched and still holds: the transcript is retained on
conversation delete, readable across conversations bound to the same workspace, normalized with
reasoning included, and contained by the `.gitignore` written into `.conversations/`. Gaps 1, 2, 4 and 5
of that record stand.

The cost is a second resolution method on a security-relevant seam, which is a permanent invitation to
call the wrong one. Three things hold it down: the name says who may call it, its XML doc says why that
is the condition rather than asserting it is safe, and the test double now models the real provenance
rule under an opt-in `OwnerAppId` so a future regression on this path fails a test instead of going
quiet. That last one is the substantive change — before it, the defect was invisible to a suite that
otherwise covers the mirror thoroughly.
