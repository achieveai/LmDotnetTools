using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The transcript mirror against a workspace that is not cooperating: every path it is about to write
/// through has been replaced with a SYMLINK first. Driven over a REAL filesystem and a REAL POSIX shell,
/// because the whole defect is a property of the operating system rather than of any C# call — <c>&gt;&gt;</c>,
/// <c>mkdir -p</c>, <c>[ -e ]</c>, <c>tail</c> and a gateway PUT all follow symlinks silently, and a fake
/// that hands back a regular file where production meets a link cannot fail.
/// </summary>
/// <remarks>
/// <para>
/// The threat is ordinary rather than exotic. A workspace is shared with the agents working in it, and the
/// transcript leaf is derived from the conversation TITLE, so its name is guessable well before it exists.
/// An agent that drops a symlink there redirects the conversation's unredacted reasoning to wherever the
/// link points — typically a tracked file, which is precisely the destination the <c>.gitignore</c> beside
/// the transcript exists to keep it out of.
/// </para>
/// <para>
/// Every case asserts the same two things: the transcript did NOT travel through the link, and the link
/// itself is STILL THERE afterwards. The second is not incidental. An indeterminate destination must be
/// refused, never repaired: unlinking it and writing a fresh file would destroy whatever the link pointed
/// at, which is the very file being protected. Deferring costs a repeated flush; there is nothing to
/// recover from having overwritten someone's source file.
/// </para>
/// </remarks>
public sealed class WorkspaceTranscriptSymlinkTests
{
    private const string RootDirectoryName = "lm-transcript-symlink";

    private const string ThreadId = "conv-link-7a3b";
    private const string WorkspaceId = "ws-link";
    private const string Title = "Link Proof";
    private const string RetitledTo = "Renamed Proof";

    /// <summary>Sub-agent ids and display names, paired by index and consumed in seeding order.</summary>
    private static readonly string[] AgentIds = ["agent-link-1", "agent-link-2"];

    private static readonly string[] AgentNames = ["researcher", "reviewer"];

    /// <summary>Content of the file each symlink points at. No flush may ever change it.</summary>
    private const string TrackedContent = "tracked source file, not a transcript\n";

    private static readonly string ShortThreadId = WorkspaceTranscriptLine.ShortId(ThreadId);

    private static readonly string Leaf = WorkspaceTranscriptLine.MainFileLeaf(Title, ShortThreadId);

    private static readonly string RetitledLeaf =
        WorkspaceTranscriptLine.MainFileLeaf(RetitledTo, ShortThreadId);

    /// <summary>
    /// The destination transcript file is a symlink to a tracked file. <c>cat &gt;&gt;</c> follows it, so the
    /// entire conversation would be appended into that file — outside the ignored directory, and staged for
    /// the next <c>git add -A</c>.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToAppendThroughASymlinkedDestinationFile()
    {
        var (root, writer, _, _) = await SetupAsync(nameof(Flush_RefusesToAppendThroughASymlinkedDestinationFile));
        var tracked = WriteTrackedFile(root, "tracked.txt");
        var destination = TranscriptPath(root, Leaf);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        LinkToFile(destination, tracked);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        AssertUntouched(tracked);
        AssertStillALink(destination);
    }

    /// <summary>
    /// The transcript DIRECTORY is a symlink. <c>mkdir -p</c> on it succeeds silently — there is nothing to
    /// create — and every file beneath it then lands in the link's target, including the <c>.gitignore</c>
    /// that is supposed to be covering them. Worse than a leak: that PUT writes <c>*</c> over whatever file
    /// the ignore path resolves to.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToWriteThroughASymlinkedTranscriptDirectory()
    {
        var (root, writer, _, _) = await SetupAsync(nameof(Flush_RefusesToWriteThroughASymlinkedTranscriptDirectory));
        var elsewhere = ElsewhereIn(root);
        var transcriptDirectory = Path.Combine(root, ConversationTranscriptWriter.TranscriptDirectory);
        LinkToDirectory(transcriptDirectory, elsewhere);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        _ = Directory.GetFileSystemEntries(elsewhere).Should().BeEmpty(
            "not one byte — transcript or .gitignore — may be written through a redirected directory");
        AssertDirectoryStillALink(transcriptDirectory);
    }

