using System.Diagnostics.CodeAnalysis;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the cross-thread contract of <see cref="MutableSubAgentTemplateSource"/>: seed + first-
/// wins registration semantics (the same trust boundary
/// <c>WorkspaceSubAgentLoader.MergeBuiltInWins</c> establishes) and the lock-free
/// enumeration / concurrent-mutation invariant the discovery webhook relies on.
/// </summary>
public class MutableSubAgentTemplateSourceTests
{
    private static readonly Func<IStreamingAgent> StubFactory = () => new Mock<IStreamingAgent>().Object;

    private sealed record TrackingSubAgentTemplate : SubAgentTemplate
    {
        public TrackingSubAgentTemplate() { }

        [SetsRequiredMembers]
        private TrackingSubAgentTemplate(TrackingSubAgentTemplate original)
            : base(original)
        {
            OnCopy = original.OnCopy;
            OnCopy();
        }

        public Action OnCopy { get; init; } = () => { };
    }

    private static SubAgentTemplate Template(string name) =>
        new()
        {
            Name = name,
            Description = $"{name} description",
            WhenToUse = $"use {name}",
            SystemPrompt = $"You are {name}.",
            AgentFactory = StubFactory,
        };

    [Fact]
    public void EmptyCtor_StartsWithNoTemplates()
    {
        var source = new MutableSubAgentTemplateSource();

        source.Templates.Should().BeEmpty();
    }

    [Fact]
    public void SeededCtor_ExposesSeedEntries()
    {
        var seed = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["researcher"] = Template("researcher"),
        };

        var source = new MutableSubAgentTemplateSource(seed);

