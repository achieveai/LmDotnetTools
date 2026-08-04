using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="ConversationTranscriptWriter"/> — the workspace transcript mirror (#251), driven
/// entirely against <see cref="FakeFileBrowser"/> so there is no gateway and no container in the loop.
/// </summary>
/// <remarks>
/// <para>
/// These assert the <b>exact bytes appended</b> and the <b>exact argv of every command</b>, not a
/// paraphrase of either. The mirror's whole value is that a reader can dedupe on <c>uid</c> and chain on
/// <c>parent_uid</c>; both properties are byte-level, so an assertion that only counts calls would pass
/// while the file on disk was wrong.
/// </para>
/// <para>
/// Every failure mode is forced rather than assumed: a non-zero splice, a non-zero <c>mv</c>, a resolve
/// that throws, a credential conflict, and rows that never settle. The invariant each of those locks is
/// the same one — <b>duplicate on retry, never silent loss</b>.
/// </para>
/// </remarks>
public sealed class ConversationTranscriptWriterTests
{
    private const string ThreadId = "conv-1";
    private const string WorkspaceId = "ws-1";
    private const string Title = "Design Review";
    private const string RetitledTo = "Shipping Plan";
    private const string GitignorePath = ".conversations/.gitignore";

    /// <summary>
    /// The splice script, duplicated here ON PURPOSE. It is the feature's only shell call site, so its
    /// text is a contract: the middle line is the newline weld that stops a torn tail from fusing two
    /// records, and the <c>$1</c>/<c>$2</c>/<c>$3</c> parameters are what keep a user-authored title
    /// inert. A test that read the constant back out of the production type could not notice either one
    /// being edited away.
    /// </summary>
    private const string ExpectedSpliceScript =
        "mkdir -p \"$1\" || exit 1\n"
        + "if [ -s \"$3\" ] && [ -n \"$(tail -c1 \"$3\")\" ]; then printf '\\n' >> \"$3\" || exit 1; fi\n"
        + "cat \"$2\" >> \"$3\" && rm -f \"$2\"\n";

    /// <summary>
    /// The cold-start watermark probe, duplicated for the same reason. Its text is a contract twice over:
    /// the <c>[ -e ]</c> test is what makes "there is no transcript" an answer the script GIVES rather than
    /// one the caller infers from a generic <c>tail</c> failure, and exit <c>42</c> is the private code that
    /// carries that answer back — a value neither <c>tail</c> (0/1) nor <c>sh</c> (126/127, 128+signal)
    /// produces, so it cannot be minted by an accident.
    /// </summary>
    private const string ExpectedProbeScript =
        "[ -e \"$1\" ] || exit 42\n"
        + "exec tail -n \"$2\" -- \"$1\"\n";

    private static readonly string ShortThreadId = WorkspaceTranscriptLine.ShortId(ThreadId);

    private static readonly string TempPath =
        $"{ConversationTranscriptWriter.TempDirectory}/{ShortThreadId}{ConversationTranscriptWriter.TempExtension}";

    // ---------------------------------------------------------------- helpers

    private static string MainPath(string? title) =>
        $"{ConversationTranscriptWriter.TranscriptDirectory}/"
        + $"{WorkspaceTranscriptLine.MainFileLeaf(title, ShortThreadId)}{ConversationTranscriptWriter.TranscriptExtension}";

    private static string AgentsDirectory(string? title) =>
        $"{ConversationTranscriptWriter.TranscriptDirectory}/"
        + $"{WorkspaceTranscriptLine.MainFileLeaf(title, ShortThreadId)}{ConversationTranscriptWriter.AgentsDirectorySuffix}";

    private static string AgentPath(string? title, string agentId, string? agentName) =>
        $"{AgentsDirectory(title)}/"
        + $"{WorkspaceTranscriptLine.AgentFileLeaf(agentName, WorkspaceTranscriptLine.ShortId(agentId))}"
        + ConversationTranscriptWriter.TranscriptExtension;

    private static ConversationTranscriptWriter CreateWriter(
        IConversationStore store,
        FakeFileBrowser browser,
        ILogger<ConversationTranscriptWriter>? logger = null,
        int maxSubAgentFilesPerFlush = ConversationTranscriptWriter.DefaultMaxSubAgentFilesPerFlush) =>
        new(
            ThreadId,
            store,
            browser,
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            logger ?? NullLogger<ConversationTranscriptWriter>.Instance,
            // Zero, so the stability probe's second read happens immediately: the tests drive settling
            // through the store double, never through elapsed time.
            TimeSpan.Zero,
            maxSubAgentFilesPerFlush
        );

    private static Task SeedConversationAsync(
        IConversationStore store,
        string? title = Title,
        string? workspaceId = WorkspaceId)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        if (workspaceId is not null)
        {
            properties[MultiTurnAgentPool.WorkspacePropertyKey] = workspaceId;
        }

        if (title is not null)
        {
            properties["title"] = title;
        }

        return store.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = properties.ToImmutable(),
            }
        );
    }

    /// <summary>Seeds one persisted sub-agent thread stamped as this conversation's child.</summary>
    private static async Task SeedSubAgentAsync(
        IConversationStore store,
        string agentId,
        string? name,
        params PersistedMessage[] messages)
    {
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
                        Name: name,
                        TemplateName: "worker",
                        Task: "do the thing",
                        Status: SubAgentStatus.Completed,
                        ThreadId: childThreadId,
                        LastActivityUtc: DateTimeOffset.UnixEpoch,
                        TerminalAtUtc: DateTimeOffset.UnixEpoch
                    )
                ),
            }
        );

        await store.AppendMessagesAsync(childThreadId, messages);
    }

    private static PersistedMessage Msg(
        string id,
        long timestamp,
        string role = "Assistant",
        string threadId = ThreadId,
        string runId = "run-1",
        string messageType = "TextMessage",
        string? messageJson = null) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = runId,
            GenerationId = runId,
            Timestamp = timestamp,
            MessageType = messageType,
            Role = role,
            // Deliberately not a shape TranscriptProjection can deserialize: Normalize passes an
            // unreadable row through VERBATIM, so the expected bytes stay legible in the assertions.
            MessageJson = messageJson ?? $"\"opaque-{id}\"",
        };

    /// <summary>
    /// The bytes the writer is expected to append: the same projection it runs, serialized the same way,
    /// minus the <paramref name="skip"/> lines a watermark already covers.
    /// </summary>
    private static string ExpectedAppend(
        IReadOnlyList<PersistedMessage> all,
        int skip = 0,
        string? agent = null,
        string? rootParentUid = null)
    {
        var lines = WorkspaceTranscriptLine.ChainMessages(
            TranscriptProjection.Normalize(all, excludeReasoning: false),
            agent,
            rootParentUid
        );

        return string.Concat(lines.Skip(skip).Select(l => WorkspaceTranscriptLine.Serialize(l) + "\n"));
    }

    /// <summary>
    /// The bytes of the i-th STAGED transcript payload. Selected by path rather than by absolute position
    /// in <see cref="FakeFileBrowser.Writes"/> because the writer also PUTs the containment
    /// <c>.gitignore</c>, ahead of the first append — a positional index would then be counting two
    /// different kinds of write and would shift the moment containment moved.
    /// </summary>
    private static string Written(FakeFileBrowser browser, int index) =>
        Encoding.UTF8.GetString(browser.Writes.Where(w => w.Path == TempPath).ElementAt(index).Bytes);

    private static IReadOnlyList<string> UidsIn(string payload) =>
        [
            .. payload
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("uid").GetString()!),
        ];

    private static SandboxCommandResult Ok(string stdout = "") =>
        new() { ExitCode = 0, StandardOutput = stdout, StandardError = "", OperationId = "op" };

    private static SandboxCommandResult Fail(string stderr = "boom") =>
        new() { ExitCode = 1, StandardOutput = "", StandardError = stderr, OperationId = "op" };

    /// <summary>
    /// What the watermark probe reports when the destination is DEFINITELY not there. The literal 42 is
    /// duplicated here on purpose, exactly like <see cref="ExpectedProbeScript"/>: that one code is the
    /// whole discrimination between "no transcript yet" and "could not tell", and a test that read it back
    /// off the production constant could not notice it drifting onto a value <c>tail</c> or <c>sh</c> also
    /// produces.
    /// </summary>
    private static SandboxCommandResult Missing() =>
        new() { ExitCode = 42, StandardOutput = "", StandardError = "", OperationId = "op" };

    /// <summary>
    /// Selects the watermark probe out of a flush's commands. The splice is the call carrying the staged
    /// temp file; every other shell call in a flush is the probe.
    /// </summary>
    private static bool IsSplice(SandboxCommand command) =>
        command.Arguments.Contains(TempPath, StringComparer.Ordinal);

    // ---------------------------------------------------------------- AC 1, 6

    /// <summary>
    /// AC 1 / AC 6. The first flush writes one line per persisted message, in store order, staged through
    /// <c>.conversations/.tmp/{shortThreadId}.part</c> and spliced with the single shell call site. The
    /// staging path is asserted because a leaked temp file (the <c>rm -f</c> never ran) must be a name no
    /// <c>**/*.jsonl</c> scan can match.
    /// </summary>
    [Fact]
    public async Task FirstFlush_WritesOneLinePerMessageInStoreOrder_ThroughTheTempPathAndOneShellCall()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2), Msg("m3", 3)];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser();
        var outcome = await CreateWriter(store, browser).FlushAsync();

        _ = outcome.Should().Be(TranscriptFlushOutcome.Written);

        var payload = ExpectedAppend(messages);
        _ = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);

        _ = browser.Writes.Select(w => w.Path).Should().Equal(GitignorePath, TempPath);
        _ = Written(browser, 0).Should().Be(payload);
        _ = browser.Writes
            .Select(w => w.Path)
            .Should()
            .NotContain(p => p.EndsWith(ConversationTranscriptWriter.TranscriptExtension, StringComparison.Ordinal));

        _ = browser.Commands.Should().HaveCount(2);
        _ = browser.Commands[0].Arguments.Should()
            .Equal("sh", "-c", ExpectedProbeScript, "sh", MainPath(Title), "5");
        _ = browser.Commands[1].Arguments.Should()
            .Equal("sh", "-c", ExpectedSpliceScript, "sh", ".conversations", TempPath, MainPath(Title));
        _ = browser.LastPersistedWorkspaceId.Should().Be(WorkspaceId);
    }

    // ---------------------------------------------------------------- AC 2, 3

    /// <summary>
    /// AC 2 / AC 3. A second flush appends ONLY the second run's bytes — the watermark is the last
    /// appended <c>uid</c>, not a byte offset — and across both flushes the distinct <c>uid</c> count
    /// equals the line count, which is the property a reader's dedupe relies on.
    /// </summary>
    [Fact]
    public async Task SecondFlush_AppendsOnlyTheNewRun_AndEveryLineKeepsADistinctUid()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] first = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, first);

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser);
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        PersistedMessage[] second = [Msg("m3", 3, "User", runId: "run-2"), Msg("m4", 4, runId: "run-2")];
        await store.AppendMessagesAsync(ThreadId, second);
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        PersistedMessage[] all = [.. first, .. second];
        _ = Written(browser, 0).Should().Be(ExpectedAppend(first));
        _ = Written(browser, 1).Should().Be(ExpectedAppend(all, skip: 2));

        // No second probe: the watermark survived in process, keyed by thread.
        _ = browser.Commands.Select(c => c.Arguments[2]).Should()
            .Equal(ExpectedProbeScript, ExpectedSpliceScript, ExpectedSpliceScript);

        var file = Written(browser, 0) + Written(browser, 1);
        var uids = UidsIn(file);
        _ = uids.Should().HaveCount(4);
        _ = uids.Distinct(StringComparer.Ordinal).Should().HaveCount(4);
    }

    // ---------------------------------------------------------------- AC 4

    /// <summary>
    /// AC 4. A cold start (crash resume) recovers its watermark from <c>tail -n 5</c> and appends only the
    /// suffix. The window is five lines, not one, precisely so a run killed mid-splice — whose last line
    /// is torn — still resolves to the last INTACT record instead of re-appending the whole history.
    /// A GET is never issued: these files reach tens of megabytes.
    /// </summary>
    [Fact]
    public async Task ColdStart_RecoversTheWatermarkFromTheTailWindow_EvenWithATornLastLine()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2), Msg("m3", 3)];
        await store.AppendMessagesAsync(ThreadId, messages);

        // What a previous process left on disk: m1, m2, then a half-written m3.
        var onDisk = ExpectedAppend(messages, skip: 0);
        var intact = string.Concat(onDisk.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(l => l + "\n"));
        var torn = onDisk.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2][..20];

        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command => IsSplice(command) ? Ok() : Ok(intact + torn),
        };

        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        _ = browser.Commands[0].Arguments.Should()
            .Equal("sh", "-c", ExpectedProbeScript, "sh", MainPath(Title), "5");
        _ = Written(browser, 0).Should().Be(ExpectedAppend(messages, skip: 2));
        _ = browser.ReadCalls.Should().Be(0);
    }

    /// <summary>
    /// The same discrimination the adoption path already makes, one step later. A probe that THREW says
    /// nothing whatsoever about the destination, yet reading it as "there is no transcript" sends the start
    /// index to zero and re-appends the ENTIRE persisted history onto a file that already holds it — a
    /// conversation with ten thousand rows duplicates all ten thousand, once per cold writer, so a host
    /// that restarts in a loop while the gateway is unwell multiplies the transcript indefinitely. A probe
    /// that did not settle defers: nothing staged, nothing spliced, and the next flush asks again.
    /// </summary>
    [Fact]
    public async Task ColdStart_DefersWhenTheWatermarkProbeThrows_InsteadOfReAppendingTheWholeHistory()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser
        {
            ExecuteHandler = _ => throw new SandboxException(SandboxErrorKind.Protocol, "gateway said no"),
        };
        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);

        // Nothing was staged and the splice was never even attempted: the flush stopped at the probe.
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().BeEmpty();
        _ = browser.Commands.Should().ContainSingle();
        _ = browser.Commands.Where(IsSplice).Should().BeEmpty();

        // And once the gateway answers, the SAME writer appends the history exactly once.
        browser.ExecuteHandler = command => IsSplice(command) ? Ok() : Missing();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().ContainSingle();
        _ = Written(browser, 0).Should().Be(ExpectedAppend(messages));
    }

    /// <summary>
    /// The exit code half of the same defect, and the reason the probe cannot be a bare <c>tail</c>. GNU
    /// <c>tail</c> answers a missing file, an unreadable one and an I/O error with the SAME status 1, so
    /// "no such file" is not something its exit code can tell you — and every wrong guess costs a full
    /// duplicate of the transcript. Here the destination is present and complete, and the probe fails for a
    /// reason that is not absence; the flush must add nothing at all rather than start again from row one.
    /// </summary>
    [Fact]
    public async Task ColdStart_DefersWhenTheWatermarkProbeFails_InsteadOfDuplicatingTheWholeTranscript()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, messages);

        // The workspace already holds every one of these rows.
        var existing = ExpectedAppend(messages);
        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command => IsSplice(command) ? Ok() : Fail("tail: cannot open: Permission denied"),
        };
        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().BeEmpty();

        // Once the probe can read the file it finds the watermark on its last line, and there is nothing
        // left to append — which is exactly what the duplicating path would have destroyed.
        browser.ExecuteHandler = command => IsSplice(command) ? Ok() : Ok(existing);
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.UpToDate);
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().BeEmpty();
    }

    /// <summary>
    /// The other side of that discrimination, and the reason it has to be narrow. A workspace that has
    /// never held a transcript makes the probe report a DEFINITE absence, and that is the ordinary first
    /// flush rather than a fault: the start index is zero and the whole history goes down. Deferring on it
    /// would leave every brand-new conversation spinning its retry budget and never writing a first line.
    /// </summary>
    [Fact]
    public async Task ColdStart_TreatsADefinitelyMissingTranscriptAsAFirstFlush_AndAppendsTheWholeHistory()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command => IsSplice(command) ? Ok() : Missing(),
        };

        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = Written(browser, 0).Should().Be(ExpectedAppend(messages));
    }

    // ---------------------------------------------------------------- AC 5

    /// <summary>
    /// AC 5. A non-zero splice leaves the watermark unadvanced, so the next flush re-appends the same
    /// suffix. The failure direction is duplicate-on-retry: an advanced watermark would have dropped
    /// those rows permanently.
    /// </summary>
    [Fact]
    public async Task FailedSplice_LeavesTheWatermarkUnadvanced_SoTheNextFlushDuplicatesRatherThanTruncates()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2), Msg("m3", 3)];
        await store.AppendMessagesAsync(ThreadId, messages);

        // Only the splice fails: a failing PROBE is a different story (the destination could not be
        // inspected at all), and it now defers before anything is staged, which would hide this case.
        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command => IsSplice(command) ? Fail("no space left on device") : Ok(),
        };
        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().ContainSingle();

        browser.ExecuteHandler = null;
        browser.ExecResult = Ok();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        var payload = ExpectedAppend(messages);
        _ = Written(browser, 0).Should().Be(payload);
        _ = Written(browser, 1).Should().Be(payload);

        // Containment came first and was not repeated: the failure was the splice, not the ignore file.
        _ = browser.Writes[0].Path.Should().Be(GitignorePath);
        _ = browser.Writes.Where(w => w.Path == GitignorePath).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- AC 8

    /// <summary>
    /// AC 8. <c>.conversations/.gitignore</c> is written exactly once, on the FIRST SUCCESSFUL flush — not
    /// on every flush, and not before anything succeeded (a conversation that never produced a transcript
    /// leaves no directory behind). A workspace is frequently a git checkout and an agent frequently runs
    /// <c>git add -A</c>.
    /// </summary>
    [Fact]
    public async Task Gitignore_IsWrittenOnceOnTheFirstSuccessfulFlush()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser);
        _ = await writer.FlushAsync();

        await store.AppendMessagesAsync(ThreadId, [Msg("m2", 2)]);
        _ = await writer.FlushAsync();

        var gitignores = browser.Writes.Where(w => w.Path == GitignorePath).ToList();
        _ = gitignores.Should().HaveCount(1);
        _ = Encoding.UTF8.GetString(gitignores[0].Bytes).Should().Be("*\n");
    }

    /// <summary>
    /// The containment file is the entire opt-out for the feature, so "we tried once and it did not work"
    /// is not an acceptable resting state: the transcript is on disk, unignored, and the next
    /// <c>git add -A</c> an agent runs in that workspace publishes the conversation's unredacted reasoning
    /// off-machine. Ordering containment ahead of the append covers everything this process writes — but
    /// not a transcript an EARLIER process left behind, which is on disk before this writer exists and may
    /// have nothing further to append. So the attempt must also be made on a flush that appends NOTHING;
    /// tying it to "this flush wrote rows" leaves that file uncovered forever.
    /// </summary>
    [Fact]
    public async Task Gitignore_IsWrittenOnAFlushThatAppendsNothing_ForATranscriptAnEarlierProcessLeft()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, messages);

        // Cold writer over a workspace whose transcript is already complete: the tail already ends at the
        // last row, so no flush this writer ever runs has anything to append.
        var existing = ExpectedAppend(messages);
        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command => IsSplice(command) ? Ok() : Ok(existing),
            WriteFailure = path =>
                path == GitignorePath ? new SandboxException(SandboxErrorKind.Protocol, "gateway said no") : null,
        };
        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Should().BeEmpty();

        browser.WriteFailure = null;
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.UpToDate);
        _ = browser.Writes.Should().ContainSingle(w => w.Path == GitignorePath);

        // And nothing was appended on either pass — the rows were already there.
        _ = browser.Writes.Where(w => w.Path == TempPath).Should().BeEmpty();
    }

    /// <summary>
    /// Retrying containment is not enough on its own, because a retry budget can run out and a finished
    /// conversation produces no further trigger. The ordering is what makes the guarantee unconditional:
    /// containment is established BEFORE the first transcript byte reaches the workspace, so "reasoning on
    /// disk with nothing covering it" is not a state the writer can be left in — not by a failed write, not
    /// by an exhausted budget, not by a process that exits between the two calls. A conversation that never
    /// gets its <c>.gitignore</c> written simply never gets a transcript either, which is the safe side.
    /// </summary>
    [Fact]
    public async Task Gitignore_IsWrittenBeforeAnyTranscriptByte_SoAFailedContainmentLeavesNothingExposed()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser
        {
            WriteFailure = path =>
                path == GitignorePath ? new SandboxException(SandboxErrorKind.Protocol, "gateway said no") : null,
        };
        var writer = CreateWriter(store, browser);

        // Nothing staged, nothing spliced: the rows stay in the store, where they are already contained.
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();

        // And a failure that is never followed by a successful flush still leaves nothing exposed — this is
        // the resting state of a conversation whose last turn has already happened.
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();

        browser.WriteFailure = null;
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Writes[0].Path.Should().Be(GitignorePath);
        _ = browser.Writes[1].Path.Should().Be(TempPath);
    }

    // ---------------------------------------------------------------- AC 7, 12

    /// <summary>
    /// AC 7 / AC 12. The fan-out writes one file per sub-agent: a null display name pins
    /// <c>agent-{shortAgentId}</c>, and two sub-agents sharing a display name still land in DISTINCT files
    /// because the short id comes from the agent id. Each file's first line hangs off the main file's
    /// watermark, so a reader that concatenates the whole set still resolves one chain and still sees each
    /// message exactly once.
    /// </summary>
    [Fact]
    public async Task SubAgentFanOut_NamesFilesDistinctly_AndRootsEachFileInTheMainFile()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] main = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, main);

        PersistedMessage[] nameless = [Msg("n1", 10, threadId: "subagent-nameless")];
        PersistedMessage[] firstReviewer = [Msg("r1a", 20, threadId: "subagent-r1")];
        PersistedMessage[] secondReviewer = [Msg("r2a", 30, threadId: "subagent-r2")];
        await SeedSubAgentAsync(store, "nameless", null, nameless);
        await SeedSubAgentAsync(store, "r1", "reviewer", firstReviewer);
        await SeedSubAgentAsync(store, "r2", "reviewer", secondReviewer);

        var browser = new FakeFileBrowser();
        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        var rootUid = WorkspaceTranscriptLine.DeriveUid("m2");
        var splices = browser.Commands.Where(c => IsSplice(c)).ToList();
        _ = splices.Select(c => c.Arguments[6]).Should().Equal(
            MainPath(Title),
            AgentPath(Title, "nameless", null),
            AgentPath(Title, "r1", "reviewer"),
            AgentPath(Title, "r2", "reviewer")
        );
        _ = AgentPath(Title, "nameless", null).Should()
            .EndWith($"/agent-{WorkspaceTranscriptLine.ShortId("nameless")}.jsonl");
        _ = AgentPath(Title, "r1", "reviewer").Should().NotBe(AgentPath(Title, "r2", "reviewer"));

        _ = Written(browser, 0).Should().Be(ExpectedAppend(main));
        _ = Written(browser, 1).Should().Be(ExpectedAppend(nameless, rootParentUid: rootUid));
        _ = Written(browser, 2).Should().Be(ExpectedAppend(firstReviewer, agent: "reviewer", rootParentUid: rootUid));
        _ = Written(browser, 3).Should().Be(ExpectedAppend(secondReviewer, agent: "reviewer", rootParentUid: rootUid));

        // AC 12: two (here four) files present, and every uid still occurs exactly once across them.
        var uids = UidsIn(string.Concat(Enumerable.Range(0, 4).Select(i => Written(browser, i))));
        _ = uids.Should().HaveCount(5);
        _ = uids.Distinct(StringComparer.Ordinal).Should().HaveCount(5);
    }

    /// <summary>
    /// A FAILED main append must skip the fan-out entirely, and this is the one failure in the pipeline
    /// that is not recoverable by retrying. Every sub-agent file's first line hangs off the main file's
    /// watermark, and that anchor is resolved once and then PINNED for the life of the writer — no later
    /// flush revisits a file that already has a first line. Running the fan-out beside a failed append
    /// mints those anchors from a watermark the failure left unadvanced, so on a first flush every
    /// descendant file is pinned to null and the conversation's files are never one lineage again.
    /// Deferring costs one repeated pass; a wrong anchor costs the chain permanently.
    /// </summary>
    [Fact]
    public async Task FailedMainAppend_SkipsTheFanOut_SoNoSubAgentFileIsRootedInANullAnchor()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] main = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, main);
        PersistedMessage[] child = [Msg("a1a", 10, threadId: "subagent-a1")];
        await SeedSubAgentAsync(store, "a1", "alpha", child);

        // Only the MAIN file's splice fails; a sub-agent splice would succeed if one were attempted, which
        // is what makes the wrong-anchor write reachable rather than hypothetical.
        var browser = new FakeFileBrowser
        {
            ExecuteHandler = command =>
                IsSplice(command) && command.Arguments[6] == MainPath(Title)
                    ? Fail("no space left on device")
                    : Ok(),
        };
        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title));

        browser.ExecuteHandler = null;
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        // The child's first line chains off the main file's real tail — a row that genuinely exists in the
        // main file — instead of off nothing.
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title), MainPath(Title), AgentPath(Title, "a1", "alpha"));
        _ = Written(browser, 2).Should()
            .Be(ExpectedAppend(child, agent: "alpha", rootParentUid: WorkspaceTranscriptLine.DeriveUid("m2")));
    }

    /// <summary>
    /// The fan-out is STRICTLY SEQUENTIAL, and this is a hard requirement rather than a style preference:
    /// every file in a conversation is staged through ONE temp path, so two overlapping writes would PUT
    /// over each other's bytes and <c>cat</c> the survivor into both destinations. Each splice is asserted
    /// to observe exactly its own staged write and no other file's — an interleaved (or
    /// <c>Task.WhenAll</c>-ed) fan-out could not produce this sequence.
    /// </summary>
    [Fact]
    public async Task SubAgentFanOut_StagesAndSplicesOneFileAtATime()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));
        await SeedSubAgentAsync(store, "b2", "beta", Msg("b2a", 20, threadId: "subagent-b2"));

        var browser = new FakeFileBrowser();
        var stagedWhenSpliced = new List<int>();
        browser.ExecuteHandler = command =>
        {
            if (IsSplice(command))
            {
                // Counted over STAGED writes only: the containment .gitignore is PUT ahead of the first
                // append and is not a staged payload, so counting every write would just measure it.
                stagedWhenSpliced.Add(browser.Writes.Count(w => w.Path == TempPath));
            }

            return Ok();
        };

        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        // 1, 2, 3 — each splice sees its own PUT and nothing further. Any overlap shows up as a splice
        // observing a later file's staged bytes already sitting in the shared slot.
        _ = stagedWhenSpliced.Should().Equal(1, 2, 3);
        _ = browser.Writes.Skip(1).Take(3).Select(w => w.Path).Should().AllBe(TempPath);
    }

    /// <summary>
    /// The fan-out is bounded per flush and advances round-robin, so a conversation with many descendants
    /// is covered across successive flushes instead of holding the store's process-wide read semaphore for
    /// every descendant at one turn boundary. A truncated pass reports <c>Progressing</c> so another flush
    /// follows — and once the sweep has covered everything, it stops asking.
    /// </summary>
    [Fact]
    public async Task SubAgentFanOut_IsBoundedPerFlush_AndCoversTheRestOnTheNextOne()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));
        await SeedSubAgentAsync(store, "b2", "beta", Msg("b2a", 20, threadId: "subagent-b2"));

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser, maxSubAgentFilesPerFlush: 1);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Progressing);
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title), AgentPath(Title, "a1", "alpha"));

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(AgentPath(Title, "b2", "beta"));

        // The sweep has now visited every descendant, so the writer stops asking. Reporting Deferred here
        // instead — as a capped pass once did — would spend the caller's FAILURE budget on a conversation
        // where nothing failed, capping how far any single trigger can ever reach into a large roster.
        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.UpToDate);
        _ = browser.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// The reason a capped pass keeps asking at all: the descendant that changed is OUTSIDE the current
    /// slice, and "this slice is current" says nothing about the ones beyond it. Stopping on the first
    /// capped pass whose own slice was already current ends one step short — the pass that would have
    /// covered the changed descendant never runs. There is no ordering that avoids this: the cursor cannot
    /// know which descendant moved without reading it.
    /// </summary>
    [Fact]
    public async Task SubAgentFanOut_KeepsAskingWhileCapped_SoAChangeBeyondTheSliceIsStillMirrored()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));
        await SeedSubAgentAsync(store, "b2", "beta", Msg("b2a", 20, threadId: "subagent-b2"));

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser, maxSubAgentFilesPerFlush: 1);

        // Two passes bring both descendants current and return the cursor to the first one.
        _ = await writer.FlushAsync();
        _ = await writer.FlushAsync();

        // Only the descendant the NEXT slice will not look at moves. In production that change IS an
        // external trigger — the mirror observes the child's completion notification — and the trigger is
        // what starts the fresh sweep this scenario depends on.
        await store.AppendMessagesAsync("subagent-b2", [Msg("b2b", 21, threadId: "subagent-b2")]);
        writer.NoteExternalTrigger();

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Progressing);
        _ = browser.Commands.Where(c => IsSplice(c)).Should().BeEmpty();

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(AgentPath(Title, "b2", "beta"));
    }

    /// <summary>
    /// The chain has to END. "Keep asking while capped" with no notion of coverage asks forever — every
    /// pass over a roster larger than the cap is a capped pass, so the writer requests a follow-up on a
    /// conversation that is completely mirrored and quiet, and the only thing that stops it is the caller
    /// burning a failure budget that exists for failures. Once a sweep has visited every descendant the
    /// writer is done asking, and it does not start over until an external trigger says something moved.
    /// </summary>
    [Fact]
    public async Task SubAgentFanOut_StopsAskingOnceEveryDescendantHasBeenCovered()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));
        await SeedSubAgentAsync(store, "b2", "beta", Msg("b2a", 20, threadId: "subagent-b2"));
        await SeedSubAgentAsync(store, "c3", "gamma", Msg("c3a", 30, threadId: "subagent-c3"));

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser, maxSubAgentFilesPerFlush: 1);

        // While descendants remain unvisited the pass is still making progress and asks for a follow-up.
        _ = (await writer.FlushAsync()).Should().NotBe(TranscriptFlushOutcome.UpToDate);
        _ = (await writer.FlushAsync()).Should().NotBe(TranscriptFlushOutcome.UpToDate);

        // The third pass completes the sweep, and the fourth has nothing left to ask about. Neither may
        // report Deferred: nothing failed, and Deferred is what spends the caller's failure budget.
        _ = (await writer.FlushAsync()).Should().NotBe(TranscriptFlushOutcome.Deferred);
        _ = (await writer.FlushAsync()).Should().NotBe(TranscriptFlushOutcome.Deferred);
    }

    /// <summary>
    /// A sub-agent that finishes AFTER its parent's turn ended never publishes the conversation's
    /// <c>RunCompletedMessage</c>, so a mirror listening only for run completion would never write its
    /// file. <see cref="ConversationTranscriptWriter.NoteSubAgentActivity"/> is that second trigger — and
    /// it is also the only safe caller of the descendant cache's refresh, which costs a full store scan.
    /// </summary>
    [Fact]
    public async Task NoteSubAgentActivity_IsWhatMakesALaterSpawnVisibleToTheFanOut()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser);
        _ = await writer.FlushAsync();

        await SeedSubAgentAsync(store, "late", "latecomer", Msg("l1", 10, threadId: "subagent-late"));
        await store.AppendMessagesAsync(ThreadId, [Msg("m2", 2)]);

        browser.Commands.Clear();
        _ = await writer.FlushAsync();
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title));

        writer.NoteSubAgentActivity();
        browser.Commands.Clear();
        _ = await writer.FlushAsync();
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(AgentPath(Title, "late", "latecomer"));
    }

    // ---------------------------------------------------------------- AC 10, 11

    /// <summary>
    /// AC 10. A retitle reaches the mirror only through <c>ThreadMetadata</c> — the UI's
    /// <c>PUT {threadId}/metadata</c> never touches the message stream — so the leaf is recomputed at the
    /// top of every flush and the rename is issued BEFORE anything is appended. Without that ordering the
    /// first flush after a retitle creates a second file and re-appends the entire history.
    /// </summary>
    [Fact]
    public async Task Retitle_MovesTheFileAndTheAgentsDirectory_BeforeAppendingIntoTheMovedFile()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser);
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        await SeedConversationAsync(store, RetitledTo);
        await store.AppendMessagesAsync(ThreadId, [Msg("m2", 2)]);

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        _ = browser.Commands.Should().HaveCount(3);
        _ = browser.Commands[0].Arguments.Should().Equal("mv", "--", MainPath(Title), MainPath(RetitledTo));
        _ = browser.Commands[1].Arguments.Should()
            .Equal("mv", "--", AgentsDirectory(Title), AgentsDirectory(RetitledTo));
        _ = browser.Commands[2].Arguments.Should()
            .Equal("sh", "-c", ExpectedSpliceScript, "sh", ".conversations", TempPath, MainPath(RetitledTo));
    }

    /// <summary>
    /// AC 11. A failed <c>mv</c> is not fatal: the old path is still complete and readable, so this flush
    /// keeps writing to it and the rename is retried next time. The watermark is keyed by THREAD, never by
    /// path, so nothing is re-appended when the move finally lands.
    /// </summary>
    [Fact]
    public async Task FailedRename_KeepsWritingToTheOldPath_AndRetriesOnTheNextFlush()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser();
        var writer = CreateWriter(store, browser);
        _ = await writer.FlushAsync();

        browser.ExecuteHandler = command => command.Arguments[0] == "mv" ? Fail("device busy") : Ok();
        await SeedConversationAsync(store, RetitledTo);
        PersistedMessage[] all = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, [all[1]]);

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands[0].Arguments.Should().Equal("mv", "--", MainPath(Title), MainPath(RetitledTo));
        _ = browser.Commands[1].Arguments[6].Should().Be(MainPath(Title));
        _ = Written(browser, 1).Should().Be(ExpectedAppend(all, skip: 1));

        browser.ExecuteHandler = null;
        PersistedMessage[] everything = [.. all, Msg("m3", 3)];
        await store.AppendMessagesAsync(ThreadId, [everything[2]]);

        browser.Commands.Clear();
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands[0].Arguments.Should().Equal("mv", "--", MainPath(Title), MainPath(RetitledTo));
        _ = browser.Commands[1].Arguments[6].Should().Be(MainPath(RetitledTo));
        _ = Written(browser, 2).Should().Be(ExpectedAppend(everything, skip: 2));
    }

    /// <summary>
    /// A retitle that happens while nothing is mirroring — the host restarts, or the conversation is
    /// evicted and later re-attached — reaches a writer that has no idea what its file used to be called.
    /// Adopting the CURRENT title outright leaves the transcript already on disk unseen, and the
    /// conversation ends up SPLIT: the old file frozen at its last row under the old slug, a second file
    /// restarting the history from zero, and the old <c>_agents/</c> directory orphaned beside the wrong
    /// name. The short id in the leaf is what makes this recoverable — it identifies the conversation
    /// independently of the title, across any number of retitles — so a cold writer looks for its own file
    /// before it creates one.
    /// </summary>
    [Fact]
    public async Task ColdStartAfterARetitle_AdoptsTheExistingFile_InsteadOfStartingASecondTranscript()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));

        // What a previous process left in the workspace, under the title in force when it ran. This writer
        // is brand new: its only route to that name is the listing.
        var stale = WorkspaceTranscriptLine.MainFileLeaf(RetitledTo, ShortThreadId);
        var browser = new FakeFileBrowser();
        browser.Listings[ConversationTranscriptWriter.TranscriptDirectory] =
        [
            new SandboxDirectoryEntry(
                $"{stale}{ConversationTranscriptWriter.TranscriptExtension}",
                SandboxEntryType.File,
                128,
                NameLossy: false
            ),
            new SandboxDirectoryEntry(
                $"{stale}{ConversationTranscriptWriter.AgentsDirectorySuffix}",
                SandboxEntryType.Directory,
                null,
                NameLossy: false
            ),
        ];

        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        // Adopted by the same move the warm path uses, and BEFORE the tail that recovers the watermark —
        // otherwise the watermark is read from a file this flush is about to stop writing to.
        _ = browser.Commands[0].Arguments.Should().Equal("mv", "--", MainPath(RetitledTo), MainPath(Title));
        _ = browser.Commands[1].Arguments.Should()
            .Equal("mv", "--", AgentsDirectory(RetitledTo), AgentsDirectory(Title));
        _ = browser.Commands[2].Arguments.Should()
            .Equal("sh", "-c", ExpectedProbeScript, "sh", MainPath(Title), "5");

        // One transcript, and the sub-agent file lands under it rather than beside the abandoned name.
        _ = browser.Commands.Where(IsSplice).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title), AgentPath(Title, "a1", "alpha"));
    }

    /// <summary>
    /// The listing is the cold writer's ONLY route to the file it already owns, so a listing that FAILS and
    /// a directory that is genuinely empty cannot be answered the same way. Treating a transport or
    /// protocol failure as "nothing to adopt" recreates the split transcript above — the old file frozen
    /// under its old slug, a second one restarting the history from zero — except now it is triggered by a
    /// momentary gateway fault rather than by a real absence, and the adoption path never runs again
    /// because the second file IS the computed leaf from then on. A failure defers instead: nothing is
    /// appended, nothing is renamed, and the next flush asks again.
    /// </summary>
    [Fact]
    public async Task ColdStart_DefersWhenTheDirectoryListingFails_InsteadOfAdoptingASecondTranscript()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        // The conversation's transcript IS in the workspace, under the slug in force when it was written.
        var stale = WorkspaceTranscriptLine.MainFileLeaf(RetitledTo, ShortThreadId);
        var browser = new FakeFileBrowser
        {
            ListThrows = new SandboxException(SandboxErrorKind.Protocol, "gateway said no"),
        };
        browser.Listings[ConversationTranscriptWriter.TranscriptDirectory] =
        [
            new SandboxDirectoryEntry(
                $"{stale}{ConversationTranscriptWriter.TranscriptExtension}",
                SandboxEntryType.File,
                128,
                NameLossy: false
            ),
        ];

        var writer = CreateWriter(store, browser);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);
        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();

        // And once the gateway answers, the SAME writer adopts the file that was there all along.
        browser.ListThrows = null;
        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands[0].Arguments.Should().Equal("mv", "--", MainPath(RetitledTo), MainPath(Title));
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title));
    }

    /// <summary>
    /// The other side of that discrimination, and the reason it has to be narrow: a workspace that has
    /// never held a transcript answers the listing with a DEFINITE miss, and that is the ordinary
    /// first-flush case rather than a fault. Deferring on it would leave every brand-new conversation
    /// spinning its retry budget and never writing a first line.
    /// </summary>
    [Fact]
    public async Task ColdStart_TreatsADefinitelyMissingDirectoryAsNothingToAdopt()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User")];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser
        {
            ListThrows = new SandboxException(SandboxErrorKind.NotFound, "no such path")
            {
                ErrorCode = "path_not_found",
            },
        };

        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = browser.Commands.Where(c => IsSplice(c)).Select(c => c.Arguments[6])
            .Should().Equal(MainPath(Title));
    }

    // ---------------------------------------------------------------- AC 19

    /// <summary>
    /// AC 19. The workspace mirror carries FULL fidelity, reasoning included. It is the one transcript
    /// read that is not cross-agent — the rows go into the conversation's own workspace — so the
    /// reasoning filter every other reader gets is deliberately not applied here.
    /// </summary>
    [Fact]
    public async Task Flush_KeepsReasoningRowsInFull()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            Converters = { new IMessageJsonConverter() },
        };
        var reasoning = new ReasoningMessage { Reasoning = "weigh option A against option B", Role = Role.Assistant };

        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages =
        [
            Msg("m1", 1, "User"),
            Msg(
                "m2",
                2,
                messageType: nameof(ReasoningMessage),
                messageJson: JsonSerializer.Serialize<IMessage>(reasoning, options)
            ),
        ];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser();
        _ = await CreateWriter(store, browser).FlushAsync();

        var lines = Written(browser, 0).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _ = lines.Should().HaveCount(2);

        var line = JsonSerializer.Deserialize<JsonElement>(lines[1]);
        _ = line.GetProperty("message_type").GetString().Should().Be(nameof(ReasoningMessage));

        var round = JsonSerializer.Deserialize<IMessage>(line.GetProperty("message_json").GetString()!, options);
        _ = round.Should().BeOfType<ReasoningMessage>()
            .Which.Reasoning.Should().Be("weigh option A against option B");
    }

    // ---------------------------------------------------------------- AC 23, 24

    /// <summary>
    /// AC 23. No sandbox session is the ordinary state of a non-sandbox conversation, not a fault: the
    /// flush returns quietly with one debug line, writes nothing, and issues no command.
    /// </summary>
    [Fact]
    public async Task NoSession_WritesNothingAndReportsNoError()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser
        {
            Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null),
        };
        var logger = new CapturingLogger<ConversationTranscriptWriter>();

        _ = (await CreateWriter(store, browser, logger).FlushAsync()).Should().Be(TranscriptFlushOutcome.Unavailable);

        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();
        _ = logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    /// <summary>
    /// A conversation with no workspace bound never even reaches the gateway — the metadata read alone
    /// settles it. Same quiet debug outcome.
    /// </summary>
    [Fact]
    public async Task NoWorkspaceBound_NeverResolvesASession()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, workspaceId: null);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var browser = new FakeFileBrowser();
        var logger = new CapturingLogger<ConversationTranscriptWriter>();

        _ = (await CreateWriter(store, browser, logger).FlushAsync()).Should().Be(TranscriptFlushOutcome.Unavailable);

        _ = browser.LastPersistedWorkspaceId.Should().BeNull();
        _ = browser.Writes.Should().BeEmpty();
        _ = logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    /// <summary>
    /// AC 24. A resolve that throws, and a session owned by another identity, each report exactly ONE
    /// warning and leave the run untouched — the mirror never propagates. Once the binding is usable
    /// again the next flush writes the full history it had deferred.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnusableSession_WarnsOnce_ThenWritesAfterRebind(bool resolveThrows)
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);
        PersistedMessage[] messages = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, messages);

        var browser = new FakeFileBrowser();
        if (resolveThrows)
        {
            browser.ResolveThrows = new SandboxException(SandboxErrorKind.Protocol, "gateway said no");
        }
        else
        {
            browser.Resolution = new SandboxSessionResolution(
                SandboxSessionResolutionOutcome.CredentialConflict,
                null,
                "other-app",
                "app"
            );
        }

        var logger = new CapturingLogger<ConversationTranscriptWriter>();
        var writer = CreateWriter(store, browser, logger);

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Unavailable);
        _ = browser.Writes.Should().BeEmpty();
        _ = logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle();

        browser.ResolveThrows = null;
        browser.Resolution = new SandboxSessionResolution(
            SandboxSessionResolutionOutcome.Resolved,
            FakeFileBrowser.LiveSession,
            "app",
            null
        );

        _ = (await writer.FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = Written(browser, 0).Should().Be(ExpectedAppend(messages));
    }

    // ---------------------------------------------------------------- read until stable

    /// <summary>
    /// The blocker this pipeline exists around. <c>MultiTurnAgentBase.AddToHistory</c> persists on a
    /// discarded, unawaited task and <c>CompleteRunAsync</c> publishes run completion without joining
    /// those tasks, so at trigger time a turn's final assistant and reasoning rows are commonly still in
    /// flight. Rows that never settle mean the flush is SKIPPED with its watermark unadvanced — appending
    /// the truncated list would also land the late row at the file tail next flush, giving it a
    /// <c>parent_uid</c> pointing at a row it did not follow.
    /// </summary>
    [Fact]
    public async Task RowsThatNeverSettle_DeferTheFlushWithoutWritingAnything()
    {
        var inner = new InMemoryConversationStore();
        await SeedConversationAsync(inner);
        await inner.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);

        var late = 0;
        var store = new ScriptedLoadStore(inner)
        {
            // One more row lands between every pair of reads — the pathological form of the real race.
            BeforeLoad = (threadId, _) => inner.AppendMessagesAsync(threadId, [Msg($"late-{++late}", 100 + late)]),
        };

        var browser = new FakeFileBrowser();
        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Deferred);

        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// The other half of the same guard: rows that settle on a RE-read are written in full. A probe that
    /// gave up after one disagreement would truncate the turn it was triggered by.
    /// </summary>
    [Fact]
    public async Task RowsThatSettleOnARetry_AreWrittenInFull()
    {
        var inner = new InMemoryConversationStore();
        await SeedConversationAsync(inner);
        PersistedMessage[] committed = [Msg("m1", 1, "User"), Msg("m2", 2)];
        await inner.AppendMessagesAsync(ThreadId, [committed[0]]);

        var store = new ScriptedLoadStore(inner)
        {
            // The in-flight assistant row lands during the first read and then stops changing.
            BeforeLoad = (threadId, call) => call == 1
                ? inner.AppendMessagesAsync(threadId, [committed[1]])
                : Task.CompletedTask,
        };

        var browser = new FakeFileBrowser();
        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);
        _ = Written(browser, 0).Should().Be(ExpectedAppend(committed));
    }

    // ---------------------------------------------------------------- shell safety

    /// <summary>
    /// The security invariant, stated as a test. <c>ExecuteWorkspaceCommandAsync</c> is a native argv
    /// vector with NO implicit shell, so every place a shell IS invoked — the splice that writes and the
    /// watermark probe that reads — must carry a compile-time constant script: the file leaf is derived
    /// from a user-authored title, and interpolating it into the script text would make <c>$(…)</c> in a
    /// title executable. <c>--</c> on the pure-argv calls stops option parsing, but it is the positional
    /// parameters that make the title inert.
    /// </summary>
    [Fact]
    public async Task EveryShellCall_UsesTheConstantScript_AndPassesTheTitleDerivedPathAsAPositionalParameter()
    {
        const string Hostile = "$(touch /tmp/pwned); rm -rf ~";

        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, Hostile);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1, "User")]);
        await SeedSubAgentAsync(store, "a1", "alpha", Msg("a1a", 10, threadId: "subagent-a1"));

        var browser = new FakeFileBrowser();
        _ = (await CreateWriter(store, browser).FlushAsync()).Should().Be(TranscriptFlushOutcome.Written);

        var leaf = WorkspaceTranscriptLine.MainFileLeaf(Hostile, ShortThreadId);
        _ = leaf.Should().Be($"touch-tmp-pwned-rm-rf-{ShortThreadId}");

        var shell = browser.Commands.Where(c => c.Arguments[0] == "sh").ToList();
        var splices = shell.Where(IsSplice).ToList();
        _ = splices.Should().HaveCount(2);
        _ = shell.Should().HaveCountGreaterThan(
            splices.Count,
            "the watermark probe is the other shell call site and is covered by this invariant too"
        );

        foreach (var command in shell)
        {
            _ = command.Arguments[1].Should().Be("-c");
            _ = command.Arguments[2].Should().BeOneOf(ExpectedProbeScript, ExpectedSpliceScript);
            _ = command.Arguments[3].Should().Be("sh");

            // Whatever the call touches reaches `sh` as a positional parameter, never as script text.
            _ = command.Arguments[2].Should().NotContain(leaf).And.NotContain(Hostile).And.NotContain("touch");
            _ = command.Arguments.Skip(4).Should().NotBeEmpty();
        }

        foreach (var splice in splices)
        {
            _ = splice.Arguments[6].Should().Contain(leaf);
        }

        // Neither script ever grows a dynamic fragment, and the pure-argv calls keep their `--`.
        _ = ExpectedSpliceScript.Should().NotContain(leaf).And.NotContain(Hostile).And.NotContain("touch");
        _ = ExpectedProbeScript.Should().NotContain(leaf).And.NotContain(Hostile).And.NotContain("touch");
        // Every shell call is covered by the loop above; what is left is the pure-argv calls, and none of
        // them may omit `--`. (Stated as "no call omits it" so the invariant does not change meaning when
        // a flow happens not to move anything.)
        _ = browser.Commands.Where(c => c.Arguments[0] != "sh")
            .Should().NotContain(c => !c.Arguments.Contains("--"));
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>
    /// <see cref="IConversationStore"/> decorator that lets a test mutate the store BETWEEN the stability
    /// probe's reads. That race is otherwise timing-dependent, and the writer is constructed with a zero
    /// settle delay, so this is what keeps the read-until-stable tests deterministic instead of sleeping.
    /// </summary>
    private sealed class ScriptedLoadStore(IConversationStore inner) : IConversationStore
    {
        private readonly Dictionary<string, int> _loads = new(StringComparer.Ordinal);

        /// <summary>Runs before each load, with the thread and that thread's 1-based read count.</summary>
        public Func<string, int, Task>? BeforeLoad { get; init; }

        public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default)
        {
            _ = _loads.TryGetValue(threadId, out var count);
            _loads[threadId] = ++count;

            if (BeforeLoad is not null)
            {
                await BeforeLoad(threadId, count);
            }

            return await inner.LoadMessagesAsync(threadId, ct);
        }

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(
            string threadId,
            ThreadMetadata metadata,
            CancellationToken ct = default) => inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) => inner.ListThreadsAsync(limit, offset, ct);
    }
}
