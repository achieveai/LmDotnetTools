# LmStreaming.Sample Output-Token Policy Implementation Plan

**Status:** Implemented — shipped in `b3699ed4` (#267). See `samples/LmStreaming.Sample/Configuration/AgentOutputTokenOptions.cs` and `Services/AgentOutputTokenPolicy.cs`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configure `LmStreaming.Sample` to use 24,576 output tokens for primary conversations and 16,384 for sample-created delegated agents while preserving explicit budgets and the global library's existing 8,192 fallback.

**Architecture:** Add a focused options type in the sample and bind it through standard .NET configuration with startup validation. Add a small sample-local policy helper that applies primary and delegated defaults without clamping explicit values, then call it from the existing root-agent, ordinary-subagent, and workflow-controller construction paths. Workflow delegates continue using the existing parent-budget inheritance path.

**Tech Stack:** .NET 9, ASP.NET Core options/configuration, C#, xUnit, FluentAssertions, CSharpier.

## Global Constraints

- `AgentOutputTokens:Primary` defaults to exactly `24576`.
- `AgentOutputTokens:Delegated` defaults to exactly `16384`.
- Configuration must reject non-positive values and `Primary < Delegated` at startup.
- Explicit `GenerateReplyOptions.MaxToken` values remain authoritative; do not clamp or rewrite them.
- Keep `MultiTurnAgentBase.DefaultMaxTokenFloor` at `8192` for non-sample consumers.
- Keep the change scoped to `LmStreaming.Sample`; do not introduce model-capability discovery or per-model maps.
- Preserve all unrelated working-tree changes.
- Do not create a git commit unless the user explicitly asks.

---

## File Structure

- Create `samples/LmStreaming.Sample/Configuration/AgentOutputTokenOptions.cs` — owns configuration keys, defaults, and validation.
- Create `samples/LmStreaming.Sample/Services/AgentOutputTokenPolicy.cs` — applies the validated sample policy to root options and subagent templates while preserving explicit values.
- Modify `samples/LmStreaming.Sample/appsettings.json` — records the deployment defaults.
- Modify `samples/LmStreaming.Sample/Program.cs` — registers options, resolves the policy, and uses it in the three construction paths.
- Create `tests/LmStreaming.Sample.Tests/Configuration/AgentOutputTokenOptionsTests.cs` — tests defaults and validation.
- Create `tests/LmStreaming.Sample.Tests/Services/AgentOutputTokenPolicyTests.cs` — tests primary/delegated application and explicit-value preservation.
- Modify `tests/LmMultiTurn.Tests/MultiTurnAgentBudgetTests.cs` only if necessary to pin the unchanged library fallback at `8192`; otherwise leave it untouched and run it as a regression test.

---

### Task 1: Add and Validate Sample Token Configuration

**Files:**
- Create: `samples/LmStreaming.Sample/Configuration/AgentOutputTokenOptions.cs`
- Modify: `samples/LmStreaming.Sample/appsettings.json`
- Create: `tests/LmStreaming.Sample.Tests/Configuration/AgentOutputTokenOptionsTests.cs`

**Interfaces:**
- Produces: `AgentOutputTokenOptions.SectionName`, `Primary`, `Delegated`, and `Validate()`.
- Consumes: standard `Microsoft.Extensions.Options.ValidateOptionsResult`.

- [ ] **Step 1: Write failing default and validation tests**

Create tests equivalent to:

```csharp
using LmStreaming.Sample.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

public sealed class AgentOutputTokenOptionsTests
{
    [Fact]
    public void Defaults_ArePrimary24K_AndDelegated16K()
    {
        var options = new AgentOutputTokenOptions();

        options.Primary.Should().Be(24_576);
        options.Delegated.Should().Be(16_384);
        options.Validate().Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 16_384)]
    [InlineData(24_576, 0)]
    [InlineData(-1, 16_384)]
    public void Validate_RejectsNonPositiveValues(int primary, int delegated)
    {
        new AgentOutputTokenOptions { Primary = primary, Delegated = delegated }
            .Validate().Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsPrimaryBelowDelegated()
    {
        new AgentOutputTokenOptions { Primary = 16_383, Delegated = 16_384 }
            .Validate().Failed.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter FullyQualifiedName~AgentOutputTokenOptionsTests
```

Expected: compilation fails because `AgentOutputTokenOptions` does not exist.

- [ ] **Step 3: Implement the options type**

Create a sealed options type with this contract:

```csharp
namespace LmStreaming.Sample.Configuration;

public sealed class AgentOutputTokenOptions
{
    public const string SectionName = "AgentOutputTokens";

    public int Primary { get; init; } = 24_576;

    public int Delegated { get; init; } = 16_384;

    public ValidateOptionsResult Validate()
    {
        if (Primary <= 0)
        {
            return ValidateOptionsResult.Fail("AgentOutputTokens:Primary must be greater than zero.");
        }

        if (Delegated <= 0)
        {
            return ValidateOptionsResult.Fail("AgentOutputTokens:Delegated must be greater than zero.");
        }

        return Primary < Delegated
            ? ValidateOptionsResult.Fail("AgentOutputTokens:Primary must be greater than or equal to Delegated.")
            : ValidateOptionsResult.Success;
    }
}
```

Add the required `Microsoft.Extensions.Options` using. Keep validation in this type so tests do not require booting the whole web host.

- [ ] **Step 4: Add checked-in defaults to appsettings**

Add this top-level section to `samples/LmStreaming.Sample/appsettings.json`:

```json
"AgentOutputTokens": {
  "Primary": 24576,
  "Delegated": 16384
}
```

Do not add a duplicate section to `appsettings.Development.json`; normal configuration layering and `AgentOutputTokens__*` environment variables already provide overrides.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the same filtered `dotnet test` command.

Expected: all `AgentOutputTokenOptionsTests` pass.

---

### Task 2: Add the Sample-Local Application Policy

**Files:**
- Create: `samples/LmStreaming.Sample/Services/AgentOutputTokenPolicy.cs`
- Create: `tests/LmStreaming.Sample.Tests/Services/AgentOutputTokenPolicyTests.cs`

**Interfaces:**
- Consumes: `AgentOutputTokenOptions`, `GenerateReplyOptions`, `SubAgentOptions`, `SubAgentTemplate`.
- Produces:
  - `GenerateReplyOptions ApplyPrimary(GenerateReplyOptions options)`
  - `GenerateReplyOptions ApplyDelegated(GenerateReplyOptions? options)`
  - `SubAgentOptions ApplyDelegated(SubAgentOptions options)`

- [ ] **Step 1: Write failing policy tests**

Cover these exact cases:

```csharp
[Fact]
public void ApplyPrimary_UsesConfiguredPrimaryWhenUnset()
{
    var policy = Policy(primary: 30_000, delegated: 18_000);

    policy.ApplyPrimary(new GenerateReplyOptions()).MaxToken.Should().Be(30_000);
}

[Fact]
public void ApplyPrimary_PreservesExplicitValue()
{
    var policy = Policy(primary: 30_000, delegated: 18_000);

    policy.ApplyPrimary(new GenerateReplyOptions { MaxToken = 4_096 })
        .MaxToken.Should().Be(4_096);
}

[Fact]
public void ApplyDelegatedOptions_UsesConfiguredDelegatedWhenUnset()
{
    Policy().ApplyDelegated((GenerateReplyOptions?)null).MaxToken.Should().Be(16_384);
}

[Fact]
public void ApplyDelegatedTemplates_FillsOnlyMissingBudgets()
{
    var options = new SubAgentOptions
    {
        Templates = new Dictionary<string, SubAgentTemplate>
        {
            ["unset"] = Template(defaultOptions: null),
            ["explicit"] = Template(defaultOptions: new GenerateReplyOptions { MaxToken = 7_000 }),
        },
    };

    var result = Policy().ApplyDelegated(options);

    result.Templates["unset"].DefaultOptions!.MaxToken.Should().Be(16_384);
    result.Templates["explicit"].DefaultOptions!.MaxToken.Should().Be(7_000);
}
```

Also assert that all non-token template fields and non-template `SubAgentOptions` fields remain equivalent.

- [ ] **Step 2: Run the focused policy tests and verify RED**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter FullyQualifiedName~AgentOutputTokenPolicyTests
```

Expected: compilation fails because `AgentOutputTokenPolicy` does not exist.

- [ ] **Step 3: Implement the minimal policy**

Implement a sealed class that accepts validated `AgentOutputTokenOptions` and uses record `with` expressions:

```csharp
public GenerateReplyOptions ApplyPrimary(GenerateReplyOptions options) =>
    options.MaxToken is null ? options with { MaxToken = _options.Primary } : options;

public GenerateReplyOptions ApplyDelegated(GenerateReplyOptions? options)
{
    var effective = options ?? new GenerateReplyOptions();
    return effective.MaxToken is null
        ? effective with { MaxToken = _options.Delegated }
        : effective;
}

public SubAgentOptions ApplyDelegated(SubAgentOptions options) =>
    options with
    {
        Templates = options.Templates.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { DefaultOptions = ApplyDelegated(pair.Value.DefaultOptions) },
            StringComparer.Ordinal),
    };
