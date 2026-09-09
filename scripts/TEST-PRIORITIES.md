# Test priorities

Every test remains in its existing suite. `test-priorities.ndjson` records priority, reasoning and
separate prerequisites for two kinds of row: a **container** (`dotnet-project`, `vitest-file`,
`powershell-test`, `python-test`, `manual-browser-script`) and a **declaration**
(`kind: test-declaration`, one per test method family). Where declaration rows exist they decide the
selection and the runner emits an exact `FullyQualifiedName=` filter for it; a container's priority
applies only to declarations that have no row of their own. Every parameter row of a selected family
runs — tiers are per family, never per row.

| Priority | Purpose | Scope |
| --- | --- | --- |
| P0 | Small critical component baseline | The one contract whose silent failure is worst, per component. Not a coverage target. |
| P1 | Primary contracts | The bulk of reviewed declarations. Also the floor for any test holding coverage no other test reaches, except P3 whole-application or operational journeys whose behavioral identity takes precedence. |
| P2 | Broader regression | Variations, subsumed branches and cases a P1 sibling already detects. |
| P3 | Whole-application and operational journeys | Decided by what a test **is**, not what it needs to run. An offline in-process journey is still P3. |

Priority is **not** permission to execute. P1 can contain live or credential-reading cases.
P3 can contain offline cases. Requirements remain independent; `not-audited` is not a safe label.
P0 is deliberately small: it is the set that survived a contract test, never a share of coverage.

### Three constraints on any future automated tiering

All were observed while classifying, and any rule that ignores them will reintroduce a known defect.

1. **Never pin P0 to coverage-prefix membership mechanically.** One project's boundary moved from
   8 members to 7 after the derivation was corrected over the same underlying capture; no declaration
   or measured execution changed. A rule keyed to membership would have moved a tier because the
   evidence became more accurate, not because the contract changed. New source, tests, instrumentation,
   or derivation fixes may legitimately move the prefix. It remains candidate evidence, never tier authority.
2. **Greedy coverage rank is biased toward exception handlers.** An error path commonly runs the happy
   path *and then* the handling, so it can outrank the baseline it depends on. Ranking alone promoted a
   proxy's mid-stream-failure envelope over `Post_forwards_the_raw_json_rpc_body_verbatim`. Coverage counts
   what a test reaches, never what it asserts, so a test can top the ranking while asserting only a
   substring and a status code.
3. **Producer/consumer agreement is not independent proof.** Two implementations that derive and consume
   the same wrong value can pass a parity test. Likewise, writing a derived value into the source of truth
   and reading it back with the same logic only proves self-consistency. Coverage overlap has the same
   blind spot: traversing the same spans does not establish assertion overlap. Critical contracts need a
   distinguishing oracle or case that fails one wrong implementation while the matched one stays green.
ProcessLauncher stays P1: its audited Windows baseline launches benign child processes and took
about nine seconds, versus subsecond runner test durations for LmLifecycle. Neither is a speedup claim.

## Preview (no test execution)

Run from the repository worktree in PowerShell 7:

```powershell
# All surfaces, rationales, requirements and selections as JSON
./scripts/run-priority-tests.ps1

# Exact tiers; All is the default
./scripts/run-priority-tests.ps1 -Priority P0,P2

# Every affected tier; unrelated component P0 tests are not included
./scripts/run-priority-tests.ps1 -Fast -ChangedPath samples/LmStreaming.Sample/Program.cs

# Explicit component scope and exact priorities
./scripts/run-priority-tests.ps1 -Project samples/LmStreaming.Sample/LmStreaming.Sample.csproj -Priority P0,P1

# Source-only declaration inventory; runtime rows remain unknown
./scripts/Get-TestInventory.ps1 -IncludeDeclarations

# One ordered plan per tier, for the inner loop below
./scripts/run-priority-tests.ps1 -Escalate P0,P1,P2 -ChangedPath src/LmCore/Messages.cs
```

`-Fast` without change/project input selects everything conservatively. `-ChangedPath` and
`-Project` scope by changed components and their consumers. Dependencies are not traversed
backward: a Sample-only edit does not select unrelated LmCore tests. `-Priority` then narrows
that scope. Directly changed test files remain selected regardless of tier. Global selection
is still available by omitting change/project scope.

Infrastructure or genuinely unknown impact expands scope. Invalid/missing/stale policy and
unassigned tests expand priorities **within** that scope, and invalid policy blocks execution.
Imported test-project metadata alone no longer adds unrelated projects. Client/manual browser
surfaces belong to the Sample component; unrelated PowerShell/Python scripts do not join it.
`unclassifiedProjects` retains projects whose test role static inspection cannot establish.
Arbitrary runtime file reads and dynamic dependencies still need explicit review; use an
unscoped `-Priority All` when static impact is insufficient.

## Case-level migration status

