extern alias Reviewd;

using System.Diagnostics;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Persistence.Models;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     A re-review may post its summary as a reply in an earlier round's summary thread. Verify accepts such a reply
///     only in a thread an earlier verified round of the same PR recorded as its summary.
/// </summary>
public sealed class ReviewdSummaryReplyTests : IDisposable
{
    private static readonly RepoIdentity Repo = PrCommands.ParseRepo("org/project/Nova");

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-summary-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Prior_summary_threads_come_only_from_this_PRs_verified_summaries()
    {
        await PutAsync(
            $"reviewd/review-run/{Repo.NormalizedKey}/17",
            Round("org/project/Nova", "17", (7, "summary"), (9, "inline"), (8, "reply"))
        );
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            Round("org/project/Nova", "17", (11, "summary"))
        );
        await PutAsync("reviewd/attempt-history/a", Round("ORG/project/nova", "17", (3, "summary")));
        await PutAsync("reviewd/attempt-history/b", Round("org/project/Nova", "18", (50, "summary")));
        await PutAsync("reviewd/attempt-history/c", Round("org/project/Other", "17", (60, "summary")));

        using var connection = PrThreadBindings.Connection(_database);
        await connection.OpenAsync();
        var threads = await ReviewRunner.PriorSummaryThreadsAsync(connection, Repo, "17", CancellationToken.None);

        threads.Select(id => id!.GetValue<int>()).Should().Equal(3, 7, 11);
    }

    [SkippableFact]
    public async Task Only_a_reply_in_a_prior_summary_thread_counts_as_the_summary()
    {
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's summary rule cannot be run");
        var bot = new JsonObject { ["id"] = "bot" };
        JsonObject Thread(int id, JsonNode? context, string openerAuthor) =>
            new()
            {
                ["id"] = id,
                ["threadContext"] = context,
                ["comments"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = 1,
                        ["author"] = new JsonObject { ["id"] = openerAuthor },
                    },
                    new JsonObject { ["id"] = 2, ["author"] = bot.DeepClone() }
                ),
            };
        JsonObject Case(JsonObject thread, int postedId) =>
            new()
            {
                ["thread"] = thread,
                ["posted"] = thread["comments"]![postedId - 1]!.DeepClone(),
                ["summaryThreads"] = new JsonArray(7),
            };
        var cases = new JsonArray(
            Case(Thread(7, null, "bot"), 2),
            // The defect: a reply in the reviewer's own general finding thread passed as the summary.
            Case(Thread(8, null, "bot"), 2),
            Case(Thread(7, new JsonObject { ["filePath"] = "/a.cs" }, "bot"), 2),
            Case(Thread(7, null, "someone"), 2),
            Case(Thread(7, null, "bot"), 1)
        );

        var results = await RunRuleAsync(python!, cases.ToJsonString());

        results.Should().Equal(true, false, false, false, false);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_database) + "*"))
            File.Delete(file);
    }

    private static JsonObject Round(string repository, string pr, params (int Thread, string Kind)[] comments) =>
        new()
        {
            ["repository"] = repository,
            ["prId"] = pr,
            ["verifiedPublication"] = new JsonObject
            {
                ["comments"] = new JsonArray([
                    .. comments.Select(comment =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["threadId"] = comment.Thread,
                                ["commentId"] = 1,
                                ["kind"] = comment.Kind,
                            }
                    ),
                ]),
            },
        };

    private async Task PutAsync(string key, JsonNode value)
    {
        using var connection = PrThreadBindings.Connection(_database);
        await new SqliteKvStore(connection).SetAsync(key, value);
    }

    /// <summary>Runs the sandbox script's own <c>summary_reply</c>, lifted out of the embedded script.</summary>
    private static async Task<bool[]> RunRuleAsync(string python, string cases)
    {
        var script = Path.Combine(Path.GetTempPath(), $"reviewd-sandbox-{Guid.NewGuid():N}.py");
        using (
            var resource = typeof(ReviewRunner).Assembly.GetManifestResourceStream(
                "Reviewd.Sample.ReviewRunnerSandbox.py"
            )!
        )
        using (var file = File.Create(script))
            await resource.CopyToAsync(file);
        try
        {
            const string harness = """
                import ast, json, sys
                tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
                rule = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'summary_reply')
                scope = {}
                exec(compile(ast.Module([rule], []), 'summary_reply', 'exec'), scope)
                cases = json.loads(sys.stdin.read())
                print(json.dumps([scope['summary_reply'](c['thread'], c['posted'], set(c['summaryThreads'])) for c in cases]))
                """;
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(harness);
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(cases);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, error);
            return [.. JsonNode.Parse(output)!.AsArray().Select(result => result!.GetValue<bool>())];
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static async Task<string?> FindPythonAsync()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo(candidate, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                )!;
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                    return candidate;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH; try the next name.
            }
        }
        return null;
    }
}
