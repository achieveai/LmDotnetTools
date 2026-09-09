# Agent Naming and Task Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an agent's human name the address the system advertises, speaks, and accepts — while `agent-N` stays the stored key — and expose per-agent token usage.

**Architecture:** Names already resolve (`AgentCollaborationDirectory.Resolve`) but nothing speaks them, nothing tells an agent its own name, and two contradictory collision policies exist. We inject an identity preamble into every collaborating agent's system prompt, replace both collision policies with ordinal auto-suffixing, rewrite the four error messages that actually fire in production so they name agents, close four task-board identity holes, and add a `detail` mode to `GetAgents` carrying a per-agent usage view over the existing root ledger.

**Tech Stack:** .NET 8, xUnit + FluentAssertions + Moq, CSharpier (formatting authority), Serilog.

**Spec:** `docs/superpowers/specs/2026-09-09-agent-naming-and-task-identity-design.md`

## Global Constraints

- **CSharpier is the formatting authority.** Config in `.csharpierrc`. Run `dotnet csharpier format .` before every commit. The pre-commit hook checks staged `.cs` files; CI runs `dotnet csharpier check .` over the whole tree.
- **Never** add `Co-Authored-By` or any AI/Claude signature to commits or PR bodies.
- **No pre-existing test failures.** A red test that is not yours is a blocker to report, not a thing to work around.
- **New test method families must be classified** in `scripts/test-priorities.ndjson`, or the manifest drifts and reddens `main` on merge.
- **`agent-N` stays the canonical identifier.** Never change `SubAgentThreadIds`, the ordinal allocator, or the identity-binding schema.
- **Ownership keys stay `AgentId`.** Display names are added *beside* the id, never *instead of* it.
- The default root agent display name is exactly `MainAgent`.
- `src/Misc` is a published leaf and **cannot** reference `LmMultiTurn`. Identity reaches it only through the `AssigneeResolver` delegate.
- Branch: `feat/agent-naming-and-task-identity`, forked from `origin/main`.

---

## File Structure

| File | Responsibility | Phase |
|---|---|---|
| `src/LmMultiTurn/Collaboration/AgentIdentityPreamble.cs` | **New.** Composes the identity block. Pure function, no dependencies beyond the setup record. | 1 |
| `src/LmMultiTurn/MultiTurnAgentLoop.cs:411` | Single chokepoint where the composed prompt reaches `base(...)`. | 1 |
| `src/LmMultiTurn/Collaboration/AgentCollaborationSetup.cs:84` | Root name default `root` → `MainAgent`. | 1 |
| `samples/LmStreaming.Sample/Models/ChatMode.cs` | New optional `RootAgentName`. | 1 |
| `samples/LmStreaming.Sample/Program.cs:3478` | Host passes the configured name instead of `"conversation"`. | 1 |
| `src/LmMultiTurn/Collaboration/AgentCollaborationDirectory.cs` | `TryRegister` returns the granted name; suffixing replaces ambiguity latching for live agents. | 2 |
| `src/LmMultiTurn/SubAgents/SubAgentManager.cs` | Legacy `_namesToIds` stops stealing names; uses the same suffix rule. | 2 |
| `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs` | Error text names agents; obligation rows gain names; `GetAgents` gains `detail`. | 2, 4 |
| `src/Misc/Utils/TaskManager.cs` | `add-task` resolves; `claim-task` refresh resolves; `DisplayName` on the resolution. | 3 |
| `samples/LmStreaming.Sample/Services/TodoBoardIdentityWiring.cs` | Supplies the display name beside the id. | 3 |
| `src/LmMultiTurn/UsageAccounting/UsageRecordMapper.cs` | Stamp `EffectiveModel`. | 4 |
| `src/LmCore/Models/ConversationUsageAggregate.cs` | `ExecutionUsageRow` gains a per-model breakdown. | 4 |
| `docs/adrs/00NN-agent-names-are-the-advertised-address.md` | **New.** The decision record. Number chosen at commit time. | 1 |

**Write ownership during parallel execution.** Phase 3 owns `src/Misc/**` and `tests/Misc.Tests/**`. Phase 4a owns `src/LmMultiTurn/UsageAccounting/**`, `src/LmCore/Models/ConversationUsageAggregate.cs` and their tests. The lead owns everything else, including **all** of `SubAgentToolProvider.cs` and `Program.cs`. No worker edits a file outside its list.

---

## Phase 1 — Identity preamble and root name

### Task 1: Compose the identity preamble

**Files:**
- Create: `src/LmMultiTurn/Collaboration/AgentIdentityPreamble.cs`
- Test: `tests/LmMultiTurn.Tests/Collaboration/AgentIdentityPreambleTests.cs`

**Interfaces:**
- Consumes: `AgentCollaborationSetup` (`Name`, `AgentId`, `Context.ParentAgentId`, `Directory`).
- Produces: `internal static string? AgentIdentityPreamble.Compose(AgentCollaborationSetup? collaboration)` and `internal static string? AgentIdentityPreamble.Prepend(string? systemPrompt, AgentCollaborationSetup? collaboration)`. Task 2 calls `Prepend`.

- [ ] **Step 1: Write the failing test**

```csharp
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// An agent that is never told who it is cannot tell a peer how to reach it. Production logs show
/// models addressing peers as 'lead', 'parent' and 'manager' — role words, never the ordinal — so
/// the name has to arrive in the prompt, not only in a GetAgents result the agent may never call.
/// </summary>
public class AgentIdentityPreambleTests
{
    [Fact]
    public void Compose_WithNoCollaboration_ReturnsNull()
    {
        AgentIdentityPreamble.Compose(null).Should().BeNull();
    }

    [Fact]
    public void Compose_ForARoot_NamesTheAgentAndOmitsAParent()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        var preamble = AgentIdentityPreamble.Compose(setup);

        preamble.Should().NotBeNull();
        preamble!.Should().Contain("MainAgent").And.Contain(setup.AgentId);
        preamble.Should().NotContain("You report to");
    }

    [Fact]
    public void Compose_ForAChild_NamesItsParentByName()
    {
        // The parent's NAME, not its id: the whole point is that the child can address it.
        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = root.Directory.TryAcquireCapacity(root.AgentId);
        _ = root.Directory.TryRegister(root.Context, root.Name, AgentCollaborationStatuses.Running);

        var childContext = root.Context.CreateChild("agent-1", AgentKind.SubAgent, "worker", "Does work.");
        var child = root.ForChild(childContext, "reviewer");

        var preamble = AgentIdentityPreamble.Compose(child);

        preamble.Should().Contain("reviewer").And.Contain("agent-1");
        preamble.Should().Contain("You report to `MainAgent`");
    }

    [Fact]
    public void Prepend_PutsTheIdentityBeforeTheCallersPromptAndKeepsIt()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        var composed = AgentIdentityPreamble.Prepend("You are a helpful assistant.", setup);

        composed.Should().StartWith("You are `MainAgent`");
        composed.Should().EndWith("You are a helpful assistant.");
    }

    [Fact]
    public void Prepend_WithNoCollaboration_ReturnsThePromptUnchanged()
    {
        // Mutation that must go red: dropping the null-collaboration guard. A non-collaborating
        // loop must keep byte-identical prompts, or every legacy conversation changes behaviour.
        AgentIdentityPreamble.Prepend("unchanged", null).Should().Be("unchanged");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~AgentIdentityPreambleTests"`
