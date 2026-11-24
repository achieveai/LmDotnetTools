extern alias Reviewd;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

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
        string Experiment = "aa-1",
        bool RecordsTree = true,
        string? Artifact = null
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
            var artifact = (review.Artifact ?? $"{variant}-{review.Pr}").PadRight(40, '0');
            Put(
                $"reviewd/review-run/{Repo}/{review.Pr}",
                new JsonObject
                {
                    ["stage"] = "completed-verified",
                    ["shadow"] = true,
                    ["headSha"] = new string(review.Head[0], 40),
                    ["artifactCommit"] = new JsonObject { ["commit"] = artifact },
                    ["provenance"] = review.RecordsTree
                        ? new JsonObject
                        {
                            ["experimentId"] = review.Experiment,
                            ["knowledgeBaseTree"] = review.KnowledgeBaseTree ?? Tree,
                        }
                        : new JsonObject { ["experimentId"] = review.Experiment },
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

    private string Out => Path.Combine(_files, "out");

    private Task<int> CompareAsync(params string[] arms) => CompareToAsync(Out, arms);

    private Task<int> CompareToAsync(string output, params string[] arms) =>
        CompareCommand.RunAsync(
            CliOptions.Parse([
                "compare",
                "--experiment",
                "aa-1",
                "--store",
                _files,
                .. arms.SelectMany(a => new[] { "--arm", a }),
                "--out",
                output,
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

    // Arms not reviewed and judged alike are refused before anything is written: a receipt with no knowledge tree,
    // other knowledge, another judge, or one review in two arms.
    [Theory]
    [InlineData("no tree", "Arm a2 */2: the review receipt records no knowledgeBaseTree*")]
    [InlineData("knowledge", "The arms reviewed with different KnowledgeBase/ trees*")]
    [InlineData("judge", "The arms were judged by different judges*")]
    [InlineData("same review", "Review a1-2* is in more than one arm (a1, a2);*")]
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
            differs switch
            {
                "no tree" => new Review(2, RecordsTree: false),
                "knowledge" => new Review(2, KnowledgeBaseTree: new string('8', 40)),
                "judge" => new Review(2, JudgeMode: "j2"),
                _ => new Review(2, Artifact: "a1-2"),
            }
        );

        var compare = () => CompareAsync(a, b);

        var refused = await compare.Should().ThrowAsync<InvalidOperationException>();
        Logger.LogInformation("Arms differ by {Differs}: refused with {Refusal}", differs, refused.Which.Message);
        refused.WithMessage(refusal);
        Directory.Exists(Out).Should().BeFalse();
        Evidence().Should().BeEmpty();
    }

    // Unequal deltas give an interval wider than a point. The bounds sit where the normal approximation puts a 90%
    // interval (mean 0.355 +- 1.645 x 0.1655), and they are pinned: the seed and resample count are part of the result,
    // so changing either, or the percentiles, moves them.
    [Fact]
    public void Paired_gives_the_90_percent_bootstrap_interval_of_the_mean_delta()
    {
        double[] deltas = [0.13, 0.91, -0.42, 0.05, 1.37, 0, 0.66, -0.18, 0.29, 0.74];

        var paired = CompareCommand.Paired([.. deltas.Select(d => (0.0, d))]);

        Logger.LogInformation("Paired summary {Paired}", paired.ToJsonString());
        paired["n"]!.GetValue<int>().Should().Be(10);
        paired["meanDelta"]!.GetValue<double>().Should().Be(0.355);
        paired["sdDelta"]!.GetValue<double>().Should().Be(0.5518);
        var interval = paired["ci90"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        interval[0].Should().BeApproximately(0.355 - 0.2723, 0.03);
        interval[1].Should().BeApproximately(0.355 + 0.2723, 0.03);
        interval.Should().Equal(0.083, 0.634);
    }

    // A result already filed counts as filed only while its record is that result's evidence record and the artifact
    // it cites still holds the result; otherwise the rerun refuses and writes nothing.
    [Theory]
    [InlineData("deleted artifact", "* cites artifacts/aa-1-compare-*.json sha256 *, but that artifact is missing;*")]
    [InlineData("changed artifact", "* cites artifacts/aa-1-compare-*.json sha256 *, but that artifact is changed;*")]
    [InlineData("another kind", "* exists but is not this result's evidence record*")]
    [InlineData("no link", "* exists but is not this result's evidence record*")]
    [InlineData("wrong cited path", "* exists but is not this result's evidence record*")]
    [InlineData("wrong cited sha", "* exists but is not this result's evidence record*")]
    public async Task A_filed_result_is_unchanged_only_while_its_record_and_artifact_still_hold_it(
        string damage,
        string refusal
    )
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1, Capture: 0.7));
        (await CompareAsync(a, b)).Should().Be(0);
        var filed = Evidence().Single();
        var artifact = Path.Combine(StudyRoot, "artifacts", filed + ".json");
        var recordPath = Path.Combine(StudyRoot, "records", filed + ".json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
        switch (damage)
        {
            case "deleted artifact":
                File.Delete(artifact);
                break;
            case "changed artifact":
                await File.AppendAllTextAsync(artifact, " ");
                break;
            case "another kind":
                record["kind"] = "lesson";
                await File.WriteAllTextAsync(recordPath, record.ToJsonString());
                break;
            case "no link":
                record["links"] = new JsonArray();
                await File.WriteAllTextAsync(recordPath, record.ToJsonString());
                break;
            default:
                // The summary is where the record cites its artifact: another path, or the right path with another hash.
                var summary = record["summary"]!.GetValue<string>();
                var sha = VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(artifact));
                record["summary"] =
                    damage == "wrong cited path"
                        ? summary.Replace($"artifacts/{filed}.json", "artifacts/other.json", StringComparison.Ordinal)
                        : summary.Replace(sha, new string('0', 64), StringComparison.Ordinal);
                record["summary"]!.GetValue<string>().Should().NotBe(summary, "the damage must change the citation");
                await File.WriteAllTextAsync(recordPath, record.ToJsonString());
                break;
        }
        Directory.Delete(Out, recursive: true);

        var rerun = () => CompareAsync(a, b);

        var refused = await rerun.Should().ThrowAsync<InvalidOperationException>();
        Logger.LogInformation(
            "Filed result with {Damage}: rerun refused with {Refusal}",
            damage,
            refused.Which.Message
        );
        refused.WithMessage(refusal);
        Directory.Exists(Out).Should().BeFalse();
        Evidence().Should().Equal(filed);
    }

    // The study's own folders are not an output: an --out there would write compare.json among its records.
    [Theory]
    [InlineData("")]
    [InlineData("records")]
    [InlineData("artifacts/out")]
    public async Task Compare_refuses_an_output_inside_the_study(string under)
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var output = Path.Combine(StudyRoot, under);

        var compare = () => CompareToAsync(output, a, b);

        var refused = await compare.Should().ThrowAsync<ArgumentException>();
        Logger.LogInformation("--out {Output} refused with {Refusal}", output, refused.Which.Message);
        refused.WithMessage("--out * is inside the study at *");
        Directory
            .EnumerateFiles(StudyRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(StudyRoot, f).Replace('\\', '/'))
            .Should()
            .BeEquivalentTo("study.json", "records/aa-1.json");
    }

    // A link is followed before the check: --out through a directory link (a junction on Windows) to records/, or a
    // folder under one, is the study's records folder and is refused before any write. A '..' the caller typed after a
    // link is collapsed by name, as .NET collapses it before writing: outer/bridge/../alias is outer/alias.
    [Theory]
    [InlineData("the link")]
    [InlineData("a folder under the link")]
    [InlineData("a link whose target passes through another link")]
    [InlineData("a typed '..' after another link")]
    public async Task Compare_refuses_an_output_that_links_into_the_study(string through)
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var alias = Path.Combine(_files, "alias");
        if (through == "a link whose target passes through another link")
        {
            // alias -> bridge/records, bridge -> the study: the text of alias's own target is outside the study.
            var bridge = Path.Combine(_files, "bridge");
            DirectoryLink.Create(bridge, StudyRoot);
            DirectoryLink.Create(alias, Path.Combine(bridge, "records"));
        }
        else
        {
            DirectoryLink.Create(alias, Path.Combine(StudyRoot, "records"));
        }
        if (through == "a typed '..' after another link")
        {
            // bridge -> elsewhere/deep: following bridge before the '..' would end at elsewhere/alias, which is absent.
            var deep = Path.Combine(_files, "elsewhere", "deep");
            Directory.CreateDirectory(deep);
            DirectoryLink.Create(Path.Combine(_files, "bridge"), deep);
        }
        var output = through switch
        {
            "a folder under the link" => Path.Combine(alias, "sub"),
            "a typed '..' after another link" => Path.Combine(_files, "bridge", "..", "alias"),
            _ => alias,
        };

        var compare = () => CompareToAsync(output, a, b);

        var refused = await compare.Should().ThrowAsync<ArgumentException>();
        Logger.LogInformation(
            "--out {Output} through {Through} refused with {Refusal}",
            output,
            through,
            refused.Which.Message
        );
        refused.WithMessage("--out * is inside the study at *");
        Directory
            .EnumerateFileSystemEntries(Path.Combine(StudyRoot, "records"))
            .Select(Path.GetFileName)
            .Should()
            .Equal("aa-1.json");
    }

    // Unix follows a link before the '..' after it: out -> bridge/../records with bridge -> study/artifacts is
    // study/records, though the target's text collapses by name to a folder outside the study. (Windows drops '..' by
    // name first, so there the same link lands outside, as its text says.)
    [LinuxOnlyFact("only Unix applies '..' after following the link before it")]
    public async Task Compare_refuses_an_output_link_whose_dot_dot_leaves_another_links_target()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        Directory.CreateDirectory(Path.Combine(StudyRoot, "artifacts"));
        DirectoryLink.Create(Path.Combine(_files, "bridge"), Path.Combine(StudyRoot, "artifacts"));
        var output = Path.Combine(_files, "out");
        Directory.CreateSymbolicLink(output, Path.Combine("bridge", "..", "records"));

        var compare = () => CompareToAsync(output, a, b);

        var refused = await compare.Should().ThrowAsync<ArgumentException>();
        Logger.LogInformation("--out {Output} refused with {Refusal}", output, refused.Which.Message);
        refused.WithMessage("--out * is inside the study at *");
        Directory
            .EnumerateFileSystemEntries(Path.Combine(StudyRoot, "records"))
            .Select(Path.GetFileName)
            .Should()
            .Equal("aa-1.json");
    }

    // A Windows device path (\\?\ or \\.\) names the same folder as the plain path but escapes GetFullPath's
    // normalisation, so a prefixed --out or --store is refused before any comparison or write.
    [WindowsOnlyFact("device paths are a Windows form; elsewhere \\\\?\\ is an ordinary file name")]
    public async Task Compare_refuses_a_device_path_for_its_output_or_its_store()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var record = Path.Combine(StudyRoot, "records", "aa-1.json");
        var before = await File.ReadAllBytesAsync(record);
        var cases = new (string Name, string Store, string Output)[]
        {
            (@"--out \\?\ to records", _files, @"\\?\" + Path.Combine(StudyRoot, "records")),
            (@"--out \\.\ to records", _files, @"\\.\" + Path.Combine(StudyRoot, "records")),
            (@"--store \\?\, --out records", @"\\?\" + _files, Path.Combine(StudyRoot, "records")),
        };

        foreach (var (name, store, output) in cases)
        {
            var compare = () =>
                CompareCommand.RunAsync(
                    CliOptions.Parse([
                        "compare",
                        "--experiment",
                        "aa-1",
                        "--store",
                        store,
                        "--arm",
                        a,
                        "--arm",
                        b,
                        "--out",
                        output,
                    ]),
                    CancellationToken.None
                );

            var refused = await compare.Should().ThrowAsync<ArgumentException>();
            Logger.LogInformation("{Case} refused with {Refusal}", name, refused.Which.Message);
            refused.WithMessage("* is a Windows device path *");
        }
        (await File.ReadAllBytesAsync(record)).Should().Equal(before);
        Directory
            .EnumerateFileSystemEntries(Path.Combine(StudyRoot, "records"))
            .Select(Path.GetFileName)
            .Should()
            .Equal("aa-1.json");
    }

    // The resolver follows at most 32 links: a chain of 32 still resolves and is written, one more link and a two-link
    // cycle are refused before any write.
    [Theory]
    [InlineData("a chain of 32 links", 32)]
    [InlineData("a chain of 33 links", 33)]
    [InlineData("a two-link cycle", 2)]
    public async Task Compare_follows_at_most_32_links_from_its_output(string chain, int links)
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var end = Path.Combine(_files, "end");
        Directory.CreateDirectory(end);
        string Link(int i) => Path.Combine(_files, $"link-{i}");
        for (var i = links - 1; i >= 0; i--)
        {
            var last = i == links - 1;
            DirectoryLink.Create(Link(i), last ? (chain == "a two-link cycle" ? Link(0) : end) : Link(i + 1));
        }
        var record = Path.Combine(StudyRoot, "records", "aa-1.json");
        var before = await File.ReadAllBytesAsync(record);

        var compare = () => CompareToAsync(Link(0), a, b);

        if (chain == "a chain of 32 links")
        {
            (await compare()).Should().Be(0);
            Logger.LogInformation("--out through {Chain} wrote {Files}", chain, Directory.GetFiles(end));
            File.Exists(Path.Combine(end, "compare.json")).Should().BeTrue();
        }
        else
        {
            var refused = await compare.Should().ThrowAsync<IOException>();
            Logger.LogInformation("--out through {Chain} refused with {Refusal}", chain, refused.Which.Message);
            refused.WithMessage("* passes through more than 32 links.");
            Directory.EnumerateFileSystemEntries(end).Should().BeEmpty();
            Evidence().Should().BeEmpty();
        }
        (await File.ReadAllBytesAsync(record)).Should().Equal(before);
    }

    // An --out file that is a hard link to a study record is replaced, never written through: the record keeps its
    // bytes. A hard link needs no privilege and no containment check can see it, so only the write can stop it.
    [Theory]
    [InlineData("compare.json")]
    [InlineData("compare.md")]
    public async Task An_output_file_hard_linked_to_a_study_record_is_replaced_not_written_through(string name)
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var experiment = Path.Combine(StudyRoot, "records", "aa-1.json");
        var before = await File.ReadAllBytesAsync(experiment);
        Directory.CreateDirectory(Out);
        var output = Path.Combine(Out, name);
        HardLink(output, experiment);

        (await CompareAsync(a, b)).Should().Be(0);

        var after = await File.ReadAllBytesAsync(experiment);
        var written = await File.ReadAllTextAsync(output);
        Logger.LogInformation(
            "{Output} hard-linked to {Record}: record {Length} bytes sha256 {Sha} after the run; output starts {Start}",
            output,
            experiment,
            after.Length,
            VariantManifest.Sha256Hex(after),
            written[..Math.Min(40, written.Length)]
        );
        after.Should().Equal(before);
        written
            .Should()
            .NotBe(System.Text.Encoding.UTF8.GetString(before), "the output holds the result, not the record");
    }

    // The same through a file symlink, where this machine may create one: each output in turn links to the record.
    [RequiresFileSymlinkFact("a file symlink needs Developer Mode or an elevated process on Windows")]
    public async Task An_output_file_symlinked_to_a_study_record_is_replaced_not_written_through()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var experiment = Path.Combine(StudyRoot, "records", "aa-1.json");
        var before = await File.ReadAllBytesAsync(experiment);
        Directory.CreateDirectory(Out);

        foreach (var name in new[] { "compare.json", "compare.md" })
        {
            var output = Path.Combine(Out, name);
            File.Delete(output);
            File.CreateSymbolicLink(output, experiment);

            (await CompareAsync(a, b)).Should().Be(0);

            var after = await File.ReadAllBytesAsync(experiment);
            Logger.LogInformation(
                "{Output} symlinked to {Target}: record {Length} bytes sha256 {Sha} after the run; output link now {Link}",
                output,
                experiment,
                after.Length,
                VariantManifest.Sha256Hex(after),
                new FileInfo(output).LinkTarget ?? "(none)"
            );
            after.Should().Equal(before);
            new FileInfo(output).LinkTarget.Should().BeNull("the link was replaced by the file");
        }
    }

    private static void HardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(
                new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            )!;
            mklink.WaitForExit();
            mklink.ExitCode.Should().Be(0, mklink.StandardError.ReadToEnd());
        }
        else
        {
            using var ln = Process.Start("ln", [target, link]);
            ln.WaitForExit();
            ln.ExitCode.Should().Be(0);
        }
        File.Exists(link).Should().BeTrue($"the test needs '{link}' to be a second name for '{target}'");
    }

    // The outputs are written under the study lock with the filing: while another writer holds it, compare writes
    // neither its evidence nor its --out files, so two compares to one --out never mix one's JSON with the other's
    // Markdown.
    [Fact]
    public async Task Compare_writes_its_outputs_only_under_the_study_lock()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var held = Path.Combine(StudyRoot, ".write-lock");
        await File.WriteAllTextAsync(held, """{"pid":1,"writer":"another compare"}""");

        var compare = () => CompareAsync(a, b);

        var refused = await compare.Should().ThrowAsync<InvalidOperationException>();
        Logger.LogInformation("With {Lock} held, compare refused with {Refusal}", held, refused.Which.Message);
        refused.WithMessage("The study is locked*");
        Directory.Exists(Out).Should().BeFalse();
        Evidence().Should().BeEmpty();
        File.Delete(held);
        (await CompareAsync(a, b)).Should().Be(0);
        Directory.EnumerateFiles(Out).Select(Path.GetFileName).Should().BeEquivalentTo("compare.json", "compare.md");
    }

    // The record's createdAt is an ISO UTC timestamp whatever the culture (th-TH counts years from 543 BC).
    [Fact]
    public async Task The_evidence_record_timestamp_is_invariant_under_another_culture()
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            (await CompareAsync(a, b)).Should().Be(0);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var record = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(StudyRoot, "records", Evidence().Single() + ".json"))
        )!;
        var createdAt = record["createdAt"]!.GetValue<string>();
        Logger.LogInformation("createdAt under th-TH: {CreatedAt}", createdAt);
        createdAt.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        DateTimeOffset
            .ParseExact(
                createdAt,
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
            )
            .Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
    }

    // A record file that does not parse, or is not an object, stops the gate, and the error names it.
    [Theory]
    [InlineData("{", "is not valid JSON:*")]
    [InlineData("[]", "is not a JSON object.")]
    public async Task A_malformed_study_record_is_named_in_the_refusal(string content, string refusal)
    {
        StudyFixture.Write(_files, ("aa-1", "experiment", null));
        var broken = Path.Combine(StudyRoot, "records", "broken.json");
        await File.WriteAllTextAsync(broken, content);
        var a = Arm("a1", new Review(1));
        var b = Arm("a2", new Review(1));

        var compare = () => CompareAsync(a, b);

        var refused = await compare.Should().ThrowAsync<InvalidOperationException>();
        Logger.LogInformation("Record content {Content} refused with {Refusal}", content, refused.Which.Message);
        refused.WithMessage($"Study record file {broken} {refusal}");
        Directory.Exists(Out).Should().BeFalse();
    }

    // An experiment id is a research-store record id: lowercase kebab, at most 100 characters, nothing after it.
    [Theory]
    [InlineData("aa-1", true)]
    [InlineData("100 chars", true)]
    [InlineData("101 chars", false)]
    [InlineData("AA-1", false)]
    [InlineData("aa_1", false)]
    [InlineData("aa.1", false)]
    [InlineData("aa--1", false)]
    [InlineData("-aa", false)]
    [InlineData("aa-", false)]
    [InlineData("aa-1\n", false)]
    public void An_experiment_id_is_a_lowercase_kebab_record_id_of_at_most_100_characters(string id, bool valid)
    {
        id = id switch
        {
            "100 chars" => new string('a', 100),
            "101 chars" => new string('a', 101),
            _ => id,
        };

        var parse = () =>
            CliOptions.Parse(["compare", "--experiment", id, "--store", "s", "--arm", "a", "--arm", "b", "--out", "o"]);
        string? error = null;
        try
        {
            parse();
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
        }
        Logger.LogInformation("--experiment {Id} (length {Length}): {Outcome}", id, id.Length, error ?? "accepted");

        if (valid)
        {
            parse.Should().NotThrow();
        }
        else
        {
            parse.Should().Throw<ArgumentException>().WithMessage("--experiment must be a study record id*");
        }
    }

    protected override void Dispose(bool disposing)
    {
        SqliteConnection.ClearAllPools();
        // Recursive delete throws on a junction; unlink first so only the links go, never their targets.
        DirectoryLink.UnlinkAllUnder(_files);
        try
        {
            Directory.Delete(_files, recursive: true);
        }
        catch (IOException exception)
        {
            Logger.LogWarning(exception, "Could not delete the test folder {Folder}", _files);
        }
        base.Dispose(disposing);
    }
}
