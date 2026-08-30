# todo-eval scripted task

The text below the marker is the user message for every eval run, verbatim. Seeds vary ONLY
the `{TOPIC}` placeholder — substitute one topic word (e.g. `aurora`, `basalt`, `cascade`)
and change nothing else. The message defines the work; it deliberately says nothing about
how the board tools are called, because the tool descriptions and error texts are what the
eval measures (#618 design rule 1).

---

We are preparing the "{TOPIC}" release. Set up the release checklist on the todo board and run it with your team — one sub-agent per workstream.

Workstream 1 — Build & Test:
1. Verify a clean build of the {TOPIC} release branch.
2. Run the full test suite and record the outcome.
3. Triage any failures and record the verdict.
4. Compatibility sign-off. Break this item down into two separate checks beneath it — "API compatibility check" and "Configuration compatibility check" — and work the two checks individually.

Workstream 2 — Documentation:
1. Draft the {TOPIC} changelog.
2. Write the upgrade notes.
3. Publish the documentation. This item must not start until Workstream 1's "Compatibility sign-off" is complete — record that dependency on the board as a block, and lift it once the sign-off is done.

Workstream 3 — Packaging:
1. Bump the version for {TOPIC}.
2. Build the release package.
3. Run a publish dry-run and record the result.

Rules of engagement:
- Lay the whole checklist onto the board before dispatching anyone.
- One sub-agent per workstream; each owns its workstream end to end.
- Every item must end completed, and every item must carry at least one progress note about what was done.
- Nothing on the board may remain blocked when you finish.
- Finish by reviewing the full board and reporting its final state.

This is a simulation: no real repository, build system, or publishing target exists. For each item, do the work by writing a short, plausible record of the action and its outcome — the board itself is the deliverable.
