using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The end-to-end read-back for #251: drive the mirror over a REAL filesystem and a REAL shell, then open
/// the file it produced and prove it is what every downstream reader is promised — one JSON object per
/// line, each line carrying a <c>uid</c>, each <c>parent_uid</c> naming the line before it.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this area asserts an INTENTION: the argv the writer issued, the bytes it staged.
/// None of them opens a file, because none of them produces one. That leaves the most consequential claim
/// of the feature — "the workspace ends up holding a readable transcript" — resting on the assumption that
/// the recorded argv, run for real, does what it reads like. This test removes that assumption; it is the
/// only place a quoting slip, a missing directory or a mis-welded newline can surface.
/// </para>
/// <para>
/// The transcript is deliberately left on disk under <see cref="LiveDirectoryName"/> in the temp folder
/// (a few KB, wiped and rewritten on every run) so the produced artifact can also be read with an external
/// JSONL reader — DuckDB's <c>read_ndjson_objects</c> — rather than only through this assembly's own idea
/// of what it wrote.
/// </para>
/// </remarks>
public sealed class WorkspaceTranscriptLiveFileTests
{
    /// <summary>Folder under the temp directory that the produced transcript is left in.</summary>
    private const string LiveDirectoryName = "lm-transcript-live";

    private const string ThreadId = "conv-live-9f2c";
    private const string WorkspaceId = "ws-live";
    private const string Title = "Live Read Proof";
    private const string AgentId = "agent-live-1";
    private const string AgentName = "researcher";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task Mirror_ProducesARealJsonlFile_ThatParsesAndChainsByUid()
    {
        var shell = LocalShellWorkspaceBrowser.FindPosixShell();
        Skip.If(
            shell is null,
            "No POSIX shell (sh) on this machine, so the writer's real append script cannot be run."
        );

        var root = Path.Combine(Path.GetTempPath(), LiveDirectoryName);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        _ = Directory.CreateDirectory(root);

        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, FirstTurn());
        await SeedSubAgentAsync(store);

        var browser = new LocalShellWorkspaceBrowser(root, shell!);
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = new WorkspaceTranscriptMirror(
            _ => agent,
            store,
            browser,
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            NullLoggerFactory.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero
        );

