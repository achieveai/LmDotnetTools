using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// <see cref="SubAgentOptions.ListedTemplates"/>: a host listing narrows the <c>Agent</c> text to the templates it
/// names, in ordinal order, followed by its hint. A null listing keeps today's text. The surface is memoized by the
/// listing's reference, so a template registered later leaves a listed surface's bytes as they were.
/// </summary>
public class SubAgentTemplateListingTests : IAsyncLifetime
{
    private const string ParentThread = "thread-root-listing";
    private const string Hint = "More sub-agent types exist: search for them, then run one by name.";
    private const string CatalogHeading = "Available sub-agent types (subagent_type):";
    private const string NoneListed = "No sub-agent types are listed.";

    // Six listed keys: their hash order matches ordinal order about once in 720 runs, so a dropped sort shows.
    private static readonly string[] SourceKeys =
    [
        "f-six",
        "a-one",
        "e-five",
        "z-unlisted",
        "c-three",
        "b-two",
        "d-four",
    ];

    private readonly List<SubAgentManager> _managers = [];

    public static TheoryData<string[]> NamesListingNoTemplate =>
        [
            [],
            ["ghost"],
        ];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
        }
    }

    [Fact]
    public void Listing_NarrowsTheAgentText_ToListedTemplates_OrdinallyWithTheHint()
    {
        string? askedFor = null;
        var listing = new TemplateListing(
            Names("f-six", "a-one", "e-five", "c-three", "b-two", "d-four", "ghost"),
            Hint
        );
        var provider = CreateProvider(
            NewSource(),
            threadId =>
            {
                askedFor = threadId;
                return listing;
            }
        );

        var agent = AgentContract(provider.GetFunctions());

        CatalogOf(agent)
            .Should()
            .Be(
                CatalogHeading
                    + "\n- a-one: Does a-one.\n- b-two: Does b-two.\n- c-three: Does c-three.\n- d-four: Does d-four."
                    + "\n- e-five: Does e-five.\n- f-six: Does f-six.\n"
                    + Hint,
                "only listed templates, ordinally; a listed name with no template is skipped"
            );
        TypeDescription(agent)
            .Should()
            .Be("Which sub-agent to spawn. Listed: a-one, b-two, c-three, d-four, e-five, f-six. " + Hint);
        askedFor.Should().Be(ParentThread, "the host answers per agent, so the manager asks for its own thread");
    }

    [Theory]
    [MemberData(nameof(NamesListingNoTemplate))]
    public void Listing_NamingNoTemplate_SaysNoneListed_WithTheHint(string[] names)
    {
        var listing = new TemplateListing(Names(names), Hint);
        var provider = CreateProvider(NewSource(), _ => listing);

        var agent = AgentContract(provider.GetFunctions());

        // PC-SC5-1: no catalog heading over an empty list; the description ends saying so, then the host's hint.
        agent.Description.Should().NotContain(CatalogHeading).And.EndWith("\n\n" + NoneListed + "\n" + Hint);
        TypeDescription(agent).Should().Be("Which sub-agent to spawn. None listed. " + Hint);
    }

    [Fact]
    public void NullListing_KeepsTodaysAgentText_EveryTemplateInSourceOrder()
    {
        var source = NewSource();
        var withoutListing = AgentContract(CreateProvider(source, listedTemplates: null).GetFunctions());

        var withNullListing = AgentContract(CreateProvider(source, _ => null).GetFunctions());

        withNullListing.Description.Should().Be(withoutListing.Description);
        TypeDescription(withNullListing).Should().Be(TypeDescription(withoutListing));
        TypeDescription(withNullListing)
            .Should()
            .Be($"Which sub-agent to spawn. One of: {string.Join(", ", source.Templates.Keys)}.");
        CatalogOf(withNullListing).Should().Contain("- z-unlisted: Does z-unlisted.").And.NotContain(Hint);
    }

    [Fact]
    public void ListedSurface_IsMemoizedByTheListingReference_AndALaterTemplateLeavesItsBytes()
    {
        var source = NewSource();
        var current = new TemplateListing(Names("a-one"), Hint);
        var provider = CreateProvider(source, _ => current);

        var first = provider.GetFunctions();
        provider.GetFunctions().Should().BeSameAs(first, "the same listing reference is a memo hit");

        source.TryRegister("b-late", Template("b-late")).Should().BeTrue();
        var afterRegister = AgentContract(provider.GetFunctions());

        afterRegister.Description.Should().Be(AgentContract(first).Description, "an unlisted template adds no bytes");
        TypeDescription(afterRegister).Should().Be(TypeDescription(AgentContract(first)));

        current = new TemplateListing(Names("a-one", "b-late"), Hint);

        TypeDescription(AgentContract(provider.GetFunctions()))
            .Should()
            .Be("Which sub-agent to spawn. Listed: a-one, b-late. " + Hint, "a new listing instance is a new key");
    }

    private static MutableSubAgentTemplateSource NewSource() =>
        new(SourceKeys.ToDictionary(key => key, Template, StringComparer.Ordinal));

    private static SubAgentTemplate Template(string key) =>
        new()
        {
            Name = key,
            SystemPrompt = $"You are {key}.",
            Description = $"Does {key}.",
            AgentFactory = () => new Mock<IStreamingAgent>().Object,
        };

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.Ordinal);

    private SubAgentToolProvider CreateProvider(
        MutableSubAgentTemplateSource source,
        Func<string, TemplateListing?>? listedTemplates
    )
    {
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent.SetupGet(p => p.ThreadId).Returns(ParentThread);
        var options = new SubAgentOptions
        {
            Templates = source.Templates,
            MaxConcurrentSubAgents = 5,
            ListedTemplates = listedTemplates,
        };
        var manager = new SubAgentManager(
            parentAgent: parent.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source
        );
        _managers.Add(manager);
        return new SubAgentToolProvider(manager, source);
    }

    private static FunctionContract AgentContract(IEnumerable<FunctionDescriptor> functions) =>
        functions.Single(f => f.Contract.Name == SubAgentToolProvider.SpawnToolName).Contract;

    private static string CatalogOf(FunctionContract agent)
    {
        var description = agent.Description!;
        return description[description.IndexOf(CatalogHeading, StringComparison.Ordinal)..];
    }

    private static string? TypeDescription(FunctionContract agent) =>
        agent.Parameters!.Single(p => p.Name == "subagent_type").Description;
}
