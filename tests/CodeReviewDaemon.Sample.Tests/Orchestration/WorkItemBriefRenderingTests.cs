using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins how a <see cref="AdoWorkItemContext"/> is rendered into the reviewer's brief, and — where the render
/// cannot be judged without it — how the walk that produced it is bounded.
/// <para>
/// The block answers the one question the reviewer previously could not: what was this change ASKED to do.
/// Before it existed the answer was offered to the model in its prompt, which told it to dispatch a context
/// gatherer; across 644 observed review sub-agent spawns ZERO carried a tool that could reach Azure DevOps, so
/// the capability was never once exercised. The reviewer still cannot check any of this for itself — its
/// sandbox has no network — so whatever this block says is the only thing it will ever know about the intent.
/// </para>
/// <para>
/// Two of these tests drive the real <see cref="AdoWorkItemContextReader"/> against a scripted handler (no
/// network) rather than rendering a hand-built record. That is deliberate and it is not integration creep: a
/// hand-built record would pin the chain the TEST assembled, not the chain the walk produces, and the walk's
/// direction and its three bounds are precisely what can go silently wrong.
/// </para>
/// </summary>
public sealed class WorkItemBriefRenderingTests : LoggingTestBase
{
    public WorkItemBriefRenderingTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static string? Render(AdoWorkItemContext context) =>
        DaemonReviewStageExecutor.DescribeWorkItemContextForTests(context);