The checked-in manifest classifies **9,869 known .NET method families and atomic script tests**:
P0 286, P1 7,579, P2 1,877 and P3 127. Each reviewed declaration carries its exact inventory ID and
path, plus behavioral rationale, evidence, component, uncertainties and prerequisites. .NET method
families also carry their exact source hash. The 36 atomic script rows explicitly carry a null hash,
so script-content drift remains a documented residual. The runner requires a reviewed policy row for every current known declaration, previews per-method
selections and rejects missing, extra or stale identities. The 39 container/default rows remain for
surface ownership, whole-suite client policy and conservative handling outside declaration scope.

**Supported method-subset execution is filtered, and the filter is proven.** For non-overloaded
families, `FullyQualifiedName=<declaration FQN>` selects every parameter row of exactly one method
family and nothing else — measured on disposable fixtures reproducing both adapter generations in
this repository (xUnit 2.9.3 with runner.visualstudio 3.1.5; xUnit 2.9.2 with 2.8.2 alongside MSTest
3.6.4). Filter clauses are computed from the inventory, never from manifest-authored strings, and
joined with `|`. Source IDs preserve overload signatures, but VSTest's filter/TRX family identity exposes only
class plus method. A project containing overloaded test methods is therefore an unsupported
subset shape: preview reports the ambiguity and execution fails closed instead of silently
selecting or reconciling multiple source families as one. For an abstract-base family, static
inventory emits exact concrete runtime identities only when it proves a complete set of direct,
sealed, non-generic, non-partial descendants in the same namespace. Cross-namespace, imported,
aliased, partial, unresolved inheritance and generic test identities fail closed rather than emitting
a source-shaped filter. A project with an unresolved
test-shaped custom attribute also refuses subset execution because omitted families would make
the selection incomplete.

Three properties the runner depends on:

- **`~` is never generated.** Contains-matching bleeds into siblings sharing a prefix: `~X.Run` also
  selects `X.Run_Extended`.
- **A zero-match filter exits 0**, byte-identical to a passing run, so a stale or renamed filter would
  present as a fast green build. Two independent guards cover this. Before running, an empty selection
  is an error rather than an empty success. After running, the selection is **reconciled against what
  the adapter actually reported**. Every batch gets a new empty directory under
  `.logs/test-results/priority-<stamp>/batch-<n>/`; every TRX below that directory is parsed recursively,
  including multi-target output. A selected family with no accepted result, or a reported family the
  batch did not select, fails the run. The exit code proves only that `dotnet` ran. Parsed TRX results
  prove that each selected **method family** was reported with `Passed`, `Failed`, `Timeout` or `Aborted`,
  or with `NotExecuted` when that exact family has an explicit inventory-recorded skip/platform
  condition. An undeclared or unmapped `NotExecuted` result fails closed. They do not prove that every
  dynamic data row ran.

  When a result has `testId`, reconciliation requires the exact
  `UnitTestResult/@testId → TestDefinitions/UnitTest/@id → TestMethod/@className + @name` chain. A
  dangling ID or conflicting definitions fail closed. Display-name compatibility matching is used only
  when `testId` is absent: exact display identity first, then a bare method name only when it uniquely
  identifies one selected family. Ambiguous names fail; there is no suffix match.
- **Over-long filters batch.** `-MaxFilterLength` (default 24000) splits a selection into
  deterministic, non-overlapping batches whose union is the whole selection; each batch runs as its
  own command, reconciles only its own results and propagates its own exit code. A single clause
  longer than the limit cannot be split safely and is refused before execution.

There is still **no unfiltered fallback**: a subset with no usable filter, a count below 1, or a `~` in
the expression blocks the run rather than executing the whole project.

One caveat carried from the proof: `=` selecting every parameter row is *observed* adapter behaviour,
not documented contract, and an adapter upgrade could change it silently. Reconciliation narrows this
but does not close it. Because it matches per family, an upgrade that dropped a family entirely is
caught, while one that returned **fewer parameter rows of a still-reporting family** is not: the
family reports, the run reconciles, and the lost rows are invisible. Closing that needs a per-row
expectation, which the static inventory cannot supply for `MemberData` without executing it.

`Get-TestInventory.ps1 -IncludeDeclarations` adds source-only C# method-family records and atomic
script records. C# parsing supports known xUnit/custom attributes, MSTest, nested types, alias
attributes and literal linked files. Dynamic data is never executed. Compile ownership,
inheritance and conditional-source gaps remain explicit. `discoveredTestCount` remains null.
These limitations block a complete-case claim, not further classification of known declarations.

**The 94 client TypeScript/Vitest files are deliberately out of scope for this phase.** Parsing them
needs `node_modules` under `samples/LmStreaming.Sample/ClientApp`, and the dependency install was not
authorized; tiering them from names or regex matches was rejected rather than presented as reviewed
classification. The checked-in contract is durable without the scratch audit file: the wildcard
`vitest-file` row in `test-priorities.ndjson` keeps every client test file at P1, while
`Get-TestInventory.ps1 -IncludeDeclarations` reports `typescript-parser-unavailable` on each file and
leaves declaration/runtime counts unknown. The 94-file scratch ledger records the audit input only; it
is not shipped policy. Consequence to state plainly: **case-level priority does not exist for the client
suite, which continues to run whole.** Nothing here degrades it — the existing client CI gate is untouched
— but no per-tier client selection is possible until those declarations are parsed and reviewed.

