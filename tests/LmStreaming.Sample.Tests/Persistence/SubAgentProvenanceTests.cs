using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Pure unit coverage for <see cref="SubAgentProvenance"/>'s <c>Build</c>/<c>TryProject</c> pair — the
/// write/read halves of the durable parent→child stamp, independent of any store or wrapper.
/// Task 1 (daemon-recursive-review-completion-barrier) adds the exact terminal status/timestamp the
/// manager pushes causally at completion, so a reconstructed roster reports the real outcome instead
/// of the old always-<c>"persisted"</c> placeholder.
/// </summary>
public sealed class SubAgentProvenanceTests
{
    private const string ParentThreadId = "thread-parent";
    private const string ChildThreadId = "subagent-child-1";

    private static SubAgentSnapshot MakeSnapshot(
        SubAgentStatus status,
        DateTimeOffset? terminalAtUtc = null) =>
        new(
            AgentId: "child-1",
            Name: "alpha",
            TemplateName: "code-reviewer:security",
            Task: "check auth",
            Status: status,
            ThreadId: ChildThreadId,
            LastActivityUtc: null,
            TerminalAtUtc: terminalAtUtc);

    [Theory]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Error)]
    [InlineData(SubAgentStatus.Stopped)]
    public void Build_StampsExactStatusAndTerminalTimestamp_ForTerminalStatuses(SubAgentStatus status)
    {
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeSnapshot(status, terminalAt));

        properties[SubAgentProvenance.StatusKey].Should().Be(status.ToString().ToLowerInvariant());
        properties[SubAgentProvenance.TerminalAtKey].Should().Be(terminalAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Build_StampsRunningStatus_AndMarksTerminalTimestampForRemoval()
    {
        var properties = SubAgentProvenance.Build(ParentThreadId, MakeSnapshot(SubAgentStatus.Running));

        properties[SubAgentProvenance.StatusKey].Should().Be("running");

        // A running sub-agent has no terminal instant, but a PRIOR terminal transition may have left one
        // persisted (e.g. after a restart). Build() must explicitly mark the key for removal rather than
        // merely omitting it, so NonOwningConversationStore's additive merge actually clears it.
        properties.Should().ContainKey(SubAgentProvenance.TerminalAtKey);
        ReferenceEquals(properties[SubAgentProvenance.TerminalAtKey], SubAgentProvenance.RemovalMarker)
            .Should().BeTrue(
                "a running sub-agent's terminal timestamp must be explicitly marked for removal, not " +
                "merely omitted, so a stale value from a prior terminal transition is cleared");
    }

    [Fact]
    public void Build_FallsBackToUtcNow_WhenSnapshotOmitsTerminalAtUtc()
    {
        var before = DateTimeOffset.UtcNow;
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeSnapshot(SubAgentStatus.Completed, terminalAtUtc: null));
        var after = DateTimeOffset.UtcNow;

        var stamped = (long)properties[SubAgentProvenance.TerminalAtKey];
        stamped.Should().BeInRange(before.ToUnixTimeMilliseconds(), after.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void RoundTrip_BuildThenTryProject_PreservesExactTerminalStatusAndTimestamp()
    {
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_500_000);
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1, // Deliberately stale/irrelevant: TerminalAtKey must win over this.
            Properties = SubAgentProvenance.Build(
                ParentThreadId, MakeSnapshot(SubAgentStatus.Completed, terminalAt)),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary.Should().NotBeNull();
        summary!.Status.Should().Be("completed");
        summary.LastActivityUtc.Should().Be(terminalAt,
            "the exact terminal instant captured at the transition must survive the round trip " +
            "rather than being recomputed from LastUpdated");
    }

    [Fact]
    public void TryProject_FallsBackToLastUpdated_WhenStatusIsRunning()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 42_000,
            Properties = SubAgentProvenance.Build(ParentThreadId, MakeSnapshot(SubAgentStatus.Running)),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.Status.Should().Be("running");
        summary.LastActivityUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(42_000),
            "with no terminal instant stamped, activity falls back to the metadata's own LastUpdated");
    }

    [Fact]
    public void TryProject_ReturnsUnknownStatus_ForLegacyMetadataWithNoStatusStamp()
    {
        // Simulates metadata persisted before Task 1's status/terminal-timestamp stamps existed:
        // only the parent link (and possibly name/template/task) is present.
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 5_000,
            Properties = SubAgentProvenance.Build(ParentThreadId, snapshot: null),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.Status.Should().Be(SubAgentProvenance.UnknownStatus);
        summary.Status.Should().Be("unknown");
        summary.LastActivityUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(5_000));
    }

    [Fact]
    public void TryProject_ReturnsNull_WhenParentThreadIdDoesNotMatch()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1,
            Properties = SubAgentProvenance.Build(
                "thread-someone-else", MakeSnapshot(SubAgentStatus.Completed, DateTimeOffset.UtcNow)),
        };

        SubAgentProvenance.TryProject(metadata, ParentThreadId).Should().BeNull();
    }

    // ── Model routing ─────────────────────────────────────────────────────────────────────────────
    // The value exists on the snapshot and on the DTO; it was being dropped in the middle, so the panel
    // that names a run's sub-agents — and the daemon artifacts built from it — could not say what any of
    // them ran on (#552).

    private static SubAgentSnapshot MakeRoutedSnapshot(
        string? effectiveModelId,
        int? tier,
        string selectionSource) =>
        MakeSnapshot(SubAgentStatus.Completed, DateTimeOffset.UnixEpoch) with
        {
            EffectiveModelId = effectiveModelId,
            EffectiveModelIntelligence = tier,
            ModelSelectionSource = selectionSource,
        };

    [Fact]
    public void Build_StampsTheEffectiveModelTierAndSelectionSource()
    {
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeRoutedSnapshot("gpt-5.6-sol", 3, "template-tier"));

        properties[SubAgentProvenance.ModelKey].Should().Be("gpt-5.6-sol");
        properties[SubAgentProvenance.ModelIntelligenceKey].Should().Be(3);
        properties[SubAgentProvenance.ModelSelectionSourceKey].Should().Be("template-tier");
    }

    [Fact]
    public void Build_OmitsTheModelKeys_RatherThanMarkingThemForRemoval_WhenNothingWasRouted()
    {
        // Deliberately NOT the RemovalMarker treatment TerminalAtKey gets. A child's effective model is
        // decided once, when its provider is built, and does not go stale across a restart the way a
        // terminal instant does — so there is no stale value to clear, and omitting is the honest write.
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeRoutedSnapshot(effectiveModelId: null, tier: null, selectionSource: "pending"));

        properties.Should().NotContainKey(SubAgentProvenance.ModelKey);
        properties.Should().NotContainKey(SubAgentProvenance.ModelIntelligenceKey);
        properties[SubAgentProvenance.ModelSelectionSourceKey].Should().Be(
            "pending", "a spawn that has not been routed yet says so, which is not the same as saying nothing");
    }

    [Fact]
    public void TryProject_ReadsBackTheModelRoutingItStamped()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1,
            Properties = SubAgentProvenance.Build(
                ParentThreadId, MakeRoutedSnapshot("gpt-5.6-terra", 2, "spawn-tier")),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.EffectiveModelId.Should().Be("gpt-5.6-terra");
        summary.EffectiveModelIntelligence.Should().Be(2);
        summary.ModelSelectionSource.Should().Be("spawn-tier");
    }

    [Fact]
    public void TryProject_ReturnsNullModel_ForMetadataThatPredatesTheStamp()
    {
        // Legacy rows, and any child that wrote metadata without ever registering with the live manager.
        // Null all the way through: a fabricated default here would reach the review artifacts and be
        // indistinguishable from a model that was actually observed.
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 5_000,
            Properties = SubAgentProvenance.Build(ParentThreadId, snapshot: null),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.EffectiveModelId.Should().BeNull();
        summary.EffectiveModelIntelligence.Should().BeNull();
        summary.ModelSelectionSource.Should().BeNull();
    }

    [Fact]
    public void TryProject_ReadsTheTierBackThroughTheJsonRoundTrip()
    {
        // Properties round-trip through JSON on their way to disk, so an int comes back as a JsonElement.
        // Reading it as a plain int cast would project every persisted tier as absent — the same
        // round-trip tolerance ReadUnixMillis already carries for the terminal timestamp.
        using var document = JsonDocument.Parse(
            $"{{\"{SubAgentProvenance.ModelIntelligenceKey}\":4}}");
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1,
            Properties = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [
                    KeyValuePair.Create(SubAgentProvenance.ParentThreadIdKey, (object)ParentThreadId),
                    KeyValuePair.Create(
                        SubAgentProvenance.ModelIntelligenceKey,
                        (object)document.RootElement.GetProperty(SubAgentProvenance.ModelIntelligenceKey)),
                ]),
        };

        SubAgentProvenance.TryProject(metadata, ParentThreadId)!
            .EffectiveModelIntelligence.Should().Be(4);
    }

    [Fact]
    public async Task TheStampedModelIsTheOneTheProviderWasBuiltFor_NotTheConfiguredOption()
    {
        // The acceptance clause for #552, asserted where it can actually fail: against the model the
        // PROVIDER FACTORY was handed, not against the option the test itself configured. The two come
        // apart — the plain path resolves spawn-model > spawn-tier > conversation-default > template >
        // parent before it asks TierAgentFactory for a transport — so comparing the record to the
        // configured knob would pass even if the stamp were wired to the wrong end of that ladder.
        string? providerBuiltForModel = null;
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(
                "The transport-correct provider must be built for the routed model, not the controller's."),
            DefaultOptions = new GenerateReplyOptions { ModelId = "template-model" },
            IsModelExplicitlySelected = true,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
            // #529's operator knob, which outranks the template. The stamp must follow the model that
            // actually reaches the provider, so this is the value the factory should be asked for.
            DefaultSubAgentModelId = "operator-configured-model",
            TierAgentFactory = model =>
            {
                providerBuiltForModel = model;
                return RespondingAgent();
            },
        };
        await using var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            parentModelId: "parent-model");

        _ = await manager.SpawnAsync("test-agent", "test task");

        providerBuiltForModel.Should().NotBeNull("the spawn must have reached the provider factory at all");

        var snapshot = manager.ListAgents().Should().ContainSingle().Subject;
        var metadata = new ThreadMetadata
        {
            ThreadId = SubAgentProvenance.ThreadIdPrefix + snapshot.AgentId,
            LastUpdated = 1,
            Properties = SubAgentProvenance.Build(ParentThreadId, snapshot),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.EffectiveModelId.Should().Be(
            providerBuiltForModel,
            "the published model must be the one the provider was actually built for");
        summary.EffectiveModelId.Should().NotBe(
            "parent-model", "and it must not be the parent's, which is what an unrouted child would show");
        summary.ModelSelectionSource.Should().Be(
            "conversation-default",
            "the label is what tells an operator their configured sub-agent model won rather than the "
                + "template's — the model id alone cannot, they may coincide");
    }

    private static IStreamingAgent RespondingAgent()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Replies());
        return agent.Object;
    }

    private static async IAsyncEnumerable<IMessage> Replies()
    {
        yield return new TextMessage { Text = "done", Role = Role.Assistant };
        await Task.Yield();
    }
}
