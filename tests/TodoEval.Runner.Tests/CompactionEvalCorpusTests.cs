namespace TodoEval.Runner.Tests;

/// <summary>
/// The parser read against the REAL corpus in <c>evals/compaction-eval</c>. Every other task test
/// builds its own fixture and therefore proves only that the parser agrees with itself; this one
/// fails when the corpus and the runner drift apart, which is the way this breaks in practice.
/// </summary>
public class CompactionEvalCorpusTests
{
    /// <summary>The corpus directory, or null when the tests run outside a checkout that carries it.</summary>
    private static string? EvalDir()
    {
        for (var probe = new DirectoryInfo(AppContext.BaseDirectory); probe is not null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "evals", "compaction-eval");
            if (Directory.Exists(Path.Combine(candidate, "tasks")))
            {
                return candidate;
            }
        }

        return null;
    }

    [SkippableFact]
    public void TheDevSuiteLoadsWithItsSeedsFloorsAndCheckers()
    {
        var evalDir = EvalDir();
        Skip.If(evalDir is null, "evals/compaction-eval is not present in this checkout.");

        var assets = EvalAssets.Load(evalDir!, "compaction-eval", ["c1", "c2", "d1", "m1", "r1", "s1"]);

        assets.Tasks.Should().HaveCount(6);
        foreach (var task in assets.Tasks)
        {
            task.Template.Should().NotBeNullOrWhiteSpace($"task '{task.Id}' must have a user message");
            task.Meta.Should().NotBeNull($"task '{task.Id}' must ship a meta.json");
            task.Meta!.Seeds.Should().NotBeEmpty($"task '{task.Id}' must declare seed words");
            task.Meta.MinCompactions.Should().NotBeNull($"task '{task.Id}' must state a compaction floor");
            task.FixturesDir.Should().NotBeNull($"task '{task.Id}' must ship a workspace fixture tree");
            task.CheckScript.Should().NotBeNull($"task '{task.Id}' must ship a check.ps1");
        }
    }

    [SkippableFact]
    public void TheSteerTaskSplitsIntoTwoMessagesTheRunnerCanSend()
    {
        var evalDir = EvalDir();
        Skip.If(evalDir is null, "evals/compaction-eval is not present in this checkout.");

        var s1 = EvalAssets.Load(evalDir!, "compaction-eval", ["s1"]).Tasks.Single();

        s1.Steer.Should().NotBeNullOrWhiteSpace("s1 is the steer family: its correction is a second message");
        s1.Template.Should().NotContain("## steer", "the correction must not leak into the first message");
        s1.Meta!.SteerAfterSeconds.Should().NotBeNull("the runner needs to know when to send it");

        // The renderer has to accept this template as-is, or the whole suite is unrunnable.
        var rendered = TaskTemplateRenderer.Render(s1.Template, "a topic", s1.Meta.SeedForIndex(0));
        rendered.Should().NotContain("{SEED}").And.Contain(s1.Meta.SeedForIndex(0)!);
    }
}