        var leaf = WorkspaceTranscriptLine.MainFileLeaf(Title, WorkspaceTranscriptLine.ShortId(ThreadId));
        var mainFile = Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            leaf + ConversationTranscriptWriter.TranscriptExtension
        );
        var agentFile = Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            leaf + ConversationTranscriptWriter.AgentsDirectorySuffix,
            WorkspaceTranscriptLine.AgentFileLeaf(AgentName, WorkspaceTranscriptLine.ShortId(AgentId))
                + ConversationTranscriptWriter.TranscriptExtension
        );

        mirror.Attach(agent);
        await PublishUntilAsync(agent, () => LineCount(mainFile) >= 4, "the first turn never reached the file");

        // A second turn, so the file is APPENDED to rather than written once — the incremental path is
        // where a torn tail or a re-welded newline would corrupt the chain.
        await store.AppendMessagesAsync(ThreadId, SecondTurn());
        await PublishUntilAsync(agent, () => LineCount(mainFile) >= 6, "the second turn never reached the file");

        await PublishUntilAsync(agent, () => LineCount(agentFile) >= 1, "the sub-agent transcript was never written");

        // ---- the read-back ----
        var mainUids = await VerifyChainAsync(mainFile, anchors: null);

        // A sub-agent file's FIRST line is not a root: it anchors to the line the parent conversation was
        // at when the child appeared, which is what makes the two files one lineage rather than two piles.
        _ = await VerifyChainAsync(agentFile, mainUids);

        // The newest turn is present, not just the first flush's rows.
        _ = string.Join('\n', ReadRecords(mainFile))
            .Should()
            .Contain("the-answer", "the second turn's content must be in the file");

        // And the opt-out the feature ships with is on disk beside the transcript.
        _ = File.Exists(
                Path.Combine(
                    root,
                    ConversationTranscriptWriter.TranscriptDirectory,
                    "." + ConversationTranscriptWriter.GitignoreName
                )
            )
            .Should()
            .BeTrue();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Opens a produced file, proves every record is one JSON object with a unique <c>uid</c>, and walks
    /// the <c>parent_uid</c> chain. The first record either roots the file (<c>parent_uid: null</c>) or,
    /// when <paramref name="anchors"/> is supplied, points at a line of the file this one descends from.
    /// </summary>
    /// <returns>The file's uids, in order.</returns>
    private static async Task<IReadOnlyList<string>> VerifyChainAsync(string file, IReadOnlyCollection<string>? anchors)
    {
        var lines = await ReadSettledRecordsAsync(file);
        _ = lines.Should().NotBeEmpty($"{file} should hold at least one record");

        var uids = new List<string>(lines.Count);
        string? previousUid = null;
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            using var document = JsonDocument.Parse(line);
            _ = document
                .RootElement.ValueKind.Should()
                .Be(JsonValueKind.Object, $"line {index + 1} of {file} must be one JSON object");

            var uid = document.RootElement.GetProperty("uid").GetString();
            _ = uid.Should().NotBeNullOrEmpty();
            _ = uids.Should().NotContain(uid!, $"uid {uid} is repeated in {file}");

            var parentUid = document.RootElement.GetProperty("parent_uid");
            if (previousUid is not null)
            {
                _ = parentUid
                    .GetString()
                    .Should()
                    .Be(previousUid, $"line {index + 1} of {file} must chain onto the line before it");
            }
            else if (anchors is null)
            {
                _ = parentUid.ValueKind.Should().Be(JsonValueKind.Null, $"{file} is a root file");
            }
            else
            {
                _ = anchors.Should().Contain(parentUid.GetString()!, $"{file} must anchor to its parent");
            }

            uids.Add(uid!);
            previousUid = uid;
        }

        return uids;
    }

    private static int LineCount(string file) => ReadRecords(file).Count;

    /// <summary>
    /// Reads the file's non-blank records with SHARED access. The writer's splice is a real
    /// <c>cat &gt;&gt;</c> in another process, and on Windows an exclusive open races it into a sharing
    /// violation — an artifact of watching the file, not a defect in it.
    /// </summary>
    private static IReadOnlyList<string> ReadRecords(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var reader = new StreamReader(stream);

            var records = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    records.Add(line);
                }
            }

            return records;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Waits until the file stops growing, so the read-back cannot observe a half-written record.</summary>
    private static async Task<IReadOnlyList<string>> ReadSettledRecordsAsync(string file)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            var before = ReadRecords(file);
            await Task.Delay(250);
            var after = ReadRecords(file);
            if (after.Count == before.Count && after.Count > 0)
            {
                return after;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{file} never stopped growing.");
            }
        }
    }

    /// <summary>
    /// Republishes a turn boundary until <paramref name="done"/> holds. The mirror starts its pump on a
    /// worker, so a single publish can legitimately land before the subscription exists; a flush is
    /// idempotent, so an extra boundary costs a no-op flush and nothing else.
    /// </summary>
    private static async Task PublishUntilAsync(PublishingAgent agent, Func<bool> done, string because)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out: {because}.");
            }

            await agent.PublishAsync(new RunCompletedMessage { CompletedRunId = "run-1" });
            await Task.Delay(25);
        }
    }

    private static Task SeedConversationAsync(IConversationStore store) =>
        store.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, WorkspaceId)
                    .Add("title", Title),
            }
        );

    private static async Task SeedSubAgentAsync(IConversationStore store)
    {
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + AgentId;
        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    ThreadId,
                    new SubAgentSnapshot(
                        AgentId,
                        Name: AgentName,
                        TemplateName: "worker",
                        Task: "look it up",
                        Status: SubAgentStatus.Completed,
                        ThreadId: childThreadId,
                        LastActivityUtc: DateTimeOffset.UnixEpoch,
                        TerminalAtUtc: DateTimeOffset.UnixEpoch
                    )
                ),
            }
        );

        await store.AppendMessagesAsync(
            childThreadId,
            [
                Msg("s1", 1, "TextMessage", "User", "\"look it up\"", childThreadId),
                Msg("s2", 2, "TextMessage", "Assistant", "\"found it\"", childThreadId),
            ]
        );
    }

    /// <summary>A full first turn: question, reasoning, a tool call and its result.</summary>
    private static PersistedMessage[] FirstTurn() =>
        [
            Msg("m1", 1, "TextMessage", "User", "\"what is it?\""),
            Msg("m2", 2, "ReasoningMessage", "Assistant", "\"weighing the options\\nline two\""),
            Msg("m3", 3, "ToolsCallMessage", "Assistant", "{\"tool\":\"search\",\"args\":\"{}\"}"),
            Msg("m4", 4, "ToolsCallResultMessage", "Tool", "\"result rows\""),
        ];

    private static PersistedMessage[] SecondTurn() =>
        [Msg("m5", 5, "TextMessage", "Assistant", "\"the-answer\""), Msg("m6", 6, "TextMessage", "User", "\"thanks\"")];

    private static PersistedMessage Msg(
        string id,
        long timestamp,
        string messageType,
        string role,
        string messageJson,
        string threadId = ThreadId
    ) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run-1",
            GenerationId = "run-1",
            Timestamp = timestamp,
            MessageType = messageType,
            Role = role,
            MessageJson = messageJson,
        };
}
