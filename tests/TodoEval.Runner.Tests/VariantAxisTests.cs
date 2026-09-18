using System.Text.RegularExpressions;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The variant axis: what a variant may be, how its option-set reaches the child process, and the
/// promise that a sweep configuring none behaves exactly as it did before the axis existed.
/// </summary>
public class VariantAxisTests
{
    // ── configuration ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_YieldsTheSingleUntouchedDefaultVariant()
    {
        var config = EvalRunnerConfig.Load(configPath: null);

        var variant = config.Variants.Should().ContainSingle().Subject;
        variant.Name.Should().Be(VariantConfig.DefaultName);
        variant.IsDefault.Should().BeTrue("a sweep that configures no variant must launch the host unmodified");
        config.Tasks.Should().BeNull("no task axis means the single evalDir/task.md layout");
    }

    [Fact]
    public void Validate_RejectsAnEmptyVariantList()
    {
        var act = new EvalRunnerConfig { Variants = [] }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*non-empty*");
    }

    [Fact]
    public void Validate_RejectsABlankVariantName()
    {
        var act = new EvalRunnerConfig { Variants = [new VariantConfig { Name = "  " }] }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*non-blank*");
    }

    [Fact]
    public void Validate_RejectsDuplicateVariantNames()
    {
        // Two option-sets under one name would share a run key and a manifest row, so the archive
        // could no longer say which host produced which run.
        var act = new EvalRunnerConfig
        {
            Variants = [new VariantConfig { Name = "off" }, new VariantConfig { Name = "off" }],
        }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsABlankTaskId(string id)
    {
        var act = new EvalRunnerConfig { Tasks = [id] }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*non-blank*");
    }

    [Fact]
    public void Validate_RejectsDuplicateTaskIds()
    {
        var act = new EvalRunnerConfig { Tasks = ["c1", "c1"] }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicates*");
    }

    [Theory]
    [InlineData("../todo-eval")]
    [InlineData("nested/task")]
    [InlineData("nested\\task")]
    public void Validate_RejectsATaskIdThatWouldEscapeTheEvalCorpus(string id)
    {
        // A task id becomes a path segment under {evalDir}/tasks/, and the fingerprints pin only the
        // corpus inside evalDir. An id that climbs out would score against assets nothing recorded.
        var act = new EvalRunnerConfig { Tasks = [id] }.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*single path segment*");
    }

    [Fact]
    public void Validate_AcceptsTheSingleTaskLayout()
    {
        var act = new EvalRunnerConfig { Tasks = [] }.Validate;

        act.Should().NotThrow("an empty task list is the pre-axis layout, not a misconfiguration");
    }

    // ── the option-set reaching the child ────────────────────────────────────────────────────────

    [Fact]
    public void For_AppendsTheVariantsArgumentsAfterTheSharedOnes()
    {
        var host = new HostConfig { ExtraArgs = ["--Shared:Key=1", "--Compaction:Mode=Off"] };

        var merged = host.For(new VariantConfig { Name = "on", ExtraArgs = ["--Compaction:Mode=Compact"] });

        merged.ExtraArgs.Should().Equal("--Shared:Key=1", "--Compaction:Mode=Off", "--Compaction:Mode=Compact");
    }

    [Fact]
    public void BuildStartInfo_PassesTheVariantArgumentLast_SoTheVariantWins()
    {
        // ASP.NET command-line configuration takes the LAST value for a key, so argument order is the
        // whole mechanism by which a variant overrides the sweep-wide host block.
        var host = new HostConfig { ExtraArgs = ["--Compaction:Mode=Off"] }.For(
            new VariantConfig { Name = "on", ExtraArgs = ["--Compaction:Mode=Compact"] }
        );

        var args = EvalHostProcess.BuildStartInfo(host, Path.GetTempPath(), "host.dll", 54321).ArgumentList;

        args.IndexOf("--Compaction:Mode=Compact")
            .Should()
            .BeGreaterThan(args.IndexOf("--Compaction:Mode=Off"), "the later argument is the one the host binds");
    }

    [Fact]
    public void For_OverwritesASharedEnvironmentVariableWithTheVariants()
    {
        var host = new HostConfig
        {
            ExtraEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Compaction__Mode"] = "Off",
                ["Shared__Key"] = "1",
            },
        };

        var merged = host.For(
            new VariantConfig
            {
                Name = "on",
                ExtraEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Compaction__Mode"] = "Compact",
                },
            }
        );

