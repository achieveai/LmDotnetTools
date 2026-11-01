extern alias Reviewd;

using System.Diagnostics;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
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

        var results = await RunRuleAsync(
            python!,
            "summary_reply",
            "c['thread'], c['posted'], set(c['summaryThreads'])",
            cases.ToJsonString()
        );

        results.Should().Equal(true, false, false, false, false);
    }

    [SkippableFact]
    public async Task Github_verify_skips_only_the_empty_review_that_wraps_a_reply()
    {
        // LmDotnetTools #803: each thread reply also made an empty COMMENTED review, the publisher listed it,
        // and verify refused the whole receipt as invalid. AgentWorkBench #83 did the same with the wrapper
        // of a new inline comment, listed as kind inline.
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's wrapper rule cannot be run");
        static JsonObject Entry(string type, string kind, string content) =>
            new()
            {
                ["commentType"] = type,
                ["commentId"] = 5,
                ["kind"] = kind,
                ["content"] = content,
            };
        var cases = new JsonArray(
            Entry("review", "reply", ""),
            Entry("review", "reply", "text"),
            Entry("review", "inline", ""),
            Entry("review", "inline", "text"),
            Entry("review", "summary", ""),
            Entry("review_comment", "reply", ""),
            Entry("issue_comment", "summary", "")
        );

        var results = await RunRuleAsync(python!, "github_reply_wrapper", "c", cases.ToJsonString());

        results.Should().Equal(true, false, true, false, false, false, false);
    }

    public void Dispose()
    {
        ReviewdSqlitePools.Clear(_database);
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

    /// <summary>Runs one of the sandbox script's own rules, lifted out of the embedded script.</summary>
    private static async Task<bool[]> RunRuleAsync(string python, string rule, string arguments, string cases)
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
                name, arguments = sys.argv[2], sys.argv[3]
                rule = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == name)
                scope = {}
                exec(compile(ast.Module([rule], []), name, 'exec'), scope)
                call = eval('lambda c: rule(' + arguments + ')', {'rule': scope[name]})
                cases = json.loads(sys.stdin.read())
                print(json.dumps([call(c) for c in cases]))
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
            start.ArgumentList.Add(rule);
            start.ArgumentList.Add(arguments);
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

    internal static async Task<string?> FindPythonAsync()
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
