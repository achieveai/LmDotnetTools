extern alias Reviewd;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     judge close: the compose rules (pure) and the two sandbox phases against a throwaway origin and store clone.
/// </summary>
public sealed class ReviewdJudgeCloseTests
{
    private const string SourcePr = "ado/azure-devops/o365exchange/weve_da/nova/5776213";

    private static JsonObject Lesson(
        string? id,
        string? policy,
        string target,
        string body = "The fact.\nWhy.",
        string op = "update"
    ) =>
        new()
        {
            ["op"] = op,
            ["target"] = target,
            ["title"] = $"Title {id}",
            ["tags"] = new JsonArray("nova", "scope"),
            ["scope"] = "system",
            ["body"] = body,
            ["evidence"] = new JsonArray("a.cs:1"),
            ["sourcePr"] = SourcePr,
            ["lessonId"] = id,
            ["writePolicy"] = policy,
        };

    private static JsonObject Miss(
        string? key,
        string category,
        string? policy = "count-only",
        string change = "Do the check."
    ) =>
        new()
        {
            ["key"] = key,
            ["category"] = category,
            ["change"] = change,
            ["evidence"] = new JsonArray("thread 1/2", "b.cs:3"),
            ["writePolicy"] = policy,
        };

    private static JsonObject Judgment(JsonObject[] knowledge, JsonObject[] improvements) =>
        new() { ["knowledge"] = new JsonArray([.. knowledge]), ["improvements"] = new JsonArray([.. improvements]) };

    private static int PrLines(string text) =>
        text.Split('\n').Count(line => line.StartsWith("- Nova !5776213 · ", StringComparison.Ordinal));

