# ADR 0011: Mirror each conversation's transcript into its own workspace, readable by anyone who can reach the workspace

* Status: Accepted
* Date: 2026-08-03
* Related issues, PRs, or commits: [#251](https://github.com/achieveai/LmDotnetTools/issues/251)

## Context

A conversation in `LmStreaming.Sample` already persists every message through `IConversationStore`, and
already exposes them over HTTP — the conversation's own `/messages` route, the cross-agent transcript
endpoint, and the `GetAgentTranscript` tool. What it does not do is put them anywhere an *agent* can read
with ordinary file tools. An agent inside the sandbox has a workspace, a shell, `Read`, `Grep` and `Glob`;
it has no way to consult the record of what it or a sibling actually did except by asking the host over
HTTP, which is a different trust path with a different answer.

#251 asks for the file. Each conversation's messages, mirrored as JSONL into `.conversations/` inside that
conversation's sandbox workspace, appended at each turn boundary, at full fidelity — reasoning included —
so a person or an agent can `cat`, `grep`, or point DuckDB at it.

Three forces shape the decision, and each has an obvious wrong answer.

**The obvious wrong answer for placement** is somewhere private to the conversation. But conversations
share workspaces: the workspace is bound to the work, not to the chat about it. A transcript inside a
shared workspace is therefore reachable by every other conversation bound to that same workspace. That is
not an accident to be engineered away — acceptance criterion 26 is literally *"conversation B reads A's
transcript"*. Removing the cross-conversation read removes the feature.

**The obvious wrong answer for exposure** is a feature flag. A flag turns a durable artifact into a
sometimes-artifact: a reader cannot tell "no transcript" from "transcript disabled", and every consumer
grows a branch. The owner overruled a flag. What the exposure does need is a guard against the transcript
leaving the machine, because a workspace is frequently a git checkout.

**The obvious wrong answer for containment** is to do nothing, on the grounds that a transcript in a
workspace is just a file. It is not just a file for the agent that wrote it. An agent asked to summarise or
explore its own workspace would walk `.conversations/`, ingest its own output, and describe itself — every
turn, growing every turn. Containment is a precondition of the feature, not a polish item.

There is also a documentation force. ADR 0009 records that in a collaboration *"contact and read permission
are separate axes, and that separation is load-bearing … only the transcript policy decides who may read
one"*, and that a caller *"must not be able to read past the policy with a direct GET"*. A transcript on a
shared filesystem does exactly what that sentence forbids, by a route that sentence does not mention. The
two records must not quietly disagree.

## Decision

**The transcript is a workspace artifact, not an API projection. Its readership is whoever can reach the
workspace, and that is stated rather than defended.**

An always-on, sample-local mirror appends each conversation's persisted messages to
`.conversations/{slug(title)}-{shortThreadId}.jsonl` inside that conversation's sandbox workspace, one file
per sub-agent under a sibling `..._agents/` directory. Ordering is carried by a derived `uid` /
`parent_uid` chain. Nothing under `src/` changes.

Three properties are decided deliberately, and are the substance of this record:

**1. `.conversations/` is readable across conversations, by design.** Two conversations bound to one
workspace can each read the other's transcript with `cat`. This is a **deliberate filesystem bypass of the
visibility policy** that ADR 0009 draws: `cat` reads past what a GET cannot. It is not a leak to be fixed
later, because it is the requested behaviour (AC 26). **This annotates ADR 0009 rather than editing it** —
ADRs here are append-only. ADR 0009's claim remains exactly true of the surface it describes, the
collaboration's addressing and read APIs. It is not true of the filesystem, and after this record it does
not claim to be: the transcript policy governs who may read a transcript *through the collaboration*, and
the workspace governs who may read one *through the filesystem*. A deployment that needs the stricter
property must not co-bind conversations to a shared workspace.

**2. The transcript survives conversation delete.** Deleting a conversation removes the agent and the
store's copy; it never `rm`s the transcript. Eviction drops the in-memory writer entry only. This makes an
otherwise unpleasant ordering hazard benign — deletion removes the agent before the store, so a final flush
can fire against a conversation being deleted — because the artifact is *meant* to outlive the
conversation. A person investigating what happened is precisely the person whose conversation is gone.

**3. The transcript is normalized and includes reasoning.** The mirror calls
`TranscriptProjection.Normalize(messages, excludeReasoning: false)`. It is a fourth caller of that method
and is **not exempted from normalization** — skipping it would put known-broken legacy rows
(`server_tool_use` discriminators, doubled tool-call args) into every transcript and force each future
reader to re-implement the fix. It is exempted from the *reasoning exclusion*, deliberately: the exclusion
exists for cross-agent reads, and this is the conversation's own full-fidelity record.

Containment is enforced in three places, none of which is a security boundary: the `workspace-summary` and
`repo-explorer` skills list `.conversations` among the directories they ignore; `FilePreviewPolicy` refuses
inline preview of any file under a dot-directory (`.jsonl` is allowlisted, so the extension check alone
would not have stopped it); and a `.gitignore` containing `*` is written into `.conversations/` on first
flush, so an agent running `git add -A && push` does not publish its own and its siblings' unredacted
reasoning off-machine, irrecoverably. Recursive `Read`/`Grep` exclusion is delegated to the gateway; if the
gateway does not already behave that way, the deliverable is a gateway-repo issue, not code here.

## Consequences

The transcript becomes a first-class, greppable artifact with no new API, no new store, and no new plumbing
in `src/`. A DuckDB read over `read_ndjson_objects` with `ignore_errors=true` answers questions that
previously needed a running host.

The cost is a set of limits that are real and are named here rather than discovered later.

**`parent_uid` recovers sequence, not turn structure.** The parent pointer is store adjacency — the `uid`
of the previous row in `LoadMessagesAsync` order. It tells a reader what came before what. It does not tell
a reader where one turn ended and the next began, and it cannot be made to: closing that gap needs a
persisted turn identifier, which the locked decisions exclude. AC 17 stays partial on purpose, and a reader
who assumes the chain encodes turn boundaries will be wrong.

Five gaps are accepted, not absorbed:

1. **A deferred tool result can freeze as a placeholder.** A late client-tool or `AskUserQuestion` answer
   arrives through `ReplaceMessageAsync`, which mutates the row in place, keeping its `Id` and `Timestamp`
   — so its `uid` is unchanged, the watermark has already passed it, and the transcript keeps the question
   but never receives the answer. This is not exotic; both are first-class features of this sample.
   Re-emitting with the same `uid` was rejected because it contradicts the uniqueness criterion.
2. **A run cancelled mid-flight is not flushed at that moment.** Cancellation never reaches run completion,
   so no completion message is published. The next completed run picks it up, because the watermark is
   cumulative. There is deliberately no disposal-time flush: an awaited flush at process shutdown loses a
   race with the session registry's disposal guard and is swallowed anyway, so the "fixed" version does not
   reliably work either.
3. **Service-to-service conversations get no transcript at all in v1.** A background flush has no inbound
   HTTP request and can only present a null credential, which conflicts permanently with an S2S-owned
   session binding. The result is a deliberate no-op: no file, no directory, no error, a debug log only.
   Interactive-UI conversations are unaffected. The owner was told plainly that S2S — a daemon run nobody
   watches live — is where transcripts would be most useful, and chose UI-only for v1; a follow-up issue
   tracks it.
4. **A dropped subscriber cannot be told apart from a disposed one.** Both end the message enumerator
   normally, with no exception and no log. An identity check against the agent pool mitigates it — if the
   pool still holds the same instance and our own cancellation was not requested, we were dropped and
   re-subscribe — but the ambiguity itself remains, and is stated here rather than left to be rediscovered
   as a silent-stop bug.
5. **`.conversations/` is readable across conversations and survives conversation delete.** Both are owner
   decisions, restated here as consequences because they are the two properties most likely to surprise
   someone who did not read the decision.

Finally, this feature adds a seventh copy of a title-slug function, on purpose: the six existing copies are
Unicode-aware `char.IsLetterOrDigit` implementations that pass CJK and accents through, none of which is
safe for a filename here and none of which is reachable from this assembly. Consolidation is a deferral,
recorded so its absence reads as a decision.
