using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     <c>judge close</c>: files one finished judgment and merges every review round of its PR into the store's
///     remote <c>main</c> as one commit with parents [main, round 1 tip, …, round k tip]. The commit is built in a
///     temporary index from freshly fetched main (JudgeSandbox.py close-prepare / close-commit, run by a local python3
///     on the host), so the store's HEAD, index, local main and working tree never move. The host's git credential
///     helper does the push, which is never forced: a moved main is fetched and composed again, up to 3 tries, then
///     exit 6. Idempotence lives on main: its CLOSING.md marker plus every round merged.
/// </summary>
internal static class JudgeClose
{
    private const int Tries = 3;

    /// <summary>Main moved on every try: nothing was pushed; a later run starts over.</summary>
    internal const int MainKeptMovingExitCode = 6;

    /// <summary>A Ctrl+C that also stopped the commit's python: its push is unknown. Program prints it.</summary>
    private const string CancelledDuringTheCommit =
        "Cancelled. If the push had started it may have landed; run close again (it says already closed or finishes the close).";

    private static readonly Regex Slug = new("^[a-z0-9][a-z0-9-]*$");
    private static readonly Regex Tag = new("^[A-Za-z0-9][A-Za-z0-9._-]*$");
    private static readonly string[] BadBodyText = ["<!-- judge-lesson", "<!-- reviewd-close"];

    /// <summary>
    ///     The files one close writes (one entry per path, full new content) and what it did not file. Added holds
    ///     one line per filed item; Ledger marks a ledger line (CLOSING.md links it to the judgment).
    /// </summary>
    internal sealed record Plan(
        SortedDictionary<string, string> Writes,
        List<(string Text, bool Ledger)> Added,
        List<string> NotFiled,
        List<string> FirstLines
    );

    /// <summary>Runs one close phase: (phase, args) → its JSON result.</summary>
    internal delegate Task<JsonObject> Phase(string name, JsonObject args, CancellationToken ct);

    public static Task<int> RunAsync(CliOptions options, CancellationToken ct) => RunAsync(options, ct, null);

