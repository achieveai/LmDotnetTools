namespace TodoEval.Runner.Tests;

/// <summary>
/// Locates the repo's real eval assets from a test binary. The coordination fixture, the reference
/// oracle and the corpus files are shared by BOTH scorers, so the tests read the committed
/// originals rather than a copied snapshot that could silently drift from them.
/// </summary>
internal static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string EvalDir => Path.Combine(RepoRoot, "evals", "todo-eval");

    public static string ScoreScript => Path.Combine(EvalDir, "score.ps1");

    public static string Fixture(string name) => Path.Combine(EvalDir, "fixtures", name);

    public static string FixtureConversations(string name) => Path.Combine(Fixture(name), "conversations");

    private static string FindRepoRoot()
    {
        for (var probe = new DirectoryInfo(AppContext.BaseDirectory); probe is not null; probe = probe.Parent)
        {
            if (Directory.Exists(Path.Combine(probe.FullName, "evals", "todo-eval")))
            {
                return probe.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No ancestor of '{AppContext.BaseDirectory}' contains evals/todo-eval; the tests cannot find the eval assets."
        );
    }
}
