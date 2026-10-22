using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Skill tiers (gateway ADR 0041, <c>Docs/tools-and-api.md</c> "GET/PATCH …/skills"): the create block and its
/// resolved echo, the per-agent catalog read and update, and <c>included_skills</c> on the agent list.
/// </summary>
public sealed class SandboxClientSkillTiersTests
{
    // The documented example catalog, trimmed to one marketplace skill and one workspace skill.
    private const string CatalogJson = """
        {"session_id":"s-1","agent_id":"main","default_tier":"available",
         "counts":{"included":5,"available":310,"unavailable":1,"loaded":2},
         "marketplaces":[{"alias":"official","plugins":[{"name":"document-skills","version":"1.2.0",
           "description":"Create and edit office documents","skills":[{"name":"document-skills:pdf",
           "path":"/marketplaces/official/document-skills/skills/pdf","description":"Work with PDF files",
           "tier":"available","tier_origin":"default","loaded":true,"loaded_at":"2026-10-11T10:02:11.123456Z"}]}]}],
         "workspace":{"skills":[{"name":"greet","path":"/workspace/.claude/skills/greet","description":"Say hi",
           "tier":"included","tier_origin":"override","loaded":false,"loaded_at":null,"notified":true}]}}
        """;

    [Fact]
    public async Task CreateAsync_SkillsPolicy_IsSentAndTheResolvedPolicyIsSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"s-1","properties":{},
             "skills":{"default_tier":"available","included":["official/document-skills/pdf"],"unlisted_hint":""}}
            """,
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                "ws",
                skills: new SandboxSkillsPolicy(SandboxSkillTier.Available, ["document-skills:pdf"], unlistedHint: "")
            )
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var skills = sent.RootElement.GetProperty("skills");
        skills.GetProperty("default_tier").GetString().Should().Be("available");
        skills
            .GetProperty("included")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("document-skills:pdf");
        skills.GetProperty("unlisted_hint").GetString().Should().Be("", "an empty hint removes the trailer");
        info.SkillsPolicy.Should().NotBeNull();
        info.SkillsPolicy!.DefaultTier.Should().Be(SandboxSkillTier.Available);
        info.SkillsPolicy.Included.Should().Equal("official/document-skills/pdf");
        info.SkillsPolicy.UnlistedHint.Should().Be("");
    }

    [Fact]
    public void SandboxSkillsPolicy_UnavailableDefaultTier_IsRejectedBeforeAnyCall()
    {
        var act = () => new SandboxSkillsPolicy(SandboxSkillTier.Unavailable);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetSkillsAsync_SendsTheFilters_AndMapsTheCatalog()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson);

        var catalog = await client.GetSkillsAsync(
            "s-1",
            agentId: "main",
            tier: SandboxSkillTier.Available,
            loaded: true
        );

        handler.Requests.Single().Uri.Query.Should().Be("?agent_id=main&tier=available&loaded=true");
        catalog.AgentId.Should().Be("main");
        catalog.DefaultTier.Should().Be(SandboxSkillTier.Available);
        catalog.Counts.Should().Be(new SandboxSkillCounts(5, 310, 1, 2));
        catalog.ToolsListChanged.Should().BeFalse();
        var plugin = catalog.Marketplaces.Should().ContainSingle().Which.Plugins.Should().ContainSingle().Subject;
        plugin.Version.Should().Be("1.2.0");
        var pdf = plugin.Skills.Should().ContainSingle().Subject;
        pdf.Name.Should().Be("document-skills:pdf");
        pdf.TierOrigin.Should().Be(SandboxSkillTierOrigin.Default);
        pdf.Loaded.Should().BeTrue();
        pdf.LoadedAt.Should().NotBeNull();
        pdf.Notified.Should().BeNull("marketplace rows carry no notified flag");
        var greet = catalog.WorkspaceSkills.Should().ContainSingle().Subject;
        greet.Tier.Should().Be(SandboxSkillTier.Included);
        greet.TierOrigin.Should().Be(SandboxSkillTierOrigin.Override);
        greet.Notified.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSkillTiersAsync_SendsSetAndReset_AndReportsThatToolsMustBeReListed()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/skills",
            CatalogJson.Replace("\"session_id\":\"s-1\",", "\"session_id\":\"s-1\",\"tools_list_changed\":true,")
        );

        var catalog = await client.UpdateSkillTiersAsync(
            "s-1",
            new Dictionary<string, SandboxSkillTier>
            {
                ["document-skills:pdf"] = SandboxSkillTier.Included,
                ["deploy-prod"] = SandboxSkillTier.Unavailable,
            },
            reset: true,
            agentId: "reviewer-1"
        );

        var request = handler.Requests.Single();
        request.Uri.Query.Should().Be("?agent_id=reviewer-1");
        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("set").GetProperty("deploy-prod").GetString().Should().Be("unavailable");
        body.GetProperty("reset").GetBoolean().Should().BeTrue();
        catalog.ToolsListChanged.Should().BeTrue();
    }

    [Fact]
    public async Task GetSkillsAsync_DefaultAgent_SendsNoAgentId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson.Replace("\"main\"", "\"\""));

        var catalog = await client.GetSkillsAsync("s-1");

        handler.Requests.Single().Uri.Query.Should().BeEmpty();
        catalog.AgentId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"tier\":\"included\"", "\"tier\":\"sometimes\"")]
    [InlineData("\"tier_origin\":\"override\"", "\"tier_origin\":\"guess\"")]
    [InlineData("\"counts\":{\"included\":5,", "\"counts\":{")]
    public async Task GetSkillsAsync_MalformedCatalog_IsProtocol(string from, string to)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson.Replace(from, to));

        var act = () => client.GetSkillsAsync("s-1");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task UpdateSkillTiersAsync_UnknownRef_IsProtocolNamingTheCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/skills",
            """{"error":"no skill matches 'nope'","code":400,"error_code":"skill_not_found","ref":"nope"}""",
            HttpStatusCode.BadRequest
        );

        var act = () =>
            client.UpdateSkillTiersAsync(
                "s-1",
                new Dictionary<string, SandboxSkillTier> { ["nope"] = SandboxSkillTier.Included }
            );

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.ErrorCode.Should().Be("skill_not_found");
    }

    [Theory]
    [InlineData(",\"included_skills\":12", 12)]
    [InlineData("", null)] // a gateway before skill tiers
    public async Task ListAgentsAsync_ReportsIncludedSkills_WhenTheGatewaySendsThem(string field, int? expected)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1/agents",
            "{\"session_id\":\"s-1\",\"agents\":[{\"agent_id\":\"\",\"created_at\":\"2026-10-11T10:00:00Z\","
                + "\"last_used_at\":\"2026-10-11T10:05:00Z\",\"open_tasks\":0,\"loaded_skills\":1"
                + field
                + "}]}"
        );

        var agents = await client.ListAgentsAsync("s-1");

        agents.Should().ContainSingle().Which.IncludedSkills.Should().Be(expected);
    }
}