```

Use a new dictionary rather than mutating discovery/shared template sources. Preserve keys and every template field except a missing `DefaultOptions.MaxToken`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the same filtered policy-test command.

Expected: all policy tests pass.

---

### Task 3: Wire the Policy Through LmStreaming.Sample

**Files:**
- Modify: `samples/LmStreaming.Sample/Program.cs:130-150`
- Modify: `samples/LmStreaming.Sample/Program.cs:1393-1426`
- Modify: `samples/LmStreaming.Sample/Program.cs:1525-1596`
- Modify: `samples/LmStreaming.Sample/Program.cs:1616-1628`
- Modify: `samples/LmStreaming.Sample/Program.cs:1720-1738`
- Modify or extend: `tests/LmStreaming.Sample.Tests/ProgramSubAgentCompositionTests.cs`
- Modify or extend: `tests/LmStreaming.Sample.Tests/WorkspaceWorkflowWiringTests.cs`

**Interfaces:**
- Consumes: `AgentOutputTokenPolicy` from Task 2 and `IOptions<AgentOutputTokenOptions>` from Task 1.
- Produces: root `GenerateReplyOptions.MaxToken = Primary`; sample templates/controller defaults use `Delegated` when unset.

- [ ] **Step 1: Add failing composition tests for Program-facing helpers**

Expose narrow `internal static` wrappers on `Program` only if direct closure testing is impractical:

```csharp
internal static SubAgentOptions ApplyDelegatedOutputTokens(
    SubAgentOptions options,
    AgentOutputTokenPolicy policy) => policy.ApplyDelegated(options);

