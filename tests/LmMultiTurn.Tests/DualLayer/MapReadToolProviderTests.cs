using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// map_read: one planner call fans out to one cheap reader per document, readers abstain on
/// documents that do not bear on the instruction, and the planner only ever sees the records.
/// </summary>
public class MapReadToolProviderTests
{
    private const string Thread = "executor-thread-1";
    private const string Model = "cheap-model";

    /// <summary>Answers each reader call from the document label; counts how many run at once.</summary>
    private sealed class ScriptedReader : IAgent
    {
        private int _inFlight;

        public Func<string, string, string> Answer { get; set; } = (_, _) => "record";

        public List<(string System, string User)> Calls { get; } = [];

        public int PeakInFlight { get; private set; }

        public bool EmitUsage { get; set; }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var list = messages.Cast<TextMessage>().ToList();
            var system = list.Single(m => m.Role == Role.System).Text;
            var user = list.Single(m => m.Role == Role.User).Text;
            lock (Calls)
            {
                Calls.Add((system, user));
                PeakInFlight = Math.Max(PeakInFlight, ++_inFlight);
            }

            await Task.Delay(20, cancellationToken);
            lock (Calls)
            {
                _inFlight--;
            }

            var reply = new List<IMessage>
            {
                new TextMessage { Role = Role.Assistant, Text = Answer(system, user) },
            };
            if (EmitUsage)
            {
                reply.Add(
                    new UsageMessage
                    {
                        Usage = new Usage
                        {
                            PromptTokens = 100,
                            CompletionTokens = 5,
                            TotalTokens = 105,
                        },
                    }
                );
            }

