using System.Diagnostics;
using System.Text.Json;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Whether the eval corpus survives a clean checkout.
/// </summary>
/// <remarks>
/// Every other test here works from files on disk, which is exactly the blind spot that let c2 ship
/// broken: its twelve access logs were present on the author's machine and excluded by the repository's
/// <c>**/logs/</c> rule, so they were never committed. Anyone else cloning the repo got an empty
/// <c>logs/</c> directory, the agent was asked to read files that were not there, and the checker scored
/// the outcome as a bad answer rather than a missing corpus. These tests therefore ask GIT what the
/// checkout contains, not the filesystem — a test that reads the disk passes in both worlds and proves
/// nothing.
/// </remarks>
public class EvalCorpusCheckoutTests
{
    private static string TasksDir => Path.Combine(RepoPaths.RepoRoot, "evals", "compaction-eval", "tasks");

    public static TheoryData<string> TasksDeclaringFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(TasksDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Required(dir).Count > 0)
            {
                data.Add(Path.GetFileName(dir));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TasksDeclaringFixtures))]
    public void EveryFixtureATaskDeclares_IsInTheCHECKOUT_NotJustOnDisk(string taskId)
    {
        var taskDir = Path.Combine(TasksDir, taskId);
        var tracked = TrackedFilesUnder(Path.Combine(taskDir, "fixtures"));

        foreach (var rule in Required(taskDir))
        {
            tracked
                .Count(p => RunWorkspace.MatchesGlob(p, rule.Glob))
                .Should()
                .Be(
                    rule.Count,
                    "task '{0}' declares {1} file(s) for '{2}', and a clean checkout has to carry them — "
                        + "check .gitignore before assuming they are there",
                    taskId,
                    rule.Count,
                    rule.Glob
                );
        }
    }

    [Fact]
    public void AtLeastOneTaskDeclaresFixtures()
    {
        // Non-vacuity. The theory above enumerates tasks that declare an inventory, so deleting every
        // declaration would leave it green with nothing to say.
        TasksDeclaringFixtures().Should().NotBeEmpty();
    }

    [Fact]
    public void C2_TheTaskThatBrokeThis_StillDeclaresItsTwelveLogs()
    {
        // The specific regression. c2's task text asks the agent to read twelve days of logs in full,
        // and its hidden answer keys were generated from those exact bytes, so twelve is a fact about
        // the corpus rather than a round number.
        Required(Path.Combine(TasksDir, "c2"))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new { Glob = "logs/*.log", Count = 12 });
    }

    private static IReadOnlyList<RequiredFixture> Required(string taskDir)
    {
        var metaPath = Path.Combine(taskDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return [];
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(
            File.ReadAllText(metaPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
        );
        return doc.RootElement.TryGetProperty("requiredFixtures", out var node)
            ? JsonSerializer.Deserialize<List<RequiredFixture>>(node.GetRawText(), options) ?? []
            : [];
    }

    /// <summary>
    /// The files git has under <paramref name="dir" />, as paths relative to it. <c>git ls-files</c> is
    /// the whole point: it answers "what would a fresh clone get", which is the question an ignored
    /// fixture gets wrong while <c>Directory.EnumerateFiles</c> answers it correctly on every machine
    /// that authored the fixture and wrongly everywhere else.
    /// </summary>
    private static IReadOnlyList<string> TrackedFilesUnder(string dir)
    {
        using var process =
            Process.Start(
                new ProcessStartInfo("git", ["-C", RepoPaths.RepoRoot, "ls-files", "--", dir])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            ) ?? throw new InvalidOperationException("git did not start.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "git ls-files failed: {0}", stderr);

        var relativeTo = Path.GetRelativePath(RepoPaths.RepoRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
        return
        [
            .. stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith(relativeTo + "/", StringComparison.Ordinal))
                .Select(l => l[(relativeTo.Length + 1)..]),
        ];
    }
}