        source.Templates.Should().ContainKey("researcher");
        source.Templates["researcher"].Name.Should().Be("researcher");
    }

    [Fact]
    public void SeededCtor_CopiesEntries_DoesNotShareSeedDictionary()
    {
        // The source must not retain a reference to the seed dictionary: a later mutation of the
        // seed by an unrelated caller cannot retroactively change the catalog the loop is reading.
        var seed = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["researcher"] = Template("researcher"),
        };

        var source = new MutableSubAgentTemplateSource(seed);
        seed.Remove("researcher");

        source.Templates.Should().ContainKey("researcher");
    }

    [Fact]
    public void TryRegister_FirstCall_AddsAndReturnsTrue()
    {
        var source = new MutableSubAgentTemplateSource();

        var added = source.TryRegister("echo", Template("echo"));

        added.Should().BeTrue();
        source.Templates.Should().ContainKey("echo");
    }

    [Fact]
    public void TryRegister_DuplicateName_KeepsFirstAndReturnsFalse()
    {
        // First-wins matches WorkspaceSubAgentLoader.MergeBuiltInWins's trust boundary: a
        // discovered template (untrusted) cannot shadow a built-in (trusted) seed.
        var seed = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal) { ["echo"] = Template("echo") };
        var source = new MutableSubAgentTemplateSource(seed);
        var replacement = Template("echo") with { Description = "replacement" };

        var added = source.TryRegister("echo", replacement);

        added.Should().BeFalse();
        source.Templates["echo"].Description.Should().Be("echo description");
    }

    [Fact]
    public void TryRegister_DuplicateNameAfterRebind_DoesNotCopyRejectedTemplate()
    {
        var source = new MutableSubAgentTemplateSource(
            new Dictionary<string, SubAgentTemplate> { ["echo"] = Template("echo") }
        );
        source.RebindFactories(StubFactory, null);
        var copyCount = 0;
        var duplicate = new TrackingSubAgentTemplate
        {
            SystemPrompt = "duplicate",
            AgentFactory = StubFactory,
            OnCopy = () => copyCount++,
        };

        source.TryRegister("echo", duplicate).Should().BeFalse();

        copyCount.Should().Be(0);
    }

    [Fact]
    public void TryRegister_NullOrBlankName_Throws()
    {
        var source = new MutableSubAgentTemplateSource();

        var blankCall = () => source.TryRegister("   ", Template("x"));
        var nullCall = () => source.TryRegister(null!, Template("x"));

        blankCall.Should().Throw<ArgumentException>();
        nullCall.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryRegister_NullTemplate_Throws()
    {
        var source = new MutableSubAgentTemplateSource();

        var act = () => source.TryRegister("echo", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ConcurrentTryRegister_NeverThrows_FinalCountMatches()
    {
        // The update gate serializes immutable snapshot publication; this test pins the surface
        // contract and ensures no race causes a torn read or lost write under high contention.
        var source = new MutableSubAgentTemplateSource();
        const int writerCount = 32;
        const int writesPerWriter = 100;

        var tasks = Enumerable
            .Range(0, writerCount)
            .Select(writerIdx =>
                Task.Run(() =>
                {
                    for (var i = 0; i < writesPerWriter; i++)
                    {
                        // Each writer uses its own keyspace so the only valid race outcome is "everything
                        // landed"; collisions across writers would mask races as a false-success.
                        source.TryRegister($"w{writerIdx}-{i}", Template($"w{writerIdx}-{i}"));
                    }
                })
            );

        await Task.WhenAll(tasks);

        source.Templates.Count.Should().Be(writerCount * writesPerWriter);
    }

    [Fact]
    public async Task EnumerationDuringConcurrentRegistration_NeverThrows()
    {
        // SubAgentToolProvider.CreateAgentDescriptor enumerates Templates while building the
        // Agent-tool descriptor; a concurrent TryRegister from the discovery webhook must not
        // throw an InvalidOperationException through that enumeration.
        var source = new MutableSubAgentTemplateSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(
            () =>
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    source.TryRegister($"key-{i++}", Template($"k{i}"));
                }
            },
            cts.Token
        );

        var reader = Task.Run(
            () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    // Force enumeration of both keys and values from each immutable snapshot.
                    foreach (var kvp in source.Templates)
                    {
                        _ = kvp.Key.Length;
                        _ = kvp.Value.Name;
                    }
                }
            },
            cts.Token
        );

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void RebindFactories_PublishesConsistentOldAndNewSnapshots()
    {
        Func<IStreamingAgent> oldFactory = StubFactory;
        Func<IStreamingAgent> newFactory = () => new Mock<IStreamingAgent>().Object;
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsFactory = characteristics =>
            throw new InvalidOperationException(characteristics.ModelId);
        var source = new MutableSubAgentTemplateSource(
            new Dictionary<string, SubAgentTemplate>
            {
                ["first"] = Template("first") with { AgentFactory = oldFactory },
                ["second"] = Template("second") with { AgentFactory = oldFactory },
            }
        );
        var oldSnapshot = source.Templates;

        source.RebindFactories(newFactory, characteristicsFactory);
        var newSnapshot = source.Templates;

        oldSnapshot
            .Values.Should()
            .OnlyContain(template =>
                ReferenceEquals(template.AgentFactory, oldFactory) && template.CharacteristicsAgentFactory == null
            );
        newSnapshot
            .Values.Should()
            .OnlyContain(template =>
                ReferenceEquals(template.AgentFactory, newFactory)
                && ReferenceEquals(template.CharacteristicsAgentFactory, characteristicsFactory)
            );
        newSnapshot.Should().NotBeSameAs(oldSnapshot);
    }

    [Fact]
    public void TryRegister_AfterRebind_UsesCurrentFactories()
    {
        Func<IStreamingAgent> newFactory = () => new Mock<IStreamingAgent>().Object;
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsFactory = characteristics =>
            throw new InvalidOperationException(characteristics.ModelId);
        var source = new MutableSubAgentTemplateSource();
        source.RebindFactories(newFactory, characteristicsFactory);

        source.TryRegister("later", Template("later")).Should().BeTrue();

        var registered = source.Templates["later"];
        registered.AgentFactory.Should().BeSameAs(newFactory);
        registered.CharacteristicsAgentFactory.Should().BeSameAs(characteristicsFactory);
    }
}
