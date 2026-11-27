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

    [SkippableFact]
    public async Task Verify_requires_the_full_reviewed_head_in_a_summary_posted_after_closure()
    {
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox verifier cannot be run");
        var reviewed = new string('a', 40);
        const string closed = "2026-01-15T12:00:00Z";
        const string late = "2026-01-15T12:00:01Z";
        JsonObject Case(string body, string posted, string? closedAt = closed) =>
            new()
            {
                ["head"] = reviewed,
                ["closedAt"] = closedAt,
                ["comments"] = new JsonArray(
                    new JsonObject
                    {
                        ["commentType"] = "issue_comment",
                        ["commentId"] = 5,
                        ["kind"] = "summary",
                        ["content"] = body,
                    }
                ),
                ["posted"] = new JsonObject
                {
                    ["id"] = 5,
                    ["issue_url"] = "https://api.github.test/repos/org/Nova/issues/17",
                    ["body"] = body,
                    ["created_at"] = posted,
                },
            };
        var cases = new JsonArray(
            Case($"REQUEST_CHANGES\n\nReviewed commit `{reviewed}`.", late),
            Case("REQUEST_CHANGES", late),
            Case($"REQUEST_CHANGES\n\nReviewed commit {reviewed[..8]}.", late),
            Case($"REQUEST_CHANGES\n\nReviewed commit {new string('b', 40)}.", late),
            Case("APPROVE", "2026-01-15T11:59:59Z"),
            Case("APPROVE", closed),
            Case("APPROVE", late, null)
        );

        var results = await RunAsync(
            python!,
            [
                "GITHUB_COMMENTS",
                "github_belongs",
                "github_reply_wrapper",
                "same_text",
                "summary_head_matches",
                "github_verify",
            ],
            "passes(lambda: github_verify(c['comments'], lambda path: c['posted'], [], c['closedAt'], c['head']))",
            cases.ToJsonString()
        );

        results.Should().Equal(true, false, false, false, true, true, true);
    }

    [SkippableFact]
    public async Task Ado_verify_keeps_a_preclosure_summary_when_a_late_reply_corrects_its_verdict()
    {
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox verifier cannot be run");
        var reviewed = new string('a', 40);
        var combined = JsonNode
            .Parse(
                """
                {"thread":{"id":7,"comments":[
                  {"id":1,"author":{"id":"reviewer"}},
                  {"id":2,"author":{"id":"reviewer"},"parentCommentId":1,"publishedDate":"2026-01-15T11:59:59Z","content":"APPROVE"},
                  {"id":3,"author":{"id":"reviewer"},"parentCommentId":2,"publishedDate":"2026-01-15T12:00:01Z","content":"REQUEST_CHANGES"}
                ]},"comments":[
                  {"threadId":7,"commentId":3,"kind":"reply","content":"REQUEST_CHANGES"},
                  {"threadId":7,"commentId":2,"kind":"reply","content":"APPROVE"}
                ]}
                """
            )!
            .AsObject();
        var onlyCorrection = combined.DeepClone().AsObject();
        onlyCorrection["comments"]!.AsArray().RemoveAt(1);
        var lateSummary = onlyCorrection.DeepClone().AsObject();
        lateSummary["thread"]!["comments"]![2]!["content"] = $"REQUEST_CHANGES\nReviewed commit {reviewed}";
        lateSummary["comments"]![0]!["content"] = $"REQUEST_CHANGES\nReviewed commit {reviewed}";

        var verified = await RunAsync(
            python!,
            ["summary_reply", "summary_head_matches", "ado_verify"],
            $"passes(lambda: ado_verify(c['comments'], lambda tid: c['thread'], [[7, 1]], {{7}}, '2026-01-15T12:00:00Z', '{reviewed}'))",
            new JsonArray(combined, onlyCorrection, lateSummary).ToJsonString()
        );
        verified.Should().Equal(true, false, true);
    }

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
    public async Task Github_verify_skips_only_the_empty_review_that_wraps_a_reply_or_inline_comment()
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

    [SkippableFact]
    public async Task Github_verify_requires_the_comment_each_skipped_wrapper_holds()
    {
        // LmDotnetTools #910: verify skipped an empty inline wrapper without looking for the inline comment it
        // holds, so a receipt whose inline comment was never posted still passed on its summary. GitHub gives that
        // comment the wrapper's id as its pull_request_review_id.
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's verify rule cannot be run");
        static JsonObject Entry(string type, int id, string kind, string content) =>
            new()
            {
                ["commentType"] = type,
                ["commentId"] = id,
                ["kind"] = kind,
                ["content"] = content,
            };
        static JsonObject Case(JsonObject live, params JsonObject[] comments)
        {
            live["issues/comments/1"] = new JsonObject
            {
                ["id"] = 1,
                ["body"] = "summary",
                ["issue_url"] = "https://api.github.com/repos/o/r/issues/17",
            };
            return new JsonObject { ["comments"] = new JsonArray([.. comments]), ["live"] = live };
        }
        static JsonObject Live(int reviewId) =>
            new()
            {
                ["pulls/comments/600"] = new JsonObject
                {
                    ["id"] = 600,
                    ["body"] = "finding",
                    ["pull_request_url"] = "https://api.github.com/repos/o/r/pulls/17",
                    ["pull_request_review_id"] = reviewId,
                },
            };
        static JsonObject Summary() => Entry("issue_comment", 1, "summary", "summary");
        var cases = new JsonArray(
            // The #908 case: the wrapper and the inline comment it holds.
            Case(
                Live(500),
                Summary(),
                Entry("review", 500, "inline", ""),
                Entry("review_comment", 600, "inline", "finding")
            ),
            // The #910 defect: the wrapper alone, its inline comment lost.
            Case([], Summary(), Entry("review", 500, "inline", "")),
            // An inline comment that belongs to another review does not stand for this wrapper.
            Case(
                Live(501),
                Summary(),
                Entry("review", 500, "inline", ""),
                Entry("review_comment", 600, "inline", "finding")
            ),
            // A thread reply's wrapper holds the reply the same way.
            Case(
                Live(500),
                Summary(),
                Entry("review", 500, "reply", ""),
                Entry("review_comment", 600, "reply", "finding")
            ),
            Case([], Summary(), Entry("review", 500, "reply", ""))
        );

        var results = await RunAsync(
            python!,
            ["GITHUB_COMMENTS", "github_belongs", "github_reply_wrapper", "same_text", "github_verify"],
            "passes(lambda: github_verify(c['comments'], c['live'].get, []))",
            cases.ToJsonString()
        );

        results.Should().Equal(true, false, false, true, false);
    }

    [SkippableFact]
    public async Task Verify_takes_the_receipts_revision_from_the_runner_and_its_round_from_the_comments()
    {
        // AWB#188 (#1006): a long-lived conversation copied an earlier round's mergeSha into the receipt, and verify
        // refused this round's posted review on every retry. Verify now reads the receipt with the runner's prepared
        // revision in place of the copied one, and writes nothing. A receipt from an earlier round still fails,
        // because its comments are in this round's baseline.
        var python = await FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's verify rule cannot be run");
        string head = new('h', 40),
            merge = new('m', 40),
            earlierHead = new('d', 40),
            earlierMerge = new('3', 40);
        static JsonObject Comment(int id) =>
            new()
            {
                ["commentType"] = "issue_comment",
                ["commentId"] = id,
                ["kind"] = "summary",
                ["content"] = $"summary {id}",
            };
        static JsonObject Receipt(
            string head,
            string merge,
            JsonObject comment,
            string? continuedIn = null,
            string prId = "17"
        )
        {
            var receipt = new JsonObject
            {
                ["prId"] = prId,
                ["headSha"] = head,
                ["mergeSha"] = merge,
                ["comments"] = new JsonArray(comment),
            };
            if (continuedIn is not null)
            {
                receipt["continued_in"] = new JsonArray(continuedIn);
            }
            return receipt;
        }
        // Summaries 1 and 2 are on PR 17; the baseline lists those already there when this round prepared. ids are
        // the comments verify must return, or null when it must refuse the receipt.
        static JsonObject Case(JsonObject files, int[]? ids, params int[] baseline)
        {
            var live = new JsonObject();
            foreach (var id in new[] { 1, 2 })
            {
                live[$"issues/comments/{id}"] = new JsonObject
                {
                    ["id"] = id,
                    ["body"] = $"summary {id}",
                    ["issue_url"] = "https://api.github.com/repos/o/r/issues/17",
                };
            }
            return new JsonObject
            {
                ["files"] = new JsonObject(
                    files.Select(file => KeyValuePair.Create(file.Key, (JsonNode?)file.Value!.ToJsonString()))
                ),
                ["live"] = live,
                ["baseline"] = new JsonArray([.. baseline.Select(id => (JsonNode)new JsonArray("issue_comment", id))]),
                ["ids"] = ids is null ? null : new JsonArray([.. ids.Select(id => (JsonNode)id)]),
            };
        }
        static JsonObject Split(JsonObject first, JsonObject second) =>
            new() { ["publication_receipts.json"] = first, ["publication_receipts-2.json"] = second };
        const string Continued = "publication_receipts-2.json";
        var cases = new JsonArray(
            // The AWB#188 receipt: this round's head and comments, an earlier round's merge. Its prId is wrong too.
            Case(
                new JsonObject { ["publication_receipts.json"] = Receipt(head, earlierMerge, Comment(1), prId: "18") },
                [1]
            ),
            // A stale head, the same way.
            Case(new JsonObject { ["publication_receipts.json"] = Receipt(earlierHead, merge, Comment(1)) }, [1]),
            // A receipt kept as two, both naming the earlier revision: both comments are verified.
            Case(
                Split(
                    Receipt(earlierHead, earlierMerge, Comment(1), Continued),
                    Receipt(earlierHead, earlierMerge, Comment(2))
                ),
                [1, 2]
            ),
            // Two files naming different revisions, as a write stopped between them would leave.
            Case(
                Split(Receipt(head, merge, Comment(1), Continued), Receipt(earlierHead, earlierMerge, Comment(2))),
                [1, 2]
            ),
            // The continuation's comment was on the PR before this round's prepare.
            Case(
                Split(
                    Receipt(earlierHead, earlierMerge, Comment(1), Continued),
                    Receipt(earlierHead, earlierMerge, Comment(2))
                ),
                null,
                2
            ),
            // An earlier round's receipt: its summary was on the PR before this round's prepare.
            Case(
                new JsonObject { ["publication_receipts.json"] = Receipt(earlierHead, earlierMerge, Comment(1)) },
                null,
                1
            ),
            // This round's revision, but a summary that is not on the PR.
            Case(new JsonObject { ["publication_receipts.json"] = Receipt(head, merge, Comment(3)) }, null)
        );
        string[] names =
        [
            "GITHUB_COMMENTS",
            "github_belongs",
            "github_reply_wrapper",
            "same_text",
            "github_verify",
            "read_receipt",
            "stamp_receipt",
        ];
        var stamp = $"stamp_receipt(c['files'].__getitem__, 17, '{head}', '{merge}')";
        var verify = $"github_verify({stamp}['comments'], c['live'].get, c['baseline'])";

        var verified = await RunAsync(
            python!,
            names,
            $"passes(lambda: {verify}) and (c['ids'] is None or [v['commentId'] for v in {verify}] == c['ids'])",
            cases.ToJsonString()
        );
        verified
            .Should()
            .Equal(
                [true, true, true, true, false, false, false],
                "the first four verify with exactly their comments, and verify refuses the last three"
            );

        var revision = await RunAsync(
            python!,
            names,
            $"[{stamp}[key] for key in ('prId', 'headSha', 'mergeSha')] == ['17', '{head}', '{merge}']",
            cases.ToJsonString()
        );
        revision.Should().AllBeEquivalentTo(true, "verify reads every receipt with the prepared revision");
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
    private static Task<bool[]> RunRuleAsync(string python, string rule, string arguments, string cases) =>
        RunAsync(python, [rule], $"{rule}({arguments})", cases);

    /// <summary>
    ///     Lifts the named top-level functions and assignments out of the embedded sandbox script and evaluates
    ///     <paramref name="expression" /> for each case as <c>c</c>. <c>passes(f)</c> is true unless <c>f()</c> raises
    ///     ReviewError; the script's <c>pr</c> is 17.
    /// </summary>
    internal static async Task<bool[]> RunAsync(string python, string[] names, string expression, string cases)
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
                import ast, datetime, hashlib, json, re, sys
                tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
                names, expression = sys.argv[2].split(','), sys.argv[3]
                def named(node):
                    if isinstance(node, ast.FunctionDef):
                        return node.name
                    if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name):
                        return node.targets[0].id
                lifted = [n for n in tree.body if named(n) in names]
                assert sorted(named(n) for n in lifted) == sorted(names), 'not found: ' + str(names)
                class ReviewError(Exception):
                    pass
                def passes(call):
                    try:
                        call()
                        return True
                    except ReviewError:
                        return False
                scope = {'pr': 17, 'datetime': datetime, 'hashlib': hashlib, 'json': json, 're': re,
                         'ReviewError': ReviewError, 'passes': passes}
                exec(compile(ast.Module(lifted, []), 'rules', 'exec'), scope)
                call = eval('lambda c: ' + expression, scope)
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
            start.ArgumentList.Add(string.Join(',', names));
            start.ArgumentList.Add(expression);
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