Expected: FAIL — `AgentIdentityPreamble` does not exist (CS0103).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// The one or two sentences that tell a collaborating agent who it is.
/// </summary>
/// <remarks>
/// <para>
/// Production evidence (1279 persisted conversations) shows models addressing peers as <c>lead</c>,
/// <c>parent</c>, <c>manager</c> and <c>Revobot</c> — role words and product names, never a mistyped
/// ordinal. The model was never told a name to use, so it invented one. The fix is to state the name
/// where the model cannot miss it.
/// </para>
/// <para>
/// The parent is named by NAME rather than id, because the entire purpose of the line is that the
/// child can address it. When the parent is not resolvable — it has not registered yet, or the row is
/// gone after a restart — the clause is omitted rather than filled with an id the model would then
/// send messages to. A missing sentence is recoverable; a wrong address is not.
/// </para>
/// <para>
/// Deliberately short. It is prepended to EVERY turn's system prompt for every agent, so each extra
/// clause is paid for on every request in the conversation.
/// </para>
/// </remarks>
internal static class AgentIdentityPreamble
{
    /// <summary>The identity block, or null when this agent is not in a collaboration.</summary>
    internal static string? Compose(AgentCollaborationSetup? collaboration)
    {
        if (collaboration is null)
        {
            return null;
        }

        var identity =
            $"You are `{collaboration.Name}` (`{collaboration.AgentId}`). "
            + $"Other agents address you as `{collaboration.Name}`.";

        var parentName = ParentNameOf(collaboration);
        return parentName is null ? identity : $"{identity} You report to `{parentName}`.";
    }

    /// <summary>
    /// The caller's prompt with the identity block in front of it, or the prompt unchanged when there
    /// is no collaboration — a non-collaborating loop must keep byte-identical prompts.
    /// </summary>
    internal static string? Prepend(string? systemPrompt, AgentCollaborationSetup? collaboration)
    {
        if (Compose(collaboration) is not { } identity)
        {
            return systemPrompt;
        }

        return string.IsNullOrWhiteSpace(systemPrompt) ? identity : $"{identity}\n\n{systemPrompt}";
    }