            return reply;
        }
    }

    private sealed class RecordingSink : IUsageSink
    {
        public List<UsageRecord> Records { get; } = [];

        public void RecordUsage(UsageRecord observation)
        {
            lock (Records)
            {
                Records.Add(observation);
            }
        }
    }

    private static ToolHandler ReadFrom(IDictionary<string, string> files) =>
        (args, _, _) =>
        {
            var path = JsonNode.Parse(args)!["file_path"]!.GetValue<string>();
            return Task.FromResult<ToolHandlerResult>(
                files.TryGetValue(path, out var text)
                    ? ToolHandlerResult.FromText(text)
                    : ToolHandlerResult.FromError($"File not found: {path}")
            );
        };

    private static ToolHandler GlobReturning(string output, List<string>? calls = null) =>
        (args, _, _) =>
        {
            calls?.Add(args);
            return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(output));
        };

    private static MapReadToolProvider Create(
        ScriptedReader reader,
        IDictionary<string, string> files,
        RecordingSink? sink = null,
        ToolHandler? glob = null,
        MapReadOptions? options = null
    ) =>
        new(
            reader,
            new GenerateReplyOptions { ModelId = Model },
            ReadFrom(files),
            glob,
            sink ?? new RecordingSink(),
            Thread,
            options
        );

    private static async Task<ToolHandlerResult.Resolved> Invoke(MapReadToolProvider provider, string argsJson)
    {
        var result = await provider
            .GetFunctions()
            .Single()
            .Handler(argsJson, new ToolCallContext { ToolCallId = "call-1" }, default);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
    }

    private static string Args(string instruction, params string[] paths) =>
        new JsonObject { ["instruction"] = instruction, ["paths"] = string.Join('\n', paths) }.ToJsonString();

    private static readonly Dictionary<string, string> Tickets = new()
    {
        ["/w/t1.md"] = "Ticket 1: outage 09:00-09:30, gold tier.",
        ["/w/t2.md"] = "Ticket 2: password reset.",
        ["/w/t3.md"] = "Ticket 3: outage 14:00-15:00, silver tier.",
    };

    [Fact]
    public async Task Each_document_gets_its_own_reader_call_and_abstentions_are_left_out()
    {
        var reader = new ScriptedReader
        {
            Answer = (_, user) =>
                user.Contains("outage") ? "outage | " + (user.Contains("gold") ? "gold" : "silver") : "ABSTAIN",
        };
        var provider = Create(reader, Tickets);

        var result = await Invoke(provider, Args("outage start | tier; abstain unless an outage", [.. Tickets.Keys]));

        result.Payload.IsError.Should().BeFalse();
        var lines = result.Payload.Text.Split('\n');
        lines[0]
            .Should()
            .Be(
                "map_read: 3 documents read (3 parts), 2 reported, 1 abstained, 0 errors. Paths are under /w/ (- = not stated)."
            );
        // Named paths: the planner chose t2.md, so its abstention is reported, not silently dropped.
        lines.Skip(1).Should().BeEquivalentTo("t1.md | outage | gold", "t3.md | outage | silver", "abstained: t2.md");
        // The planner never sees document text; the readers see the instruction and exactly one document each.
        result.Payload.Text.Should().NotContain("password reset");
        reader.Calls.Should().HaveCount(3);
        reader.Calls.Should().OnlyContain(c => c.System.Contains("abstain unless an outage"));
        reader.Calls.Select(c => c.User).Should().Contain(u => u.StartsWith("Document: /w/t2.md"));
    }

    [Fact]
    public async Task Readers_run_in_parallel_up_to_the_configured_limit()
    {
        var reader = new ScriptedReader();
        var files = Enumerable.Range(0, 12).ToDictionary(i => $"/w/{i}.md", i => $"doc {i}");
        var provider = Create(reader, files, options: new MapReadOptions { MaxParallel = 4 });

        await Invoke(provider, Args("anything", [.. files.Keys]));

        reader.Calls.Should().HaveCount(12);
        reader.PeakInFlight.Should().BeInRange(2, 4);
    }

    [Fact]
    public async Task A_glob_resolves_through_the_executor_glob_tool_and_merges_with_named_paths()
    {
        var reader = new ScriptedReader();
        var globCalls = new List<string>();
        var glob = GlobReturning("/w/t1.md\n/w/t2.md\n\n2 files matched\n", globCalls);
        var provider = Create(reader, Tickets, glob: glob);
        var args = new JsonObject
        {
            ["instruction"] = "tier",
            ["paths"] = "/w/t3.md, /w/t1.md",
            ["glob_path"] = "/w",
            ["glob"] = "*.md",
        }.ToJsonString();

        var result = await Invoke(provider, args);

        globCalls
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("\"pattern\":\"*.md\"")
            .And.Contain("\"path\":\"/w\"");
        result.Payload.Text.Split('\n')[0].Should().StartWith("map_read: 3 documents read");
        // Over a glob, abstaining is the filter: no list of abstained names.
        result.Payload.Text.Should().NotContain("abstained:");
        reader
            .Calls.Select(c => c.User.Split('\n')[0])
            .Should()
            .BeEquivalentTo("Document: /w/t3.md", "Document: /w/t1.md", "Document: /w/t2.md");
    }

    [Fact]
    public async Task Every_reader_call_is_billed_to_the_executor_as_its_own_record()
    {
        var reader = new ScriptedReader { EmitUsage = true };
        var sink = new RecordingSink();
        var provider = Create(reader, Tickets, sink);

        await Invoke(provider, Args("tier", [.. Tickets.Keys]));

        // Three identical usage messages must stay three records, not MAX-merge into one.
        sink.Records.Should().HaveCount(3);
        sink.Records.Select(r => r.ProviderAttemptId).Should().OnlyHaveUniqueItems();
        sink.Records.Should().OnlyContain(r => r.ExecutionKind == UsageExecutionKind.Executor);
        sink.Records.Should().OnlyContain(r => r.RootConversationId == Thread);
        sink.Records.Should().OnlyContain(r => r.RequestedModel == Model && r.InputTokens == 100);
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_is_reported_as_an_error_line_not_a_failure()
    {
        var reader = new ScriptedReader();
        var provider = Create(reader, Tickets);

        var result = await Invoke(provider, Args("tier", "/w/t1.md", "/w/missing.md"));

        result.Payload.IsError.Should().BeFalse();
        var lines = result.Payload.Text.Split('\n');
        lines[0].Should().StartWith("map_read: 2 documents read (1 parts), 1 reported, 0 abstained, 1 errors.");
        lines.Should().Contain("missing.md | ERROR: File not found: /w/missing.md");
        reader.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_long_document_is_read_in_parts_and_each_part_is_labelled()
    {
        var reader = new ScriptedReader { Answer = (_, user) => user.Contains("part 2") ? "ABSTAIN" : "found" };
        var text = string.Join('\n', Enumerable.Repeat("x".PadRight(99, 'x'), 30));
        var provider = Create(
            reader,
            new Dictionary<string, string> { ["/w/big.md"] = text },
            options: new MapReadOptions { ChunkChars = 1000 }
        );

        var skipped = await Invoke(provider, Args("anything", "/w/big.md"));

        // Off by default: one slice of a long report cannot tell the right statement or year.
        skipped.Payload.IsError.Should().BeFalse();
        skipped
            .Payload.Text.Should()
            .StartWith(
                "map_read: 1 documents read (0 parts), 0 reported, 0 abstained, 0 errors. 1 skipped as too long."
            );
        skipped.Payload.Text.Should().Contain("/w/big.md | TOO LONG: 3 parts of 1000 chars. Grep");
        reader.Calls.Should().BeEmpty();

        var result = await Invoke(provider, """{"instruction":"anything","paths":"/w/big.md","allow_long":"true"}""");

        reader.Calls.Should().HaveCount(3);
        reader.Calls[0].User.Should().StartWith("Document: /w/big.md (part 1 of 3)");
        result
            .Payload.Text.Split('\n')
            .Should()
            .BeEquivalentTo(
                "map_read: 1 documents read (3 parts), 1 reported, 0 abstained, 0 errors.",
                "/w/big.md | part 1: found",
                "/w/big.md | part 3: found"
            );
    }

    [Theory]
    [InlineData("""{"paths":"/w/t1.md"}""", "'instruction' is required")]
    [InlineData("""{"instruction":"tier"}""", "no documents")]
    [InlineData("""{"instruction":"tier","glob":"*.md"}""", "'glob' needs 'glob_path'")]
    [InlineData("not json", "not valid JSON")]
    public async Task Bad_arguments_are_rejected_before_any_reader_runs(string argsJson, string reason)
    {
        var reader = new ScriptedReader();
        var provider = Create(reader, Tickets, glob: GlobReturning(""));

        var result = await Invoke(provider, argsJson);

        result.Payload.IsError.Should().BeTrue();
        result.Payload.Text.Should().StartWith("Rejected: ").And.Contain(reason);
        reader.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task More_documents_than_the_limit_are_rejected_so_one_call_cannot_run_away()
    {
        var reader = new ScriptedReader();
        var provider = Create(reader, Tickets, options: new MapReadOptions { MaxDocuments = 2 });

        var result = await Invoke(provider, Args("tier", [.. Tickets.Keys]));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.Text.Should().Contain("3 documents is more than the 2");
        reader.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_glob_tool_a_glob_request_says_to_name_the_paths()
    {
        var provider = Create(new ScriptedReader(), Tickets);

        var result = await Invoke(provider, """{"instruction":"tier","glob_path":"/w","glob":"*.md"}""");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.Text.Should().Contain("no Glob tool");
    }

    [Fact]
    public void The_shared_directory_is_named_once_and_never_cuts_a_file_name()
    {
        MapReadToolProvider.CommonDirectory(["/w/tickets/T-1.md", "/w/tickets/T-2.md"]).Should().Be("/w/tickets/");
        // "/w/t" is common text but not a directory: the cut goes back to the last separator.
        MapReadToolProvider.CommonDirectory(["/w/tickets/a.md", "/w/tools/b.md"]).Should().Be("/w/");
        MapReadToolProvider.CommonDirectory([@"C:\w\a.md", @"C:\w\b.md"]).Should().Be(@"C:\w\");
        MapReadToolProvider.CommonDirectory(["/w/a.md"]).Should().BeEmpty();
        MapReadToolProvider.CommonDirectory(["/a/x.md", "/b/x.md"]).Should().Be("/");
    }

    [Fact]
    public void A_read_tool_line_number_gutter_is_stripped_before_the_reader_sees_the_text()
    {
        MapReadToolProvider.StripLineNumbers("1→alpha\n2→beta\n3→\n").Should().Be("alpha\nbeta\n\n");
        MapReadToolProvider.StripLineNumbers("     1\talpha\n     2\tbeta").Should().Be("alpha\nbeta");
        // Prose that happens to start with a number is not a gutter.
        MapReadToolProvider
            .StripLineNumbers("1 | one\ntwo\nthree\nfour\nfive")
            .Should()
            .Be("1 | one\ntwo\nthree\nfour\nfive");
    }

    [Fact]
    public void Chunks_break_at_line_ends_and_never_split_a_line()
    {
        var parts = MapReadToolProvider.Chunk("aaaa\nbbbb\ncccc\ndddd", 10);

        parts.Should().Equal("aaaa\nbbbb", "cccc\ndddd");
        MapReadToolProvider.Chunk("short", 10).Should().Equal("short");
        MapReadToolProvider.Chunk("one-very-long-line", 5).Should().Equal("one-very-long-line");
    }
}