        merged.ExtraEnv["Compaction__Mode"].Should().Be("Compact");
        merged
            .ExtraEnv["Shared__Key"]
            .Should()
            .Be("1", "a variant specialises the shared block, it does not replace it");
    }

    [Fact]
    public void For_DoesNotMutateTheSharedHostConfig()
    {
        // The same HostConfig is specialised once per variant, so a merge that wrote through would
        // make every variant after the first inherit its predecessor's arguments.
        var host = new HostConfig { ExtraArgs = ["--Shared:Key=1"] };

        _ = host.For(new VariantConfig { Name = "a", ExtraArgs = ["--A=1"] });
        var second = host.For(new VariantConfig { Name = "b", ExtraArgs = ["--B=1"] });

        host.ExtraArgs.Should().Equal("--Shared:Key=1");
        second.ExtraArgs.Should().Equal("--Shared:Key=1", "--B=1");
    }

    // ── the sandbox toggle ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildStartInfo_SandboxOff_PinsAutoSpawnFalse()
    {
        var args = EvalHostProcess.BuildStartInfo(new HostConfig(), Path.GetTempPath(), "host.dll", 54321).ArgumentList;

        args.Should().Contain("--SandboxGateway:AutoSpawn=false", "the todo-eval's task needs no file or shell tool");
    }

    [Fact]
    public void BuildStartInfo_SandboxOn_OmitsThePinEntirely()
    {
        // Omitted rather than set to true: the host's own configuration — which carries the gateway
        // and agent-cli paths through ExtraArgs — is what decides, and a pin here would fight it.
        var host = new HostConfig { Sandbox = true, ExtraArgs = ["--SandboxGateway:AutoSpawn=true"] };

        var args = EvalHostProcess.BuildStartInfo(host, Path.GetTempPath(), "host.dll", 54321).ArgumentList;

        args.Should().NotContain("--SandboxGateway:AutoSpawn=false");
        args.Should().Contain("--SandboxGateway:AutoSpawn=true");
    }

    // ── run keys ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MakeRunKey_DefaultVariantAndNoTask_IsThePreAxisKey()
    {
        // The legacy promise: a sweep that configures neither axis writes the keys every archived
        // baseline already carries, so the two stay diffable.
        RunManifestEntry.MakeRunKey(VariantConfig.Default, taskId: null, "m1", 3).Should().Be("m1/seed3");
    }

    [Fact]
    public void MakeRunKey_NamedVariantAndTask_QualifiesTheCell()
    {
        var variant = new VariantConfig { Name = "compact-v0" };

        RunManifestEntry.MakeRunKey(variant, "c1", "m1", 0).Should().Be("compact-v0/c1/m1/seed0");
    }

    [Fact]
    public void MakeRunKey_IsUniquePerCell()
    {
        var variants = new[]
        {
            VariantConfig.Default,
            new VariantConfig { Name = "compact-v0" },
        };
        string[] tasks = ["c1", "d1"];

        var keys =
            from variant in variants
            from task in tasks
            from seed in new[] { 0, 1 }
            select RunManifestEntry.MakeRunKey(variant, task, "m1", seed);

        keys.Should().OnlyHaveUniqueItems();
    }

    // ── the committed example config ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheCommittedExampleConfig_LoadsAndValidates()
    {
        // A shipped example that no longer parses is worse than none: the first thing an operator
        // does with it is point --config at it.
        var path = Path.Combine(RepoPaths.RepoRoot, "evals", "compaction-eval", "runner.example.jsonc");

        var config = EvalRunnerConfig.Load(path);

        config.Variants.Select(v => v.Name).Should().Equal("off", "compact-v0");
        config.Tasks.Should().NotBeNullOrEmpty();
        config.Host.Sandbox.Should().BeTrue("a coding eval needs the sandbox gateway the todo-eval pins off");
        config.Models.Should().Equal("gpt-5.6-terra");
    }

    /// <summary>
    ///     A forwarded host argument whose value is an absolute path pointing into a source checkout.
    /// </summary>
    private const string CheckoutPathInForwardedArg =
        @"--[^""]*=[A-Za-z]:[\\/][^""]*(?:LmDotnetTools|worktrees|[\\/]evals[\\/])";

    [Fact]
    public void PathTokens_ExpandToAbsolutePathsUnderTheCurrentCheckout()
    {
        // The host launches with its working directory set to a scratch instance dir, so a relative
        // path in extraArgs resolves somewhere the repository is not. The token has to come out
        // absolute, or the host's File.ReadAllText fails on a path that looks fine in the config.
        var config = new EvalRunnerConfig
        {
            EvalDir = Path.Combine("evals", "compaction-eval"),
            Host = new HostConfig { ExtraArgs = ["--Compaction:SummaryPromptPath={evalDir}/prompts/v1.md"] },
            Variants =
            [
                new VariantConfig
                {
                    Name = "v",
                    ExtraArgs = ["--Some:Path={repoRoot}/scripts/ci-test.ps1"],
                    ExtraEnv = new Dictionary<string, string> { ["SOME_PATH"] = "{evalDir}/mode.json" },
                },
            ],
        }.ResolvePathTokens();

        var expanded = config.Host.ExtraArgs[0];
        expanded.Should().NotContain("{evalDir}");
        Path.IsPathRooted(expanded.Split('=', 2)[1]).Should().BeTrue("the child resolves it from a scratch directory");
        expanded.Should().Contain("compaction-eval").And.EndWith("prompts/v1.md");
        config.Variants[0].ExtraArgs[0].Should().NotContain("{repoRoot}");
        config.Variants[0].ExtraEnv["SOME_PATH"].Should().NotContain("{evalDir}");
    }

    [Fact]
    public void TheCommittedConfigs_CarryNoPathFromTheAuthorsMachine()
    {
        // The eight summary-prompt arguments were absolute paths into one worktree, so every variant
        // failed anywhere else. Asserting on the raw text rather than on a loaded config, because the
        // loader is what expands the token and would hide a re-introduced literal.
        //
        // Scoped to paths that point inside a source checkout. SandboxGateway:WorkspaceBasePath is
        // deliberately NOT covered: it names a sandbox mount outside the repository, which every
        // operator sets for their own machine and runner.example.jsonc documents. The rule is narrowed
        // rather than that one line exempted, so a newly added checkout path still fails here.
        foreach (var name in new[] { "runner.jsonc", "runner.example.jsonc" })
        {
            var text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "evals", "compaction-eval", name));

            Regex
                .Matches(text, CheckoutPathInForwardedArg)
                .Should()
                .BeEmpty($"{name} must not forward a path from the author's checkout; use the evalDir token");
        }
    }

    [Fact]
    public void TheCommittedSweepConfig_ResolvesEverySummaryPromptItNames()
    {
        // The token is only worth anything if what it expands to is really there.
        var config = EvalRunnerConfig.Load(
            Path.Combine(RepoPaths.RepoRoot, "evals", "compaction-eval", "runner.jsonc")
        );

        var prompts = config
            .Variants.SelectMany(v => v.ExtraArgs)
            .Where(a => a.StartsWith("--Compaction:SummaryPromptPath=", StringComparison.Ordinal))
            .Select(a => a.Split('=', 2)[1])
            .ToList();

        prompts.Should().NotBeEmpty("the sweep config is the one that varies the prompt");
        prompts.Should().OnlyContain(p => File.Exists(p));
    }

    [Fact]
    public void TheCommittedExampleConfig_NamesTasksThatExistOnDisk()
    {
        var evalDir = Path.Combine(RepoPaths.RepoRoot, "evals", "compaction-eval");
        var config = EvalRunnerConfig.Load(Path.Combine(evalDir, "runner.example.jsonc"));

        var act = () => EvalAssets.Load(evalDir, config.ModeName, config.Tasks);

        act.Should().NotThrow("the example must run as shipped, so each id it names needs its task.md");
    }
}

