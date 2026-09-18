using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The directory one run works in: the name it gets (which the host has to agree with) and what is
/// copied into it (which decides whether the agent can read the checker's answer key).
/// </summary>
public class RunWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"todo-eval-ws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string TaskDirWithFixtures()
    {
        var taskDir = Path.Combine(_root, "task");
        var fixtures = Path.Combine(taskDir, "fixtures");
        Directory.CreateDirectory(Path.Combine(fixtures, "data"));
        Directory.CreateDirectory(Path.Combine(taskDir, "hidden"));
        File.WriteAllText(Path.Combine(fixtures, "README.md"), "read me");
        File.WriteAllText(Path.Combine(fixtures, "data", "sample.csv"), "a,b\n1,2\n");
        File.WriteAllText(Path.Combine(taskDir, "hidden", "expected.json"), """{"answer":42}""");
        return taskDir;
    }

    // ── the directory name ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void LeafFor_FoldsTheRunKeyIntoOneSegment()
    {
        // The host stores a workspace as a single sanitized leaf under its gateway base path, so a
        // key with its slashes intact would land somewhere neither side could name.
        RunWorkspace.LeafFor("compact-v0/c1/gpt-5.6-terra/seed0").Should().Be("compact-v0-c1-gpt-5.6-terra-seed0");
    }

    [Fact]
    public void LeafFor_SurvivesTheHostsOwnSanitizer()
    {
        // FileWorkspaceStore.SanitizeDirectory lowercases, collapses whitespace to '-', strips
        // separators and '..'. Applying it to our leaf must be a no-op, or the runner would fill one
        // directory while the agent mounted another.
        var leaf = RunWorkspace.LeafFor("Compact V0/C1/Model X/seed3");

        leaf.Should().Be(HostSanitize(leaf), "the leaf has to be a fixed point of the host's sanitizer");
        leaf.Should().NotContain(" ").And.NotContain("/").And.NotContain("\\");
        leaf.Should().Be(leaf.ToLowerInvariant());
    }

    /// <summary>A transcription of <c>FileWorkspaceStore.SanitizeDirectory</c> (LmStreaming.Sample).</summary>
    private static string HostSanitize(string raw)
    {
        var lowered = raw.Trim().ToLowerInvariant();
        var collapsed = string.Join('-', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var sanitized = new string([.. collapsed.Where(c => !invalid.Contains(c))]);
        return sanitized.Replace("..", string.Empty).Trim('-');
    }

    [Fact]
    public void LeafFor_KeyWithNothingUsable_Throws()
    {
        var act = () => RunWorkspace.LeafFor("///");

        act.Should().Throw<InvalidOperationException>();
    }

    // ── what lands in it ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prepare_CopiesTheFixtureTreeAndNothingElse()
    {
        var taskDir = TaskDirWithFixtures();

        var path = RunWorkspace.Prepare(
            Path.Combine(_root, "workspaces"),
            "off/c1/m1/seed0",
            Path.Combine(taskDir, "fixtures")
        );

        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(path, "data", "sample.csv")).Should().BeTrue("nested fixture directories come too");
    }

    [Fact]
    public void Prepare_NeverCopiesTheHiddenDirectory()
    {
        // hidden/ holds the checker's tests and answer keys. An agent that can read them can pass the
        // checker without doing the task, which would make every J1 score meaningless.
        var taskDir = TaskDirWithFixtures();

        var path = RunWorkspace.Prepare(
            Path.Combine(_root, "workspaces"),
            "off/c1/m1/seed0",
            Path.Combine(taskDir, "fixtures")
        );

        Directory.Exists(Path.Combine(path, "hidden")).Should().BeFalse();
        Directory
            .EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Should()
            .NotContain(f => f.Contains("expected.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Prepare_LandsUnderTheConfiguredRootWithTheRunKeysLeaf()
    {
        var taskDir = TaskDirWithFixtures();
        var workspacesRoot = Path.Combine(_root, "workspaces");

        var path = RunWorkspace.Prepare(workspacesRoot, "compact-v0/c1/m1/seed2", Path.Combine(taskDir, "fixtures"));

        path.Should().Be(Path.GetFullPath(Path.Combine(workspacesRoot, "compact-v0-c1-m1-seed2")));
    }

    [Fact]
    public void Prepare_RefusesADirectoryAnotherRunAlreadyFilled()
    {
        // Silently copying over it would judge a mixture of two runs and report the score as one.
        var taskDir = TaskDirWithFixtures();
        var workspacesRoot = Path.Combine(_root, "workspaces");
        _ = RunWorkspace.Prepare(workspacesRoot, "off/c1/m1/seed0", Path.Combine(taskDir, "fixtures"));

        var act = () => RunWorkspace.Prepare(workspacesRoot, "off/c1/m1/seed0", Path.Combine(taskDir, "fixtures"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    // ── the declared inventory ───────────────────────────────────────────────────────

    [Fact]
    public void Prepare_AFixtureTheTaskDeclaredIsMissing_FailsTheRun()
    {
        // The c2 failure, reduced. Its twelve logs were never committed, so a clean checkout prepared an
        // EMPTY log directory and nothing complained: the agent was asked to read files that did not
        // exist and the checker scored the result as a bad answer. An absent input has to stop the run,
        // because a run against it is not measuring the task.
        var taskDir = TaskDirWithFixtures();

        var act = () =>
            RunWorkspace.Prepare(
                Path.Combine(_root, "workspaces"),
                "off/c1/m1/seed0",
                Path.Combine(taskDir, "fixtures"),
                [new RequiredFixture { Glob = "logs/*.log", Count = 12 }]
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'logs/*.log' matched 0 file(s), expected 12*")
            .WithMessage("*.gitignore*", "the message has to name the way this actually happens");
    }

    [Fact]
    public void Prepare_TheWrongNUMBEROfFilesAlsoFailsTheRun()
    {
        // A partial corpus is the harder case: the task still runs and still produces an answer, which
        // is then judged against a key built from all twelve days.
        var taskDir = TaskDirWithFixtures();
        var logs = Path.Combine(taskDir, "fixtures", "logs");
        _ = Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "2026-03-02.log"), "# note\n");
        File.WriteAllText(Path.Combine(logs, "2026-03-03.log"), "# note\n");

        var act = () =>
            RunWorkspace.Prepare(
                Path.Combine(_root, "workspaces"),
                "off/c1/m1/seed0",
                Path.Combine(taskDir, "fixtures"),
                [new RequiredFixture { Glob = "logs/*.log", Count = 12 }]
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*matched 2 file(s), expected 12*");
    }

    [Fact]
    public void Prepare_ASatisfiedInventoryIsNotAnError()
    {
        // The non-vacuity check on the guard: it has to be able to pass, or every run fails and the
        // declaration says nothing.
        var taskDir = TaskDirWithFixtures();
        var logs = Path.Combine(taskDir, "fixtures", "logs");
        _ = Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");
        File.WriteAllText(Path.Combine(logs, "b.log"), "y");

        var path = RunWorkspace.Prepare(
            Path.Combine(_root, "workspaces"),
            "off/c1/m1/seed0",
            Path.Combine(taskDir, "fixtures"),
            [new RequiredFixture { Glob = "logs/*.log", Count = 2 }]
        );

        Directory.EnumerateFiles(Path.Combine(path, "logs")).Should().HaveCount(2);
    }

    [Fact]
    public void Prepare_AGlobsStarDoesNotCrossADirectoryBoundary()
    {
        // "*.log" asserts a log at the ROOT of the workspace. If its star crossed a separator it would
        // be satisfied by logs/a.log, and a glob could no longer assert a layout at all.
        var taskDir = TaskDirWithFixtures();
        var logs = Path.Combine(taskDir, "fixtures", "logs");
        _ = Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");

        var act = () =>
            RunWorkspace.Prepare(
                Path.Combine(_root, "workspaces"),
                "off/c1/m1/seed0",
                Path.Combine(taskDir, "fixtures"),
                [new RequiredFixture { Glob = "*.log", Count = 1 }]
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*matched 0 file(s)*");
    }
}