    [Fact]
    public void Close_compose_writes_each_file_once_with_one_ledger_line_per_miss_C4()
    {
        const string Ledger = "Improvements/nova/process.md";
        var existing =
            "# Nova review process ledger\n\n- Nova !1 · process-repo · old · evidence: e · key process-repo/x/old · count-only · 2026-10-01\n";
        var judgment = Judgment(
            [Lesson("nova-5776213-counted", "count-only", "KnowledgeBase/system/a.md")],
            [
                Miss("process-repo/x/one", "process-repo"),
                Miss("process-repo/x/two", "process-repo"),
                Miss("process-repo/x/three", "process-repo"),
                Miss("process-generic/x/four", "process-generic"),
            ]
        );

        var plan = JudgeClose.Compose(
            judgment,
            "Nova",
            "5776213",
            "2026-10-22",
            path => path == Ledger ? existing : null
        );

        plan.Writes.Keys.Should().Equal("Improvements/nova/process.md", "Improvements/process-generic.md");
        plan.Writes[Ledger].Should().StartWith(existing, "close only appends");
        PrLines(plan.Writes[Ledger]).Should().Be(4, "3 repo misses and 1 count-only lesson, each one line");
        PrLines(plan.Writes["Improvements/process-generic.md"]).Should().Be(1);
        plan.NotFiled.Should().BeEmpty();

        // The same misses again (a rerun on a main that already has them) add nothing.
        var again = JudgeClose.Compose(
            judgment,
            "Nova",
            "5776213",
            "2026-10-23",
            path => plan.Writes.GetValueOrDefault(path)
        );
        again.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Close_compose_appends_each_lesson_once_and_creates_a_missing_target()
    {
        var onMain = new Dictionary<string, string>
        {
            ["KnowledgeBase/system/existing.md"] =
                "---\ntitle: Existing\ntags: [a, b]\nscope: system\nsourcePrs: [\"ado/x/1\"]\nupdated: 2026-01-01\n---\n\nOld body.\n",
            ["KnowledgeBase/system/filed.md"] =
                "---\ntitle: Filed\ntags: [a]\nscope: system\nsourcePrs: [\"ado/x/1\"]\nupdated: 2026-01-01\n---\n\nOld.\n\n"
                + "<!-- judge-lesson nova-5776213-filed -->\n### Filed\n\nx\n\nSeen in: Nova !5776213\n",
        };
        var judgment = Judgment(
            [
                Lesson("nova-5776213-a", "write-now", "KnowledgeBase/system/existing.md", op: "create"),
                Lesson("nova-5776213-b", "write-now", "KnowledgeBase/system/existing.md"),
                Lesson("nova-5776213-c", "write-now", "KnowledgeBase/system/new-entry.md"),
                Lesson("nova-5776213-filed", "write-now", "KnowledgeBase/system/filed.md"),
                Lesson("nova-5776213-a", "write-now", "KnowledgeBase/system/existing.md"),
            ],
            []
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", onMain.GetValueOrDefault);

        plan.Writes.Keys.Should()
            .Equal(
                "KnowledgeBase/_index.jsonl",
                "KnowledgeBase/_toc.md",
                "KnowledgeBase/system/existing.md",
                "KnowledgeBase/system/new-entry.md"
            );
        var appended = plan.Writes["KnowledgeBase/system/existing.md"];
        var meta = KnowledgeIndex.ParseFrontmatter("system/existing.md", appended)!;
        meta.Title.Should().Be("Existing");
        meta.SourcePrs.Should().Equal("ado/x/1", SourcePr);
        meta.Updated.Should().Be("2026-10-22");
        appended.Should().Contain("Old body.");
        appended
            .Split("<!-- judge-lesson nova-5776213-a -->")
            .Should()
            .HaveCount(2, "lesson a is appended exactly once");
        appended
            .IndexOf("nova-5776213-a", StringComparison.Ordinal)
            .Should()
            .BeLessThan(appended.IndexOf("nova-5776213-b", StringComparison.Ordinal));
        appended.Split("Seen in: Nova !5776213").Should().HaveCount(3);
        var created = KnowledgeIndex.ParseFrontmatter(
            "system/new-entry.md",
            plan.Writes["KnowledgeBase/system/new-entry.md"]
        )!;
        created.Scope.Should().Be("system");
        created.SourcePrs.Should().Equal(SourcePr);
        plan.Writes["KnowledgeBase/system/new-entry.md"].Should().Contain("<!-- judge-lesson nova-5776213-c -->");
        plan.NotFiled.Should()
            .HaveCount(2)
            .And.OnlyContain(reason => reason.Contains("already", StringComparison.Ordinal));
        plan.FirstLines.Should().Contain("nova-5776213-c: The fact.");
    }

    [Fact]
    public void Close_compose_changes_only_the_listing_lines_of_the_entries_it_wrote()
    {
        // Main's listings are curated, not regenerator output: an unsorted index and a toc with its own preamble.
        var onMain = new Dictionary<string, string>
        {
            ["KnowledgeBase/system/existing.md"] =
                "---\ntitle: Existing\ntags: [a]\nscope: system\nsourcePrs: [\"ado/x/1\"]\nupdated: 2026-01-01\n---\n\nOld.\n",
            ["KnowledgeBase/_index.jsonl"] =
                "{\"title\":\"Zeta\", \"file\":\"system/zeta.md\",\"extra\":1}\n"
                + "{\"file\":\"system/existing.md\",\"title\":\"Existing\",\"sourcePrs\":[\"ado/x/1\"]}\n",
            ["KnowledgeBase/_toc.md"] =
                "# Knowledge Base\n\nCurated preamble.\n\n## developers\n\n- [Dev](developers/d.md)\n\n## system\n\n- [Existing](system/existing.md)\n- [Zeta](system/zeta.md)\n",
        };
        var judgment = Judgment(
            [
                Lesson("nova-5776213-a", "write-now", "KnowledgeBase/system/existing.md"),
                Lesson("nova-5776213-c", "write-now", "KnowledgeBase/system/new-entry.md"),
                Lesson("nova-5776213-t", "write-now", "KnowledgeBase/testing/new-scope.md"),
            ],
            []
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", onMain.GetValueOrDefault);

        var index = KnowledgeIndex.ParseIndex(plan.Writes["KnowledgeBase/_index.jsonl"]);
        index
            .Select(e => e.File)
            .Should()
            .Equal("system/zeta.md", "system/existing.md", "system/new-entry.md", "testing/new-scope.md");
        index[1].SourcePrs.Should().Equal("ado/x/1", SourcePr);
        plan.Writes["KnowledgeBase/_index.jsonl"]
            .Split('\n')[0]
            .Should()
            .Be(onMain["KnowledgeBase/_index.jsonl"].Split('\n')[0], "a line close did not write stays byte-identical");
        plan.Writes["KnowledgeBase/_toc.md"]
            .Should()
            .Be(
                "# Knowledge Base\n\nCurated preamble.\n\n## developers\n\n- [Dev](developers/d.md)\n\n## system\n\n- [Existing](system/existing.md)\n"
                    + "- [Zeta](system/zeta.md)\n- [Title nova-5776213-c](system/new-entry.md)\n\n## testing\n\n- [Title nova-5776213-t](testing/new-scope.md)\n"
            );
    }

    [Fact]
    public void Close_compose_does_not_file_malformed_or_marker_bearing_items_C5_C12()
    {
        var judgment = Judgment(
            [
                Lesson("nova-5776213-fm", "write-now", "KnowledgeBase/system/a.md", body: "---\ntitle: x\n---\nbody"),
                Lesson(
                    "nova-5776213-spoof",
                    "write-now",
                    "KnowledgeBase/system/a.md",
                    body: "x <!-- judge-lesson other --> y"
                ),
                Lesson(
                    "nova-5776213-close",
                    "write-now",
                    "KnowledgeBase/system/a.md",
                    body: "<!-- reviewd-close judgment=1 -->"
                ),
                Lesson("nova-5776213-nopolicy", null, "KnowledgeBase/system/a.md"),
                Lesson("nova-5776213-up", "write-now", "KnowledgeBase/../a.md"),
                Lesson("nova-5776213-dev", "write-now", "KnowledgeBase/developers/a.md"),
                Lesson("nova-5776213-case", "write-now", "KnowledgeBase/system/Bad_Name.md"),
                Lesson(null, "write-now", "KnowledgeBase/system/a.md"),
            ],
            [Miss(null, "process-repo"), Miss("process-repo/x/nopolicy", "process-repo", policy: null)]
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", _ => null);

        plan.Writes.Should().BeEmpty();
        plan.NotFiled.Should().HaveCount(10);
        plan.NotFiled.Should()
            .Contain(reason =>
                reason.StartsWith("nova-5776213-fm:", StringComparison.Ordinal)
                && reason.Contains("front matter", StringComparison.Ordinal)
            );
        plan.NotFiled.Should()
            .Contain(reason =>
                reason.StartsWith("nova-5776213-spoof:", StringComparison.Ordinal)
                && reason.Contains("marker", StringComparison.Ordinal)
            );
        plan.NotFiled.Should()
            .Contain(reason =>
                reason.StartsWith("nova-5776213-nopolicy:", StringComparison.Ordinal)
                && reason.Contains("writePolicy", StringComparison.Ordinal)
            );
        plan.NotFiled.Should()
            .Contain(reason =>
                reason.StartsWith("nova-5776213-dev:", StringComparison.Ordinal)
                && reason.Contains("target", StringComparison.Ordinal)
            );
    }

    [Fact]
    public void Close_compose_keeps_a_ledger_line_on_one_short_line_C12()
    {
        var change = "line one\r\nline two | pipe " + new string('x', 300);

        var plan = JudgeClose.Compose(
            Judgment([], [Miss("process-repo/x/long", "process-repo", change: change)]),
            "Nova",
            "5776213",
            "2026-10-22",
            _ => null
        );

        var line = plan.Writes["Improvements/nova/process.md"]
            .Split('\n')
            .Single(l => l.StartsWith("- Nova !5776213 · ", StringComparison.Ordinal));
        line.Should()
            .Contain("line one line two / pipe")
            .And.NotContain("|")
            .And.EndWith(" · key process-repo/x/long · count-only · 2026-10-22");
        line.Length.Should().BeLessThan(320);
    }

    internal static JsonObject Run(decimal? usd, string? thread = "t", decimal cap = 6m, long tokens = 100) =>
        new()
        {
            ["stage"] = "completed",
            ["outputStage"] = "final",
            ["finishedAt"] = "2026-10-22T13:00:00.1234567Z",
            ["judgeThreadId"] = thread,
            ["caps"] = new JsonObject { ["maxUsd"] = cap },
            ["judgeUsage"] = new JsonObject
            {
                ["measured"] = usd is not null,
                ["usd"] = usd,
                ["completeness"] = 2,
                ["totalTokens"] = usd is null ? null : tokens,
            },
        };

    internal static JsonObject Packet(int rounds = 1, int threads = 1) =>
        new()
        {
            ["identity"] = new JsonObject
            {
                ["repo"] = "Nova",
                ["prId"] = "5776213",
                ["title"] = "T",
                ["status"] = "completed",
                ["closedDate"] = "2026-10-20T06:40:54.8851748Z",
                ["reviewDirectory"] = "PRs/nova-5776213",
            },
            ["cost"] = new JsonObject
            {
                ["totalUsd"] = 3.42m * threads,
                ["lowerBound"] = false,
                ["threads"] = new JsonArray([
                    .. Enumerable
                        .Range(1, threads)
                        .Select(i =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["threadId"] = $"thread-1792160687408-188d8390f78040869e976dc0a16313c{i}",
                                    ["usd"] = 3.42m,
                                    ["lowerBound"] = false,
                                    ["totalTokens"] = 12858457,
                                }
                        ),
                ]),
            },
            ["review"] = new JsonObject
            {
                ["rounds"] = new JsonArray([
                    .. Enumerable
                        .Range(1, rounds)
                        .Select(i =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["branch"] = i == 1 ? "review/nova-5776213" : $"review/nova-5776213-r{i}",
                                    ["tip"] = new string((char)('a' + i), 40),
                                }
                        ),
                ]),
            },
        };

    [Fact]
    public void Close_cost_total_counts_known_costs_only_and_lists_unknown_runs_with_their_cap_D_C5()
    {
        // The total is known costs only ("at least" when anything is unknown or a lower bound); an
        // unknown run, the closed judgment included, is listed under the table with its cap and never added.
        var judgments = new SortedDictionary<int, JsonObject>
        {
            [4] = Run(5.67m),
            [5] = Run(0.33m),
            [6] = Run(null, cap: 0.5m),
            [7] = Run(null, thread: null),
        };

        var text = JudgeClose.Closing(4, judgments, new JsonObject { ["verdict"] = "v" }, Packet(), Plan(), []);
        var ownUnknown = JudgeClose.Closing(6, judgments, new JsonObject { ["verdict"] = "v" }, Packet(), Plan(), []);

        var lines = text.Split('\n');
        lines.Should().Contain("| Judge, judgment 4 | $5.67 | 100 |");
        lines
            .Should()
            .Contain("| Other judge runs (5) | $0.33 | 100 |", "a run that never started a judge spent nothing");
        lines.Should().Contain("- judgment 6: cost unknown, capped at $0.50");
        lines
            .Should()
            .Contain("| Total | at least $9.42 | |", "review 3.42 + judgment 4 5.67 + judgment 5 0.33; no cap");
        var own = ownUnknown.Split('\n');
        own.Should().Contain("| Judge, judgment 6 | unknown | |");
        own.Should().Contain("- judgment 6 (this one): cost unknown, capped at $0.50");
        own.Should().Contain("| Other judge runs (4, 5) | $6.00 | 200 |");
        own.Should().Contain("| Total | at least $9.42 | |");
    }

    private static JudgeClose.Plan Plan() => new([], [], [], []);

    [Fact]
    public void Close_closing_reads_well_on_a_phone()
    {
        var judgment = Judgment(
            [Lesson("nova-5776213-a", "write-now", "KnowledgeBase/system/new-entry.md")],
            [Miss("process-repo/x/one", "process-repo", change: "Check the filter before posting.")]
        );
        judgment["verdict"] = "v";
        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", _ => null);
        var judgments = new SortedDictionary<int, JsonObject> { [4] = Run(5.67m, tokens: 1234567) };

        var text = JudgeClose.Closing(
            4,
            judgments,
            judgment,
            Packet(rounds: 2),
            plan,
            ["PRs/nova-5776213/review.md", "PRs/nova-5776213/setup-context.json"]
        );

        var lines = text.Split('\n');
        lines.Should().Contain("- PR: completed, closed 2026-10-20", "a date is enough");
        lines.Should().Contain("- Judgment 4: [judgment-4/judgment.md](judgment-4/judgment.md), finished 2026-10-22");
        lines.Should().Contain("## Cost (USD; 2 review rounds; the host has no per-round split)");
        lines.Should().Contain("| Review (rounds 1-2) | $3.42 | 12,858,457 |", "a short label, not the thread id");
        lines.Should().Contain("| Judge, judgment 4 | $5.67 | 1,234,567 |");
        lines.Should().Contain("- Replaced by a later round (the older file stays in its round's history): 2 files");
        lines.Should().Contain("  - review.md");
        lines.Should().Contain("  - setup-context.json");
        lines.Should().Contain("- Title nova-5776213-a · created KnowledgeBase/system/new-entry.md");
        lines
            .Should()
            .Contain(
                "- Check the filter before posting. · Improvements/nova/process.md · [details](judgment-4/judgment.md)"
            );
        text.Should().NotContain("thread-1792160687408").And.NotContain(".1234567");
    }

    [Fact]
    public void Close_lists_at_most_ten_replaced_files()
    {
        var replaced = Enumerable.Range(1, 12).Select(i => $"PRs/nova-5776213/f{i:00}.md").ToList();

        var lines = JudgeClose
            .Closing(4, new() { [4] = Run(1m) }, new JsonObject { ["verdict"] = "v" }, Packet(2), Plan(), replaced)
            .Split('\n');

        lines.Should().Contain("- Replaced by a later round (the older file stays in its round's history): 12 files");
        lines.Should().Contain(["  - f10.md", "  - … and 2 more"]).And.NotContain("  - f11.md");
    }

    [Fact]
    public void Close_compose_does_not_file_a_title_with_a_marker_or_front_matter_D_C2()
    {
        var titled = (string title) =>
        {
            var lesson = Lesson("nova-5776213-t", "write-now", "KnowledgeBase/system/a.md");
            lesson["title"] = title;
            return lesson;
        };
        var judgment = Judgment(
            [
                titled("<!-- judge-lesson nova-5776918-other-lesson --> Title"),
                titled("Title <!-- reviewd-close judgment=1 -->"),
                titled("--- front matter"),
            ],
            []
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", _ => null);

        plan.Writes.Should().BeEmpty("a title marker would make a later lesson look already filed");
        plan.NotFiled.Should()
            .HaveCount(3)
            .And.OnlyContain(r => r.StartsWith("nova-5776213-t: title ", StringComparison.Ordinal));
    }

    [Fact]
    public void Close_compose_demotes_lesson_headings_below_the_entry_title()
    {
        var judgment = Judgment(
            [
                Lesson(
                    "nova-5776213-h",
                    "write-now",
                    "KnowledgeBase/system/a.md",
                    body: "## Why\n\nText.\n\n### Detail\n\n```bash\n# a shell comment\n```"
                ),
            ],
            []
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", _ => null);

        var entry = plan.Writes["KnowledgeBase/system/a.md"];
        entry.Should().Contain("### Title nova-5776213-h\n\n#### Why\n\nText.\n\n##### Detail");
        entry.Should().Contain("```bash\n# a shell comment\n```", "a fenced line is code, not a heading");
    }

    [Fact]
    public void Close_compose_inserts_a_new_toc_scope_alphabetically_and_follows_link_spacing()
    {
        var onMain = new Dictionary<string, string>
        {
            ["KnowledgeBase/_toc.md"] =
                "# Knowledge Base\n\n## api\n\n- [A](api/a.md)\n\n- [B](api/b.md)\n\n## system\n\n- [S](system/s.md)\n",
        };
        var judgment = Judgment(
            [
                Lesson("nova-5776213-a", "write-now", "KnowledgeBase/api/new-a.md"),
                Lesson("nova-5776213-m", "write-now", "KnowledgeBase/middleware/new-m.md"),
            ],
            []
        );

        var plan = JudgeClose.Compose(judgment, "Nova", "5776213", "2026-10-22", onMain.GetValueOrDefault);

        plan.Writes["KnowledgeBase/_toc.md"]
            .Should()
            .Be(
                "# Knowledge Base\n\n## api\n\n- [A](api/a.md)\n\n- [B](api/b.md)\n\n- [Title nova-5776213-a](api/new-a.md)\n\n"
                    + "## middleware\n\n- [Title nova-5776213-m](middleware/new-m.md)\n\n## system\n\n- [S](system/s.md)\n"
            );
    }

    [SkippableFact]
    public async Task Secret_pattern_catches_azure_client_secrets_and_passwords_but_not_placeholders_D_C3()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        string[] secrets =
        [
            "client_secret=Abc8Q~xyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123",
            "password=Sup3rS3cretPassw0rd!2026",
            "the app secret Xyz7Q~abcdefghijklmnopqrstuvwxyz012345 leaked",
            "\"clientSecret\": \"s3cr3tValue99\"",
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123",
            "AccountKey=abc",
        ];
        string[] clean =
        [
            "client_secret=<your-secret>",
            "password=***",
            "password=changeme123",
            "password=${DB_PASSWORD}",
            "password: string;",
            "var password = request.Password;",
            "ClientSecret = Configuration[\"AzureAd:ClientSecret\"]",
            "the password must be rotated every 90 days",
        ];

        var matched = await SecretMatchesAsync(python!, [.. secrets, .. clean]);

        matched.Take(secrets.Length).Should().AllBeEquivalentTo(true, "each is a real-looking secret");
        matched.Skip(secrets.Length).Should().AllBeEquivalentTo(false, "placeholders and code are not secrets");
    }

    // A SQL connection string's short form and a CLI flag with a space passed the
    // credential pattern. A password with no digit stays allowed (too noisy), and so do a shell's PWD path and a flag
    // fed from a variable.
    [SkippableFact]
    public async Task Secret_pattern_catches_Pwd_and_client_secret_flags()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        string[] secrets =
        [
            "Server=db;Database=x;Uid=svc;Pwd=Hunter2Hunter2;",
            "az ad sp credential reset --client-secret Abc123def456ghi789",
            "--client-secret=Abc123def456ghi789",
        ];
        string[] clean =
        [
            "password=CorrectHorseBatteryStaple",
            "PWD=/home/user1/repo2",
            "pwd = os.getcwd()",
            "--client-secret \"$CLIENT_SECRET\"",
            "--client-secret <secret>",
        ];

        var matched = await SecretMatchesAsync(python!, [.. secrets, .. clean]);

        matched.Take(secrets.Length).Should().AllBeEquivalentTo(true, "each is a real-looking secret");
        matched.Skip(secrets.Length).Should().AllBeEquivalentTo(false, "these are not secrets");
    }

    // Both scans read JSON text (json.dumps of ADO data at judge-inputs, the judge's judgment.json at close), where a
    // quote comes escaped. Each shape the README lists must match as written, JSON-escaped once and twice.
    [SkippableFact]
    public async Task Secret_pattern_catches_every_listed_shape_plain_and_json_escaped()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        string[] secrets =
        [
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123",
            "Authorization: Basic QUJDREVGR0hJSktMTU5PUFFSU1RVVldY",
            "Authorization: token 0123456789abcdef0123456789abcdef01234567",
            "\"Authorization\": \"Basic dXNlcjpwYXQxMjM0NTY3ODkwYWJjZGVm\"",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0",
            "DefaultEndpointsProtocol=https;AccountKey=abc",
            "SharedAccessSignature=sv=2022",
            "sig=abcdefghijklmnopqrstuvwxyz",
            "ghp_abcdefghijklmnopqrstuvwxyz0123456789",
            "-----BEGIN RSA PRIVATE KEY-----",
            "the app secret Xyz7Q~abcdefghijklmnopqrstuvwxyz012345 leaked",
            "password = \"Sup3rS3cret99\"",
            "\"password\": \"Sup3rS3cret99\"",
            "password='Sup3rS3cret99'",
            "client_secret = \"Abc123def456ghi\"",
            "\"clientSecret\": \"s3cr3tValue99\"",
            "Server=db;Uid=svc;Pwd=Hunter2Hunter2;",
            "--client-secret Abc123def456ghi789",
            "--client-secret=Abc123def456ghi789",
            "Authorization: Basic QWxhZGRpbjpvcGVuIHNlc2FtZTEyMzQ=",
            "Authorization: abc123def456ghi789",
        ];
        string[] clean =
        [
            "client_secret=<your-secret>",
            "password = \"<your-password>\"",
            "\"password\": \"changeme123\"",
            "\"password\": \"\"",
            "password=${DB_PASSWORD}",
            "\"clientSecret\": \"$CLIENT_SECRET\"",
            "Authorization: Basic <base64 of user:pat>",
            "Authorization: Bearer $TOKEN",
            "Authorization: Basic authentication is disabled",
            "Authorization: <redacted>",
            "Authorization: $AUTH_HEADER",
            "\"authorization\": \"required\"",
            "PWD=\"/home/user1/repo2\"",
            "--client-secret \"$CLIENT_SECRET\"",
            "the password must be rotated every 90 days",
        ];
        static string Escaped(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        static string[] Forms(string[] texts) =>
            [.. texts.SelectMany(t => new[] { t, Escaped(t), Escaped(Escaped(t)) })];

        var matched = await SecretMatchesAsync(python!, [.. Forms(secrets), .. Forms(clean)]);

        var samples = Forms(secrets).Concat(Forms(clean)).ToArray();
        samples
            .Where((_, i) => matched[i] != (i < secrets.Length * 3))
            .Should()
            .BeEmpty("every listed secret shape matches in each form, and no placeholder does");
    }

    [SkippableFact]
    public async Task Close_sweep_removes_only_its_own_leftovers_older_than_an_hour_D_C6()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var (old, fresh) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        foreach (var nonce in new[] { old, fresh })
        {
            universe.Git(universe.Store, "update-ref", $"refs/reviewd/close/{nonce}/main", "HEAD");
            Directory.CreateDirectory(Path.Combine(universe.Store, ".scratch", "reviewd-close", nonce, "tree"));
        }
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(
            Path.Combine(universe.Store, ".git", "refs", "reviewd", "close", old, "main"),
            twoHoursAgo
        );
        Directory.SetLastWriteTimeUtc(Path.Combine(universe.Store, ".scratch", "reviewd-close", old), twoHoursAgo);
        universe.Git(universe.Store, "update-ref", "refs/other/keep", "HEAD");

        var swept = await JudgeClose.Local(universe.Store)(
            "close-sweep",
            new JsonObject { ["nonce"] = Guid.NewGuid().ToString("N") },
            default
        );

        swept["removed"]!.AsArray().Select(r => r!["nonce"]!.GetValue<string>()).Should().Equal(old);
        universe
            .Git(universe.Store, "for-each-ref", "--format=%(refname)")
            .Split('\n')
            .Should()
            .Contain([$"refs/reviewd/close/{fresh}/main", "refs/other/keep"])
            .And.NotContain($"refs/reviewd/close/{old}/main");
        Directory.Exists(Path.Combine(universe.Store, ".scratch", "reviewd-close", old)).Should().BeFalse();
        Directory.Exists(Path.Combine(universe.Store, ".scratch", "reviewd-close", fresh)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_failed_close_step_names_the_step_and_the_git_error_not_a_sandbox()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        var dir = Directory.CreateTempSubdirectory("rj-close-nogit-").FullName;
        try
        {
            var prepare = () =>
                JudgeClose.Local(dir)(
                    "close-prepare",
                    new JsonObject
                    {
                        ["nonce"] = Guid.NewGuid().ToString("N"),
                        ["remote"] = Path.Combine(dir, "missing.git"),
                        ["reviewDir"] = "PRs/x",
                        ["judgment"] = 1,
                        ["rounds"] = new JsonArray(
                            new JsonObject { ["branch"] = "review/x", ["tip"] = new string('a', 40) }
                        ),
                    },
                    default
                );

            var failure = (await prepare.Should().ThrowAsync<InvalidOperationException>()).Which;

            // The first error is the cause (the fetch); the cleanup that fails after it in a non-repo comes second.
            failure.Message.Should().StartWith("close step close-prepare failed (exit 2): git fetch ");
            failure.Message.Should().NotContain("for-each-ref");
            failure.Message.Should().NotContain("Sandbox").And.NotContain("retained");
        }
        finally
        {
            RealGitFixture.ForceDelete(dir);
        }
    }

    [Fact]
    public void Close_help_lists_exit_6_for_a_main_that_kept_moving_and_130_for_ctrl_c()
    {
        var options = CliOptions.Parse(["judge", "close", "--help"]);
        var help = CliOptions.HelpFor(options.Command);

        var exitCodes = help[help.IndexOf("judge close exit codes:", StringComparison.Ordinal)..];
        exitCodes.Should().Contain("  6    main kept moving").And.Contain("  130  Ctrl+C");
        help.Should().Contain("only when exactly one completed final judgment exists");
    }

    // Runs the embedded JudgeSandbox.py's SECRET_PATTERN over each sample.
    private static async Task<bool[]> SecretMatchesAsync(string python, string[] samples) =>
        [
            .. (
                await ReviewdJudgeTests.SandboxAsync(
                    python,
                    "[bool(sandbox.SECRET_PATTERN.search(s)) for s in data]",
                    new JsonArray([.. samples.Select(s => (JsonNode)s)])
                )
            )
                .AsArray()
                .Select(m => m!.GetValue<bool>()),
        ];

    [Fact]
    public async Task Close_refusal_exits_1_like_judge_run()
    {
        var dir = Directory.CreateTempSubdirectory("rj-close-exit-").FullName;
        try
        {
            var db = Path.Combine(dir, "reviews.db");
            using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE cache (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
                create.ExecuteNonQuery();
            }
            var options = CliOptions.Parse([
                "judge",
                "close",
                "--repo",
                "O365Exchange/Weve_DA/Nova",
                "--pr",
                "5776213",
                "--db",
                db,
                "--store",
                dir,
            ]);

            var exit = await JudgeClose.RunAsync(options, default);

            exit.Should().Be(1, "no final judgment is a refusal, and refusals exit 1 like judge run; 2 is usage only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            RealGitFixture.ForceDelete(dir);
        }
    }

    // ---- the two phases, on a real origin and store clone

    [SkippableFact]
    public async Task Close_phases_merge_every_round_and_the_later_round_wins_C1()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        // Git for Windows' defaults, and a store that marks files as text: close composes against main's bytes, so the
        // scratch tree must not get CRLF.
        universe.Git(universe.Store, "config", "core.autocrlf", "true");
        universe.Git(universe.Store, "config", "core.eol", "crlf");
        File.WriteAllText(Path.Combine(universe.Store, ".git", "info", "attributes"), "* text\n");
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);

        prepared["status"]!.GetValue<string>().Should().Be("ready");
        prepared["main"]!.GetValue<string>().Should().Be(universe.Main);
        prepared["overridden"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal("PRs/x/review.md");
        var scratch = Path.Combine(universe.Store, prepared["scratch"]!.GetValue<string>());
        File.ReadAllText(Path.Combine(scratch, "tree/KnowledgeBase/system/a.md")).Should().Be("main entry\n");
        var closed = await phase("close-commit", universe.CommitArgs(prepared, push: true), default);

        closed["status"]!.GetValue<string>().Should().Be("closed");
        var commit = closed["commit"]!.GetValue<string>();
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(commit);
        universe
            .Git(universe.Origin, "rev-list", "--parents", "-n1", commit)
            .Split(' ')
            .Skip(1)
            .Should()
            .Equal(universe.Main, universe.Round1, universe.Round2);
        universe
            .Git(universe.Origin, "show", $"{commit}:PRs/x/review.md")
            .Should()
            .Be("round 2", "the later round wins");
        universe
            .Git(universe.Origin, "ls-tree", "--name-only", $"{commit}:PRs/x")
            .Split('\n')
            .Should()
            .Equal("CLOSING.md", "r1.md", "r2.md", "review.md");
        universe
            .Git(universe.Origin, "show", $"{commit}:KnowledgeBase/system/b.md")
            .Should()
            .Be("main moved", "main's own changes stay");
        universe
            .Git(universe.Origin, "log", "-1", "--format=%an <%ae>|%s", commit)
            .Should()
            .Be("Store Owner <owner@example.com>|close: X !1 (judgment 3)");
        universe.AssertClean(pushed: commit);
    }

    [SkippableFact]
    public async Task Close_prepare_says_already_closed_for_the_same_judgment_and_refuses_another()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var closed = universe.CloseByHand(judgment: 3);
        var phase = JudgeClose.Local(universe.Store);

        var again = await phase("close-prepare", universe.PrepareArgs(3), default);
        var other = await phase("close-prepare", universe.PrepareArgs(4), default);

        again["status"]!.GetValue<string>().Should().Be("already-closed");
        other["reason"]!.GetValue<string>().Should().Contain("re-closing is not supported");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(closed);
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_prepare_refuses_a_moved_round_and_a_PR_dir_changed_on_main()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);
        var moved = universe.PrepareArgs(3);
        moved["rounds"]![1]!["tip"] = universe.Main;

        var stale = await phase("close-prepare", moved, default);
        universe.CommitOnMain("PRs/x/note.md", "PR dir on main");
        var changed = await phase("close-prepare", universe.PrepareArgs(3), default);

        stale["reason"]!.GetValue<string>().Should().Contain("on the remote is");
        changed["reason"]!.GetValue<string>().Should().Contain("changed on main");
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_prepare_does_not_fetch_inside_a_populated_submodule()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var (round, submodule) = universe.PopulateSubmoduleAndBumpItsPin();
        var before = universe.Git(submodule, "for-each-ref", "--format=%(refname) %(objectname)");
        var phase = JudgeClose.Local(universe.Store);
        var args = universe.PrepareArgs(3);
        args["rounds"]!.AsArray().Add(round);

        var prepared = await phase("close-prepare", args, default);
        await phase(
            "close-commit",
            new JsonObject { ["nonce"] = args["nonce"]!.DeepClone(), ["abandon"] = true },
            default
        );

        prepared["status"]!.GetValue<string>().Should().Be("ready");
        universe
            .Git(submodule, "for-each-ref", "--format=%(refname) %(objectname)")
            .Should()
            .Be(before, "close fetches into the store's private refs only, never inside a submodule");
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_pushes_nothing_when_main_moves_after_the_fetch_C3()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        universe.CommitOnMain("KnowledgeBase/system/c.md", "other writer");
        var pushed = await phase("close-commit", universe.CommitArgs(prepared, push: true), default);

        pushed["status"]!.GetValue<string>().Should().Be("main-moved");
        pushed["detail"]!
            .GetValue<string>()
            .Should()
            .StartWith("! [rejected]", "the JSON line carries git's one rejected line, not a tail cut mid-word")
            .And.NotContain("\n");
        universe.Git(universe.Origin, "log", "-1", "--format=%s", "main").Should().Be("other writer");
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_says_closed_when_another_writer_built_on_it_before_the_check()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);
        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        universe.BuildOnMainRightAfterThePush();