    /// <summary>The CLI entry; <paramref name="phase" /> null runs the phases as a local python3 in --store.</summary>
    internal static async Task<int> RunAsync(CliOptions options, CancellationToken ct, Phase? phase)
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        try
        {
            var store = Path.GetFullPath(options.Required("store"));
            return await CloseAsync(options, store, phase ?? Local(store), ct);
        }
        // Program prints a cancelled close's message and exits 130. Only a commit the Ctrl+C also stopped throws
        // CancelledDuringTheCommit; every other cancel comes before the commit starts.
        catch (OperationCanceledException cancelled)
            when (ct.IsCancellationRequested && cancelled.Message != CancelledDuringTheCommit)
        {
            throw new OperationCanceledException(
                "Cancelled before the commit; nothing was committed or pushed.",
                cancelled,
                ct
            );
        }
        catch (Exception failure) when (!(failure is OperationCanceledException && ct.IsCancellationRequested))
        {
            Console.Error.WriteLine($"{repo.DisplayName} !{options.Required("pr")}: {failure.Message}");
            return 1;
        }
    }

    /// <summary>Runs a phase as a local python3 process in <paramref name="store" />.</summary>
    internal static Phase Local(string store) =>
        async (name, args, ct) =>
        {
            var start = new ProcessStartInfo("python3")
            {
                WorkingDirectory = store,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (
                var arg in new[]
                {
                    "-B",
                    "-c",
                    await JudgeRunner.ReadResourceAsync(JudgeRunner.ScriptResource, ct),
                    name,
                    args.ToJsonString(),
                }
            )
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var (stdout, stderr) = (await output, await error);
            if (process.ExitCode != 0)
            {
                // A push git accepted but close could not read back has landed, so its line does not say "failed".
                // It still exits 1: the operator runs close again.
                var failure = StepError(name, stderr);
                throw new InvalidOperationException(
                    Regex.IsMatch(failure, "^the push of [0-9a-f]+ was accepted but not confirmed: ")
                        ? $"close: push accepted but not confirmed (exit {process.ExitCode}): {failure}"
                        : $"close step {name} failed (exit {process.ExitCode}): {failure}"
                );
            }
            return JsonNode.Parse(stdout)?.AsObject()
                ?? throw new InvalidOperationException($"close step {name} printed no result.");
        };

    /// <summary>
    ///     The script's first error (after "judge &lt;phase&gt;: ERROR: "; a cleanup error after it is not the cause),
    ///     else stderr's last line, on one line.
    /// </summary>
    private static string StepError(string name, string error)
    {
        var marker = $"judge {name}: ERROR: ";
        var at = error.IndexOf(marker, StringComparison.Ordinal);
        var text =
            at >= 0
                ? error[(at + marker.Length)..].Split(marker)[0]
                : error.Split('\n').LastOrDefault(line => line.Trim().Length > 0) ?? "no error output";
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 400 ? text : text[..399] + "…";
    }

    private static async Task<int> CloseAsync(CliOptions options, string store, Phase phase, CancellationToken ct)
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var execute = options.Has("execute");
        var db = options.Required("db");
        if (!File.Exists(db))
            return Fail($"there is no database at {db}.");
        var prefix = $"reviewd/judge-run/{repo.NormalizedKey}/{pr}/";
        SortedDictionary<int, JsonObject> judgments;
        using (var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
        {
            await connection.OpenAsync(ct);
            judgments = JudgeBatch.Judgments(await JudgeBatch.RowsAsync(connection, prefix, ct), prefix);
        }
        if (judgments.Count == 0)
            return Refuse($"no judgment exists for {repo.RepoName} !{pr} in this database.");

        // ---- gate: one completed final judgment whose files still match its receipt. The default is the only
        // completed final one: with two, a test judgment could be filed, and a closed PR cannot be closed again.
        int n;
        if (options.GetCount("judgment") is { } chosen)
            n = chosen;
        else if (
            judgments.FirstOrDefault(j => JudgeRunner.ResumableStages.Contains(Text(j.Value, "stage"))) is
            { Value: not null } running
        )
            return Refuse($"judgment {running.Key} is still running; close after it ends or pass --judgment.");
        else
        {
            var finals = judgments.Where(j => IsFinal(j.Value)).Select(j => j.Key).ToList();
            if (finals.Count > 1)
                return Refuse(
                    $"judgments {string.Join(", ", finals)} are completed final judgments; pass --judgment <n> to pick one."
                );
            n = finals.FirstOrDefault();
        }
        if (!judgments.TryGetValue(n, out var receipt) || !IsFinal(receipt))
            return Refuse(
                n == 0 ? "no completed final judgment to close." : $"judgment {n} is not a completed final judgment."
            );
        var dir = Text(receipt, "judgmentDir");
        if (!Regex.IsMatch(dir, @"^PRs/[A-Za-z0-9][A-Za-z0-9._-]*/judgment-\d+$"))
            return Refuse($"judgment {n} has no valid judgmentDir.");
        foreach (var output in receipt["outputs"]?.AsArray() ?? [])
        {
            var path = Path.Combine(store, dir, Text(output!, "path"));
            if (
                !File.Exists(path)
                || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)))
                    != Text(output!, "sha256")
            )
                return Refuse($"{dir}/{Text(output!, "path")} is missing or differs from the judge receipt.");
        }
        var judgment = JsonNode
            .Parse(await File.ReadAllTextAsync(Path.Combine(store, dir, "judgment.json"), ct))!
            .AsObject();
        // The packet is not a receipt output; its sha in judgment.json says it is the one the judge read. The judge
        // can write both files, so the receipt's sha (recorded by judge-inputs) and the packet's own claims are
        // checked too.
        var packetBytes = await File.ReadAllBytesAsync(Path.Combine(store, dir, "inputs", "packet.json"), ct);
        var packetSha = Convert.ToHexStringLower(SHA256.HashData(packetBytes));
        if (packetSha != Text(judgment["inputs"], "packetSha256"))
            return Refuse(
                $"{dir}/inputs/packet.json is not the packet the judge read (judgment.json inputs.packetSha256)."
            );
        if (Text(receipt["inputs"], "packetSha256") is { Length: > 0 } written && written != packetSha)
            return Refuse(
                $"{dir}/inputs/packet.json is not the packet judge-inputs wrote (the receipt's inputs.packetSha256)."
            );
        var packet = JsonNode.Parse(packetBytes)!.AsObject();
        if (Text(packet["identity"]!, "status") != "completed")
            return Refuse("the packet says the PR is not completed.");
        var reviewDir = Text(packet["identity"]!, "reviewDirectory");
        var rounds = packet["review"]!["rounds"]!
            .AsArray()
            .Select(r => (Branch: Text(r!, "branch"), Tip: Text(r!, "tip")))
            .ToList();
        if (Text(packet["identity"]!, "prId") != pr)
            return Refuse($"the packet is for PR {Text(packet["identity"]!, "prId")}, not {pr}.");
        if (dir != $"{reviewDir}/judgment-{n}")
            return Refuse($"the packet's review dir {reviewDir} does not hold judgment {n} ({dir}).");
        var leaf = Regex.Escape(reviewDir[(reviewDir.LastIndexOf('/') + 1)..]);
        if (
            rounds.FirstOrDefault(r => !Regex.IsMatch(r.Branch, $"^review/{leaf}(-[A-Za-z0-9._-]+)?$")).Branch is
            { } stray
        )
            return Refuse($"round branch {stray} is not a review branch of {reviewDir}.");
        var cost = packet["cost"]!;
        var judgeUsage = receipt["judgeUsage"];
        Console.WriteLine(
            $"judgment {n} (finished {Text(receipt, "finishedAt").Split('T')[0]}; judge {Usd(judgeUsage?["usd"])}, review {Usd(cost["totalUsd"])}"
                + $"{(cost["lowerBound"]?.GetValue<bool>() == true ? " lower bound" : "")}): {judgment["knowledge"]?.AsArray().Count ?? 0} knowledge, "
                + $"{Plural(judgment["improvements"]?.AsArray().Count ?? 0, "improvement")}; {Plural(rounds.Count, "round")} of {reviewDir}"
        );

        // ---- sweep: an interrupted close (killed, power loss) leaves its private refs and scratch behind
        var swept = await phase("close-sweep", [], ct);
        if (Text(swept, "status") != "swept")
            throw new InvalidOperationException($"close-sweep returned {swept["status"]}.");
        foreach (var removed in (swept["removed"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var nonce = Text(removed, "nonce");
            var refs = removed["refs"]?.GetValue<int>() ?? 0;
            var what = new List<string>();
            if (refs > 0)
                what.Add(Plural(refs, "private ref"));
            if (removed["scratch"]?.GetValue<bool>() == true)
                what.Add("its scratch directory");
            Console.Error.WriteLine(
                $"removed a leftover close ({nonce[..Math.Min(8, nonce.Length)]}…, {(removed["ageMinutes"] is null ? "age unknown" : $"{Text(removed, "ageMinutes")} min old")}): {string.Join(" and ", what)}"
            );
        }

        var roundsJson = new JsonArray([
            .. rounds.Select(r => (JsonNode)new JsonObject { ["branch"] = r.Branch, ["tip"] = r.Tip }),
        ]);
        var (lastMain, detail) = ("", "");
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            var once = await OnceAsync();
            if (once.Exit is { } exit)
                return exit;
            (lastMain, detail) = (once.Main, once.Detail);
            if (attempt < Tries)
                Console.Error.WriteLine(
                    $"main moved since the fetch (try {attempt} of {Tries}); fetching the new main and composing again."
                );
        }
        Console.WriteLine(
            new JsonObject
            {
                ["judgment"] = n,
                ["status"] = "main-kept-moving",
                ["tries"] = Tries,
                ["main"] = lastMain,
                ["detail"] = detail,
            }.ToJsonString()
        );
        Console.Error.WriteLine("main kept moving; nothing was pushed. Run close again.");
        return MainKeptMovingExitCode;

        // One try: fetch main and the rounds, compose against that main, commit (and push). Exit null: main moved.
        async Task<(int? Exit, string Main, string Detail)> OnceAsync()
        {
            // ---- prepare: fetch main and every round into private refs; extract main's KnowledgeBase/ and Improvements/
            var common = new JsonObject
            {
                ["nonce"] = Guid.NewGuid().ToString("N"),
                ["remote"] = options.Get("remote") ?? "origin",
            };
            try
            {
                var prepared = await phase(
                    "close-prepare",
                    Merge(
                        common,
                        new JsonObject
                        {
                            ["reviewDir"] = reviewDir,
                            ["rounds"] = roundsJson.DeepClone(),
                            ["judgment"] = n,
                        }
                    ),
                    ct
                );
                if (Text(prepared, "status") == "refused")
                    return (Refuse(Text(prepared, "reason")), "", "");
                if (Text(prepared, "status") == "already-closed")
                {
                    Console.WriteLine(
                        $"already closed: main {Text(prepared, "main")[..9]} has judgment {n}'s CLOSING.md and every round."
                    );
                    return (0, "", "");
                }
                if (Text(prepared, "status") != "ready")
                    throw new InvalidOperationException($"close-prepare returned {prepared["status"]}.");
                var main = Text(prepared, "main");
                var scratch = Text(prepared, "scratch");
                // ---- compose against main's files, in this try's private scratch. Compose is not cancelled part way:
                // one check, right before the commit, decides whether a Ctrl+C stops this close.
                var tree = Path.Combine(store, scratch, "tree");
                var plan = Compose(
                    judgment,
                    repo.RepoName,
                    pr,
                    DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    path => File.Exists(Path.Combine(tree, path)) ? File.ReadAllText(Path.Combine(tree, path)) : null
                );
                foreach (var (path, content) in plan.Writes)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tree, path))!);
                    await File.WriteAllTextAsync(Path.Combine(tree, path), content, CancellationToken.None);
                }
                var closing = Closing(
                    n,
                    judgments,
                    judgment,
                    packet,
                    plan,
                    prepared["overridden"]!.AsArray().Select(p => p!.GetValue<string>()).ToList()
                );
                await File.WriteAllTextAsync(
                    Path.Combine(store, scratch, "CLOSING.md"),
                    closing,
                    CancellationToken.None
                );
                var files = new JsonArray([
                    .. plan.Writes.Keys.Select(p =>
                        (JsonNode)new JsonObject { ["path"] = p, ["source"] = $"{scratch}/tree/{p}" }
                    ),
                    new JsonObject { ["path"] = $"{reviewDir}/CLOSING.md", ["source"] = $"{scratch}/CLOSING.md" },
                    .. (receipt["outputs"]?.AsArray() ?? []).Select(o =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["path"] = $"{dir}/{Text(o!, "path")}",
                                ["source"] = $"{dir}/{Text(o!, "path")}",
                            }
                    ),
                ]);

                // ---- commit (and with --execute, push); close-commit always removes the private refs and scratch
                // A Ctrl+C before this point cancels: nothing irreversible has begun. Once the commit has started, a
                // cancel never stops the wait on it, because its push may land anyway; close reports what it did.
                // A commit the cancel stopped too (a terminal's Ctrl+C reaches python) ends cancelled: its push is
                // unknown.
                ct.ThrowIfCancellationRequested();
                JsonObject result;
                try
                {
                    result = await phase(
                        "close-commit",
                        Merge(
                            common,
                            new JsonObject
                            {
                                ["main"] = main,
                                ["rounds"] = roundsJson.DeepClone(),
                                ["files"] = files,
                                ["message"] = $"close: {repo.RepoName} !{pr} (judgment {n})",
                                ["push"] = execute,
                                ["reviewDir"] = reviewDir,
                            }
                        ),
                        CancellationToken.None
                    );
                }
                catch (Exception failure) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(CancelledDuringTheCommit, failure, ct);
                }
                if (ct.IsCancellationRequested)
                    Console.Error.WriteLine(
                        "Ctrl+C came during the commit; close waited for it to finish. What it did is below."
                    );
                // A refused commit prints nothing it composed: a refused file may hold a secret.
                if (Text(result, "status") == "refused")
                    return (Refuse(Text(result, "reason")), main, "");
                if (Text(result, "status") == "main-moved")
                    return (null, main, Text(result, "detail"));
                if (Text(result, "status") is not ("planned" or "closed"))
                    return (Fail($"close-commit returned {result["status"]}."), main, "");
                foreach (var line in plan.FirstLines)
                    Console.WriteLine($"body: {line}");
                foreach (var file in result["files"]?.AsArray() ?? [])
                    Console.WriteLine($"  +{Text(file!, "added")} -{Text(file!, "removed")}  {Text(file!, "path")}");
                Console.WriteLine(closing);
                Console.WriteLine(
                    Merge(
                            new JsonObject
                            {
                                ["judgment"] = n,
                                ["main"] = main,
                                ["notFiled"] = new JsonArray([.. plan.NotFiled.Select(s => (JsonNode)s)]),
                            },
                            result
                        )
                        .ToJsonString()
                );
                return (0, main, "");
            }
            finally
            {
                // After every try, whatever it did: a Ctrl+C can kill prepare's or commit's python before its own
                // cleanup runs. The abandon removes only this try's private refs and scratch, so a second run is
                // harmless.
                await phase(
                    "close-commit",
                    Merge(common, new JsonObject { ["abandon"] = true }),
                    CancellationToken.None
                );
            }
        }
    }

    /// <summary>
    ///     The KB entries and ledger lines for one judgment, composed against main's current files
    ///     (<paramref name="onMain" />: path → content, or null when absent). Every write to one path is folded into
    ///     one result, in judgment order.
    /// </summary>
    internal static Plan Compose(JsonObject judgment, string repo, string pr, string date, Func<string, string?> onMain)
    {
        var plan = new Plan(new SortedDictionary<string, string>(StringComparer.Ordinal), [], [], []);
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Read(string path) => current.TryGetValue(path, out var text) ? text : current[path] = onMain(path);
        void Write(string path, string text) => plan.Writes[path] = current[path] = text;
        var repoLedger = $"Improvements/{repo.ToLowerInvariant()}/process.md";

        foreach (var item in (judgment["knowledge"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var (id, policy, target, title, body) = (
                Text(item, "lessonId"),
                Text(item, "writePolicy"),
                Text(item, "target"),
                Text(item, "title"),
                Text(item, "body")
            );
            var sourcePr = Text(item, "sourcePr");
            var tags = (item["tags"]?.AsArray() ?? []).Select(t => t?.ToString() ?? "").ToList();
            var parts = target.Split('/');
            var reason =
                policy.Length == 0 ? "no writePolicy"
                : policy is not ("write-now" or "count-only") ? $"unknown writePolicy {policy}"
                : !Slug.IsMatch(id) ? "no valid lessonId"
                : null;
            if (reason is null && policy == "count-only")
            {
                Ledger(repoLedger, "knowledge", title, item["evidence"], id, policy);
                continue;
            }
            reason ??=
                parts is not ["KnowledgeBase", var scope, var file]
                || !Slug.IsMatch(scope)
                || scope == "developers"
                || !file.EndsWith(".md", StringComparison.Ordinal)
                || !Slug.IsMatch(file[..^3])
                    ? $"bad target {target}"
                : title.Trim().Length == 0 || body.Trim().Length == 0 ? "no title or body"
                : title.TrimStart().StartsWith("---", StringComparison.Ordinal) ? "title starts with front matter"
                : BadBodyText.Any(m => title.Contains(m, StringComparison.Ordinal))
                    ? "title carries a lesson or close marker"
                : body.TrimStart().StartsWith("---", StringComparison.Ordinal) ? "body starts with front matter"
                : BadBodyText.Any(m => body.Contains(m, StringComparison.Ordinal))
                    ? "body carries a lesson or close marker"
                : !Regex.IsMatch(sourcePr, @"^[a-z0-9][^"",\[\]\r\n]*$") ? "no valid sourcePr"
                : !tags.All(Tag.IsMatch) ? "bad tags"
                : null;
            if (reason is not null)
            {
                plan.NotFiled.Add($"{(id.Length > 0 ? id : OneLine(title, 80))}: {reason}");
                continue;
            }
            var marker = $"<!-- judge-lesson {id} -->";
            var existing = Read(target);
            if (existing?.Contains(marker, StringComparison.Ordinal) == true)
            {
                plan.NotFiled.Add($"{id}: already in {target}");
                continue;
            }
            // The judge's op is a hint: it read this host's local KB, which can lag main.
            var section =
                $"{marker}\n### {OneLine(title, 200)}\n\n{DemoteHeadings(body.Trim())}\n\nSeen in: {repo} !{pr}";
            if (existing is null)
                Write(
                    target,
                    KnowledgeAgent.BuildEntry(OneLine(title, 200), tags, parts[1], [sourcePr], date, section)
                );
            else if (KnowledgeIndex.ParseFrontmatter(target["KnowledgeBase/".Length..], existing) is { } meta)
                Write(
                    target,
                    KnowledgeAgent.BuildEntry(
                        meta.Title,
                        meta.Tags,
                        meta.Scope,
                        KnowledgeAgent.MergeSourcePrs(meta.SourcePrs, sourcePr),
                        date,
                        ReviewFeedbackAgent.StripFrontmatter(existing) + "\n\n" + section
                    )
                );
            else
            {
                plan.NotFiled.Add($"{id}: {target} on main has no front matter");
                continue;
            }
            plan.Added.Add(($"{OneLine(title, 200)} · {(existing is null ? "created" : "appended")} {target}", false));
            Listings(target, plan.Writes[target], created: existing is null);
            plan.FirstLines.Add($"{id}: {body.Trim().Split('\n')[0]}");
        }
        foreach (var item in (judgment["improvements"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var (key, policy, category) = (Text(item, "key"), Text(item, "writePolicy"), Text(item, "category"));
            if (key.Length == 0 || policy.Length == 0)
                plan.NotFiled.Add(
                    key.Length == 0 ? $"improvement ({OneLine(category, 40)}): no key" : $"{key}: no writePolicy"
                );
            else
                Ledger(
                    category == "process-generic" ? "Improvements/process-generic.md" : repoLedger,
                    category,
                    Text(item, "change"),
                    item["evidence"],
                    key,
                    policy
                );
        }
        return plan;

        // Main's listings are curated, not regenerator output (2fd62b92e and 436d0b702 both drift), so close changes
        // only the lines of entries it wrote: the entry's index line in place (or appended), a toc link for a new one.
        void Listings(string target, string content, bool created)
        {
            const string IndexPath = "KnowledgeBase/_index.jsonl",
                TocPath = "KnowledgeBase/_toc.md";
            var meta = KnowledgeIndex.ParseFrontmatter(target["KnowledgeBase/".Length..], content)!;
            var line = KnowledgeIndex.RenderIndex([meta]).TrimEnd('\n');
            List<string> index = [.. (Read(IndexPath) ?? "").Split('\n').Where(l => l.Length > 0)];
            var at = index.FindIndex(l => KnowledgeIndex.ParseIndex(l).FirstOrDefault()?.File == meta.File);
            if (at >= 0)
                index[at] = line;
            else
                index.Add(line);
            Write(IndexPath, string.Join('\n', index) + "\n");
            if (!created)
                return;
            List<string> toc = [.. (Read(TocPath) ?? KnowledgeTableOfContents.Header).TrimEnd('\n').Split('\n')];
            var scope = target.Split('/')[1];
            var heading = toc.IndexOf($"## {scope}");
            var link = $"- [{meta.Title}]({meta.File})";
            if (heading < 0)
            {
                // A new scope goes before the first later one (sections are alphabetical), else at the end.
                var next = toc.FindIndex(l =>
                    l.StartsWith("## ", StringComparison.Ordinal) && string.CompareOrdinal(l[3..], scope) > 0
                );
                if (next < 0)
                    toc.AddRange(["", $"## {scope}", "", link]);
                else
                    toc.InsertRange(next, [$"## {scope}", "", link, ""]);
            }
            else
            {
                var end = heading + 1;
                while (end < toc.Count && !toc[end].StartsWith("## ", StringComparison.Ordinal))
                    end++;
                while (toc[end - 1].Length == 0)
                    end--;
                // A section whose links are blank-separated gets a blank-separated link.
                var spaced = Enumerable
                    .Range(heading + 2, Math.Max(0, end - heading - 3))
                    .Any(i =>
                        toc[i].Length == 0
                        && toc[i - 1].StartsWith("- [", StringComparison.Ordinal)
                        && toc[i + 1].StartsWith("- [", StringComparison.Ordinal)
                    );
                toc.InsertRange(end, spaced ? ["", link] : [link]);
            }
            Write(TocPath, string.Join('\n', toc) + "\n");
        }

        // One line per miss; a rerun or a second judgment of the same PR never repeats a key.
        void Ledger(string path, string category, string change, JsonNode? evidence, string key, string policy)
        {
            var prefix = $"- {repo} !{pr} · ";
            var text = Read(path);
            if (
                text?.Split('\n')
                    .Any(l =>
                        l.StartsWith(prefix, StringComparison.Ordinal)
                        && l.Contains($" · key {key} · ", StringComparison.Ordinal)
                    ) == true
            )
            {
                plan.NotFiled.Add($"{key}: already in {path}");
                return;
            }
            var first = (evidence is JsonArray { Count: > 0 } list ? list[0] : evidence)?.ToString() ?? "none";
            text ??=
                $"# {(path == repoLedger ? repo : "All repositories:")} review process ledger\n\n"
                + "One line per judged miss: PR · category · change · first evidence · key · write policy · date. "
                + "judge close appends lines; it never edits one.\n\n";
            Write(
                path,
                text
                    + (text.EndsWith('\n') ? "" : "\n")
                    + $"{prefix}{OneLine(category, 40)} · {OneLine(change, 200)} · evidence: {OneLine(first, 120)} · key {OneLine(key, 120)} · {OneLine(policy, 20)} · {date}\n"
            );
            plan.Added.Add(($"{OneLine(change, 120)} · {path}", true));
        }
    }

    /// <summary>
    ///     PRs/&lt;dir&gt;/CLOSING.md: the marker close checks for idempotence, then what this close did and cost. The
    ///     total adds known costs only ("at least" when any part is unknown or a lower bound); an unknown judge cost is
    ///     listed under the table with its cap, never added.
    /// </summary>
    internal static string Closing(
        int n,
        SortedDictionary<int, JsonObject> judgments,
        JsonObject judgment,
        JsonObject packet,
        Plan plan,
        List<string> overridden
    )
    {
        var receipt = judgments[n];
        var (identity, cost) = (packet["identity"]!, packet["cost"]!);
        var rounds = packet["review"]!["rounds"]!.AsArray().OfType<JsonObject>().ToList();
        var reviewUsd = Amount(cost["totalUsd"]);
        var reviewLower =
            reviewUsd is null
            || cost["lowerBound"]?.GetValue<bool>() != false
            || cost["unknownThreads"] is JsonArray { Count: > 0 };
        var own = JudgeBatch.SpendOf($"judgment {n} (this one)", receipt, JudgeRunner.DefaultMaxUsd);
        var text = new StringBuilder()
            .Append(
                CultureInfo.InvariantCulture,
                $"<!-- reviewd-close judgment={n} review={Text(rounds[^1], "tip")} -->\n"
            )
            .Append(
                CultureInfo.InvariantCulture,
                $"# Closed: {Text(identity, "repo")} !{Text(identity, "prId")}: {OneLine(Text(identity, "title"), 200)}\n\n"
            )
            .Append(
                CultureInfo.InvariantCulture,
                $"- PR: {Text(identity, "status")}, closed {Date(Text(identity, "closedDate"))}\n"
            )
            .Append(
                CultureInfo.InvariantCulture,
                $"- Judgment {n}: [judgment-{n}/judgment.md](judgment-{n}/judgment.md), finished {Date(Text(receipt, "finishedAt"))}\n"
            )
            .Append(CultureInfo.InvariantCulture, $"- Verdict: {OneLine(Text(judgment, "verdict"), 400)}\n\n")
            .Append(
                CultureInfo.InvariantCulture,
                $"## Cost (USD; {Plural(rounds.Count, "review round")}; the host has no per-round split)\n\n"
            )
            .Append("| Run | USD | Tokens |\n|---|---:|---:|\n");
        // The host keeps cost per conversation, and one conversation can hold several rounds.
        var threads = (cost["threads"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        foreach (var (thread, i) in threads.Select((t, i) => (t, i + 1)))
        {
            var label =
                threads.Count > 1 ? $"Review conversation {i}"
                : rounds.Count == 1 ? "Review (round 1)"
                : $"Review (rounds 1-{rounds.Count})";
            text.Append(
                CultureInfo.InvariantCulture,
                $"| {label} | {Usd(thread["usd"])}{(thread["lowerBound"]?.GetValue<bool>() == false ? "" : " (lower bound)")} | {Tokens(thread["totalTokens"])} |\n"
            );
        }
        if (own.Unknown)
            text.Append(CultureInfo.InvariantCulture, $"| Judge, judgment {n} | unknown | |\n");
        else
            text.Append(
                CultureInfo.InvariantCulture,
                $"| Judge, judgment {n} | {Usd(own.Usd)}{(own.LowerBound ? " (lower bound)" : "")} | {Tokens(receipt["judgeUsage"]?["totalTokens"])} |\n"
            );
        // Every other judge run of this PR (one that never started a judge spent nothing).
        var others = judgments
            .Where(j => j.Key != n && j.Value["judgeThreadId"] is not null)
            .Select(j =>
                (
                    j.Key,
                    Receipt: j.Value,
                    Spend: JudgeBatch.SpendOf($"judgment {j.Key}", j.Value, JudgeRunner.DefaultMaxUsd)
                )
            )
            .ToList();
        var known = others.Where(o => !o.Spend.Unknown).ToList();
        if (known.Count > 0)
            text.Append(
                CultureInfo.InvariantCulture,
                $"| Other judge runs ({string.Join(", ", known.Select(o => o.Key))}) "
                    + $"| {Usd(known.Sum(o => o.Spend.Usd))}{(known.Any(o => o.Spend.LowerBound) ? " (lower bound)" : "")} "
                    + $"| {known.Sum(o => Amount(o.Receipt["judgeUsage"]?["totalTokens"]) ?? 0).ToString("N0", CultureInfo.InvariantCulture)} |\n"
            );
        var unknown = new[] { own }.Concat(others.Select(o => o.Spend)).Where(s => s.Unknown).ToList();
        var atLeast = reviewLower || own.LowerBound || unknown.Count > 0 || known.Any(o => o.Spend.LowerBound);
        var total = (reviewUsd ?? 0) + (own.Unknown ? 0 : own.Usd) + known.Sum(o => o.Spend.Usd);
        text.Append(CultureInfo.InvariantCulture, $"| Total | {(atLeast ? "at least " : "")}{Usd(total)} | |\n");
        if (unknown.Count > 0)
            text.Append('\n')
                .Append(string.Concat(unknown.Select(u => $"- {u.Label}: cost unknown, capped at {Usd(u.Cap)}\n")));
        text.Append("\n## Review rounds merged\n\n");
        foreach (var (round, i) in rounds.Select((r, i) => (r, i)))
            text.Append(
                CultureInfo.InvariantCulture,
                $"- round {i + 1}: {Text(round, "branch")} at {Text(round, "tip")[..Math.Min(9, Text(round, "tip").Length)]}\n"
            );
        if (overridden.Count > 0)
        {
            var under = Text(identity, "reviewDirectory") + "/";
            text.Append(
                    CultureInfo.InvariantCulture,
                    $"- Replaced by a later round (the older file stays in its round's history): {Plural(overridden.Count, "file")}\n"
                )
                .Append(
                    string.Concat(
                        overridden
                            .Take(10)
                            .Select(p =>
                                $"  - {(p.StartsWith(under, StringComparison.Ordinal) ? p[under.Length..] : p)}\n"
                            )
                    )
                );
            if (overridden.Count > 10)
                text.Append(CultureInfo.InvariantCulture, $"  - … and {overridden.Count - 10} more\n");
        }
        text.Append("\n## Added\n\n")
            .Append(
                plan.Added.Count == 0
                    ? "- nothing\n"
                    : string.Concat(
                        plan.Added.Select(a =>
                            a.Ledger ? $"- {a.Text} · [details](judgment-{n}/judgment.md)\n" : $"- {a.Text}\n"
                        )
                    )
            );
        text.Append("\n## Not filed\n\n")
            .Append(plan.NotFiled.Count == 0 ? "- nothing\n" : string.Concat(plan.NotFiled.Select(a => $"- {a}\n")));
        return text.ToString();
    }

    /// <summary>
    ///     Pushes each body heading below the entry's own "### title": the shallowest becomes ####, deeper ones keep
    ///     their depth under it (6 at most). Fenced code is left alone.
    /// </summary>
    private static string DemoteHeadings(string body)
    {
        var lines = body.Split('\n');
        var headings = new List<(int Line, int Level)>();
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (
                fence is null
                && (
                    trimmed.StartsWith("```", StringComparison.Ordinal)
                    || trimmed.StartsWith("~~~", StringComparison.Ordinal)
                )
            )
                fence = trimmed[..3];
            else if (fence is not null && trimmed.StartsWith(fence, StringComparison.Ordinal))
                fence = null;
            else if (fence is null && Regex.Match(lines[i], "^(#{1,6})[ \t]") is { Success: true } heading)
                headings.Add((i, heading.Groups[1].Length));
        }
        if (headings.Count == 0)
            return body;
        var shift = Math.Max(0, 4 - headings.Min(h => h.Level));
        foreach (var (i, level) in headings)
            lines[i] = new string('#', Math.Min(6, level + shift)) + lines[i][level..];
        return string.Join('\n', lines);
    }

    private static string Plural(int count, string word) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {word}{(count == 1 ? "" : "s")}");

    private static string Date(string timestamp) => timestamp.Split('T')[0];

    private static decimal? Amount(JsonNode? node) =>
        node is JsonValue value
        && (
            value.TryGetValue<decimal>(out var amount)
            || decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
        )
            ? amount
            : null;

    private static string Tokens(JsonNode? tokens) =>
        Amount(tokens) is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "";

    private static string OneLine(string text, int cap)
    {
        var line = Regex.Replace(text, @"[\r\n]+", " ").Replace('|', '/').Trim();
        return line.Length <= cap ? line : line[..(cap - 1)] + "…";
    }

    private static bool IsFinal(JsonObject receipt) =>
        Text(receipt, "stage") == "completed" && Text(receipt, "outputStage") == "final";

    private static string Text(JsonNode? node, string name) => node?[name] is JsonValue value ? value.ToString() : "";

    private static string Usd(JsonNode? usd) =>
        usd is JsonValue value && value.TryGetValue<decimal>(out var amount)
            ? "$" + amount.ToString("0.00", CultureInfo.InvariantCulture)
            : "unknown";

    private static string Usd(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);

    private static JsonObject Merge(JsonObject first, JsonObject second)
    {
        var merged = first.DeepClone().AsObject();
        foreach (var (key, value) in second)
            merged[key] = value?.DeepClone();
        return merged;
    }

    /// <summary>A refusal exits 1, like judge run; exit 2 is a usage error only.</summary>
    private static int Refuse(string reason)
    {
        Console.Error.WriteLine($"refused: {reason}");
        return 1;
    }

    private static int Fail(string reason)
    {
        Console.Error.WriteLine(reason);
        return 1;
    }
}