    private static string? ParentNameOf(AgentCollaborationSetup collaboration)
    {
        if (collaboration.Context.ParentAgentId is not { } parentId)
        {
            return null;
        }

        return collaboration.Directory.FindById(parentId)?.Name;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~AgentIdentityPreambleTests"`
Expected: PASS — 5 tests.

Note: `Compose_ForARoot_...` asserts `MainAgent`, which requires Task 3's default. Run Task 3 first if this fails only on that assertion.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add src/LmMultiTurn/Collaboration/AgentIdentityPreamble.cs tests/LmMultiTurn.Tests/Collaboration/AgentIdentityPreambleTests.cs
git commit -m "feat: compose an agent identity preamble from its collaboration handle"
```

---

### Task 2: Inject the preamble into every collaborating loop

**Files:**
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs:411`
- Test: `tests/LmMultiTurn.Tests/Collaboration/MultiTurnAgentLoopCollaborationTests.cs`

**Interfaces:**
- Consumes: `AgentIdentityPreamble.Prepend` from Task 1.
- Produces: nothing new. `MultiTurnAgentBase.SystemPrompt` now carries the identity block for collaborating loops.

`MultiTurnAgentLoop` has two constructors; the one at line 272 chains via `: this(...)` to the one at 389, whose `: base(...)` call passes `systemPrompt` at **line 411**. That is the single chokepoint. `SystemPrompt` is assigned in the base constructor *before* `Collaboration` is set in the derived body, so the composition must happen in the argument expression — a static call — not in the constructor body.

- [ ] **Step 1: Write the failing test**

Add to `MultiTurnAgentLoopCollaborationTests`. `SystemPrompt` is `protected`, so assert through the internal composer plus the loop's observable prompt. Expose the prompt for tests by asserting on `GetMessagesWithSystemPrompt()` via an existing test seam if one exists; otherwise assert the composer is wired by checking the registered directory entry name and adding this focused test:

```csharp
    [Fact]
    public async Task ALoopWithACollaboration_CarriesTheIdentityPreambleInItsSystemPrompt()
    {
        // Mutation that must go red: reverting line 411 to pass `systemPrompt` unchanged.
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        await using var loop = CreateLoop(setup, systemPrompt: "Base instructions.");

        loop.SystemPromptForTests.Should().StartWith("You are `MainAgent`");
        loop.SystemPromptForTests.Should().EndWith("Base instructions.");
    }

    [Fact]
    public async Task ALoopWithoutACollaboration_LeavesTheSystemPromptByteIdentical()
    {
        await using var loop = CreateLoop(collaboration: null, systemPrompt: "Base instructions.");

        loop.SystemPromptForTests.Should().Be("Base instructions.");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~MultiTurnAgentLoopCollaborationTests"`
Expected: FAIL — `SystemPromptForTests` does not exist, then FAIL on the assertion once it does.

- [ ] **Step 3: Write minimal implementation**

In `src/LmMultiTurn/MultiTurnAgentBase.cs`, beside `protected string? SystemPrompt { get; }` (line 142):

```csharp
    /// <summary>
    /// The composed system prompt, for tests. Internal rather than public: the prompt is an
    /// implementation detail of the loop, but the identity preamble (#agent-naming) is a behaviour
    /// worth pinning, and asserting it through a mocked provider's captured request would test the
    /// mock's plumbing rather than the composition.
    /// </summary>
    internal string? SystemPromptForTests => SystemPrompt;
```

In `src/LmMultiTurn/MultiTurnAgentLoop.cs`, change line 411 from `systemPrompt,` to:

```csharp
            // The identity block has to be composed HERE, in the base-call argument list: SystemPrompt
            // is assigned by the base constructor, which runs before `Collaboration` is set in this
            // constructor's body. Composing it in the body would leave the prompt already stored.
            AgentIdentityPreamble.Prepend(systemPrompt, collaboration),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~MultiTurnAgentLoopCollaborationTests"`
Expected: PASS — including the four pre-existing tests in the class.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add src/LmMultiTurn/MultiTurnAgentLoop.cs src/LmMultiTurn/MultiTurnAgentBase.cs tests/LmMultiTurn.Tests/Collaboration/MultiTurnAgentLoopCollaborationTests.cs
git commit -m "feat: tell every collaborating agent its own name in its system prompt"
```

---

### Task 3: Name the root agent from host config

**Files:**
- Modify: `src/LmMultiTurn/Collaboration/AgentCollaborationSetup.cs:84`
- Modify: `samples/LmStreaming.Sample/Models/ChatMode.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs:3468-3480`
- Test: `tests/LmMultiTurn.Tests/Collaboration/CollaborationIdentityWiringTests.cs`

**Interfaces:**
- Produces: `ChatMode.RootAgentName` (`string?`). `Program.CreateRootCollaboration` gains a `string? rootAgentName` parameter.

The property keeps the word *root* because it names **which** agent it configures. `MainAgent` is the default **value**. `AgentExecutionRef.RootAgentId` is an attribution sentinel, not a display name — leave it alone.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void CreateRoot_WithNoNameSupplied_NamesTheRootMainAgent()
    {
        // Mutation that must go red: restoring the old "root" default.
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        setup.Name.Should().Be("MainAgent");
    }

    [Fact]
    public void CreateRoot_WithABlankName_FallsBackToMainAgent()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), name: "   ");

        setup.Name.Should().Be("MainAgent");
    }

    [Fact]
    public void CreateRoot_WithAConfiguredName_UsesIt()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), name: "orchestrator");

        setup.Name.Should().Be("orchestrator");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~CollaborationIdentityWiringTests"`
Expected: FAIL — `Expected setup.Name to be "MainAgent", but found "root"`. The blank-name case currently throws `ArgumentException` from the `AgentCollaborationSetup` constructor.

- [ ] **Step 3: Write minimal implementation**

In `AgentCollaborationSetup.cs`, add the default beside the existing constants and change the signature default:

```csharp
    /// <summary>
    /// The root agent's display name when a host configures none. Named rather than left as a literal
    /// because it appears in the identity preamble every agent reads, and a drifting spelling would
    /// make a child's "You report to X" disagree with the directory row it resolves.
    /// </summary>
    public const string DefaultRootName = "MainAgent";
```

Change `string name = "root"` to `string? name = null` and resolve inside:

```csharp
        var resolvedName = string.IsNullOrWhiteSpace(name) ? DefaultRootName : name;
        ...
        return new AgentCollaborationSetup(bundle, context, resolvedName);
```

In `ChatMode.cs`, after `SubAgentReasoningEffort`:

```csharp
    /// <summary>
    /// Optional display name for this mode's ROOT agent — the name sub-agents see in their identity
    /// preamble and use to address the top-level conversation. Null or blank means
    /// <see cref="AgentCollaborationSetup.DefaultRootName"/>. The `primary` alias keeps working either
    /// way.
    /// </summary>
    public string? RootAgentName { get; init; }
```

In `Program.cs`, change `CreateRootCollaboration` to take and forward the name:

```csharp
    internal static AgentCollaborationSetup? CreateRootCollaboration(
        AgentCollaborationHostOptions hostOptions,
        ModeCapabilities caps,
        string threadId,
        string? rootAgentName = null
    ) =>
        hostOptions.ResolveForMode(defaultEnabled: caps.Collaboration) is { } collabOptions
            ? AgentCollaborationSetup.CreateRoot(
                collabOptions,
                collaborationId: threadId,
                agentId: threadId,
                name: rootAgentName
            )
            : null;
```

Update the call site to pass `mode.RootAgentName`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~CollaborationIdentityWiringTests"` then `dotnet build LmDotnetTools.sln`
Expected: PASS, and the solution builds — the `name:` argument at the old `Program.cs:3478` no longer passes `"conversation"`.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "feat: name the root agent MainAgent by default and let a chat mode override it"
```

---

### Task 4: Record the decisions in an ADR

**Files:**
- Create: `docs/adrs/00NN-agent-names-are-the-advertised-address.md`

**Numbering.** Check the highest existing entry in `docs/adrs/` **immediately before committing**, not now. Parallel ADR numbering collides without producing a merge conflict, so a number reserved earlier is one two branches can both take. `0018` is the highest in this worktree — re-verify against `origin/main`.

- [ ] **Step 1: Write the ADR**

Follow `docs/adrs/templates/`. It must record, with reasoning and rejected alternatives:

| Decision | Content |
|---|---|
| Names are the advertised address; `agent-N` is the stored key | Rejected: making the name canonical — it would rewrite `SubAgentThreadIds`, the ordinal allocator and the identity-binding schema, and break every persisted conversation |
| Collisions auto-suffix with the ordinal | Rejected: *latest wins* (`SubAgentManager.cs:936-956`, silently misroutes) and *latch ambiguous forever* (`AgentCollaborationDirectory.cs:643-658`, permanently bricks a name). Both existed; both are replaced. Ambiguity latching survives for tombstones — say why |
| Root named from host config, default `MainAgent`; every agent told its identity in its prompt | — |
| Per-agent usage is a **view** over the one root ledger | Rejected: relocating ownership, with the six blockers from the spec. State explicitly that this **amends nothing** in ADR 0003, so a later reader does not read it as a supersession |
| The board keeps `AgentId` as the ownership key and adds a display name beside it | The reason at `TodoBoardIdentityWiring.cs:76-83` is **upheld, not reversed** — worth saying, because the change looks like a reversal at a glance |

It must also carry the honest sizing (~5 % of `SendMessage` errors by occurrence) and why the work was done anyway. An ADR that records only the upside is one a later reader cannot trust.

Cross-reference ADR 0003 (untouched) and ADR 0009 (message fabric stays non-durable).

- [ ] **Step 2: Commit**

```bash
git add docs/adrs/
git commit -m "docs: record that agent names are the advertised address and ordinals the stored key"
```

---

## Phase 2 — Unique names, and errors that speak them

### Task 5: Auto-suffix colliding names in the directory

**Files:**
- Modify: `src/LmMultiTurn/Collaboration/AgentCollaborationDirectory.cs` (`TryRegister`, `BindName`)
- Test: `tests/LmMultiTurn.Tests/Collaboration/AgentCollaborationDirectoryTests.cs`

**Interfaces:**
- Produces: `AgentRegistrationResult.Entry.Name` now carries the **granted** name, which may differ from the requested one. Task 6 and Task 7 both read it.

Ordinal source: the agent id is already `agent-N`, so the suffix is the trailing number of `context.AgentId`. When the id is not an ordinal (a root, whose id is the thread id), fall back to a counter — a root's name collides with nothing at registration time because it registers first.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void TryRegister_WithANameAnotherLiveAgentHolds_GrantsASuffixedName()
    {
        // Mutation that must go red: restoring the IsAmbiguous latch for live agents.
        // Production already hit this: 'finance-controller' named 2 agents and the board could only
        // tell the model to "pass the agent id instead".
        var directory = NewDirectory(out var root);
        RegisterChild(directory, root, "agent-1", "reviewer");

        var second = RegisterChild(directory, root, "agent-2", "reviewer");

        second.Succeeded.Should().BeTrue();
        second.Entry!.Name.Should().Be("reviewer-2");
    }

    [Fact]
    public void Resolve_AfterASuffixedRegistration_AddressesEachAgentUnambiguously()
    {
        var directory = NewDirectory(out var root);
        RegisterChild(directory, root, "agent-1", "reviewer");
        RegisterChild(directory, root, "agent-2", "reviewer");

        directory.Resolve("reviewer").Entry!.AgentId.Should().Be("agent-1");
        directory.Resolve("reviewer-2").Entry!.AgentId.Should().Be("agent-2");
    }

    [Fact]
    public void Resolve_ForANameOnlyTombstonedAgentsShared_StaysAmbiguous()
    {
        // Latching survives for tombstones: two DEAD agents that answered to one name leave a sender
        // no way to say which it meant, and there is no live agent to suffix against.
        var directory = NewDirectory(out _);
        directory.MarkInvalidated(NodeRecord("agent-7", "ghost"));
        directory.MarkInvalidated(NodeRecord("agent-8", "ghost"));

        var resolution = directory.Resolve("ghost");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureCode.Should().Be(AgentDirectoryFailureCodes.AmbiguousName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~AgentCollaborationDirectoryTests"`
Expected: FAIL — `Expected second.Entry.Name to be "reviewer-2", but found "reviewer"`.

- [ ] **Step 3: Write minimal implementation**

In `TryRegister`, resolve the granted name **before** building the entry, and use it for both the entry and the binding:

```csharp
        var grantedName = GrantName(name, context.AgentId);
```

```csharp
    /// <summary>
    /// The name this agent will actually answer to: the one it asked for, or that name suffixed with
    /// its ordinal when another LIVE agent already holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces two collision policies that disagreed with each other. The directory used to latch a
    /// collided name permanently ambiguous, so the name resolved to nothing even after one of the two
    /// agents left; the legacy sub-agent map reassigned the name to the newcomer, silently
    /// re-targeting anyone who had been talking to the first. One bricked a name, the other misrouted.
    /// </para>
    /// <para>
    /// The suffix is the agent's own ordinal rather than a fresh counter, because ordinals are unique
    /// within a root conversation by construction, so one pass is always enough — and because
    /// <c>DeriveReadableName</c> already produces <c>{role}-{ordinal}</c>, so a suffixed name has the
    /// exact shape the model already reads back from a spawn receipt.
    /// </para>
    /// <para>
    /// Tombstones are deliberately not consulted. A name is free to be claimed again after a restart,
    /// and suffixing around a dead agent would hand the live one a stranger name for no benefit.
    /// </para>
    /// </remarks>
    private string GrantName(string name, string agentId)
    {
        if (!_byName.TryGetValue(name, out var existing) || !_byAgentId.ContainsKey(existing.AgentId))
        {
            return name;
        }

        if (string.Equals(existing.AgentId, agentId, StringComparison.Ordinal))
        {
            return name;
        }

        var suffix = SubAgentThreadIds.TryGetOrdinal(agentId, out var ordinal)
            ? ordinal.ToString(CultureInfo.InvariantCulture)
            : agentId;
        var candidate = $"{name}-{suffix}";

        // An ordinal is unique within the root, so one pass normally suffices. The loop covers the
        // pathological case where a model literally named an earlier agent "reviewer-2".
        var attempt = 0;
        while (_byName.TryGetValue(candidate, out var taken) && _byAgentId.ContainsKey(taken.AgentId))
        {
            candidate = $"{name}-{suffix}-{++attempt}";
        }

        return candidate;
    }
```

`BindName` keeps its latching implementation for the tombstone path but is now only reached with a free name on the live path; leave the latch in place as defence rather than deleting it.

Add `SubAgentThreadIds.TryGetOrdinal` if absent — parse the digits after the `agent-` prefix.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~AgentCollaborationDirectoryTests"`
Expected: PASS. Existing tests asserting the ambiguity latch **for live agents** will fail — update them to assert suffixing and keep the tombstone latch test.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "feat: grant a suffixed name instead of bricking a collided one"
```

---

### Task 6: Stop the legacy name map from stealing names

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs:936-956`
- Test: `tests/LmMultiTurn.Tests/SubAgentManagerNameCollisionTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task SpawningASecondAgentWithATakenName_LeavesTheFirstAddressable()
    {
        // Mutation that must go red: restoring `_namesToIds[effectiveName] = agentId` unconditionally.
        // The old behaviour logged a warning and silently re-pointed the name at the newcomer, so a
        // caller mid-conversation with the first agent started talking to the second.
        var manager = CreateLegacyManager();
        var first = await SpawnAsync(manager, name: "reviewer");

        var second = await SpawnAsync(manager, name: "reviewer");

        ResolveTarget(manager, "reviewer").Should().Be(first.AgentId);
        second.Name.Should().NotBe("reviewer");
        ResolveTarget(manager, second.Name).Should().Be(second.AgentId);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerNameCollisionTests"`
Expected: FAIL — `reviewer` resolves to the second agent.

- [ ] **Step 3: Write minimal implementation**

Replace the displacement block. Keep `displacedNameOwnerId` **only** for the failed-spawn rollback path (`CleanupFailedSpawnAsync`), which now has nothing to restore:

```csharp
                if (!string.IsNullOrWhiteSpace(effectiveName))
                {
                    // First writer wins. A name already answering for a live agent is not taken away
                    // from it: the caller that has been talking to that agent would silently start
                    // talking to a different one. The newcomer is given a suffixed name instead, which
                    // the spawn receipt reports, matching what the collaboration directory grants.
                    effectiveName = GrantLegacyName(effectiveName, agentId);
                    _namesToIds[effectiveName] = agentId;
                }
```

`GrantLegacyName` mirrors `GrantName` from Task 5 against `_agents`/`_namesToIds`. `effectiveName` must be reassigned **before** the spawn receipt is serialised, so the caller learns the granted name.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManager"`
Expected: PASS across the whole `SubAgentManager` family.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "fix: a spawned agent no longer steals a live agent's name"
```

---

### Task 7: Make the messages that actually fire name the agents

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:1469`, `:1858`, `:2438-2454`, `:1579-1608`
- Test: `tests/LmMultiTurn.Tests/SubAgents/UnknownTargetMessageTests.cs` (create)

These are the four strings production logs show firing. `DescribeUnknownAgent` (`:2438`) and the directory's `ambiguous_name` **never fired in the corpus** — both apparent matches were agents reading this repository's source. Fix them anyway for consistency, but say so in the commit body: they carry no current production exposure.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void UnknownSendTarget_ListsLiveAgentsByNameNotOnlyById()
    {
        // Production: 61 conversations were told "Call GetAgents for current agent_ids" after
        // addressing 'lead', 'parent', 'manager' or 'Revobot'. The reply named no agent at all, so
        // the only actionable handle it offered was the ordinal.
        var message = SubAgentToolProvider.DescribeUnknownTarget("lead", KnownAgents());

        message.Should().Contain("lead");
        message.Should().Contain("reviewer (agent-1)");
        message.Should().NotContain("current agent_ids");
    }

    [Fact]
    public void UnknownSendTarget_WithNoLiveAgents_SaysSoRatherThanListingNothing()
    {
        var message = SubAgentToolProvider.DescribeUnknownTarget("lead", []);

        message.Should().Contain("no other agents");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~UnknownTargetMessageTests"`
Expected: FAIL — `DescribeUnknownTarget` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add one shared internal helper and route all four sites through it, so the four cannot drift into four different vocabularies:

```csharp
    /// <summary>
    /// Explains an unresolvable target by naming the agents that would have worked, as
    /// <c>name (agent-id)</c> pairs.
    /// </summary>
    /// <remarks>
    /// The name comes FIRST because it is the handle the caller should use; the id is in parentheses
    /// for the case where a name is genuinely not enough. Every earlier version of these messages
    /// listed ids alone and told the model to "pass the agent id", which is what taught it that the
    /// ordinal is the address. The cap announces itself for the same reason it always did: a silently
    /// truncated list invites the reader to conclude the agent it wanted does not exist.
    /// </remarks>
    internal static string DescribeUnknownTarget(string target, IReadOnlyList<AgentDirectoryEntry> known)
    {
        if (known.Count == 0)
        {
            return $"No agent matches '{target}', and there are no other agents in this conversation to address.";
        }

        var sorted = known.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var listed = sorted.Take(MaxListedAgentIds).Select(e => $"{e.Name} ({e.AgentId})");
        var suffix = sorted.Length > MaxListedAgentIds ? $" (showing {MaxListedAgentIds} of {sorted.Length})" : "";

        return $"No agent matches '{target}'. Address one of these by name: {string.Join(", ", listed)}{suffix}.";
    }
```

Apply the same treatment to `:1858` (`You have no sub-agent matching: …`) and `DescribeUnknownAgent`. Add `to_agent_name` to each obligation row at `:1586`, resolved through the directory; omit the member when the agent is no longer resolvable rather than writing null.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentToolProvider|FullyQualifiedName~UnknownTargetMessageTests"`
Expected: PASS. Existing tests asserting the old wording must be updated.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "fix: name agents in every unresolved-target message instead of listing ids"
```

---

### Task 8: One addressing vocabulary across the tool schemas

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:696`, `:732`, `:739`, and the `name` parameter at `:455-466`
- Test: `tests/LmMultiTurn.Tests/SubAgentToolContractTests.cs` (extend, or create)

Descriptions only. Wire parameter names (`target`, `agent_id`, `agent_ids`) stay — renaming them breaks replayed tool calls from persisted conversations for no behavioural gain.

- [ ] **Step 1: Write the failing test**

```csharp
    [Theory]
    [InlineData("CheckAgent", "agent_id")]
    [InlineData("WaitAgent", "agent_id")]
    [InlineData("SendMessage", "target")]
    [InlineData("CheckAgents", "agent_ids")]
    public void EveryAddressingParameter_TellsTheModelANameWorks(string toolName, string parameterName)
    {
        // The resolver has accepted id-or-name on both paths all along; only the schemas said "id".
        var description = DescriptionOf(toolName, parameterName);

        description.Should().Contain("name");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentToolContractTests"`
Expected: FAIL for `CheckAgent` and `WaitAgent`.

- [ ] **Step 3: Write minimal implementation**

`CheckAgent.agent_id`:
```
"The agent to check: its name (preferred) or the id from Agent or SendMessage."
```

`WaitAgent.agent_id`:
```
"The agent to wait for: its name (preferred) or the id from Agent."
```
and drop *"Use an `agent_id` returned by `Agent`"* from the tool description, keeping the workflow-id redirect.

`Agent.name` gains: *"If the name you ask for is already taken by a live agent, you are given it with a numeric suffix; the result tells you the name you actually got."*

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentToolContractTests"`
Expected: PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "docs: every addressing parameter states that a name works"
```

---

## Phase 3 — Task board identity (owns `src/Misc/**`, `tests/Misc.Tests/**`)

### Task 9: `add-task` resolves its assignee

**Files:**
- Modify: `src/Misc/Utils/TaskManager.cs:319-386` (`AddTaskCore`), `:1257-1259` (the false doc comment)
- Test: `tests/Misc.Tests/Utils/TaskManagerAssigneeResolutionTests.cs`

**This is a correctness bug independent of naming.** `add-task {assignee:"reviewer"}` stores the literal `reviewer` while `claim-task {agent:"reviewer"}` resolves to and compares `agent-3`, so the assigned agent can never claim its own task.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void AddTask_WithAnUnknownAssignee_IsRefused()
    {
        // Mutation that must go red: removing the ResolveAssignee call from AddTaskCore.
        var board = new TaskManager { AssigneeResolver = _ => Unknown() };

        var result = board.AddTask("Wire the SSE endpoint", parentId: null, assignee: "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Should().BeEmpty();
    }

    [Fact]
    public void AddTask_ThenClaimByTheSameName_Succeeds()
    {
        // The D1 regression, end to end: add-task stored raw text while claim-task stored the
        // resolved id, so the two never compared equal and the assignee could not claim its own task.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask("Wire the SSE endpoint", parentId: null, assignee: "reviewer");

        var claim = board.ClaimTask("1", "reviewer");

        claim.IsError.Should().BeFalse();
        var task = board.GetTasks().Single();
        task.Assignee.Should().Be("agent-3");
        task.Status.Should().Be(TaskManager.TaskStatus.InProgress);
    }

    [Fact]
    public void AddTask_WithNoResolverWired_KeepsTheRawTextExactlyAsBefore()
    {
        var board = new TaskManager();

        _ = board.AddTask("Wire the SSE endpoint", parentId: null, assignee: "reviewer");

        board.GetTasks().Single().Assignee.Should().Be("reviewer");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Misc.Tests/Misc.Tests.csproj --filter "FullyQualifiedName~TaskManagerAssigneeResolutionTests"`
Expected: FAIL — `AddTask_WithAnUnknownAssignee_IsRefused` gets a success result; `AddTask_ThenClaimByTheSameName_Succeeds` gets `task_already_claimed` or an ownership mismatch.

- [ ] **Step 3: Write minimal implementation**

At the top of `AddTaskCore`, after the title and parentId guards and **before** the `lock`:

```csharp
        // #agent-naming: the fourth write path to Assignee. It used to store the caller's text
        // verbatim while claim-task and assign-task stored the resolved identity, so a task assigned
        // by name could never be claimed by the agent it was assigned to.
        string? resolvedAssignee = assignee;
        if (assignee is not null && ResolveAssignee(assignee, out var canonicalAssignee) is { } resolutionError)
        {
            return resolutionError;
        }
        else if (assignee is not null)
        {
            resolvedAssignee = canonicalAssignee;
        }
```

Use `resolvedAssignee` at both write sites (`:352` and `:378`). The inherited value at `:378` is already canonical — only the explicit argument needs resolving.

Correct the doc comment at `:1257-1259` to enumerate all four paths, and correct the class doc of `TaskManagerAssigneeResolutionTests`, which makes the same false claim.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Misc.Tests/Misc.Tests.csproj`
Expected: PASS, whole project.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "fix: add-task resolves its assignee like every other path that writes one"
```

---

### Task 10: `claim-task` refresh resolves before comparing, and the board shows names

**Files:**
- Modify: `src/Misc/Utils/TaskManager.cs:774-814`, `:83-88` (`AssigneeResolution`), the `list-tasks` renderer
- Modify: `samples/LmStreaming.Sample/Services/TodoBoardIdentityWiring.cs:76-83` — **lead-owned, coordinate before editing**
- Test: `tests/Misc.Tests/Utils/TaskManagerAssigneeResolutionTests.cs`, `tests/LmStreaming.Sample.Tests/Services/TodoBoardIdentityWiringTests.cs`

**Interfaces:**
- Produces: `AssigneeResolution` gains `string? DisplayName`. It is a positional record — add the parameter **last** with a default so existing construction sites keep compiling.

Ownership still compares `AgentId`. The reason at `TodoBoardIdentityWiring.cs:76-83` is upheld, not reversed.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void ClaimTask_RefreshingAnExistingClaimByName_ResolvesBeforeComparing()
    {
        // The refresh branch compared task.Assignee to the raw trimmed text before resolving, so a
        // literal "agent-3" skipped resolution entirely.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask("Wire the SSE endpoint");
        _ = board.ClaimTask("1", "reviewer");

        var refresh = board.ClaimTask("1", "reviewer");

        refresh.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ListTasks_ShowsTheDisplayNameWhileOwnershipComparesTheId()
    {
        var board = new TaskManager { AssigneeResolver = _ => LiveNamed("agent-3", "reviewer") };
        _ = board.AddTask("Wire the SSE endpoint", parentId: null, assignee: "reviewer");

        board.ListTasks().Text.Should().Contain("reviewer").And.NotContain("agent-3");
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Misc.Tests/Misc.Tests.csproj --filter "FullyQualifiedName~TaskManagerAssigneeResolutionTests"`
Expected: FAIL — no `LiveNamed` helper, no `DisplayName`.

- [ ] **Step 3: Write minimal implementation**

Add to `AssigneeResolution`:

```csharp
    /// <param name="DisplayName">
    ///     The human-facing name to SHOW for this assignee. Ownership still compares
    ///     <paramref name="CanonicalName" />, because an identifier is the only thing guaranteed
    ///     unique within a conversation; this exists so a listing can say <c>reviewer</c> where the
    ///     key says <c>agent-3</c>. Null falls back to the canonical value, which is the pre-existing
    ///     behaviour.
    /// </param>
```

Store it on the task node beside `Assignee` (`AssigneeDisplayName`, nullable, camelCase-pinned like its neighbours in `TodoBoardSnapshot`) so it survives the snapshot round trip. Render it in `list-tasks`, falling back to `Assignee`.

Move the raw-text comparison in `ClaimTaskCore` to **after** `ResolveAssignee`.

In `TodoBoardIdentityWiring.Resolve`, pass `entry.Name` as the display name while keeping `entry.AgentId` in both identity slots.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Misc.Tests/Misc.Tests.csproj` and `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~TodoBoard"`
Expected: PASS both.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "feat: the todo board shows agent names while keying ownership on ids"
```

---

### Task 11: Re-point `TaskAssignmentProbe` at the resolver

**Files:**
- Modify: `samples/LmStreaming.Sample/Program.cs:3842-3860`
- Test: `tests/LmMultiTurn.Tests/SubAgentRequiredToolsTests.cs:816-828`

The probe is invoked with the spawn **name** and compared case-insensitively against `task.Assignee`, which holds `agent-N` once the resolver is attached. **It can never match.** Impact is bounded — it gates a `LogWarning` about a sub-agent lacking task tools — but it is the same name/id seam.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void TaskAssignmentProbe_MatchesASpawnNameAgainstAResolvedAssignee()
    {
        // Mutation that must go red: comparing the spawn name to task.Assignee directly.
        var board = BoardAssignedTo(agentId: "agent-3", displayName: "reviewer");

        Probe(board, spawnName: "reviewer").Should().BeTrue();
        Probe(board, spawnName: "unrelated").Should().BeFalse();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentRequiredToolsTests"`
Expected: FAIL — the probe returns false for `reviewer`.

- [ ] **Step 3: Write minimal implementation**

Compare the spawn name against the stored **display name** first, then the canonical id, both case-insensitively. Keep the existing case-insensitivity — the comment at `:3855-3858` correctly notes both sides are human-typed.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentRequiredToolsTests"`
Expected: PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "fix: the task-assignment probe can match a resolved assignee again"
```

---

## Phase 4 — Usage prerequisites and the `GetAgents` detail mode

### Task 12: Stamp `EffectiveModel` (owns `src/LmMultiTurn/UsageAccounting/**`)

**Files:**
- Modify: `src/LmMultiTurn/UsageAccounting/UsageRecordMapper.cs:60`
- Test: `tests/LmMultiTurn.Tests/UsageAccounting/UsageRecordMapperTests.cs`

`UsageRecord.EffectiveModel` is **never written anywhere in `src/`** — verified by grep. `EffectiveModelId` therefore always falls back to `RequestedModel`. ADR 0003's claim that the model *actually used* is captured is not delivered today, and a per-agent usage view would inherit that lie.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Map_WhenTheProviderReportsADifferentModel_StampsItAsEffective()
    {
        // Mutation that must go red: reverting the stamp so EffectiveModel stays null.
        var record = UsageRecordMapper.Map(
            usage: UsageWithModel("claude-opus-5-20260101"),
            requestedModel: "claude-opus-5",
            ownerExecutionId: "thread-1",
            rootConversationId: "thread-1"
        );

        record.EffectiveModel.Should().Be("claude-opus-5-20260101");
        record.EffectiveModelId.Should().Be("claude-opus-5-20260101");
    }

    [Fact]
    public void Map_WhenTheProviderReportsNoModel_LeavesEffectiveNullAndFallsBack()
    {
        var record = UsageRecordMapper.Map(
            usage: UsageWithModel(null),
            requestedModel: "claude-opus-5",
            ownerExecutionId: "thread-1",
            rootConversationId: "thread-1"
        );

        record.EffectiveModel.Should().BeNull();
        record.EffectiveModelId.Should().Be("claude-opus-5");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~UsageRecordMapperTests"`
Expected: FAIL — `EffectiveModel` is null.

- [ ] **Step 3: Write minimal implementation**

Read `UsageRecordMapper.Map`'s real signature before editing — the call above is illustrative. Stamp `EffectiveModel` from the provider-reported model on the `Usage` payload when present and different from the requested one; leave it null otherwise so `EffectiveModelId`'s documented fallback is preserved. Confirm which member of the incoming usage carries the provider's model before wiring it; if none does, **stop and report** rather than inventing one.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~Usage"` and `dotnet test tests/LmCore.Tests/LmCore.Tests.csproj --filter "FullyQualifiedName~Usage"`
Expected: PASS both.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "fix: record the model a provider actually used, not only the one requested"
```

---

### Task 13: Give `ExecutionUsageRow` a model dimension (owns `src/LmCore/Models/ConversationUsageAggregate.cs`)

**Files:**
- Modify: `src/LmCore/Models/ConversationUsageAggregate.cs:77-129` (`ExecutionUsageRow`), `FoldByExecution`
- Test: `tests/LmCore.Tests/Models/ExecutionUsageFoldTests.cs`

`ExecutionUsageRow` sums across models per execution. "Usage carrying the model used" needs the breakdown on **this** row, not on `ModelUsageRow` — which answers a conversation-wide question and stays unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void FoldByExecution_ForAnAgentThatSwitchedModels_BreaksTheRowDownByModel()
    {
        var records = new[]
        {
            RecordFor(execution: "subagent-root-agent-1", model: "claude-opus-5", input: 100, output: 10),
            RecordFor(execution: "subagent-root-agent-1", model: "claude-haiku-4-5", input: 40, output: 4),
        };

        var row = ConversationUsageAggregate.FoldByExecution(records).Single();

        row.TotalTokens.Should().Be(154);
        row.PerModel.Should().HaveCount(2);
        row.PerModel.Single(m => m.ModelId == "claude-opus-5").InputTokens.Should().Be(100);
        row.PerModel.Single(m => m.ModelId == "claude-haiku-4-5").InputTokens.Should().Be(40);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmCore.Tests/LmCore.Tests.csproj --filter "FullyQualifiedName~ExecutionUsageFoldTests"`
Expected: FAIL — `ExecutionUsageRow` has no `PerModel`.

- [ ] **Step 3: Write minimal implementation**

Add `PerModel` (`IReadOnlyList<ModelUsageRow>`, defaulting to empty) to `ExecutionUsageRow`, and populate it in `FoldByExecution` by grouping the execution's records on `EffectiveModelId`. **Additive only** — the projection tolerates additive fields via `SchemaVersion`; do not reorder or remove existing members.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmCore.Tests/LmCore.Tests.csproj`
Expected: PASS, whole project — including `UsageSerializationTests`.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "feat: per-execution usage carries its own per-model breakdown"
```

---

### Task 14: `GetAgents` gains a detail mode carrying usage

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentToolProvider.cs:1702-1783` and the `GetAgents` contract
- Test: `tests/LmMultiTurn.Tests/SubAgents/GetAgentsDetailTests.cs` (create)

Today the tool always emits 16 fields per agent — about 340 bytes per row, capped at 32 agents, so roughly 11 KB on **every** call.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task GetAgents_ByDefault_ReturnsTheNormalShapeWithoutIdsOrUsage()
    {
        var json = await InvokeGetAgentsAsync(detail: null);

        var agent = json["agents"]![0]!;
        agent["name"].Should().NotBeNull();
        agent["description"].Should().NotBeNull();
        agent["parent_name"].Should().NotBeNull();
        agent["status"].Should().NotBeNull();
        agent["agent_id"].Should().BeNull();
        agent["usage"].Should().BeNull();
    }

    [Fact]
    public async Task GetAgents_WithDetailed_AddsTheIdAndAUsageBlockCarryingTheModel()
    {
        var json = await InvokeGetAgentsAsync(detail: "detailed");

        var agent = json["agents"]![0]!;
        agent["agent_id"]!.GetValue<string>().Should().Be("agent-1");
        agent["delegation_depth"].Should().NotBeNull();
        agent["usage"]!["total_tokens"].Should().NotBeNull();
        agent["usage"]!["per_model"]![0]!["model_id"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetAgents_WithAnUnknownDetailValue_IsRefusedRatherThanSilentlyNormal()
    {
        // A typo must not quietly return the smaller shape — the caller would conclude the ids are
        // gone rather than that it asked wrongly.
        var result = await InvokeGetAgentsRawAsync(detail: "verbose");

        result.IsError.Should().BeTrue();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~GetAgentsDetailTests"`
Expected: FAIL — no `detail` parameter; `agent_id` is always present.

- [ ] **Step 3: Write minimal implementation**

Add the contract parameter:

```csharp
                new FunctionParameterContract
                {
                    Name = "detail",
                    Description =
                        "How much to return per agent. 'normal' (default) gives the name, what the "
                        + "agent is for, who it reports to, and whether it is still running — enough "
                        + "to decide whom to contact. 'detailed' adds internal ids, depths, "
                        + "transcript readability, and token usage. Prefer 'normal': a detailed "
                        + "listing of a large collaboration is several thousand tokens you pay for "
                        + "on every call.",
                    ParameterType = new JsonSchemaObject { Type = new("string"), Enum = ["normal", "detailed"] },
                    IsRequired = false,
                },
```

Split the row projection in two. `normal`: `name`, `description`, `parent_name`, `status`, `is_you`. `detailed`: those plus `agent_id`, `aliases`, `agent_type`, `structural_depth`, `delegation_depth`, `transcript_readable`, `is_live`, `usage`.

`parent_name` resolves the parent id through the directory; omit the member when the parent is unresolvable rather than writing null.

`usage` is a **view**: fold the root ledger's records with `ConversationUsageAggregate.FoldByExecution`, key on `AgentExecutionRef.ExecutionIdOf`, and map back to the agent with `AgentExecutionRef.AgentIdFromThreadId`. **Do not create a second ledger.** When the loop has no ledger, or the agent has no records, omit `usage` rather than writing zeros — a zero reads as "this agent spent nothing", which is a different claim from "nothing was recorded".

Reject an unrecognised `detail` value with `invalid_args`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~GetAgents|FullyQualifiedName~SubAgentToolProvider"`
Expected: PASS. Existing tests asserting the always-present 16-field shape must be updated to pass `detail: "detailed"`.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format .
git add -A
git commit -m "feat: GetAgents returns a compact listing by default and usage on request"
```

---

## Task 15: Classify the new test families and run the full gate

**Files:**
- Modify: `scripts/test-priorities.ndjson`

- [ ] **Step 1: List the new test method families**

```bash
git diff --name-only origin/main -- 'tests/**/*.cs'
```

- [ ] **Step 2: Add one classification row per new family**

Follow `scripts/TEST-PRIORITIES.md`. Suggested tiers: identity preamble, name granting, and the `add-task` resolution regression are **P0** (they are the correctness baseline for this change); tool-contract wording and `GetAgents` shape are **P1**; usage folding is **P1**.

- [ ] **Step 3: Preview the plan without running anything**

```bash
./scripts/run-priority-tests.ps1 -Escalate P0,P1,P2 -ChangedPath src/LmMultiTurn/Collaboration/AgentIdentityPreamble.cs
```

- [ ] **Step 4: Build, then run the tiers**

```bash
dotnet build LmDotnetTools.sln
./scripts/run-priority-tests.ps1 -Escalate P0,P1,P2 -Kind dotnet-project -Execute -ApproveSelectedProjects
```

`-Kind dotnet-project` is required locally because P1 also selects the client Vitest files and `ClientApp/node_modules` is not provisioned. Execution needs `--no-build` binaries, so **build first** or you test the last commit.

- [ ] **Step 5: Run the authoritative suite**

```bash
./scripts/ci-test.ps1
```

A tier passing proves only that each selected method family was reported. This is the gate.

- [ ] **Step 6: Format check and commit**

```bash
dotnet csharpier check .
git add scripts/test-priorities.ndjson
git commit -m "ci: classify the test declarations this change adds to the priority manifest"
```

---

## Self-review notes

**Spec coverage.** §1 → Tasks 1-3. §2 → Tasks 5-6. §3 → Task 7. §4 → Task 8. §5 → Tasks 9-11. §6 → Tasks 12-14. ADR → Task 4. Repository gates → Task 15. No spec section is unimplemented.

**Known signature gaps to resolve at implementation time, not to guess:**
- `UsageRecordMapper.Map`'s real parameter list (Task 12) — the test code above is illustrative. Read it first; if nothing on the incoming usage carries the provider's model, **stop and report** rather than inventing a source.
- `SubAgentThreadIds.TryGetOrdinal` may not exist (Task 5) — add it beside `TryGetAgentId` if absent.
- The `list-tasks` renderer's exact shape (Task 10).
- Whether `TaskManager` instances at `ToolCatalog.cs:70` and `ModeSubAgentRequiredTools.cs:47` reach a live conversation (spec §5 open item). **Confirm during Task 9.** If either does, that board has no resolver and this plan's guarantees do not hold there — report it rather than silently widening scope.