        var closed = await phase("close-commit", universe.CommitArgs(prepared, push: true), default);

        closed["status"]!.GetValue<string>().Should().Be("closed");
        universe.Git(universe.Origin, "rev-parse", "main^").Should().Be(closed["commit"]!.GetValue<string>());
        universe.AssertClean(pushed: closed["commit"]!.GetValue<string>());
    }

    // git accepted the push, so a failed read of main after it is not a plain git failure: the close landed, and the
    // line does not say "failed" up front. The remote pushes to origin but reads from a missing path, so ls-remote
    // fails after the push.
    [SkippableFact]
    public async Task Close_commit_says_a_push_it_cannot_read_back_was_accepted_but_not_confirmed()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);
        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        args["remote"] = universe.SplitRemote(fetchUrl: universe.Origin + "-missing");

        var commit = () => phase("close-commit", args, default);

        var failure = (await commit.Should().ThrowAsync<InvalidOperationException>()).Which;
        var landed = universe.Git(universe.Origin, "rev-parse", "main");
        universe.Git(universe.Origin, "log", "-1", "--format=%s", landed).Should().Be("close: X !1 (judgment 3)");
        failure
            .Message.Should()
            .StartWith(
                $"close: push accepted but not confirmed (exit 2): the push of {landed[..9]} was accepted but not "
                    + "confirmed: git ls-remote failed ("
            )
            .And.Contain("; run close again: it says already closed.");
        universe.AssertClean();
    }

    // The other read: ls-remote shows a main without the close (the store's own), and the fetch that would confirm
    // it cannot write its private ref, which a ref below it blocks.
    [SkippableFact]
    public async Task Close_commit_says_accepted_but_not_confirmed_when_the_confirming_fetch_fails()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);
        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        args["remote"] = universe.SplitRemote(fetchUrl: universe.Store);
        universe.Git(universe.Store, "update-ref", $"refs/reviewd/close/{args["nonce"]}/after/blocker", "HEAD");

        var commit = () => phase("close-commit", args, default);

        var failure = (await commit.Should().ThrowAsync<InvalidOperationException>()).Which;
        universe.Git(universe.Origin, "log", "-1", "--format=%s", "main").Should().Be("close: X !1 (judgment 3)");
        failure
            .Message.Should()
            .StartWith("close: push accepted but not confirmed (exit 2): the push of ")
            .And.Contain(" was accepted but not confirmed: git fetch failed (")
            .And.Contain("; run close again: it says already closed.");
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_refuses_a_file_that_looks_like_a_secret_C11()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        File.AppendAllText(
            Path.Combine(universe.Store, prepared["scratch"]!.GetValue<string>(), "CLOSING.md"),
            "AccountKey=abc\n"
        );
        var refused = await phase("close-commit", args, default);

        refused["reason"]!
            .GetValue<string>()
            .Should()
            .Be("PRs/x/CLOSING.md matches a credential pattern; nothing was committed");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main);
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_scans_only_the_text_it_adds_not_old_text_on_main_C11()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        const string Entry = "KnowledgeBase/security/old.md";
        universe.CommitOnMain(Entry, "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123");
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: false);
        var scratch = prepared["scratch"]!.GetValue<string>();
        File.AppendAllText(Path.Combine(universe.Store, scratch, "tree", Entry), "\n\nA clean appended lesson.\n");
        args["files"]!.AsArray().Add(new JsonObject { ["path"] = Entry, ["source"] = $"{scratch}/tree/{Entry}" });
        var planned = await phase("close-commit", args, default);

        planned["status"]!.GetValue<string>().Should().Be("planned", "the token-like text was already on main");
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_refuses_a_secret_in_a_round_file_it_overlays_C11()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        universe.AddToRound2("PRs/x/leak.md", "sig=abcdefghijklmnopqrstuvwxyz");
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var refused = await phase("close-commit", universe.CommitArgs(prepared, push: true), default);

        refused["status"]!.GetValue<string>().Should().Be("refused", "an overlaid round file is text close adds");
        refused["reason"]!
            .GetValue<string>()
            .Should()
            .Be("PRs/x/leak.md matches a credential pattern; nothing was committed");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main);
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_refuses_a_judgment_whose_only_secret_is_an_escaped_quoted_password()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        const string Judgment = "PRs/x/judgment-3/judgment.json";
        Directory.CreateDirectory(Path.Combine(universe.Store, "PRs/x/judgment-3"));
        File.WriteAllText(
            Path.Combine(universe.Store, Judgment),
            """{"issues": [{"evidence": "password = \"Sup3rS3cret99\""}]}"""
        );
        universe.Rebaseline();
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        args["files"]!.AsArray().Add(new JsonObject { ["path"] = Judgment, ["source"] = Judgment });
        var refused = await phase("close-commit", args, default);

        refused["reason"]!
            .GetValue<string>()
            .Should()
            .Be($"{Judgment} matches a credential pattern; nothing was committed");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main);
        universe.AssertClean();
    }

    // A missing prepared index reads as an empty one: the tree would hold only this close's files, and a plain push
    // of it still descends from main.
    [SkippableFact]
    public async Task Close_commit_refuses_when_its_prepared_index_is_gone()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        File.Delete(Path.Combine(universe.Store, prepared["scratch"]!.GetValue<string>(), "index"));
        var refused = await phase("close-commit", args, default);

        refused["status"]!.GetValue<string>().Should().Be("refused");
        refused["reason"]!.GetValue<string>().Should().Contain("prepared index is gone");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main);
        universe.AssertClean();
    }

    [SkippableFact]
    public async Task Close_commit_refuses_a_tree_that_drops_a_path_on_main_outside_the_PR_dir()
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = Universe.Create();
        var phase = JudgeClose.Local(universe.Store);

        var prepared = await phase("close-prepare", universe.PrepareArgs(3), default);
        var args = universe.CommitArgs(prepared, push: true);
        var drop = new ProcessStartInfo("git") { WorkingDirectory = universe.Store, RedirectStandardError = true };
        drop.Environment["GIT_INDEX_FILE"] = Path.Combine(
            universe.Store,
            prepared["scratch"]!.GetValue<string>(),
            "index"
        );
        foreach (var arg in new[] { "rm", "-q", "--cached", "KnowledgeBase/system/a.md" })
            drop.ArgumentList.Add(arg);
        using (var git = Process.Start(drop)!)
        {
            var error = await git.StandardError.ReadToEndAsync();
            await git.WaitForExitAsync();
            git.ExitCode.Should().Be(0, error);
        }
        var refused = await phase("close-commit", args, default);

        refused["status"]!.GetValue<string>().Should().Be("refused");
        refused["reason"]!.GetValue<string>().Should().Contain("KnowledgeBase/system/a.md");
        universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main);
        universe.AssertClean();
    }

    /// <summary>A bare origin with main and two sibling review rounds of PRs/x, and a store clone of it.</summary>
    internal sealed class Universe : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rj-close-" + Guid.NewGuid().ToString("N"));
        private string _nonce = "";
        private string _baseline = "";
        private string _tracking = "";

        public string Origin => Path.Combine(_root, "origin.git");
        public string Store => Path.Combine(_root, "store");
        private string Seed => Path.Combine(_root, "seed");
        public string Main { get; private set; } = "";
        public string Round1 { get; private set; } = "";
        public string Round2 { get; private set; } = "";

        public static Universe Create()
        {
            var u = new Universe();
            Directory.CreateDirectory(u._root);
            u.Git(u._root, "init", "-q", "--bare", "--initial-branch=main", "origin.git");
            u.Git(u._root, "init", "-q", "--initial-branch=main", "seed");
            var basis = u.Commit("KnowledgeBase/system/a.md", "main entry\n", "base");
            u.Round1 = u.Branch("review/x", basis, ("PRs/x/review.md", "round 1"), ("PRs/x/r1.md", "1"));
            u.Round2 = u.Branch("review/x-2e69ca4b", basis, ("PRs/x/review.md", "round 2"), ("PRs/x/r2.md", "2"));
            u.Git(u.Seed, "checkout", "-q", "main");
            u.Main = u.Commit("KnowledgeBase/system/b.md", "main moved", "main moved");
            u.Git(u.Seed, "push", "-q", u.Origin, "main", "review/x", "review/x-2e69ca4b");
            u.Git(u._root, "clone", "-q", u.Origin, "store");
            u.Git(u.Store, "config", "user.name", "Store Owner");
            u.Git(u.Store, "config", "user.email", "owner@example.com");
            u.Rebaseline();
            return u;
        }

        /// <summary>
        /// Records the store's HEAD, local main, index, status and refs; AssertClean compares against it. origin/main
        /// (and origin/HEAD, which points at it) is recorded apart, because a push may move it.
        /// </summary>
        public void Rebaseline() => (_baseline, _tracking) = (Fingerprint(), Tracking());

        private string Tracking() => Git(Store, "rev-parse", "refs/remotes/origin/main");

        private string Fingerprint() =>
            string.Join(
                '\n',
                "HEAD " + Git(Store, "rev-parse", "HEAD"),
                "main " + Git(Store, "rev-parse", "refs/heads/main"),
                "index "
                    + Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(
                            File.ReadAllBytes(Path.Combine(Store, ".git", "index"))
                        )
                    ),
                "status " + Git(Store, "--no-optional-locks", "status", "--porcelain=v1", "-uall"),
                "refs "
                    + string.Join(
                        '\n',
                        Git(Store, "for-each-ref", "--format=%(refname) %(objectname)")
                            .Split('\n')
                            .Where(r =>
                                !r.StartsWith("refs/remotes/origin/HEAD ", StringComparison.Ordinal)
                                && !r.StartsWith("refs/remotes/origin/main ", StringComparison.Ordinal)
                            )
                    )
            );

        public JsonObject PrepareArgs(int judgment)
        {
            _nonce = Guid.NewGuid().ToString("N");
            return new JsonObject
            {
                ["nonce"] = _nonce,
                ["remote"] = Origin,
                ["reviewDir"] = "PRs/x",
                ["judgment"] = judgment,
                ["rounds"] = Rounds(),
            };
        }

        public JsonObject CommitArgs(JsonObject prepared, bool push)
        {
            File.WriteAllText(
                Path.Combine(Store, prepared["scratch"]!.GetValue<string>(), "CLOSING.md"),
                "<!-- reviewd-close judgment=3 review=x -->\n"
            );
            return new()
            {
                ["nonce"] = _nonce,
                ["remote"] = Origin,
                ["main"] = prepared["main"]!.DeepClone(),
                ["rounds"] = Rounds(),
                ["files"] = new JsonArray(
                    new JsonObject { ["path"] = "PRs/x/CLOSING.md", ["source"] = $"{prepared["scratch"]}/CLOSING.md" }
                ),
                ["message"] = "close: X !1 (judgment 3)",
                ["push"] = push,
                ["reviewDir"] = "PRs/x",
            };
        }

        private JsonArray Rounds() =>
            new(
                new JsonObject { ["branch"] = "review/x", ["tip"] = Round1 },
                new JsonObject { ["branch"] = "review/x-2e69ca4b", ["tip"] = Round2 }
            );

        public void CommitOnMain(string path, string message)
        {
            Commit(path, message, message);
            Git(Seed, "push", "-q", Origin, "main");
        }

        public void AddToRound2(string path, string content)
        {
            Git(Seed, "checkout", "-q", "review/x-2e69ca4b");
            Round2 = Commit(path, content, "round 2 again");
            Git(Seed, "push", "-q", Origin, "review/x-2e69ca4b");
            Git(Seed, "checkout", "-q", "main");
        }

        /// <summary>
        /// Gives main a submodule repos/src that the store has populated, then pushes round review/x-5ab0c1d2, which
        /// moves its pin to a commit the store's copy has never fetched. Returns that round and the submodule's path.
        /// </summary>
        public (JsonObject Round, string Submodule) PopulateSubmoduleAndBumpItsPin()
        {
            var src = Path.Combine(_root, "src");
            Git(_root, "init", "-q", "--initial-branch=main", "src");
            string CommitInSource(string content)
            {
                File.WriteAllText(Path.Combine(src, "a.txt"), content);
                Git(src, "add", "-A");
                Git(src, "commit", "-q", "-m", content);
                return Git(src, "rev-parse", "HEAD");
            }
            var pinned = CommitInSource("1");
            File.WriteAllText(
                Path.Combine(Seed, ".gitmodules"),
                // Forward slashes: git config reads a backslash in a Windows path as an escape.
                $"[submodule \"repos/src\"]\n\tpath = repos/src\n\turl = {src.Replace('\\', '/')}\n"
            );
            Git(Seed, "add", ".gitmodules");
            Git(Seed, "update-index", "--add", "--cacheinfo", $"160000,{pinned},repos/src");
            Git(Seed, "commit", "-q", "-m", "add source");
            Main = Git(Seed, "rev-parse", "HEAD");
            Git(Seed, "push", "-q", Origin, "main");
            Git(Store, "pull", "-q", "--ff-only");
            Git(Store, "-c", "protocol.file.allow=always", "submodule", "update", "-q", "--init");
            Git(Store, "config", "protocol.file.allow", "always");
            var bumped = CommitInSource("2");
            Git(Seed, "checkout", "-q", "-b", "review/x-5ab0c1d2");
            Directory.CreateDirectory(Path.Combine(Seed, "PRs/x"));
            File.WriteAllText(Path.Combine(Seed, "PRs/x/r3.md"), "3");
            Git(Seed, "add", "PRs/x/r3.md");
            Git(Seed, "update-index", "--cacheinfo", $"160000,{bumped},repos/src");
            Git(Seed, "commit", "-q", "-m", "round 3 bumps the pin");
            var tip = Git(Seed, "rev-parse", "HEAD");
            Git(Seed, "checkout", "-q", "main");
            Git(Seed, "push", "-q", Origin, "review/x-5ab0c1d2");
            Rebaseline();
            return (
                new JsonObject { ["branch"] = "review/x-5ab0c1d2", ["tip"] = tip },
                Path.Combine(Store, "repos/src")
            );
        }

        /// <summary>Another writer: a post-receive hook on origin commits on top of the next pushed main.</summary>
        public void BuildOnMainRightAfterThePush()
        {
            var hook = Path.Combine(Origin, "hooks", "post-receive");
            File.WriteAllText(
                hook,
                """
                #!/bin/sh
                while read old new ref; do
                    [ "$ref" = refs/heads/main ] || continue
                    export GIT_AUTHOR_NAME=Other GIT_AUTHOR_EMAIL=other@example.com
                    export GIT_COMMITTER_NAME=Other GIT_COMMITTER_EMAIL=other@example.com
                    git update-ref refs/heads/main "$(git commit-tree "$new^{tree}" -p "$new" -m other)" "$new"
                done
                rm -f "$0"

                """.Replace("\r\n", "\n", StringComparison.Ordinal)
            );
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>
        /// Adds the store remote "split", which pushes to origin and reads (ls-remote, fetch) from
        /// <paramref name="fetchUrl" />. It has no fetch refspec, so a push to it moves no refs/remotes ref.
        /// </summary>
        public string SplitRemote(string fetchUrl)
        {
            Git(Store, "config", "remote.split.url", fetchUrl);
            Git(Store, "config", "remote.split.pushurl", Origin);
            return "split";
        }

        /// <summary>Puts a close of <paramref name="judgment" /> on origin main by hand: parents [main, r1, r2] plus CLOSING.md.</summary>
        public string CloseByHand(int judgment)
        {
            Directory.CreateDirectory(Path.Combine(Seed, "PRs/x"));
            File.WriteAllText(
                Path.Combine(Seed, "PRs/x/CLOSING.md"),
                $"<!-- reviewd-close judgment={judgment} review=x -->\n"
            );
            Git(Seed, "add", "-A");
            var commit = Git(
                Seed,
                "commit-tree",
                Git(Seed, "write-tree"),
                "-p",
                Main,
                "-p",
                Round1,
                "-p",
                Round2,
                "-m",
                "closed"
            );
            Git(Seed, "push", "-q", Origin, $"{commit}:refs/heads/main");
            return commit;
        }

        /// <param name="pushed">
        /// The commit close pushed to origin main, if it pushed one. git moves origin/main to it after a push to origin,
        /// and from Git 2.56 also after a push to origin's URL, which is how these tests name the remote.
        /// </param>
        public void AssertClean(string? pushed = null)
        {
            Git(Store, "for-each-ref", "refs/reviewd").Should().BeEmpty("close deletes its private refs");
            Fingerprint()
                .Should()
                .Be(_baseline, "close never moves the store's HEAD, local main, index, status or refs");
            Tracking()
                .Should()
                .BeOneOf(
                    pushed is null ? [_tracking] : [_tracking, pushed],
                    "only git's own update after a push moves origin/main; close's fetches use --refmap="
                );
            var scratch = Path.Combine(Store, ".scratch", "reviewd-close");
            (Directory.Exists(scratch) ? Directory.GetFileSystemEntries(scratch) : [])
                .Should()
                .BeEmpty("close deletes its scratch");
        }

        private string Commit(string path, string content, string message)
        {
            var file = Path.Combine(Seed, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            Git(Seed, "add", "-A");
            Git(Seed, "commit", "-q", "-m", message);
            return Git(Seed, "rev-parse", "HEAD");
        }

        private string Branch(string name, string from, params (string Path, string Content)[] files)
        {
            Git(Seed, "checkout", "-q", "-b", name, from);
            foreach (var (path, content) in files)
                Commit(path, content, name);
            return Git(Seed, "rev-parse", "HEAD");
        }

        public string Git(string dir, params string[] args)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in (string[])["-c", "user.name=Seed", "-c", "user.email=seed@example.com", .. args])
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)}: {error}");
            return output.TrimEnd('\n');
        }

        public void Dispose() => RealGitFixture.ForceDelete(_root);
    }
}