internal static GenerateReplyOptions ApplyPrimaryOutputTokens(
    GenerateReplyOptions options,
    AgentOutputTokenPolicy policy) => policy.ApplyPrimary(options);
```

Write tests proving the wrappers preserve explicit values and apply configured defaults. For workflow wiring, construct `WorkflowManager` with controller defaults returned by `policy.ApplyDelegated(new GenerateReplyOptions { ModelId = "controller" })`, then use the existing observable/controller construction seams to assert the effective controller `MaxToken` is delegated. If `WorkflowManager` does not expose that value, keep the assertion at the policy helper boundary rather than adding production observability solely for a test.

- [ ] **Step 2: Run composition tests and verify RED**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ProgramSubAgentCompositionTests|FullyQualifiedName~WorkspaceWorkflowWiringTests"
```

Expected: new assertions fail or compilation fails because policy wiring helpers are absent.

- [ ] **Step 3: Register and validate options at startup**

Immediately after `AddLmStreaming`, register:

```csharp
_ = builder.Services
    .AddOptions<AgentOutputTokenOptions>()
    .Bind(builder.Configuration.GetSection(AgentOutputTokenOptions.SectionName))
    .Validate(options => options.Validate().Succeeded, "Invalid AgentOutputTokens configuration.")
    .ValidateOnStart();
_ = builder.Services.AddSingleton<AgentOutputTokenPolicy>();
```

If preserving the detailed validation message requires `IValidateOptions<AgentOutputTokenOptions>`, register the options type as that validator instead of collapsing messages into the predicate. Prefer detailed startup messages if this can be done without adding another type.

- [ ] **Step 4: Resolve the policy once in the conversation factory**

Near the existing provider/subagent setup, resolve:

```csharp
var outputTokenPolicy = sp.GetRequiredService<AgentOutputTokenPolicy>();
```

Do not read configuration directly inside the closure.

- [ ] **Step 5: Apply delegated defaults to ordinary subagents**

After `BuildSubAgentOptionsAsync` returns non-null and before catalog binding/inheritance, call:

