extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     A study store in research-store.mjs's format: <c>study.json</c> and one <c>records/&lt;id&gt;.json</c> per
///     record, under <c>.experimentation/reviewer-evolution/</c> of <c>store</c>.
/// </summary>
internal static class StudyFixture
{
    internal static void Write(string store, params (string Id, string Kind, string? Supersedes)[] records)
    {
        var root = Path.Combine(store, ".experimentation", "reviewer-evolution");
        Directory.CreateDirectory(Path.Combine(root, "records"));
        File.WriteAllText(
            Path.Combine(root, "study.json"),
            """{"schemaVersion":1,"id":"reviewer-evolution","question":"q","createdAt":"2026-11-23T00:00:00.000Z"}"""
        );
        foreach (var (id, kind, supersedes) in records)
        {
            var record = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["kind"] = kind,
                ["title"] = id,
                ["summary"] = id,
                ["createdAt"] = "2026-11-23T00:00:00.000Z",
                ["links"] = new JsonArray(),
                ["data"] = new JsonObject(),
            };
            if (supersedes is not null)
            {
                record["supersedes"] = supersedes;
            }
            File.WriteAllText(Path.Combine(root, "records", id + ".json"), record.ToJsonString());
        }
    }
}

/// <summary>
///     <c>compare</c> pairs two shadow arms' blind judgments by PR and reviewed head, and files the paired result in the
///     study store as an evidence record.
/// </summary>
public sealed class ReviewdCompareTests(ITestOutputHelper output) : LoggingTestBase(output)
{
    private const string Repo = "ado/org/project/repo";
    private static readonly string Tree = new('7', 40);
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"reviewd-compare-{Guid.NewGuid():N}");

    /// <summary>One judged review: its PR, reviewed head and the judgment's graded values.</summary>
    private sealed record Review(
        int Pr,
        string Head = "c",
        double Capture = 0.5,
        int Completeness = 3,
        int False = 0,
        int Unproven = 1,
        double Usd = 4,
        string JudgeMode = "judge",
        string? KnowledgeBaseTree = null,
        string JudgmentStage = "completed",
        string Experiment = "aa-1"
    );

    private string Arm(string variant, params Review[] reviews)
    {
        Directory.CreateDirectory(_files);
        var path = Path.Combine(_files, variant + ".db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()
        );
        connection.Open();
        void Put(string key, JsonNode value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO cache(key, value) VALUES (@key, @value)";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value.ToJsonString());
            command.ExecuteNonQuery();
        }
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE cache(key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }
        Put(ReviewArm.Key, new JsonObject { ["shadow"] = true, ["variant"] = variant });
        foreach (var review in reviews)
        {
            var artifact = $"{variant}-{review.Pr}".PadRight(40, '0');
            Put(
                $"reviewd/review-run/{Repo}/{review.Pr}",
                new JsonObject
                {
                    ["stage"] = "completed-verified",
                    ["shadow"] = true,
                    ["headSha"] = new string(review.Head[0], 40),
                    ["artifactCommit"] = new JsonObject { ["commit"] = artifact },
                    ["provenance"] = new JsonObject
                    {
                        ["experimentId"] = review.Experiment,
                        ["knowledgeBaseTree"] = review.KnowledgeBaseTree ?? Tree,
                    },
                }
            );
            Put(
                $"reviewd/judge-run/{Repo}/{review.Pr}/1",
                new JsonObject
                {
                    ["stage"] = review.JudgmentStage,
                    ["identity"] = new JsonObject
                    {
                        ["reviewArtifactCommit"] = artifact,
                        ["provider"] = "gpt-6.1-sol",
                        ["effort"] = "xhigh",
                        ["mode"] = review.JudgeMode,
                        ["promptTemplateSha256"] = "p",
                        ["wouldPostPromptSha256"] = "w",
                    },
                    ["judgeUsage"] = new JsonObject { ["measured"] = true, ["usd"] = 2 },
                    ["judgment"] = new JsonObject
                    {
                        ["stage"] = review.JudgmentStage == "completed" ? "final" : "draft",
                        ["issues"] = new JsonArray(),
                        ["grades"] = new JsonObject
                        {
                            ["foundCapture"] = new JsonObject
                            {
                                ["value"] = review.Capture.ToString(
                                    "0.00",
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                            },
                            ["accuracy"] = new JsonObject { ["fp"] = review.False, ["unproven"] = review.Unproven },
                            ["cost"] = new JsonObject { ["usd"] = review.Usd },
                            ["overall"] = "UNKNOWN",
                            ["dimensions"] = new JsonObject
                            {
                                ["completeness"] = new JsonObject { ["score"] = review.Completeness },
                                ["accuracy"] = new JsonObject { ["score"] = "N/A" },
                            },
                        },
                    },
                }
            );
        }
        return path;
    }

    private Task<int> CompareAsync(params string[] arms) =>
        CompareCommand.RunAsync(
            CliOptions.Parse([
                "compare",
                "--experiment",
                "aa-1",
                "--store",
                _files,
                .. arms.SelectMany(a => new[] { "--arm", a }),
                "--out",
                Path.Combine(_files, "out"),
            ]),
            CancellationToken.None
        );

    private string StudyRoot => Path.Combine(_files, ".experimentation", "reviewer-evolution");

    private string[] Evidence() =>
        [
            .. Directory
                .EnumerateFiles(Path.Combine(StudyRoot, "records"))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => id != "aa-1")
                .Select(id => id!),
        ];

    // A pair is one PR judged in both arms on the same head; a deleted head check pairs PR 3 and moves every delta.
    [Fact]
    public async Task Compare_pairs_final_judgments_by_pr_and_head_and_files_one_evidence_record_per_result()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm(
            "a1",
            new Review(1, Capture: 0.4, Completeness: 3, Usd: 4),
            new Review(2, Capture: 0.6, Completeness: 4, False: 1, Usd: 6),
            new Review(3, Capture: 0.1, Completeness: 1, Usd: 1),
            new Review(4),
            new Review(5, JudgmentStage: "completed-draft"),
            new Review(6, Experiment: "other")
        );
        var b = Arm(
            "a2",
            new Review(1, Capture: 0.5, Completeness: 4, Usd: 5),
            new Review(2, Capture: 0.7, Completeness: 4, Unproven: 0, Usd: 6),
            new Review(3, Head: "d", Capture: 0.9, Completeness: 5, Usd: 9),
            new Review(5)
        );

        (await CompareAsync(a, b)).Should().Be(0);

        var result = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_files, "out", "compare.json")))!;
        var comparison = result["comparisons"]![0]!;
        comparison["pairs"]!
            .AsArray()
            .Select(p => p!["pr"]!.GetValue<string>())
            .Should()
            .Equal($"{Repo}/1", $"{Repo}/2");
        comparison["unpaired"]!
            .AsArray()
            .Select(u => $"{u!["pr"]}: {u["reason"]}")
            .Should()
            .Equal(
                $"{Repo}/3: reviewed at different heads (cccccccc vs dddddddd)",
                $"{Repo}/4: judged only in a1",
                $"{Repo}/5: judged only in a2"
            );
        result["arms"]![0]!["excluded"]!
            .AsArray()
            .Select(e => e!["reason"]!.GetValue<string>())
            .Should()
            .Equal(
                "no final judgment of this review (latest judgment stage: completed-draft)",
                "reviewed for experiment other"
            );
        var metrics = comparison["metrics"]!;
        // Both pairs gain 0.1 capture, so every resample's mean is 0.1.
        metrics["foundCapture"]!["meanDelta"]!.GetValue<double>().Should().Be(0.1);
        metrics["foundCapture"]!["ci90"]!.AsArray().Select(v => v!.GetValue<double>()).Should().Equal(0.1, 0.1);
        metrics["completeness"]!["meanDelta"]!.GetValue<double>().Should().Be(0.5);
        metrics["falseOrUnproven"]!["meanDelta"]!.GetValue<double>().Should().Be(-1);
        metrics["accuracy"]!["n"]!.GetValue<int>().Should().Be(0, "N/A is no score");
        metrics["reviewUsd"]!["ratio"]!["value"]!.GetValue<double>().Should().Be(1.1);

        // The result is filed once, under its hash, citing the artifact it wrote.
        var filed = Evidence().Should().ContainSingle().Which;
        var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(StudyRoot, "records", filed + ".json")))!;
        record["kind"]!.GetValue<string>().Should().Be("evidence");
        record["links"]![0]!.ToJsonString().Should().Be("""{"rel":"derived-from","target":"aa-1"}""");
        var artifact = await File.ReadAllBytesAsync(Path.Combine(StudyRoot, "artifacts", filed + ".json"));
        artifact.Should().Equal(await File.ReadAllBytesAsync(Path.Combine(_files, "out", "compare.json")));
        record["data"]!["source"]!.GetValue<string>().Should().Contain(VariantManifest.Sha256Hex(artifact));

        (await CompareAsync(a, b)).Should().Be(0);
        Evidence().Should().Equal([filed], "the same result is the same record");
    }

    // Arms reviewed with other knowledge, or judged by another judge, are refused before anything is written.
    [Theory]
    [InlineData("knowledge", "The arms reviewed with different KnowledgeBase/ trees*")]
    [InlineData("judge", "The arms were judged by different judges*")]
    public async Task Compare_refuses_arms_reviewed_with_other_knowledge_or_judged_by_another_judge(
        string differs,
        string refusal
    )
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1), new Review(2));
        var b = Arm(
            "a2",
            new Review(1),
            differs == "knowledge"
                ? new Review(2, KnowledgeBaseTree: new string('8', 40))
                : new Review(2, JudgeMode: "j2")
        );

        var compare = () => CompareAsync(a, b);

        (await compare.Should().ThrowAsync<InvalidOperationException>()).WithMessage(refusal);
        Directory.Exists(Path.Combine(_files, "out")).Should().BeFalse();
        Evidence().Should().BeEmpty();
    }

    protected override void Dispose(bool disposing)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_files, recursive: true);
        }
        catch (IOException) { }
        base.Dispose(disposing);
    }
}