## Execute a reviewed selection

Build current binaries separately. Review the preview and provision prerequisites first.
`-Execute` requires an explicit repository-relative `-ApprovedPath` for **every** selected
surface. Missing approval blocks the entire run before any tests start. Do not bulk-approve
paths merely to silence this check. It is an acknowledgement, not a sandbox or safety audit.

```powershell
./scripts/run-priority-tests.ps1 -Priority P0 -Execute `
  -ApprovedPath tests/LmLifecycle.Tests/AchieveAi.LmDotnetTools.LmLifecycle.Tests.csproj
```

`-ApproveSelectedProjects` approves exactly the surfaces the run already selected, printing each
one with its outstanding requirements first. It removes the hand-listing that makes a wide tier
impractical; it does **not** narrow what runs, and an unselected surface is still refused. It is
an acknowledgement shortcut, never a prerequisite audit or a safety check — priority is unrelated
to whether a family needs credentials, a host or network, so read the printed list.

### Ordered tiers

`-Escalate P0,P1,P2` runs one ordinary single-tier plan per tier, in the given order, and stops at
the first failing tier so a broken baseline is never buried under a longer run. Each tier passes
through the identical policy, preflight, filter and TRX reconciliation gates; it is a driver over
this same script, not a second execution path, and not a merged selection. A tier may appear only
once, and `-Escalate` cannot be combined with `-Priority`. Without `-Execute` it previews an
ordered JSON array with one plan per tier. `.husky/pre-commit` uses it over the staged paths, and
honours `SKIP_PRIORITY_TESTS=1`.

`-Kind` restricts a run to the named surface kinds. It exists because a local loop cannot run a
modality whose dependencies are absent: P1 selects the 94 client `vitest-file` surfaces, and
`ClientApp/node_modules` is not provisioned here, so the pre-commit hook passes
`-Kind dotnet-project`. Excluded surfaces are visibly unselected with reason
`outside-requested-kinds` rather than silently dropped, and **CI still runs every kind** — this
narrows one opt-in local command, never a gate.

.NET execution uses the project with `--no-build --no-restore` and no single-framework restriction.
A fully selected project runs unfiltered; a partial selection runs the exact-family filter above and
writes a TRX per batch so the executed selection can be reconciled. A `NotExecuted` result counts as
an expected reported skip only when the matching selected family has an explicit static skip/platform
condition in inventory; undeclared or unmapped skips still fail closed. PowerShell uses a fresh `pwsh`, Python uses `python -m pytest`,
and client files use the existing local Vitest installation. Dependencies are not installed
by this runner. Manual browser scripts require their own scenario instructions; the generic
runner refuses them rather than pretending they ran. Native failures propagate as errors.
A zero native exit is only the runner's result. For a filtered subset the TRX reconciliation
additionally proves every selected **family** reported, but neither proves that every parameter
row ran or that a conditional test exercised its integration path.

## CI keeps the full test gates

The priority runner is opt-in local tooling. It does **not** replace `ci-test.ps1` or add a
priority filter to any workflow. The existing full solution command remains authoritative,
with its existing browser opt-out; the dedicated browser and client/Linux gates remain.

`ci.yml` also defines a separate Windows **Test inventory and priority tooling** job. It runs
`Get-TestInventory.Tests.ps1`, `select-impacted-tests.Tests.ps1`, `ci-test-telemetry.Tests.ps1`
and `run-priority-tests.Tests.ps1` in separate PowerShell processes. These offline fixtures
check the selection policy and runner without launching product suites or live providers.
The priority fixture rejects unassigned current test surfaces and an empty P0 baseline.
The job uses the existing PR/push/manual workflow triggers; no schedule is added.

Live/manual tests retain their existing prerequisite/opt-in behavior. **Not every manual or
live test is currently executed by CI.** Categorizing a test does not schedule it or prove a
CI run succeeded. No new live execution or scheduled workflow is authorized here.

## Verification and limitations

`run-priority-tests.Tests.ps1` uses disposable Git projects and fake native commands. It checks
priority union, P0 inclusion, consumer closure (not just full fallback), inherited-project
uncertainty, unknown/stale/invalid fallback, all modality dispatch, manual preflight, failure
propagation and real-repository policy completeness. `ci-test-telemetry.Tests.ps1` guards the unchanged full test command.
These fixtures do not run the whole product suite. Filtered runs provide family-level TRX identity
reconciliation, but they cannot prove every dynamic data row ran. Broad prerequisite audit,
representative fault checks and five alternating full/fast timing runs remain separate work. No
measured savings or complete runtime coverage is claimed.