    /// <summary>
    /// The sub-agent directory is a symlink. The main file's own path is untouched, so the flush gets past
    /// every check that only looks at the root conversation and reaches the fan-out — the descendant
    /// transcripts are what leave through the link.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToWriteADescendantThroughASymlinkedAgentsDirectory()
    {
        var (root, writer, _, _) = await SetupAsync(
            nameof(Flush_RefusesToWriteADescendantThroughASymlinkedAgentsDirectory),
            subAgents: 1);
        var elsewhere = ElsewhereIn(root);
        var agentsDirectory = AgentsDirectoryPath(root, Leaf);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(agentsDirectory)!);
        LinkToDirectory(agentsDirectory, elsewhere);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        _ = Directory.GetFileSystemEntries(elsewhere).Should().BeEmpty(
            "a sub-agent's reasoning is as exposed as the root conversation's");
        AssertDirectoryStillALink(agentsDirectory);

        // The main transcript DID get written: the refusal is scoped to the redirected path, and this flush
        // was doing real work rather than failing before it started.
        _ = File.Exists(TranscriptPath(root, Leaf)).Should().BeTrue();
    }

    /// <summary>
    /// The STAGING path is a symlink. Nothing in the shell scripts can defend this one — the payload is put
    /// there by the gateway before any script runs — and the bytes it carries are the same unredacted rows
    /// the destination would have received. Its name is as guessable as the transcript's: a compile-time
    /// directory plus a short hash of the thread id.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToStageThroughASymlinkedTempPath()
    {
        var (root, writer, _, _) = await SetupAsync(nameof(Flush_RefusesToStageThroughASymlinkedTempPath));
        var tracked = WriteTrackedFile(root, "tracked-staging.txt");
        var tempPath = TempPath(root);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        LinkToFile(tempPath, tracked);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        AssertUntouched(tracked);
        AssertStillALink(tempPath);
    }

    /// <summary>
    /// The containment file itself is a symlink. This one destroys rather than leaks: the PUT writes
    /// <c>*</c> over the link's target, so a tracked file becomes a one-character ignore file. Refusing is
    /// the only safe answer, and refusing containment stops the flush outright.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToWriteContainmentThroughASymlink()
    {
        var (root, writer, _, _) = await SetupAsync(nameof(Flush_RefusesToWriteContainmentThroughASymlink));
        var tracked = WriteTrackedFile(root, "tracked-config.txt");
        var gitignore = Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            "." + ConversationTranscriptWriter.GitignoreName);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(gitignore)!);
        LinkToFile(gitignore, tracked);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        AssertUntouched(tracked);
        AssertStillALink(gitignore);
        _ = Directory
            .GetFiles(Path.Combine(root, ConversationTranscriptWriter.TranscriptDirectory), "*.jsonl")
            .Should()
            .BeEmpty("containment could not be established, so no transcript may exist either");
    }

    /// <summary>
    /// The staging path becomes a symlink PART-WAY THROUGH a flush — after one descendant has been spliced
    /// and before the next is staged. One temp path serves the whole flush, and between two consecutive
    /// PUTs sits a full shell execution, so a guard that answers once per flush leaves every append after
    /// the first writing through whatever was planted in that window.
    /// </summary>
    /// <remarks>
    /// Planting the link before the flush starts is a different and far weaker claim — it is already
    /// covered by <see cref="Flush_RefusesToStageThroughASymlinkedTempPath"/>, and it passes against a
    /// once-per-flush guard, so it says nothing about this. The discriminating assertion is the tracked
    /// file's CONTENT: the flush defers either way (the append script guards its own destination), but only
    /// a per-append staging guard keeps the second descendant's rows out of the link's target.
    /// </remarks>
    [SkippableFact]
    public async Task Flush_RefusesToStageThroughATempPathSymlinkedBetweenTwoDescendants()
    {
        var (root, writer, _, browser) = await SetupAsync(
            nameof(Flush_RefusesToStageThroughATempPathSymlinkedBetweenTwoDescendants),
            subAgents: 2);
        var tracked = WriteTrackedFile(root, "tracked-interleaved.txt");
        var tempPath = TempPath(root);

        // Fire on the first splice INTO the sub-agent directory: descendant one is on disk by then, and
        // descendant two has not been staged yet. The append script's own `rm -f` has just removed the temp
        // file, so the link goes down on a clear path — which is also why planting it after the PUT instead
        // would not survive to be followed.
        var planted = false;
        browser.AfterCommand = command =>
        {
            if (planted || !IsDescendantSplice(command))
            {
                return;
            }

            planted = true;
            _ = Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            File.Delete(tempPath);
            LinkToFile(tempPath, tracked);
        };

        var outcome = await writer.FlushAsync();

        _ = planted.Should().BeTrue("the plant has to land mid-flush or the case is not being exercised");
        AssertUntouched(tracked);
        AssertStillALink(tempPath);
        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);

        // The first descendant DID get written, so the flush was mid-fan-out rather than stopped early.
        _ = Directory.GetFiles(AgentsDirectoryPath(root, Leaf)).Should().ContainSingle();
    }

    /// <summary>
    /// A retitle whose destination leaf is a symlink resolving to a DIRECTORY. <c>mv</c> then moves the
    /// transcript INSIDE that directory instead of renaming it, carrying the whole unredacted file out of
    /// <c>.conversations</c> and out from under the <c>.gitignore</c> beside it.
    /// </summary>
    /// <remarks>
    /// An earlier round of this work left the rename unguarded on the rationale that "<c>mv A B</c>
    /// replaces the destination PATH rather than writing through a link at B". That rationale is wrong for
    /// the case that matters. It holds only when the link resolves to a FILE — <c>rename(2)</c> then
    /// replaces the link itself and leaves its target alone — and a link to a directory inverts it
    /// completely.
    /// </remarks>
    [SkippableFact]
    public async Task Retitle_RefusesToMoveTheTranscriptThroughASymlinkedDestination()
    {
        var (root, writer, store, _) = await SetupAsync(
            nameof(Retitle_RefusesToMoveTheTranscriptThroughASymlinkedDestination));
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        var original = TranscriptPath(root, Leaf);
        var before = await File.ReadAllTextAsync(original);
        var elsewhere = ElsewhereIn(root);
        var destination = TranscriptPath(root, RetitledLeaf);
        LinkToDirectory(destination, elsewhere);

        await SaveTitleAsync(store, RetitledTo);
        await store.AppendMessagesAsync(ThreadId, [Msg("m3", 3, "User", "\"and now?\"", ThreadId)]);
        _ = await writer.FlushAsync();

        _ = Directory.GetFileSystemEntries(elsewhere).Should().BeEmpty(
            "mv moves its source INSIDE a destination that resolves to a directory, which would carry the "
                + "whole unredacted transcript out of the ignored directory");
        AssertDirectoryStillALink(destination);

        // Refusing the rename must not cost the conversation its mirror: the transcript keeps the old name
        // and keeps growing there.
        _ = File.Exists(original).Should().BeTrue();
        _ = (await File.ReadAllTextAsync(original)).Should().StartWith(before);
    }

    /// <summary>
    /// The <c>_agents</c> half of the same rename, which carries every descendant transcript and has
    /// exactly the same exposure. Its destination is a symlink to a directory, so an unguarded <c>mv</c>
    /// relocates the whole directory rather than renaming it.
    /// </summary>
    [SkippableFact]
    public async Task Retitle_RefusesToMoveTheAgentsDirectoryThroughASymlinkedDestination()
    {
        var (root, writer, store, _) = await SetupAsync(
            nameof(Retitle_RefusesToMoveTheAgentsDirectoryThroughASymlinkedDestination),
            subAgents: 1);
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        var agentsDirectory = AgentsDirectoryPath(root, Leaf);
        _ = Directory.GetFiles(agentsDirectory).Should().ContainSingle();

        var elsewhere = ElsewhereIn(root);
        var destination = AgentsDirectoryPath(root, RetitledLeaf);
        LinkToDirectory(destination, elsewhere);

        await SaveTitleAsync(store, RetitledTo);
        _ = await writer.FlushAsync();

        _ = Directory.GetFileSystemEntries(elsewhere).Should().BeEmpty(
            "a descendant's reasoning leaves through a redirected rename exactly as the root's does");
        AssertDirectoryStillALink(destination);
        _ = Directory.GetFiles(agentsDirectory).Should().ContainSingle(
            "the sub-agent transcripts stay where they were rather than travelling through the link");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Whether an argv is the append script splicing a SUB-AGENT file — i.e. a descendant has just landed.
    /// The append script's directory argument is what distinguishes it from the main transcript's splice.
    /// </summary>
    private static bool IsDescendantSplice(SandboxCommand command) =>
        command.Arguments.Count == 7
        && command.Arguments[4].EndsWith(ConversationTranscriptWriter.AgentsDirectorySuffix, StringComparison.Ordinal);

    /// <summary>
    /// A clean workspace root and a writer pointed at it through a real shell. Skips when the machine has
    /// no POSIX shell — the same condition <see cref="WorkspaceTranscriptLiveFileTests"/> skips on.
    /// </summary>
    /// <param name="caseName">Names the workspace root, so cases cannot see each other's files.</param>
    /// <param name="subAgents">
    /// How many descendant threads to seed. Two of them is what makes a mid-flush plant expressible: the
    /// fan-out stages each descendant through the SAME temp path, one after the other.
    /// </param>
    private static async Task<Fixture> SetupAsync(string caseName, int subAgents = 0)
    {
        var shell = LocalShellWorkspaceBrowser.FindPosixShell();
        Skip.If(
            shell is null,
            "No POSIX shell (sh) on this machine, so the writer's real scripts cannot be run.");

        var root = Path.Combine(Path.GetTempPath(), RootDirectoryName, caseName);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        _ = Directory.CreateDirectory(root);

        var store = new InMemoryConversationStore();
        await SaveTitleAsync(store, Title);
        await store.AppendMessagesAsync(
            ThreadId,
            [
                Msg("m1", 1, "User", "\"what is it?\"", ThreadId),
                Msg("m2", 2, "Assistant", "\"a secret\"", ThreadId),
            ]);

        for (var i = 0; i < subAgents; i++)
        {
            var agentId = AgentIds[i];
            var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;
            await store.SaveMetadataAsync(
                childThreadId,
                new ThreadMetadata
                {
                    ThreadId = childThreadId,
                    LastUpdated = 0,
                    Properties = SubAgentProvenance.Build(
                        ThreadId,
                        new SubAgentSnapshot(
                            agentId,
                            Name: AgentNames[i],
                            TemplateName: "worker",
                            Task: "look it up",
                            Status: SubAgentStatus.Completed,
                            ThreadId: childThreadId,
                            LastActivityUtc: DateTimeOffset.UnixEpoch,
                            TerminalAtUtc: DateTimeOffset.UnixEpoch)),
                });
            await store.AppendMessagesAsync(
                childThreadId,
                [Msg($"s{i}", 1, "Assistant", "\"found it\"", childThreadId)]);
        }

        var browser = new LocalShellWorkspaceBrowser(root, shell);
        var writer = new ConversationTranscriptWriter(
            ThreadId,
            store,
            browser,
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            NullLogger<ConversationTranscriptWriter>.Instance,
            TimeSpan.Zero,
            ConversationTranscriptWriter.DefaultMaxSubAgentFilesPerFlush);

        return new Fixture(root, writer, store, browser);
    }

    /// <summary>Everything a case needs to reach past the writer and change the world mid-flush.</summary>
    private sealed record Fixture(
        string Root,
        ConversationTranscriptWriter Writer,
        InMemoryConversationStore Store,
        LocalShellWorkspaceBrowser Browser);

    private static Task SaveTitleAsync(InMemoryConversationStore store, string title) =>
        store.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, WorkspaceId)
                    .Add("title", title),
            });

    private static string TranscriptPath(string root, string leaf) =>
        Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            leaf + ConversationTranscriptWriter.TranscriptExtension);

    private static string AgentsDirectoryPath(string root, string leaf) =>
        Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            leaf + ConversationTranscriptWriter.AgentsDirectorySuffix);

    private static string TempPath(string root) =>
        Path.Combine(
            root,
            ConversationTranscriptWriter.TempDirectory,
            ShortThreadId + ConversationTranscriptWriter.TempExtension);

    private static string ElsewhereIn(string root) =>
        Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;

    private static string WriteTrackedFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, TrackedContent);
        return path;
    }

    /// <summary>
    /// Plants a real symlink, or skips. Windows only permits this with Developer Mode or elevation, and a
    /// test that quietly substituted a regular file would assert nothing at all — following a link is the
    /// entire behaviour under test.
    /// </summary>
    private static void LinkToFile(string link, string target) =>
        Plant(() => File.CreateSymbolicLink(link, target));

    private static void LinkToDirectory(string link, string target) =>
        Plant(() => Directory.CreateSymbolicLink(link, target));

    private static void Plant(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, $"This machine does not permit creating symlinks ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private static void AssertUntouched(string trackedFile)
    {
        _ = File.ReadAllText(trackedFile)
            .Should()
            .Be(TrackedContent, "the file the link pointed at must be exactly as it was");
    }

    /// <summary>
    /// The link survived. An indeterminate destination is REFUSED, not repaired — removing the link to
    /// write a clean file would destroy the target, which is the file the refusal exists to protect.
    /// </summary>
    private static void AssertStillALink(string path)
    {
        _ = new FileInfo(path)
            .LinkTarget.Should()
            .NotBeNull($"{path} must still be the symlink it was, untouched");
    }

    /// <inheritdoc cref="AssertStillALink"/>
    private static void AssertDirectoryStillALink(string path)
    {
        _ = new DirectoryInfo(path)
            .LinkTarget.Should()
            .NotBeNull($"{path} must still be the symlink it was, untouched");
    }

    private static PersistedMessage Msg(
        string id,
        long timestamp,
        string role,
        string messageJson,
        string threadId) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run-1",
            GenerationId = "run-1",
            Timestamp = timestamp,
            MessageType = "TextMessage",
            Role = role,
            MessageJson = messageJson,
        };
}