/// <summary>
/// <c>--variants</c> and <c>--tasks</c>. Both narrow what the config declares; a name that narrows to
/// nothing is an error, because a typo would otherwise sweep a different experiment and still exit 0.
/// </summary>
public class VariantCliOptionsTests
{
    private static EvalRunnerConfig Configured() =>
        new()
        {
            Variants =
            [
                new VariantConfig { Name = "off", ExtraArgs = ["--Compaction:Mode=Off"] },
                new VariantConfig { Name = "compact-v0", ExtraArgs = ["--Compaction:Mode=Compact"] },
            ],
            Tasks = ["c1", "d1"],
        };

    [Fact]
    public void Parse_ReadsBothListSwitches()
    {
        var options = CliOptions.Parse(["--variants", "off, compact-v0", "--tasks", "c1"]);

        options.Variants.Should().Equal("off", "compact-v0");
        options.Tasks.Should().Equal("c1");
    }

    [Fact]
    public void ApplyTo_NeitherSwitch_KeepsEveryConfiguredCell()
    {
        var merged = CliOptions.Parse([]).ApplyTo(Configured());

        merged.Variants.Select(v => v.Name).Should().Equal("off", "compact-v0");
        merged.Tasks.Should().Equal("c1", "d1");
    }

    [Fact]
    public void ApplyTo_VariantsSwitch_KeepsTheNamedVariantsWithTheirConfiguredArguments()
    {
        var merged = CliOptions.Parse(["--variants", "compact-v0"]).ApplyTo(Configured());

        var variant = merged.Variants.Should().ContainSingle().Subject;
        variant.Name.Should().Be("compact-v0");
        // A filter selects; it never redefines. The surviving variant keeps the arguments it was configured with.
        variant.ExtraArgs.Should().Equal("--Compaction:Mode=Compact");
    }

