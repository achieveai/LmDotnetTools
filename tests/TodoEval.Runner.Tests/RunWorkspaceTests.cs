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
}