    private static readonly RepoIdentity Repo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso",
        Project = "Platform",
        RepoName = "core",
    };

    private const string PrId = "5505458";

    private AdoWorkItemContextReader CreateReader(FakeHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new FakeOAuthTokenProvider("ado", "ado-token-abc"),
            LoggerFactory.CreateLogger<AdoWorkItemContextReader>());

    /// <summary>The PR-links response, which carries the work item ids as STRINGS (the wit endpoint sends the
    /// same ids as numbers — one parser has to take both).</summary>
    private static string PrLinks(params int[] ids) =>
        $$"""
        {
          "count": {{ids.Length}},
          "value": [ {{string.Join(", ", ids.Select(id => $$"""{ "id": "{{id}}", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/{{id}}" }"""))}} ]
        }
        """;

    /// <summary>One work item, optionally naming a parent (Hierarchy-Reverse) and a child
    /// (Hierarchy-Forward). Both directions are present on the fixtures that test the walk, so following the
    /// wrong one produces a visibly wrong chain rather than an empty one.</summary>
    private static string Item(int id, string type, string title, int? parent = null, int? child = null)
    {
        var relations = new List<string>();
        if (parent is { } p)
        {
            relations.Add(
                $$"""{ "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/{{p}}" }""");
        }

        if (child is { } c)
        {
            relations.Add(
                $$"""{ "rel": "System.LinkTypes.Hierarchy-Forward", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/{{c}}" }""");
        }

        return $$"""
            {
              "id": {{id}},
              "fields": {
                "System.WorkItemType": "{{type}}",
                "System.Title": "{{title}}",
                "System.State": "Active"
              },
              "relations": [ {{string.Join(", ", relations)}} ]
            }
            """;
    }

    private static string Batch(params string[] items) =>
        $$"""{ "count": {{items.Length}}, "value": [ {{string.Join(", ", items)}} ] }""";

    /// <summary>
    /// The whole point of the feature: the reviewer is handed the Bug it was asked to fix AND the Epic that
    /// Bug serves, so "does this diff do what was asked" has something to be answered against. The chain is
    /// walked UPWARD — <c>Hierarchy-Reverse</c> — and the fixture carries a downward link too, so a walk that
    /// went the other way would report a sub-task instead of the Epic rather than merely finding nothing.
    /// </summary>
    [Fact]
    public async Task Linked_items_render_with_the_ancestry_chain_up_to_the_epic()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pullRequests/", PrLinks(1234))
            .OnJson(HttpMethod.Get, "ids=1234", Batch(Item(1234, "Bug", "Tag cache returns stale entries", parent: 1200, child: 1299)))
            .OnJson(HttpMethod.Get, "ids=1200", Batch(Item(1200, "User Story", "Tag lookups are correct", parent: 1100)))
            .OnJson(HttpMethod.Get, "ids=1100", Batch(Item(1100, "Feature", "Tag service reliability", parent: 1000)))
            .OnJson(HttpMethod.Get, "ids=1000", Batch(Item(1000, "Epic", "Retail platform health")));

        var context = await CreateReader(handler).ReadAsync(Repo, PrId, CancellationToken.None);

        context.Outcome.Should().Be(AdoWorkItemLookup.Linked);
        context.Items.Should().HaveCount(4);

        var text = Render(context);

        text.Should().NotBeNull();
        text.Should().Contain("Bug 1234").And.Contain("Tag cache returns stale entries");
        text.Should().Contain(
            "Epic 1000",
            "the top of the chain is what says why the change was wanted at all");
        text.Should().Contain("User Story 1200").And.Contain("Feature 1100");
        text.Should().NotContain(
            "1299",
            "1299 is the Bug's CHILD; walking Hierarchy-Forward instead of -Reverse would descend into "
                + "sub-tasks and never reach the Epic");
        text.Should().Contain(
            "ASKED to do",
            "the block has to tell the reviewer what to do with this, not just list identifiers");
    }

    /// <summary>
    /// A pull request that genuinely links nothing. This is a fact about the PR, established by a lookup that
    /// SUCCEEDED, and the block says so in words — see the next test for why saying it in words is the entire
    /// design.
    /// </summary>
    [Fact]
    public void A_pull_request_with_no_linked_items_says_so_explicitly()
    {
        var text = Render(AdoWorkItemContext.NoneLinked);

        text.Should().NotBeNull("an empty block would be indistinguishable from a failed lookup");
        text.Should().Contain(
            "links NO work items", "the reviewer is told the absence outright rather than left to infer it");
        text.Should().Contain(
            "The lookup succeeded",
            "the statement is only useful if the reviewer knows it rests on an answer rather than on silence");
    }

    /// <summary>
    /// THE distinction this feature turns on. "This pull request has no work items" and "we could not read
    /// this pull request's work items" are different facts — the first licenses reviewing against the
    /// description alone, the second means nobody knows what was asked — and a reviewer that cannot tell them
    /// apart will read the second as the first every time, because that is the reassuring one.
    /// <para>
    /// The assertion is on the DIFFERENCE, not merely on a marker being present. A marker can be added to
    /// both arms and still leave them identical where it counts; only comparing the two rendered briefs
    /// against each other pins that the reviewer is actually able to distinguish them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failed_lookup_is_marked_and_reads_differently_from_no_linked_items()
    {
        // First: a read that fails must reach the Failed arm, not the NoneLinked one. A reader that reported
        // "no work items" on a 403 would make every assertion below true and the feature still wrong.
        var denied = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pullRequests/", "{}", HttpStatusCode.Forbidden);

        var context = await CreateReader(denied).ReadAsync(Repo, PrId, CancellationToken.None);

        context.Outcome.Should().Be(
            AdoWorkItemLookup.Failed,
            "an unreadable answer is not the same as an answer of 'none'");

        var failed = Render(context);
        var noneLinked = Render(AdoWorkItemContext.NoneLinked);

        failed.Should().NotBeNull();
        failed.Should().Contain("lookup FAILED", "the failure is named, not implied by a gap");
        failed.Should().Contain(
            "NOT the same as the pull request having no work items",
            "the reviewer is told the distinction outright, because it cannot check it from the sandbox");

        failed.Should().NotBe(
            noneLinked,
            "a failed lookup and a PR with no work items must not render identically — that is exactly how "
                + "'nobody could read the intent' becomes 'there was no intent'");
    }

    /// <summary>
    /// The walk is bounded, and a bounded walk must say when the bound bit. A chain reported as ending at a
    /// Feature, when in truth the walk simply stopped there, tells the reviewer the Epic does not exist.
    /// </summary>
    [Fact]
    public async Task The_parent_walk_stops_at_the_depth_cap_and_says_the_chain_may_continue()
    {
        // Six-deep, which is two past the cap.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pullRequests/", PrLinks(1))
            .OnJson(HttpMethod.Get, "ids=1&", Batch(Item(1, "Task", "level 0", parent: 2)))
            .OnJson(HttpMethod.Get, "ids=2&", Batch(Item(2, "Task", "level 1", parent: 3)))
            .OnJson(HttpMethod.Get, "ids=3&", Batch(Item(3, "Task", "level 2", parent: 4)))
            .OnJson(HttpMethod.Get, "ids=4&", Batch(Item(4, "Task", "level 3", parent: 5)))
            .OnJson(HttpMethod.Get, "ids=5&", Batch(Item(5, "Task", "level 4", parent: 6)))
            .OnJson(HttpMethod.Get, "ids=6&", Batch(Item(6, "Epic", "level 5 — past the cap")));

        var context = await CreateReader(handler).ReadAsync(Repo, PrId, CancellationToken.None);

        context.Items.Should().HaveCount(
            AdoWorkItemContextReader.MaxAncestorDepth + 1,
            "the linked item plus one item per permitted hop");
        context.Items.Max(i => i.Depth).Should().Be(AdoWorkItemContextReader.MaxAncestorDepth);
        context.Items.Should().NotContain(i => i.Id == 6, "item 6 sits past the depth cap");
        handler.CountRequests("ids=6&").Should().Be(0, "the cap stops the REQUEST, not just the record");
        context.DepthCapReached.Should().BeTrue();

        Render(context).Should().Contain(
            "may continue past what is shown",
            "a chain cut by the cap must not read as a chain that ended");
    }

    /// <summary>
    /// A dropped tail must be announced, on exactly the rule
    /// <c>A_capped_failure_list_says_how_many_it_dropped</c> pins for CI failures: a capped list that says
    /// nothing about the cap reads as the complete set, and a reviewer that believes it has seen every linked
    /// item will happily conclude the rest of the work is out of scope.
    /// </summary>
    [Fact]
    public async Task A_capped_item_list_says_how_many_it_dropped()
    {
        const int Linked = AdoWorkItemContextReader.MaxWorkItems + 5;
        var admitted = Enumerable.Range(1, AdoWorkItemContextReader.MaxWorkItems)
            .Select(id => Item(id, "Task", $"task {id}"))
            .ToArray();

        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pullRequests/", PrLinks([.. Enumerable.Range(1, Linked)]))
            .OnJson(HttpMethod.Get, "_apis/wit/workitems", Batch(admitted));

        var context = await CreateReader(handler).ReadAsync(Repo, PrId, CancellationToken.None);

        context.Items.Should().HaveCount(AdoWorkItemContextReader.MaxWorkItems);
        context.OmittedItems.Should().Be(5);

        Render(context).Should().Contain("5 further work item(s) omitted");
    }

    /// <summary>
    /// ADO does not forbid a hierarchy that loops, and a walk that assumes a tree hangs on one. The guard is
    /// an explicit visited set rather than the depth cap alone, so the terminating condition is the cycle
    /// itself and not merely the walk running out of permitted hops.
    /// </summary>
    [Fact]
    public async Task A_relation_cycle_terminates_rather_than_walking_forever()
    {
        // 1's parent is 2; 2's parent is 1.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pullRequests/", PrLinks(1))
            .OnJson(HttpMethod.Get, "ids=1&", Batch(Item(1, "Bug", "first", parent: 2)))
            .OnJson(HttpMethod.Get, "ids=2&", Batch(Item(2, "User Story", "second", parent: 1)));

        var context = await CreateReader(handler).ReadAsync(Repo, PrId, CancellationToken.None);

        context.Outcome.Should().Be(AdoWorkItemLookup.Linked);
        context.Items.Should().HaveCount(2, "each id is fetched at most once, so the loop closes immediately");
        handler.CountRequests("ids=1&").Should().Be(1, "re-fetching a visited id is what a cycle turns into");

        // The renderer walks ParentId too, and 1 → 2 → 1 is still a cycle in the records it was handed. If it
        // did not carry its own guard, this call would not return.
        var text = Render(context);
        text.Should().NotBeNull();
        text.Should().Contain("Bug 1").And.Contain("User Story 2");
    }

    /// <summary>
    /// Nobody asked, so there is nothing to report. Distinct from a failed lookup on purpose: this is the
    /// GitHub daemon and the ADO repo with no project, where announcing a failure would describe an attempt
    /// that never happened — and spending the reviewer's attention to say "we did not look" is the same
    /// trade <c>An_unavailable_read_renders_no_block_at_all</c> makes for CI.
    /// </summary>
    [Fact]
    public void A_lookup_nobody_attempted_renders_no_block_at_all()
    {
        Render(AdoWorkItemContext.Unavailable).Should().BeNull();
    }
}
