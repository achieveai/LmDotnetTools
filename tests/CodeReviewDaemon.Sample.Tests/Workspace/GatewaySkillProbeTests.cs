using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// The gateway pre-flight behind <c>RequireSkillSupport</c> on the S2S path: Revobot must not review without
/// the skill it reviews WITH. The probe reads the session-free marketplace catalog
/// (<c>GET api/v1/marketplaces/preview</c>) and answers one question — does an allowed marketplace supply
/// <c>code-reviewer:pr-review</c> AND at least one <c>code-reviewer:*</c> sub-agent?
///
/// Every case here is driven through the real <see cref="SandboxClient"/> over a scripted transport, so what
/// is under test is the probe's reading of the ACTUAL wire contract, not a hand-rolled parse of a shape the
/// gateway may not send.
/// </summary>
public sealed class GatewaySkillProbeTests
{
    private const string PreviewUrl = "api/v1/marketplaces/preview";

    [Fact]
    public async Task Probe_SkillAndAgentsPresent_IsSupported()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: null, ReviewerPlugin(skills: ["pr-review"], agents: 16)))
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeTrue();
        support.HasReviewSkill.Should().BeTrue();
        support.ReviewerAgentCount.Should().Be(16);
        support.MarketplaceErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_ReviewSkillMissing_IsNotSupported()
    {
        // The plugin is installed and its sub-agents are there, but the one skill the prompt makes mandatory
        // ("that skill IS how you review") is absent — which is exactly the shallow-review failure mode the
        // flag exists to stop, and precisely the case a plugin-count check would wave through.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(
                Marketplace(
                    "gb-plugins",
                    error: null,
                    ReviewerPlugin(skills: ["code-review", "post-pr-review"], agents: 16)
                )
            )
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeFalse();
        support.HasReviewSkill.Should().BeFalse();
        support.ReviewerAgentCount.Should().Be(16);
        support.Describe().Should().Contain("MISSING").And.Contain("code-reviewer:pr-review");
    }

    [Fact]
    public async Task Probe_NoReviewerSubAgents_IsNotSupported()
    {
        // The mirror case: the skill is present but there is nothing to dispatch the deep passes to, so the
        // review collapses to a single agent reading a diff.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: null, ReviewerPlugin(skills: ["pr-review"], agents: 0)))
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeFalse();
        support.HasReviewSkill.Should().BeTrue();
        support.ReviewerAgentCount.Should().Be(0);
        support.Describe().Should().Contain("sub-agents=0");
    }

    [Fact]
    public async Task Probe_OtherPluginsOnly_DoesNotCountTowardsTheRequirement()
    {
        // A catalog full of unrelated plugins is still an unsupported catalog: the counts must be scoped to
        // the code-reviewer plugin, never to "the gateway returned some skills and some agents".
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(
                Marketplace(
                    "gb-plugins",
                    error: null,
                    Plugin("debugging", skills: ["pr-review"], agents: 2),
                    Plugin("orleans-dev", skills: ["pr-review"], agents: 1)
                )
            )
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeFalse();
        support.HasReviewSkill.Should().BeFalse("a same-named skill in another plugin is not code-reviewer:pr-review");
        support.ReviewerAgentCount.Should().Be(0);
    }

    [Fact]
    public async Task Probe_MarketplaceFailedToLoad_SurfacesTheGatewayReason()
    {
        // A marketplace that failed to load contributes no plugins, so the two checks come back empty for a
        // reason that has nothing to do with the plugin being uninstalled. Carrying the gateway's own words
        // through is what turns "nothing found" into something an operator can act on.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: "clone failed: authentication required"))
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeFalse();
        support
            .MarketplaceErrors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("gb-plugins")
            .And.Contain("authentication required");
        support.Describe().Should().Contain("authentication required");
    }

    [Fact]
    public async Task Probe_SupportSpreadAcrossMarketplaces_AggregatesRatherThanRequiringOneToHaveBoth()
    {
        // Two marketplaces each contribute half of the requirement. The daemon reviews with the union of the
        // configured set, so the verdict must be taken over the union too.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(
                Marketplace("gb-plugins", error: null, ReviewerPlugin(skills: ["pr-review"], agents: 0)),
                Marketplace("superpowers", error: null, ReviewerPlugin(skills: [], agents: 3))
            )
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins", "superpowers"], CancellationToken.None);

        support.IsSupported.Should().BeTrue();
        support.ReviewerAgentCount.Should().Be(3);
    }

    [Fact]
    public async Task Probe_MatchesPluginAndSkillNamesCaseInsensitively()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: null, Plugin("Code-Reviewer", ["PR-Review"], agents: 4)))
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        support.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_AsksAboutTheConfiguredMarketplaces_NotTheGatewayDefaultSet()
    {
        // The daemon's review is built from SubAgentMarketplaces, so a verdict taken over the gateway's own
        // default set would be answering a different question than the one that governs the run.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: null, ReviewerPlugin(skills: ["pr-review"], agents: 1)))
        );
        await using var probe = BuildProbe(handler);

        _ = await probe.ProbeAsync(["gb-plugins", "superpowers"], CancellationToken.None);

        var uri = handler.Requests.Should().ContainSingle().Subject.Uri.ToString();
        uri.Should().Contain("marketplaces=");
        Uri.UnescapeDataString(uri).Should().Contain("gb-plugins,superpowers");
    }

    [Fact]
    public async Task Probe_EmptyMarketplaceList_LetsTheGatewayApplyItsDefaults()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            Catalog(Marketplace("gb-plugins", error: null, ReviewerPlugin(skills: ["pr-review"], agents: 1)))
        );
        await using var probe = BuildProbe(handler);

        var support = await probe.ProbeAsync([], CancellationToken.None);

        support.IsSupported.Should().BeTrue();
        handler
            .Requests.Should()
            .ContainSingle()
            .Which.Uri.ToString()
            .Should()
            .NotContain("marketplaces=", "an empty configured set means 'whatever the gateway defaults to'");
    }

    [Fact]
    public async Task Probe_GatewayReturnsAnError_Throws_SoItIsNeverMistakenForAbsentSkills()
    {
        // This is the distinction the whole fail-fast rests on: an unreadable catalog is a DIFFERENT finding
        // from an unsupported one. If the probe answered "unsupported" here, a momentary gateway blip would
        // stop the daemon and demand a marketplace fix that was never wrong.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            PreviewUrl,
            "{\"error\":\"boom\"}",
            HttpStatusCode.InternalServerError
        );
        await using var probe = BuildProbe(handler);

        var act = () => probe.ProbeAsync(["gb-plugins"], CancellationToken.None);

        await act.Should().ThrowAsync<SandboxException>();
    }

    private static GatewaySkillProbe BuildProbe(HttpMessageHandler transport) =>
        new(
            "http://127.0.0.1:3000",
            // SandboxClientOptions rejects an app key that is not standard base64 of at least 32 bytes, so
            // the fixture credential has to be well-formed even though the scripted transport never checks it.
            new SandboxCredential("code-review-daemon", Convert.ToBase64String(new byte[32])),
            NullLogger<GatewaySkillProbe>.Instance,
            transport
        );

    // --- catalog JSON builders (the gateway's api/v1/marketplaces/preview wire shape) ---

    private static string Catalog(params string[] marketplaces) =>
        $$"""{"selected":["gb-plugins"],"marketplaces":[{{string.Join(",", marketplaces)}}]}""";

    private static string Marketplace(string alias, string? error, params string[] plugins) =>
        $$"""
            {"alias":"{{alias}}","error":{{(error is null ? "null" : $"\"{error}\"")}},"plugins":[{{string.Join(
                ",",
                plugins
            )}}]}
            """;

    private static string ReviewerPlugin(IReadOnlyList<string> skills, int agents) =>
        Plugin("code-reviewer", skills, agents);

    private static string Plugin(string name, IReadOnlyList<string> skills, int agents) =>
        $$"""
            {"name":"{{name}}","version":"1.0.0","description":"{{name}} plugin",
             "skills":[{{string.Join(",", skills.Select(s => Item(s, name)))}}],
             "agents":[{{string.Join(",", Enumerable.Range(0, agents).Select(i => Item($"agent-{i}", name)))}}]}
            """;

    private static string Item(string name, string plugin) =>
        $$"""
            {"name":"{{name}}","description":"d","plugin":"{{plugin}}","marketplace":"gb-plugins","path":"/x/{{name}}.md"}
            """;
}