    [Fact]
    public void ApplyTo_UnknownVariantName_Throws()
    {
        var act = () => CliOptions.Parse(["--variants", "compact-v1"]).ApplyTo(Configured());

        act.Should().Throw<InvalidOperationException>().WithMessage("*compact-v1*");
    }

    [Fact]
    public void ApplyTo_TasksSwitch_FiltersTheConfiguredTasks()
    {
        var merged = CliOptions.Parse(["--tasks", "d1"]).ApplyTo(Configured());

        merged.Tasks.Should().Equal("d1");
    }

    [Fact]
    public void ApplyTo_UnknownTaskName_Throws()
    {
        var act = () => CliOptions.Parse(["--tasks", "z9"]).ApplyTo(Configured());

        act.Should().Throw<InvalidOperationException>().WithMessage("*z9*");
    }

    [Fact]
    public void ApplyTo_TasksSwitch_SetsTheAxisWhenTheConfigDeclaresNone()
    {
        // Unlike a variant, a task id needs no configuration to mean something: it resolves straight
        // to {evalDir}/tasks/{id}/, so --eval-dir X --tasks a,b is a complete instruction.
        var merged = CliOptions.Parse(["--tasks", "c1,d1"]).ApplyTo(new EvalRunnerConfig());

        merged.Tasks.Should().Equal("c1", "d1");
    }
}
