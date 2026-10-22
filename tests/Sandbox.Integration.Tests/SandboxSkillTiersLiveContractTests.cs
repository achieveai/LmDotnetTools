using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for per-agent skill tiers (gateway ADR 0041, v0.1.17): the create <c>skills</c> block and its
/// echo, <c>GET …/skills</c> for the default and a named agent, and <c>PATCH …/skills</c> on one agent only.
/// The skills come from whatever marketplace the gateway serves, picked through the preview, so the test does not
/// depend on one deployment's plugins. Every sandbox it creates is deleted: the gateway may be shared.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxSkillTiersLiveContractTests
{
    private const string ChildAgent = "probe-child";

    private readonly LiveGatewayFixture _fixture;

    public SandboxSkillTiersLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    private SandboxClient Client
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Client;
        }
    }

    [SkippableFact]
    public async Task SkillTiers_CreatePolicy_PerAgentCatalog_AndPatchOnlyTouchesThatAgent()
    {
        var client = Client;
        var preview = await client.PreviewMarketplacesAsync();
        var (alias, plugin) = preview
            .Marketplaces.SelectMany(m => m.Plugins.Select(p => (m.Alias, Plugin: p)))
            .FirstOrDefault(x => x.Plugin.Skills.Count >= 2);
        if (plugin is null)
        {
            const string reason = "The gateway serves no marketplace plugin with two skills to tier.";
            Skip.IfNot(_fixture.ContractRequired, reason);
            throw new InvalidOperationException(
                reason + " SANDBOX_CONTRACT_REQUIRED forbids skipping: mount one (PLUGINS_DIRS)."
            );
        }
        var included = $"{plugin!.Name}:{plugin.Skills[0].Name}";
        var other = $"{plugin.Name}:{plugin.Skills[1].Name}";

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                workspace: "skilltiers-" + Guid.NewGuid().ToString("N"),
                marketplaces: [alias],
                skills: new SandboxSkillsPolicy(SandboxSkillTier.Available, [included])
            )
        );
        try
        {
            var sessionId = info.SessionId;
            info.Skills.Should().NotBeNull("a v0.1.17 gateway echoes the resolved skills policy on create");
            info.Skills!.DefaultTier.Should().Be(SandboxSkillTier.Available);

            var root = await client.GetSkillCatalogAsync(sessionId, agentId: "");
            root.AgentId.Should().BeEmpty();
            root.SessionId.Should().Be(sessionId);
            root.DefaultTier.Should().Be(SandboxSkillTier.Available);
            root.ToolsListChanged.Should().BeFalse();
            var rootIncluded = Row(root, included);
            rootIncluded.Tier.Should().Be(SandboxSkillTier.Included);
            rootIncluded.TierOrigin.Should().Be(SandboxSkillTierOrigin.IncludedList);
            Row(root, other).Tier.Should().Be(SandboxSkillTier.Available);
            Row(root, other).TierOrigin.Should().Be(SandboxSkillTierOrigin.Default);
            // The echo reports resolved keys (the skill directory), not the name the request sent.
            info.Skills.Included.Should().Equal(rootIncluded.Path);
            var pluginNode = root
                .Marketplaces.Single(m => m.Alias == alias)
                .Plugins.Single(p => p.Skills.Any(s => s.Name == included));
            pluginNode.Name.Should().Be(plugin.Name);
            pluginNode.Description.Should().Be(plugin.Description);

            var child = await client.GetSkillCatalogAsync(sessionId, ChildAgent);
            child.AgentId.Should().Be(ChildAgent);
            Row(child, included).Tier.Should().Be(SandboxSkillTier.Included);
            Row(child, other).Tier.Should().Be(SandboxSkillTier.Available);

            var patched = await client.PatchSkillTiersAsync(
                sessionId,
                ChildAgent,
                new Dictionary<string, SandboxSkillTier> { [other] = SandboxSkillTier.Included }
            );
            patched.ToolsListChanged.Should().BeTrue();
            patched.AgentId.Should().Be(ChildAgent);
            Row(patched, other).Tier.Should().Be(SandboxSkillTier.Included);
            Row(patched, other).TierOrigin.Should().Be(SandboxSkillTierOrigin.Override);
            patched.Counts.Included.Should().Be(child.Counts.Included + 1);

            var childIncluded = await client.GetSkillCatalogAsync(sessionId, ChildAgent, SandboxSkillTier.Included);
            Skills(childIncluded).Select(s => s.Name).Should().Contain([included, other]);
            Skills(childIncluded).Should().OnlyContain(s => s.Tier == SandboxSkillTier.Included);

            var rootAfter = await client.GetSkillCatalogAsync(sessionId, agentId: null);
            Row(rootAfter, other).Tier.Should().Be(SandboxSkillTier.Available, "a PATCH changes only its own agent");
            Row(rootAfter, other).TierOrigin.Should().Be(SandboxSkillTierOrigin.Default);
            rootAfter.Counts.Should().Be(root.Counts);

            var rejected = await Assert.ThrowsAsync<SandboxException>(() =>
                client.PatchSkillTiersAsync(
                    sessionId,
                    ChildAgent,
                    new Dictionary<string, SandboxSkillTier>
                    {
                        ["no-such-plugin:no-such-skill"] = SandboxSkillTier.Included,
                    }
                )
            );
            rejected.Kind.Should().Be(SandboxErrorKind.SkillRejected);
            rejected.ErrorCode.Should().Be("skill_not_found");
            rejected.SkillRef.Should().Be("no-such-plugin:no-such-skill");
            rejected.StatusCode.Should().Be(400);
        }
        finally
        {
            await client.DeleteAsync(info.SessionId);
        }
    }

    private static IEnumerable<SandboxSkill> Skills(SandboxSkillCatalog catalog) =>
        catalog.Marketplaces.SelectMany(m => m.Plugins).SelectMany(p => p.Skills).Concat(catalog.WorkspaceSkills);

    private static SandboxSkill Row(SandboxSkillCatalog catalog, string name) =>
        Skills(catalog).Single(s => s.Name == name);
}
