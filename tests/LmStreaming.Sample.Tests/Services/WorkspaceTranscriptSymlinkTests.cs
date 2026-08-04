using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    private const string AgentId = "agent-link-1";
    private const string AgentName = "researcher";

    /// <summary>Content of the file each symlink points at. No flush may ever change it.</summary>
    private const string TrackedContent = "tracked source file, not a transcript\n";

    private static readonly string Leaf =
        WorkspaceTranscriptLine.MainFileLeaf(Title, WorkspaceTranscriptLine.ShortId(ThreadId));

    /// <summary>
    /// The destination transcript file is a symlink to a tracked file. <c>cat &gt;&gt;</c> follows it, so the
    /// entire conversation would be appended into that file — outside the ignored directory, and staged for
    /// the next <c>git add -A</c>.
    /// </summary>
    [SkippableFact]
    public async Task Flush_RefusesToAppendThroughASymlinkedDestinationFile()
    {
        var (root, writer) = await SetupAsync(nameof(Flush_RefusesToAppendThroughASymlinkedDestinationFile));
        var tracked = WriteTrackedFile(root, "tracked.txt");
        var destination = Path.Combine(root, ConversationTranscriptWriter.TranscriptDirectory, Leaf + ".jsonl");
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
        var (root, writer) = await SetupAsync(nameof(Flush_RefusesToWriteThroughASymlinkedTranscriptDirectory));
        var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
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
        var (root, writer) = await SetupAsync(
            nameof(Flush_RefusesToWriteADescendantThroughASymlinkedAgentsDirectory),
            withSubAgent: true);
        var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
        var agentsDirectory = Path.Combine(
            root,
            ConversationTranscriptWriter.TranscriptDirectory,
            Leaf + ConversationTranscriptWriter.AgentsDirectorySuffix);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(agentsDirectory)!);
        LinkToDirectory(agentsDirectory, elsewhere);

        var outcome = await writer.FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Deferred);
        _ = Directory.GetFileSystemEntries(elsewhere).Should().BeEmpty(
            "a sub-agent's reasoning is as exposed as the root conversation's");
        AssertDirectoryStillALink(agentsDirectory);

        // The main transcript DID get written: the refusal is scoped to the redirected path, and this flush
        // was doing real work rather than failing before it started.
        _ = File.Exists(Path.Combine(root, ConversationTranscriptWriter.TranscriptDirectory, Leaf + ".jsonl"))
            .Should().BeTrue();
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
        var (root, writer) = await SetupAsync(nameof(Flush_RefusesToStageThroughASymlinkedTempPath));
        var tracked = WriteTrackedFile(root, "tracked-staging.txt");
        var tempPath = Path.Combine(
            root,
            ConversationTranscriptWriter.TempDirectory,
            WorkspaceTranscriptLine.ShortId(ThreadId) + ConversationTranscriptWriter.TempExtension);
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
        var (root, writer) = await SetupAsync(nameof(Flush_RefusesToWriteContainmentThroughASymlink));
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

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A clean workspace root and a writer pointed at it through a real shell. Skips when the machine has
    /// no POSIX shell — the same condition <see cref="WorkspaceTranscriptLiveFileTests"/> skips on.
    /// </summary>
    private static async Task<(string Root, ConversationTranscriptWriter Writer)> SetupAsync(
        string caseName,
        bool withSubAgent = false)
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
        await store.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, WorkspaceId)
                    .Add("title", Title),
            });
        await store.AppendMessagesAsync(
            ThreadId,
            [
                Msg("m1", 1, "User", "\"what is it?\"", ThreadId),
                Msg("m2", 2, "Assistant", "\"a secret\"", ThreadId),
            ]);

        if (withSubAgent)
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
                            TerminalAtUtc: DateTimeOffset.UnixEpoch)),
                });
            await store.AppendMessagesAsync(
                childThreadId,
                [Msg("s1", 1, "Assistant", "\"found it\"", childThreadId)]);
        }

        var writer = new ConversationTranscriptWriter(
            ThreadId,
            store,
            new LocalShellWorkspaceBrowser(root, shell),
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            NullLogger<ConversationTranscriptWriter>.Instance,
            TimeSpan.Zero,
            ConversationTranscriptWriter.DefaultMaxSubAgentFilesPerFlush);

        return (root, writer);
    }

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