```csharp
subAgentOptions = outputTokenPolicy.ApplyDelegated(subAgentOptions);
```

Apply this before creating/reusing the shared template source so dynamically shared templates start with the host default. For templates discovered later, identify the registration seam; if late registrations bypass this normalization, wrap the source registration or apply the policy at the final spawn/template resolution boundary available in the sample. Do not modify `SubAgentManager` globally.

- [ ] **Step 6: Apply delegated defaults to workflow controller templates and controller options**

In `BuildControllerOptions`, normalize the returned `SubAgentOptions` through `outputTokenPolicy.ApplyDelegated` before applying the conversation store.

Set controller defaults using:

```csharp
controllerDefaultOptions: outputTokenPolicy.ApplyDelegated(
    new GenerateReplyOptions
    {
        ModelId = controllerModelId,
        ExtraProperties = BuildControllerReasoningExtraProperties(...),
    })
```

The existing workflow delegate inheritance then carries 16K from controller to unset delegate templates. Explicit delegate-template `MaxToken` values continue to win.

- [ ] **Step 7: Replace the root hardcode with primary policy application**

Build the existing root options without `MaxToken = 8192`, then wrap them:

```csharp
defaultOptions: outputTokenPolicy.ApplyPrimary(
    new GenerateReplyOptions
    {
        ModelId = modelId,
        BuiltInTools = filteredBuiltInTools,
        RequestResponseDumpFileName = requestResponseDumpFileName,
        PromptCaching = PromptCachingMode.Auto,
        ExtraProperties = extraProperties,
    })
```

Delete the obsolete comment about a fixed 2,048-token thinking budget and replace it with a short comment pointing to `AgentOutputTokens` configuration only if the code is not self-explanatory.

- [ ] **Step 8: Run composition tests and verify GREEN**

Run the filtered composition command from Step 2.

Expected: all tests pass.

---

### Task 4: Prove the Policy End to End and Protect Global Compatibility

**Files:**
- Test: `tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj`
- Test: `tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj`
- Format: all modified `.cs` files

**Interfaces:**
- Consumes all prior tasks.
- Produces verified sample-local behavior with unchanged global fallback.

- [ ] **Step 1: Run the full sample unit-test project**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --logger "trx;LogFileName=lmstreaming-output-policy.trx" --results-directory .logs/test-results
```

Expected: PASS with zero failed tests.

- [ ] **Step 2: Run the global multi-turn budget regression tests**

```powershell
dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter FullyQualifiedName~MultiTurnAgentBudgetTests --logger "trx;LogFileName=multiturn-budget.trx" --results-directory .logs/test-results
```

Expected: PASS. Confirm `src/LmMultiTurn/MultiTurnAgentBase.cs` remains unchanged and its `DefaultMaxTokenFloor` is still `8192`.

- [ ] **Step 3: Format only changed C# files**

Run CSharpier against the exact changed files, for example:

```powershell
dotnet csharpier format samples/LmStreaming.Sample/Configuration/AgentOutputTokenOptions.cs samples/LmStreaming.Sample/Services/AgentOutputTokenPolicy.cs samples/LmStreaming.Sample/Program.cs tests/LmStreaming.Sample.Tests/Configuration/AgentOutputTokenOptionsTests.cs tests/LmStreaming.Sample.Tests/Services/AgentOutputTokenPolicyTests.cs tests/LmStreaming.Sample.Tests/ProgramSubAgentCompositionTests.cs tests/LmStreaming.Sample.Tests/WorkspaceWorkflowWiringTests.cs
```

Do not format unrelated modified files.

- [ ] **Step 4: Re-run both test commands after formatting**

Expected: both test runs pass.

- [ ] **Step 5: Inspect the final diff for scope and unrelated changes**

```powershell
git diff -- samples/LmStreaming.Sample/Configuration/AgentOutputTokenOptions.cs samples/LmStreaming.Sample/Services/AgentOutputTokenPolicy.cs samples/LmStreaming.Sample/appsettings.json samples/LmStreaming.Sample/Program.cs tests/LmStreaming.Sample.Tests
```

Verify:

- Primary default is 24,576.
- Delegated default is 16,384.
- Explicit `MaxToken` values are preserved.
- No global library constant changed.
- No unrelated user edits were overwritten.
- No commit was created.
