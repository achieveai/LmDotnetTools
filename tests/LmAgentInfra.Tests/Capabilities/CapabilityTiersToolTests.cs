using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// Sandbox tools as a tiered kind (<see cref="TierSettings.ToolCap"/>): what <c>SearchTool</c> answers. Driven the way
/// the host drives it: stage the build, attach the tools the sandbox connect registered, commit, then call the search
/// through the root provider.
/// </summary>
public class CapabilityTiersToolTests
{
    private const string T = "thread-1792713107994-65ef4d58";
    private const string SearchTool = CapabilityTiers.SearchToolToolName;

    [Fact]
    public async Task SearchTool_Args_CarryEachParametersDescriptionAndItsArrayItems()
    {
        using var tiers = await TiersAsync(
            new FunctionContract
            {
                Name = "math_eval",
                Description = "Evaluate mathematical expressions.",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "function",
                        Description = "The function to call: calculate, solve",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "coefficients",
                        Description = "Rows of A.",
                        ParameterType = JsonSchemaObject.Array(JsonSchemaObject.Number()),
                    },
                ],
            }
        );

        var text = (await SearchAsync(tiers, """{"query":"math_eval"}""")).Text;

        var args = text.Split('\n').Single(l => l.StartsWith("   Args: ", StringComparison.Ordinal))[
            "   Args: ".Length..
        ];
        using var schema = JsonDocument.Parse(args);
        var properties = schema.RootElement.GetProperty("properties");
        properties
            .GetProperty("function")
            .GetProperty("description")
            .GetString()
            .Should()
            .Be("The function to call: calculate, solve");
        properties.GetProperty("coefficients").GetProperty("description").GetString().Should().Be("Rows of A.");
        properties
            .GetProperty("coefficients")
            .GetProperty("items")
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("number");
        schema
            .RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("function");
    }

    // ExecuteTool checks arguments with JsonSchemaValidator, which lets an extra argument through (the MCP server
    // decides). The schema the model is shown must not claim otherwise.
    [Fact]
    public async Task SearchTool_Args_AllowTheExtraArgumentsExecuteToolAccepts()
    {
        var contract = new FunctionContract
        {
            Name = "math_eval",
            Description = "Evaluate mathematical expressions.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "function",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };
        using var tiers = await TiersAsync(contract);

        var text = (await SearchAsync(tiers, """{"query":"math_eval"}""")).Text;

        new JsonSchemaValidator()
            .ValidateDetailed("""{"function":"calculate","bogus":1}""", contract)
            .IsValid.Should()
            .BeTrue("guard: the validator accepts the extra argument");
        var args = text.Split('\n').Single(l => l.StartsWith("   Args: ", StringComparison.Ordinal))[
            "   Args: ".Length..
        ];
        using var schema = JsonDocument.Parse(args);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeTrue();
    }

    // The six plain queries MT ran on SC2, and off-topic queries that still hit something (most hit nothing). Not
    // here: "search the web" and "create a pull request", which hit Grep at 1.99, the score of the right answer to
    // "search text inside files" (see CapabilityTiers.WeakToolScore).
    [Fact]
    public async Task SearchTool_ARightTopHitIsNotWeak_AnOffTopicQuerysHitsAllAre()
    {
        using var tiers = await TiersAsync([.. SandboxTools.Select(t => Tool(t.Name, t.Description))]);

        foreach (
            var (query, top) in new[]
            {
                ("run a shell command", "Bash"),
                ("find files by name", "Glob"),
                ("search text inside files", "Grep"),
                ("edit a file", "Edit"),
                ("write a new file", "Write"),
                ("read a file", "Read"),
            }
        )
        {
            HitRows((await SearchAsync(tiers, $$"""{"query":"{{query}}"}""")).Text)[0]
                .Should()
                .Be($"1. {top} [available]", query);
        }

        foreach (var query in new[] { "translate text to french", "ask the user a question" })
        {
            var rows = HitRows((await SearchAsync(tiers, $$"""{"query":"{{query}}"}""")).Text);
            rows.Should().NotBeEmpty("guard: the query must hit something for its hits to be weak");
            rows.Should().OnlyContain(row => row.Contains(" [weak] ", StringComparison.Ordinal), query);
        }
    }

    // SC4. The root pins Read; each sub-agent's view is seeded with Glob. The template leaves Write out.
    [Fact]
    public async Task SubAgent_ToolsListItsOwnView_PlusSearchToolAndExecuteTool()
    {
        using var tiers = await TiersAsync(SubAgentToolSettings, await StoreAsync(), ManagedTools);

        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash");

        child
            .BuildContracts()
            .Select(c => c.Name)
            .Should()
            .BeEquivalentTo("Glob", SearchTool, CapabilityTiers.ExecuteToolToolName);
    }

    [Fact]
    public async Task SubAgent_SearchToolAndExecuteTool_ReachOnlyTheToolsItInherited()
    {
        using var tiers = await TiersAsync(SubAgentToolSettings, await StoreAsync(), ManagedTools);
        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash");
        var execute = child.GetProviders().OfType<IDeferredToolProvider>().Single();

        const string WriteQuery = """{"query":"write a new file"}""";
        HitRows((await SearchAsync(tiers, WriteQuery)).Text)
            .Should()
            .Contain(row => row.Contains(" Write ", StringComparison.Ordinal), "guard: the root's search finds Write");

        HitRows(await ChildSearchAsync(child, """{"query":"run a shell command"}"""))[0]
            .Should()
            .Be("1. Bash [available]");
        HitRows(await ChildSearchAsync(child, WriteQuery))
            .Should()
            .NotContain(row => row.Contains(" Write ", StringComparison.Ordinal));
        execute
            .TryUnwrap(CapabilityTiers.ExecuteToolToolName, """{"tool":"Bash","arguments":{"command":"echo"}}""")!
            .FunctionName.Should()
            .Be("Bash");
        execute
            .TryUnwrap(CapabilityTiers.ExecuteToolToolName, """{"tool":"Write","arguments":{"file_path":"/x"}}""")!
            .ErrorCode.Should()
            .Be("tool_not_available");
    }

    // PC-SC5-3: Write is excluded at the root (never attached) and not inherited by the child. Neither agent can ever
    // get it, so ExecuteTool's refusal says so instead of sending the model to SearchTool, which cannot find it.
    [Fact]
    public async Task ExecuteTool_ForAToolTheAgentCanNeverGet_SaysItIsNotAvailable_WithoutPointingToSearchTool()
    {
        FunctionContract[] withoutWrite = [.. ManagedTools.Where(t => t.Name != "Write")];
        using var tiers = await TiersAsync(SubAgentToolSettings, await StoreAsync(), withoutWrite);
        var root = RootRegistry(tiers, withoutWrite);
        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash");

        foreach (var (agent, registry) in new[] { ("root", root), ("child", child) })
        {
            var refusal = registry
                .GetProviders()
                .OfType<IDeferredToolProvider>()
                .Single()
                .TryUnwrap(ExecuteTool, """{"tool":"Write","arguments":{"file_path":"/x"}}""")!;
            refusal.ErrorCode.Should().Be("tool_not_available", agent);
            using var error = JsonDocument.Parse(refusal.ErrorJson!);
            error
                .RootElement.GetProperty("error")
                .GetString()
                .Should()
                .Contain("not available to this agent", agent)
                .And.NotContain("SearchTool", agent);
        }
    }

    [Fact]
    public async Task SubAgentSpawn_LeavesTheRootsToolsAndPersistedViewUnchanged()
    {
        var store = await StoreAsync();
        using var tiers = await TiersAsync(SubAgentToolSettings, store, ManagedTools);
        var root = RootRegistry(tiers, ManagedTools);

        var tools = JsonSerializer.Serialize(root.BuildContracts());
        var view = await RawViewAsync(store, T);

        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash", "Write");
        _ = await ChildSearchAsync(child, """{"query":"find files by name"}""");

        child
            .BuildContracts()
            .Select(c => c.Name)
            .Should()
            .Contain("Glob", "guard: the child's view was seeded, with a tool the root does not list");
        JsonSerializer.Serialize(root.BuildContracts()).Should().Be(tools);
        (await RawViewAsync(store, T)).Should().Be(view);
    }

    // SC5. The root pins Read and loads Glob, with ToolCap 2. Since its last compaction it called Grep, then ran Bash
    // through ExecuteTool, called Read, and asked ExecuteTool for Write with bad arguments (refused, still a use), for an
    // unknown tool, and searched. At its compaction the two most recent uses are Loaded, Glob goes and Read stays.
    [Fact]
    public async Task Root_ToolsUsedSinceItsLastCompaction_AreLoaded_MostRecentUpToToolCap_ToolsHoldBetween()
    {
        using var tiers = await TiersAsync(RotationSettings, await StoreAsync(), RotationTools);
        var root = RootRegistry(tiers, RotationTools);
        ListedTools(root).Should().BeEquivalentTo(["Read", "Glob"], "guard: Glob starts loaded");
        var before = JsonSerializer.Serialize(root.BuildContracts());
        root.GetProviders()
            .OfType<IDeferredToolProvider>()
            .Single()
            .TryUnwrap(ExecuteTool, BashCall)!
            .FunctionName.Should()
            .Be("Bash", "guard: Bash runs before it is loaded");
        JsonSerializer
            .Serialize(root.BuildContracts())
            .Should()
            .Be(before, "running an available tool changes no request");

        var changed = await tiers.OnCheckpointActivatedAsync(
            Activation(
                T,
                "root",
                10,
                9,
                (2, "Grep", """{"pattern":"x"}"""),
                (3, ExecuteTool, BashCall),
                (4, "Read", """{"file_path":"/x"}"""),
                (5, ExecuteTool, """{"tool":"Write","arguments":"not an object"}"""),
                (6, ExecuteTool, """{"tool":"Nope"}"""),
                (7, SearchTool, """{"query":"edit a file"}""")
            ),
            CancellationToken.None
        );

        changed.Should().BeTrue("the request now lists other tools");
        ListedTools(root).Should().BeEquivalentTo("Read", "Bash", "Write");
        var after = JsonSerializer.Serialize(root.BuildContracts());
        JsonSerializer
            .Serialize(root.BuildContracts())
            .Should()
            .Be(after, "the new set holds until the next compaction");
    }

    [Fact]
    public async Task Root_NoToolCallsSinceItsLastCompaction_EvictsLoadedGlob_KeepsPinnedRead()
    {
        using var tiers = await TiersAsync(RotationSettings, await StoreAsync(), RotationTools);
        var root = RootRegistry(tiers, RotationTools);
        ListedTools(root).Should().BeEquivalentTo(["Read", "Glob"], "guard: Glob starts loaded");

        (await tiers.OnCheckpointActivatedAsync(Activation(T, "root", 5, 4), CancellationToken.None)).Should().BeTrue();

        ListedTools(root).Should().BeEquivalentTo("Read");
    }

    // The child inherited Read, Glob and Bash, and its view starts with Glob. It runs Bash, and asks for Write, which
    // it did not inherit (refused, never its use). Its own compaction rotates its own view; the root's stays as it was.
    [Fact]
    public async Task SubAgent_ToolsRotateAtItsOwnCompaction_TheRootsStayUnchanged()
    {
        var store = await StoreAsync();
        using var tiers = await TiersAsync(SubAgentToolSettings, store, ManagedTools);
        var root = RootRegistry(tiers, ManagedTools);
        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash");
        ListedTools(child).Should().BeEquivalentTo(["Glob"], "guard: the child's view starts with Glob");
        var rootTools = JsonSerializer.Serialize(root.BuildContracts());
        var rootView = await RawViewAsync(store, T);

        (
            await tiers.OnCheckpointActivatedAsync(
                Activation(
                    SubAgentThreadIds.For(T, "agent-1"),
                    "agent-1",
                    10,
                    9,
                    (3, ExecuteTool, BashCall),
                    (4, ExecuteTool, """{"tool":"Write","arguments":{"file_path":"/x"}}""")
                ),
                CancellationToken.None
            )
        )
            .Should()
            .BeTrue();

        ListedTools(child).Should().BeEquivalentTo("Bash");
        JsonSerializer.Serialize(root.BuildContracts()).Should().Be(rootTools);
        (await RawViewAsync(store, T)).Should().Be(rootView);
    }

    // F-005. A child ranks among the tools it inherited: one ranked below the default limit of root-only tools is
    // still its hit. Red: rank the root's catalog, cut at the limit, then keep the inherited hits (none are left).
    [Fact]
    public async Task SubAgent_SearchTool_RanksAmongItsInheritedTools_NotTheRootsTopHits()
    {
        FunctionContract[] rootOnly =
        [
            .. Enumerable
                .Range(1, 5)
                .Select(i => Tool($"ShellRunner{i}", "Run a shell command. Runs any shell command in a new shell.")),
        ];
        using var tiers = await TiersAsync(SubAgentToolSettings, await StoreAsync(), [.. ManagedTools, .. rootOnly]);
        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash");
        const string Query = """{"query":"run a shell command"}""";
        HitRows((await SearchAsync(tiers, Query)).Text)
            .Should()
            .HaveCount(5)
            .And.OnlyContain(
                row => row.Contains(" ShellRunner", StringComparison.Ordinal),
                "guard: at the root, five root-only tools outrank Bash at the default limit"
            );

        HitRows(await ChildSearchAsync(child, Query))
            .Should()
            .NotBeEmpty()
            .And.Subject.First()
            .Should()
            .StartWith("1. Bash ");
    }

    // D-SC4-2. A child whose setup failed inherits the root's ExecuteTool as a plain function, so no unwrap runs and
    // this handler answers. The model must learn which call to make instead.
    [Fact]
    public async Task ExecuteTool_NotUnwrapped_TellsTheModelToCallTheTargetDirectly()
    {
        using var tiers = await TiersAsync(ManagedTools);
        var execute = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.ExecuteToolToolName);

        foreach (
            var (args, target) in new[]
            {
                ("""{"tool":"Bash","arguments":{"command":"echo sc4-fail"}}""", "Bash"),
                ("{}", "the tool"),
            }
        )
        {
            var result = await execute.Handler(args, new ToolCallContext(), CancellationToken.None);

            var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
            payload.IsError.Should().BeTrue(args);
            payload.ErrorCode.Should().Be("not_supported", args);
            payload.Text.Should().Contain($"Call {target} directly", args);
        }
    }

    // G2. ExecuteTool's refusal grammar: each malformed call gets its code, and the argument forms a weak model sends
    // (null, missing, an object double-encoded as a string) still run. Red: drop a check (a blank "tool" then reads as
    // tool_not_available), change a code, or stop reading null or a double-encoded object.
    [Theory]
    [InlineData("""{"arguments":{}}""", "invalid_args", null)]
    [InlineData("""{"tool":"  ","arguments":{}}""", "invalid_args", null)]
    [InlineData("""{"tool":"Bash","arguments":""", "invalid_args", null)]
    [InlineData("""{"tool":"ExecuteTool","arguments":{}}""", "call_directly", null)]
    [InlineData("""{"tool":"SearchTool","arguments":{}}""", "call_directly", null)]
    [InlineData("""{"tool":"Bash","arguments":[1]}""", "invalid_args", null)]
    [InlineData("""{"tool":"Bash","arguments":"[1]"}""", "invalid_args", null)]
    [InlineData("""{"tool":"Bash","arguments":null}""", null, "{}")]
    [InlineData("""{"tool":"Bash"}""", null, "{}")]
    [InlineData("""{"tool":"Bash","arguments":"{\"command\":\"echo\"}"}""", null, """{"command":"echo"}""")]
    public async Task ExecuteTool_GivesEachMalformedCallItsCode_AndRunsTheArgumentFormsAWeakModelSends(
        string args,
        string? code,
        string? runsWith
    )
    {
        using var tiers = await TiersAsync(ManagedTools);
        var execute = RootRegistry(tiers, ManagedTools).GetProviders().OfType<IDeferredToolProvider>().Single();

        var unwrap = execute.TryUnwrap(ExecuteTool, args);

        unwrap.Should().NotBeNull();
        if (code is not null)
        {
            unwrap!.ErrorCode.Should().Be(code);
            unwrap.IsRefusal.Should().BeTrue();
        }
        else
        {
            unwrap!.IsRefusal.Should().BeFalse(unwrap.ErrorJson);
            unwrap.FunctionName.Should().Be("Bash");
            unwrap.ArgumentsJson.Should().Be(runsWith);
        }
    }

    // G1 (data loss). The first build with tools tiered gets an empty listing (a gateway hiccup). Seeding the tool kind
    // against it would record tools as seeded with none loaded and persist that, so the mode's loaded tools would
    // never be seeded again. Red: drop the tools.Count == 0 guard in AttachTools (the next build lists only Read).
    [Fact]
    public async Task ABuildWhoseSandboxListsNoTools_StagesNone_SoTheNextBuildStillSeedsTheModesTools()
    {
        var store = await StoreAsync();
        var log = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, log);

        await BuildAsync(tiers, RotationSettings, new FakeSkillGateway().For(T), []);

        log.Starting("Capabilities tools not staged").Should().ContainSingle();
        await BuildAsync(tiers, RotationSettings, new FakeSkillGateway().For(T), RotationTools);
        ListedTools(RootRegistry(tiers, RotationTools)).Should().BeEquivalentTo("Read", "Glob");
        var view = (await RawViewAsync(store, T)).Should().NotBeNull().And.Subject;
        view.Should().Contain(CapabilityCatalog.ToolKeyPrefix + "Glob");
    }

    // G3. A gateway that stops answering mid-spawn or mid-rotation costs one budget each, not a hang. The child then
    // inherits the parent's tools (one WARN; its ExecuteTool falls back without waiting again), and the rotation
    // reports no change with a WARN. Red: drop either CancelAfter (the 5 s wait fails), or the budget's WARN.
    [Fact]
    public async Task AGatewayThatHangs_CostsASpawnAndARotationOneBudgetEach_WithOneWarnEach()
    {
        var log = new RecordingLogger();
        var live = new FakeSkillGateway().For(T);
        var hang = false;
        var hung = 0;
        var gateway = live with { PatchTiers = (id, set, ct) => hang ? HangAsync(ct) : live.PatchTiers(id, set, ct) };
        using var tiers = new CapabilityTiers(T, await StoreAsync(), log)
        {
            SpawnBudget = TimeSpan.FromMilliseconds(100),
            RotationBudget = TimeSpan.FromMilliseconds(100),
        };
        await BuildAsync(tiers, RotationSettings, gateway, RotationTools);
        ListedTools(RootRegistry(tiers, RotationTools))
            .Should()
            .BeEquivalentTo(
                ["Read", "Glob"],
                "guard: Glob is loaded, so a rotation with no uses evicts it and PATCHes"
            );
        hang = true;

        var child = await SpawnAsync(tiers, "agent-1", "Read", "Glob", "Bash").WaitAsync(TimeSpan.FromSeconds(5));
        var rotated = await tiers
            .OnCheckpointActivatedAsync(Activation(T, "root", 10, 9), CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        hung.Should().Be(2, "guard: the spawn and the rotation each reached the hanging PATCH");
        child
            .BuildContracts()
            .Select(c => c.Name)
            .Should()
            .BeEquivalentTo("Read", "Glob", "Bash", SearchTool, ExecuteTool);
        child
            .GetProviders()
            .OfType<IDeferredToolProvider>()
            .Should()
            .BeEmpty("the child runs the parent's ExecuteTool");
        rotated.Should().BeFalse();
        log.Starting("Capabilities apply failed")
            .Select(r => (string)r.Props["Error"]!)
            .Should()
            .SatisfyRespectively(
                spawn => spawn.Should().StartWith("sub-agent setup exceeded"),
                rotation => rotation.Should().StartWith("rotation exceeded")
            );

        async Task HangAsync(CancellationToken ct)
        {
            _ = Interlocked.Increment(ref hung);
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    // G6. A view that cannot be read is treated as absent: the build seeds the mode's view again (revision 1), and one
    // WARN names the thread, never the content. Red: drop ReadView's JsonException catch (the build goes inert), its
    // schema-version floor (an object that is not a view reads as an empty revision-0 view) or its WARN.
    [Theory]
    [InlineData("text that is not JSON")]
    [InlineData("a JSON string holding text that is not JSON")]
    [InlineData("a JSON array")]
    [InlineData("a JSON object that is not a view")]
    [InlineData("JSON null")]
    [InlineData("a string holding null")]
    [InlineData("a value that is neither a string nor JSON")]
    public async Task AnUnreadablePersistedView_IsSeededAgainFromTheMode_WithAWarnNamingOnlyTheThread(string junk)
    {
        object value = junk switch
        {
            "text that is not JSON" => "{junk-g6",
            "a JSON string holding text that is not JSON" => JsonSerializer.SerializeToElement("{junk-g6"),
            "a JSON array" => JsonSerializer.SerializeToElement(new[] { "junk-g6" }),
            "a JSON object that is not a view" => JsonSerializer.SerializeToElement(new { note = "junk-g6" }),
            "JSON null" => JsonSerializer.SerializeToElement<object?>(null),
            "a string holding null" => "null",
            _ => new Uri("https://junk-g6.test/"),
        };
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            T,
            new ThreadMetadata
            {
                ThreadId = T,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.Add(CapabilityTiers.ViewPropertyKey, value),
            }
        );
        var log = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, log);

        await BuildAsync(tiers, RotationSettings, new FakeSkillGateway().For(T), RotationTools);

        tiers.IsActive.Should().BeTrue();
        ListedTools(RootRegistry(tiers, RotationTools)).Should().BeEquivalentTo("Read", "Glob");
        using var view = JsonDocument.Parse((await RawViewAsync(store, T))!);
        view.RootElement.GetProperty("revision").GetInt32().Should().Be(1);
        var warn = log.Starting("Capabilities view of {ThreadId} is unreadable").Should().ContainSingle().Subject;
        warn.Level.Should().Be(LogLevel.Warning);
        warn.Props["ThreadId"].Should().Be(T);
        warn.Props.Values.Should().NotContain(v => v != null && v.ToString()!.Contains("junk-g6"));
    }

    /// <summary>The sandbox's tools as the gateway listed them on 2026-10-22, Skill aside (it is never tier-managed).</summary>
    private static readonly (string Name, string Description)[] SandboxTools =
    [
        ("Read", "Reads a file from the filesystem. Returns file contents with line numbers."),
        ("Write", "Writes content to a file. Overwrites existing file if present, unless append is true."),
        (
            "Edit",
            "Performs exact string replacements in files. The old_string must be unique in the file unless replace_all is true."
        ),
        (
            "Bash",
            "Executes a given bash command in a persistent shell session with optional timeout. Note that this tool creates a new instantiation every run, so environment variables are not preserved, but the working directory is preserved if specified/defaulted. Use TaskOutput to read output if run_in_background is true."
        ),
        (
            "PowerShell",
            "Executes a PowerShell (pwsh) command with checkpoint-based context persistence. The current working directory and environment variables are saved between calls within the same context_id and restored on the next call. State is persisted per-sandbox and dies with the sandbox. Use TaskOutput to read output if run_in_background is true. Concurrent calls with the same context_id are not supported and may race on the checkpoint file."
        ),
        ("TaskOutput", "Get output and status of a background shell task started in this sandbox session"),
        ("KillShell", "Kill a background shell task started in this sandbox session"),
        (
            "Glob",
            "Fast file pattern matching tool. Supports glob patterns like \"**/*.js\" or \"src/**/*.ts\". Returns matching file paths. Use this when you need to find files by name patterns."
        ),
        (
            "Grep",
            "A powerful search tool built on ripgrep. Supports full regex syntax, glob-based file filtering, context lines, and various output modes."
        ),
        (
            "math_eval",
            "Evaluate mathematical expressions using mathcore. Supports calculation, equation solving (single and systems), differentiation, and integration."
        ),
    ];

    private static FunctionContract Tool(string name, string description) =>
        new()
        {
            Name = name,
            Description = description,
            Parameters = [],
        };

    private static string[] HitRows(string text) =>
        [.. text.Split('\n').Where(line => line.Length > 0 && char.IsAsciiDigit(line[0]) && line.Contains(". "))];

    private static readonly TierSettings SubAgentToolSettings = new([], ["tool:Read"], ["tool:Glob"], [], 10, null, 5);

    private static readonly FunctionContract[] ManagedTools =
    [
        .. SandboxTools
            .Where(t => t.Name is "Read" or "Glob" or "Bash" or "Write")
            .Select(t => Tool(t.Name, t.Description)),
    ];

    private static readonly ToolHandler NoHandler = (_, _, _) =>
        throw new InvalidOperationException("no tool runs in these tests");

    private const string ExecuteTool = CapabilityTiers.ExecuteToolToolName;
    private const string BashCall = """{"tool":"Bash","arguments":{"command":"echo sc5"}}""";

    private static readonly TierSettings RotationSettings = new(["tool:Glob"], ["tool:Read"], [], [], 10, null, 2);

    private static readonly FunctionContract[] RotationTools =
    [
        .. SandboxTools
            .Where(t => t.Name is "Read" or "Glob" or "Bash" or "Write" or "Grep")
            .Select(t => Tool(t.Name, t.Description)),
    ];

    /// <summary>The root's registry as the host builds it: the tiers' provider, then the sandbox tools.</summary>
    private static FunctionRegistry RootRegistry(CapabilityTiers tiers, IEnumerable<FunctionContract> tools)
    {
        var root = new FunctionRegistry().AddProvider(tiers.RootProvider);
        foreach (var tool in tools)
        {
            _ = root.AddFunction(tool, NoHandler);
        }

        return root;
    }

    /// <summary>The sandbox tools a request of <paramref name="registry"/> lists.</summary>
    private static string[] ListedTools(FunctionRegistry registry) =>
        [.. registry.BuildContracts().Select(c => c.Name).Where(n => SandboxTools.Any(t => t.Name == n))];

    private static CheckpointActivation Activation(
        string thread,
        string agentId,
        long checkpointSeq,
        long lastMessageSeq,
        params (long Seq, string Tool, string Args)[] calls
    ) =>
        new(
            thread,
            agentId,
            $"cp-{checkpointSeq}",
            checkpointSeq,
            lastMessageSeq,
            [
                .. calls.Select(c =>
                    (
                        c.Seq,
                        new ToolCall
                        {
                            FunctionName = c.Tool,
                            FunctionArgs = c.Args,
                            ToolCallId = $"call-{c.Seq}",
                        }
                    )
                ),
            ]
        );

    /// <summary>
    /// A spawn as the library drives it: the override for each inherited tool in the parent's order (the named
    /// managed tools, then <c>SearchTool</c> and <c>ExecuteTool</c>), each provider it returns added to the child's
    /// registry and the parent's function in its place when it returns null.
    /// </summary>
    private static async Task<FunctionRegistry> SpawnAsync(
        CapabilityTiers tiers,
        string agentId,
        params string[] inherited
    )
    {
        var registry = new FunctionRegistry();
        var parentTools = tiers.RootProvider.GetFunctions().ToDictionary(f => f.Contract.Name);
        foreach (
            var (contract, handler) in ManagedTools
                .Where(t => inherited.Contains(t.Name))
                .Select(t => (t, NoHandler))
                .Concat(
                    new[] { SearchTool, CapabilityTiers.ExecuteToolToolName }.Select(n =>
                        (parentTools[n].Contract, parentTools[n].Handler)
                    )
                )
        )
        {
            var own = await tiers.ForInheritedToolAsync(new InheritedTool(agentId, contract, handler), default);
            _ = own is not null ? registry.AddProvider(own) : registry.AddFunction(contract, handler, "ParentTools");
        }

        return registry;
    }

    private static async Task<string> ChildSearchAsync(FunctionRegistry child, string args)
    {
        var (_, handlers) = child.Build();
        var result = await handlers[SearchTool](args, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
    }

    private static async Task<InMemoryConversationStore> StoreAsync()
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(T, new ThreadMetadata { ThreadId = T, LastUpdated = 1 });
        return store;
    }

    private static async Task<string?> RawViewAsync(InMemoryConversationStore store, string thread) =>
        (await store.LoadMetadataAsync(thread))?.Properties?.TryGetValue(CapabilityTiers.ViewPropertyKey, out var raw)
        == true
            ? (string)raw
            : null;

    /// <summary>One host build with tools tiered: stage, attach <paramref name="tools"/>, commit.</summary>
    private static async Task<CapabilityTiers> TiersAsync(params FunctionContract[] tools) =>
        await TiersAsync(new TierSettings([], [], [], [], 10, null, 5), await StoreAsync(), tools);

    private static async Task<CapabilityTiers> TiersAsync(
        TierSettings settings,
        InMemoryConversationStore store,
        FunctionContract[] tools
    )
    {
        var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        await BuildAsync(tiers, settings, new FakeSkillGateway().For(T), tools);
        tiers.IsActive.Should().BeTrue("guard: the build applied");
        return tiers;
    }

    /// <summary>One host build: stage, attach the tools the sandbox connect registered, commit.</summary>
    private static async Task BuildAsync(
        CapabilityTiers tiers,
        TierSettings settings,
        SkillTierGateway gateway,
        FunctionContract[] tools
    )
    {
        using var stage = await tiers.StageAsync(settings, gateway, CancellationToken.None);
        stage.AttachTools(tools);
        await stage.CommitAsync(CancellationToken.None);
    }

    private static async Task<ToolHandlerResultPayload> SearchAsync(CapabilityTiers tiers, string args)
    {
        var search = tiers.RootProvider.GetFunctions().Single(f => f.Contract.Name == SearchTool);
        var result = await search.Handler(args, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }
}
