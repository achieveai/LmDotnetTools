using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     <c>compare</c>: turns the blind judgments of two or more shadow arms into a paired result (plan
///     docs/plans/2026-11-16-reviewer-evolution-ab.md, "Experiment design"). The first <c>--arm</c> is the baseline
///     (the champion A′); every other arm is compared with it, PR by PR, on the same reviewed head. Each metric gets
///     the mean paired difference and a 90% percentile bootstrap interval, and review cost also gets the arm-to-baseline
///     ratio its guardrail reads. The result is written to <c>--out</c> and filed in the study store as an evidence
///     record, so it is never skipped or rewritten.
/// </summary>
internal static class CompareCommand
{
    internal const int Resamples = 10_000;

    // Fixed, so the same judgments always give the same interval; recorded in the result.
    internal const int Seed = 20261116;
    internal const double CostGuardrail = 1.3;

    /// <summary>One judged metric; <c>Better</c> says which direction is an improvement.</summary>
    internal sealed record Metric(string Key, string What, string Better, Func<JsonObject, JsonObject, double?> Read);

    internal static readonly Metric[] Metrics =
    [
        new(
            "foundCapture",
            "completeness: recall over validated issues, would-post or found",
            "higher",
            (j, _) => Number(j["grades"]?["foundCapture"]?["value"])
        ),
        new("completeness", "completeness score (1-5)", "higher", (j, _) => Score(j, "completeness")),
        new(
            "falseOrUnproven",
            "accuracy: false plus unproven findings per review",
            "lower",
            (j, _) =>
                Number(j["grades"]?["accuracy"]?["fp"]) is { } fp
                && Number(j["grades"]?["accuracy"]?["unproven"]) is { } unproven
                    ? fp + unproven
                    : null
        ),
        new("accuracy", "accuracy score (1-5)", "higher", (j, _) => Score(j, "accuracy")),
        new(
            "hallucinations",
            "guardrail: issues the judge filed as hallucinations",
            "lower",
            (j, _) => Hallucinations(j)
        ),
        new(
            "overall",
            "overall score (mean of the dimension scores)",
            "higher",
            (j, _) => Number(j["grades"]?["overall"])
        ),
        new(
            "reviewUsd",
            "guardrail: review cost in USD, as the judge copied it from host usage",
            "lower",
            (j, _) => Number(j["grades"]?["cost"]?["usd"])
        ),
        new(
            "judgeUsd",
            "judge cost in USD (host usage the runner measured)",
            "lower",
            (_, r) => r["judgeUsage"]?["measured"]?.GetValue<bool>() == true ? Number(r["judgeUsage"]?["usd"]) : null
        ),
    ];

    /// <summary>A judged review of one PR in one arm.</summary>
    internal sealed record Judged(
        string Pr,
        string Head,
        string ArtifactCommit,
        string? KnowledgeBaseTree,
        JsonObject JudgeIdentity,
        JsonObject Judgment,
        JsonObject JudgeReceipt
    );

    internal sealed record Arm(
        string Database,
        string? Variant,
        Dictionary<string, Judged> Reviews,
        JsonArray Excluded
    );

    public static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        var experiment = options.Required("experiment");
        var store = options.Required("store");
        var output = options.Required("out");
        StudyStore.RequireExperiment(store, experiment);
        var databases = options.GetAll("arm");
        if (databases.Count < 2)
        {
            throw new ArgumentException("compare needs at least two --arm databases: the baseline first.");
        }
        if (databases.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != databases.Count)
        {
            throw new ArgumentException("Each --arm must be a different database.");
        }
        var arms = new List<Arm>();
        foreach (var database in databases)
        {
            arms.Add(await ReadArmAsync(database, experiment, ct));
        }
        var result = Compare(experiment, arms);
        var json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "compare.json"), json, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "compare.md"), Markdown(result), ct);
        var (outcome, recordId, artifact) = StudyStore.AddEvidence(
            store,
            experiment,
            $"Compare of {experiment}: {string.Join(" vs ", arms.Select(a => a.Variant ?? "shadow"))}",
            json,
            (path, sha) =>
                new JsonObject
                {
                    ["mode"] = "observed",
                    ["source"] =
                        $"reviewd compare ({ShadowArm.ReviewdBuild}) over arm databases "
                        + $"{string.Join(", ", arms.Select(a => a.Database))}; results {path} sha256 {sha}.",
                    ["observation"] = Observation(result),
                    ["limitations"] =
                        "Judgments are one blind judge's per PR; review cost is the host usage the judge copied (a lower "
                        + "bound while any conversation is incomplete); a pair needs both arms judged on the same head; "
                        + $"intervals are {Resamples} percentile bootstrap resamples of PR pairs, seed {Seed}, not "
                        + "corrected for multiple arms.",
                }
        );
        Console.WriteLine(
            new JsonObject
            {
                ["command"] = "compare",
                ["experimentId"] = experiment,
                ["evidence"] = outcome,
                ["recordId"] = recordId,
                ["artifact"] = artifact,
                ["out"] = Path.GetFullPath(output),
            }.ToJsonString()
        );
        return 0;
    }

    /// <summary>
    ///     Reads one arm's database (read-only): each verified shadow review of <paramref name="experiment" /> with its
    ///     latest final judgment of that review. Anything else is listed under <c>excluded</c> with the reason.
    /// </summary>
    internal static async Task<Arm> ReadArmAsync(string database, string experiment, CancellationToken ct)
    {
        if (!File.Exists(database))
        {
            throw new ArgumentException($"--arm {database} does not exist.");
        }
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(ct);
        var pin = (await JudgeBatch.RowsAsync(connection, ReviewArm.Key, ct)).FirstOrDefault(r =>
            r.Key == ReviewArm.Key
        );
        if (pin.Value is null || JsonNode.Parse(pin.Value)?["shadow"]?.GetValue<bool>() != true)
        {
            throw new ArgumentException(
                $"--arm {database} is not a shadow arm's database (no shadow {ReviewArm.Key})."
            );
        }
        var variant = JsonNode.Parse(pin.Value)?["variant"]?.GetValue<string>();
        const string ReviewPrefix = "reviewd/review-run/";
        var reviews = new Dictionary<string, Judged>(StringComparer.Ordinal);
        var excluded = new JsonArray();
        void Exclude(string pr, string reason) => excluded.Add(new JsonObject { ["pr"] = pr, ["reason"] = reason });
        foreach (var (key, value) in await JudgeBatch.RowsAsync(connection, ReviewPrefix, ct))
        {
            var pr = key[ReviewPrefix.Length..];
            if (JsonNode.Parse(value) is not JsonObject review)
            {
                Exclude(pr, "the review receipt is not a JSON object");
                continue;
            }
            var provenance = review["provenance"] as JsonObject;
            if (provenance?["experimentId"]?.GetValue<string>() is var other && other != experiment)
            {
                Exclude(pr, $"reviewed for experiment {other ?? "none"}");
                continue;
            }
            if (review["stage"]?.GetValue<string>() is var stage && stage != "completed-verified")
            {
                Exclude(pr, $"review stage {stage ?? "none"}, not completed-verified");
                continue;
            }
            if (review["artifactCommit"]?["commit"]?.GetValue<string>() is not { Length: 40 } artifact)
            {
                Exclude(pr, "the review receipt has no artifact commit");
                continue;
            }
            var judgePrefix = $"reviewd/judge-run/{pr}/";
            var judgments = JudgeBatch.Judgments(await JudgeBatch.RowsAsync(connection, judgePrefix, ct), judgePrefix);
            // The latest final judgment of this very review: stage "completed" is the runner's word for a judge that
            // wrote a final judgment.json ("completed-draft" is one the cap stopped).
            var final = judgments
                .Values.Reverse()
                .FirstOrDefault(j =>
                    j["stage"]?.GetValue<string>() == "completed"
                    && j["identity"]?["reviewArtifactCommit"]?.GetValue<string>() == artifact
                    && j["judgment"] is JsonObject
                );
            if (final is null)
            {
                var latest = judgments.Count == 0 ? "none" : judgments.Values.Last()["stage"]?.GetValue<string>();
                Exclude(pr, $"no final judgment of this review (latest judgment stage: {latest})");
                continue;
            }
            var identity = final["identity"]!.AsObject();
            reviews[pr] = new Judged(
                pr,
                (review["reviewedHeadSha"] ?? review["headSha"])?.GetValue<string>() ?? "",
                artifact,
                provenance?["knowledgeBaseTree"]?.GetValue<string>(),
                new JsonObject
                {
                    ["provider"] = identity["provider"]?.DeepClone(),
                    ["effort"] = identity["effort"]?.DeepClone(),
                    ["mode"] = identity["mode"]?.DeepClone(),
                    ["promptTemplateSha256"] = identity["promptTemplateSha256"]?.DeepClone(),
                    ["wouldPostPromptSha256"] = identity["wouldPostPromptSha256"]?.DeepClone(),
                },
                final["judgment"]!.AsObject(),
                final
            );
        }
        return new Arm(database, variant, reviews, excluded);
    }

    /// <summary>
    ///     The paired result. Refuses arms that were not judged alike: a review whose receipt records no
    ///     <c>KnowledgeBase/</c> tree, two different trees, two judge identities, or one review in two arms.
    /// </summary>
    internal static JsonObject Compare(string experiment, IReadOnlyList<Arm> arms)
    {
        var all = arms.SelectMany(a => a.Reviews.Values.Select(r => (Arm: a, Review: r))).ToList();
        if (all.FirstOrDefault(x => x.Review.KnowledgeBaseTree is null) is { Review: not null } untreed)
        {
            throw new InvalidOperationException(
                $"Arm {Name(untreed.Arm)} {untreed.Review.Pr}: the review receipt records no knowledgeBaseTree, so it "
                    + "cannot be shown to share the champion's knowledge."
            );
        }
        var trees = all.GroupBy(x => x.Review.KnowledgeBaseTree!).ToList();
        if (trees.Count > 1)
        {
            throw new InvalidOperationException(
                "The arms reviewed with different KnowledgeBase/ trees, so they are not comparable: "
                    + string.Join(
                        "; ",
                        trees.Select(t => $"{t.Key} in {string.Join(", ", t.Select(x => Name(x.Arm)).Distinct())}")
                    )
                    + "."
            );
        }
        var judges = all.GroupBy(x => x.Review.JudgeIdentity.ToJsonString()).ToList();
        if (judges.Count > 1)
        {
            throw new InvalidOperationException(
                "The arms were judged by different judges (provider, effort, mode or prompt), so they are not "
                    + "comparable: "
                    + string.Join(
                        "; ",
                        judges.Select(j =>
                            $"{j.Key} for {string.Join(", ", j.Select(x => $"{Name(x.Arm)} {x.Review.Pr}").Take(3))}"
                        )
                    )
                    + "."
            );
        }
        if (all.GroupBy(x => x.Review.ArtifactCommit).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new InvalidOperationException(
                $"Review {twice.Key} is in more than one arm ({string.Join(", ", twice.Select(x => Name(x.Arm)))}); "
                    + "each arm must be its own database of its own reviews."
            );
        }

        var baseline = arms[0];
        var comparisons = new JsonArray();
        foreach (var arm in arms.Skip(1))
        {
            var unpaired = new JsonArray();
            var pairs = new List<(Judged A, Judged B)>();
            foreach (var pr in baseline.Reviews.Keys.Union(arm.Reviews.Keys).Order(StringComparer.Ordinal))
            {
                var inA = baseline.Reviews.TryGetValue(pr, out var a);
                var inB = arm.Reviews.TryGetValue(pr, out var b);
                if (!inA || !inB)
                {
                    unpaired.Add(
                        new JsonObject { ["pr"] = pr, ["reason"] = $"judged only in {Name(inA ? baseline : arm)}" }
                    );
                }
                else if (a!.Head != b!.Head)
                {
                    unpaired.Add(
                        new JsonObject
                        {
                            ["pr"] = pr,
                            ["reason"] = $"reviewed at different heads ({Short(a.Head)} vs {Short(b.Head)})",
                        }
                    );
                }
                else
                {
                    pairs.Add((a, b));
                }
            }
            var metrics = new JsonObject();
            foreach (var metric in Metrics)
            {
                var values = pairs
                    .Select(p =>
                        (A: metric.Read(p.A.Judgment, p.A.JudgeReceipt), B: metric.Read(p.B.Judgment, p.B.JudgeReceipt))
                    )
                    .Where(v => v.A is not null && v.B is not null)
                    .Select(v => (v.A!.Value, v.B!.Value))
                    .ToArray();
                var summary = Paired(values);
                summary["better"] = metric.Better;
                summary["what"] = metric.What;
                if (metric.Key == "reviewUsd")
                {
                    var ratio = Ratio(values);
                    ratio["limit"] = CostGuardrail;
                    summary["ratio"] = ratio;
                }
                metrics[metric.Key] = summary;
            }
            comparisons.Add(
                new JsonObject
                {
                    ["baseline"] = Name(baseline),
                    ["arm"] = Name(arm),
                    ["pairs"] = new JsonArray([
                        .. pairs.Select(p =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["pr"] = p.A.Pr,
                                    ["head"] = p.A.Head,
                                    ["values"] = new JsonObject(
                                        Metrics.Select(m =>
                                            KeyValuePair.Create(
                                                m.Key,
                                                (JsonNode?)
                                                    new JsonArray(
                                                        Value(m.Read(p.A.Judgment, p.A.JudgeReceipt)),
                                                        Value(m.Read(p.B.Judgment, p.B.JudgeReceipt))
                                                    )
                                            )
                                        )
                                    ),
                                }
                        ),
                    ]),
                    ["unpaired"] = unpaired,
                    ["metrics"] = metrics,
                }
            );
        }
        return new JsonObject
        {
            ["schema"] = "reviewd-compare/1",
            ["experimentId"] = experiment,
            ["knowledgeBaseTree"] = trees.SingleOrDefault()?.Key,
            ["judge"] = judges.SingleOrDefault() is { } judge ? JsonNode.Parse(judge.Key) : null,
            ["bootstrap"] = new JsonObject
            {
                ["resamples"] = Resamples,
                ["seed"] = Seed,
                ["interval"] = "90% percentile, over PR pairs",
            },
            ["arms"] = new JsonArray([
                .. arms.Select(a =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["name"] = Name(a),
                            ["database"] = a.Database,
                            ["variant"] = a.Variant,
                            ["judged"] = a.Reviews.Count,
                            ["excluded"] = a.Excluded.DeepClone(),
                        }
                ),
            ]),
            ["comparisons"] = comparisons,
        };
    }

    /// <summary>n, both means, the mean paired difference (arm minus baseline), its SD and 90% bootstrap interval.</summary>
    internal static JsonObject Paired(IReadOnlyList<(double A, double B)> values)
    {
        var deltas = values.Select(v => v.B - v.A).ToArray();
        var result = new JsonObject
        {
            ["n"] = values.Count,
            ["baselineMean"] = Value(values.Count == 0 ? null : values.Average(v => v.A)),
            ["armMean"] = Value(values.Count == 0 ? null : values.Average(v => v.B)),
            ["meanDelta"] = Value(deltas.Length == 0 ? null : deltas.Average()),
            ["sdDelta"] = Value(deltas.Length < 2 ? null : StandardDeviation(deltas)),
        };
        result["ci90"] = Interval(values, sample => sample.Average(v => v.B - v.A));
        return result;
    }

    /// <summary>Arm cost over baseline cost (ratio of the means) and its 90% bootstrap interval.</summary>
    internal static JsonObject Ratio(IReadOnlyList<(double A, double B)> values)
    {
        static double? Of(IEnumerable<(double A, double B)> sample)
        {
            var list = sample.ToList();
            var a = list.Sum(v => v.A);
            return a > 0 ? list.Sum(v => v.B) / a : null;
        }
        return new JsonObject
        {
            ["value"] = Value(values.Count == 0 ? null : Of(values)),
            ["ci90"] = Interval(values, s => Of(s) ?? double.NaN),
        };
    }

    private static JsonArray? Interval(
        IReadOnlyList<(double A, double B)> values,
        Func<IReadOnlyList<(double A, double B)>, double> statistic
    )
    {
        if (values.Count == 0)
        {
            return null;
        }
        var random = new Random(Seed);
        var estimates = new List<double>(Resamples);
        var sample = new (double, double)[values.Count];
        for (var i = 0; i < Resamples; i++)
        {
            for (var k = 0; k < sample.Length; k++)
            {
                sample[k] = values[random.Next(values.Count)];
            }
            var estimate = statistic(sample);
            if (!double.IsNaN(estimate))
            {
                estimates.Add(estimate);
            }
        }
        if (estimates.Count == 0)
        {
            return null;
        }
        estimates.Sort();
        return new JsonArray(Value(Percentile(estimates, 0.05)), Value(Percentile(estimates, 0.95)));
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var position = p * (sorted.Count - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return sorted[low] + ((sorted[high] - sorted[low]) * (position - low));
    }

    private static double StandardDeviation(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    /// <summary>A number, or a numeric string such as the judge's "0.67"; "UNKNOWN", "N/A" and anything else are null.</summary>
    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }
        return
            value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? Score(JsonObject judgment, string dimension) =>
        Number(judgment["grades"]?["dimensions"]?[dimension]?["score"]) is { } score and >= 1 and <= 5 ? score : null;

    private static double? Hallucinations(JsonObject judgment) =>
        judgment["issues"] is JsonArray issues
            ? issues.Count(issue =>
                issue?["gapCategory"]?.GetValue<string>() == "hallucination"
                || (
                    issue?["secondaryCategories"] is JsonArray secondary
                    && secondary.Any(c => c?.GetValue<string>() == "hallucination")
                )
            )
            : null;

    private static JsonNode? Value(double? value) =>
        value is { } v && double.IsFinite(v) ? JsonValue.Create(Math.Round(v, 4)) : null;

    private static string Name(Arm arm) => arm.Variant ?? Path.GetFileNameWithoutExtension(arm.Database);

    private static string Short(string head) => head.Length > 8 ? head[..8] : head;

    private static string Format(JsonNode? node) =>
        node is null ? "-" : Number(node)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Each comparison's metrics in one line each, for the evidence record.</summary>
    private static string Observation(JsonObject result)
    {
        var lines = new List<string>();
        foreach (var comparison in result["comparisons"]!.AsArray())
        {
            var metrics = comparison!["metrics"]!.AsObject();
            lines.Add(
                $"{comparison["arm"]} vs {comparison["baseline"]} ({comparison["pairs"]!.AsArray().Count} pairs, "
                    + $"{comparison["unpaired"]!.AsArray().Count} unpaired): "
                    + string.Join(
                        "; ",
                        metrics.Select(m =>
                            $"{m.Key} n={m.Value!["n"]} delta={Format(m.Value["meanDelta"])} "
                            + $"ci90=[{Format(m.Value["ci90"]?[0])}, {Format(m.Value["ci90"]?[1])}]"
                            + (
                                m.Value["ratio"] is { } ratio
                                    ? $" ratio={Format(ratio["value"])} ci90=[{Format(ratio["ci90"]?[0])}, {Format(ratio["ci90"]?[1])}]"
                                    : ""
                            )
                        )
                    )
            );
        }
        return string.Join("\n", lines);
    }

    internal static string Markdown(JsonObject result)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Compare: {result["experimentId"]}");
        text.AppendLine();
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"KnowledgeBase tree `{result["knowledgeBaseTree"]}`. Judge: {result["judge"]?.ToJsonString()}."
        );
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"Intervals: 90% percentile bootstrap over PR pairs, {Resamples} resamples, seed {Seed}. Delta = arm minus baseline."
        );
        foreach (var comparison in result["comparisons"]!.AsArray())
        {
            text.AppendLine();
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"## {comparison!["arm"]} vs {comparison["baseline"]} ({comparison["pairs"]!.AsArray().Count} pairs)"
            );
            text.AppendLine();
            text.AppendLine("| Metric | Better | n | Baseline | Arm | Delta | 90% CI | SD of delta |");
            text.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var (key, m) in comparison["metrics"]!.AsObject())
            {
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {key} | {m!["better"]} | {m["n"]} | {Format(m["baselineMean"])} | {Format(m["armMean"])} | "
                        + $"{Format(m["meanDelta"])} | [{Format(m["ci90"]?[0])}, {Format(m["ci90"]?[1])}] | {Format(m["sdDelta"])} |"
                );
                if (m["ratio"] is { } ratio)
                {
                    text.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"| {key} ratio | at most {Format(ratio["limit"])} | {m["n"]} | | | {Format(ratio["value"])} | "
                            + $"[{Format(ratio["ci90"]?[0])}, {Format(ratio["ci90"]?[1])}] | |"
                    );
                }
            }
            foreach (var u in comparison["unpaired"]!.AsArray())
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- Unpaired {u!["pr"]}: {u["reason"]}");
            }
        }
        foreach (var arm in result["arms"]!.AsArray())
        {
            foreach (var e in arm!["excluded"]!.AsArray())
            {
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- Excluded from {arm["name"]}: {e!["pr"]}: {e["reason"]}"
                );
            }
        }
        return text.ToString();
    }
}
