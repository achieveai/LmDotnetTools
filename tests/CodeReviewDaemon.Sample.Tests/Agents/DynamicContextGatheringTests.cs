using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public sealed class DynamicContextGatheringTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(30);

    [Fact]
    public async Task GatherContextAsync_rejects_null_claim_and_gap_collections_as_malformed()
    {
        var loop = new FakeMultiTurnAgent(
            "gather-run-1",
            new TextMessage
            {
                Role = Role.Assistant,
                RunId = "gather-run-1",
                Text = """{"version":1,"engagementRoundId":42,"claims":null,"gaps":null}""",
            }
        );
        var sut = new ReviewAgent(loop, new LoggerFactory().CreateLogger<ReviewAgent>());

        var act = () => sut.GatherContextAsync(Bootstrap(42), Deadline, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*malformed manifest*");
    }

    [Fact]
    public async Task GatherContextAsync_dispatches_the_exact_gatherer_and_returns_only_semantic_output()
    {
        var semantic = SemanticManifest(42);
        var loop = new FakeMultiTurnAgent(
            "gather-run-1",
            new TextMessage
            {
                Role = Role.Assistant,
                RunId = "gather-run-1",
                Text = JsonSerializer.Serialize(semantic),
            }
        );
        var sut = new ReviewAgent(loop, new LoggerFactory().CreateLogger<ReviewAgent>());

        var result = await sut.GatherContextAsync(Bootstrap(42), Deadline, CancellationToken.None);

        var dispatched = loop.ReceivedInputs.Should().ContainSingle().Subject.Messages.Should().ContainSingle().Subject;
        dispatched.Should().BeOfType<TextMessage>().Which.Text.Should().Contain("code-reviewer:pr-context-gatherer");
        result.SemanticManifest.Should().BeEquivalentTo(semantic);
        result.ThreadId.Should().Be("fake-thread");
        result.RunId.Should().Be("gather-run-1");
    }

    [Fact]
    public void Host_evidence_derives_gatherer_identity_and_attaches_only_matching_immutable_reads()
    {
        using var fixture = new EvidenceFixture();
        var fileRead = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        var issueRead = fixture.StoreToolResult(
            "source-issue",
            "gatherer-thread",
            "mcp__github__get_issue",
            "issue:117"
        );
        _ = fixture.StoreToolResult("source-unrelated", "gatherer-thread", "Read", "docs/Other.md");
        var result = new DynamicContextGatheringResult(
            SemanticManifest(fixture.Round.Id),
            "gather-run-1",
            "parent-thread"
        );

        var manifest = fixture.Validator.Validate(result, Bootstrap(fixture.Round.Id), Roster());

        manifest.GathererAgentId.Should().Be("gatherer-1");
        manifest.GathererTemplate.Should().Be("code-reviewer:pr-context-gatherer");
        manifest.ScopedReadCount.Should().Be(3);
        manifest.ThreadId.Should().Be("parent-thread");
        manifest
            .Claims.Should()
            .ContainSingle()
            .Which.SourceRecordRefs.Should()
            .BeEquivalentTo([
                new AuditSourceReference(fileRead.Id, fileRead.ContentSha256),
                new AuditSourceReference(issueRead.Id, issueRead.ContentSha256),
            ]);
    }

    [Fact]
    public void Host_evidence_accepts_child_records_whose_run_id_differs_from_the_parent_run()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs", runId: "child-run-1");
        _ = fixture.StoreToolResult(
            "source-issue",
            "gatherer-thread",
            "mcp__github__get_issue",
            "issue:117",
            runId: "child-run-1"
        );

        var manifest = fixture.ValidateDefault();

        manifest.ScopedReadCount.Should().Be(2);
    }

    [Fact]
    public void Host_evidence_does_not_select_an_older_gatherer_only_because_it_has_more_records()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("old-file", "gatherer-old-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult("old-issue", "gatherer-old-thread", "mcp__github__get_issue", "issue:117");
        fixture.StoreTwoQualifiedReads();
        var roster = Roster() with
        {
            Nodes =
            [
                Roster().Nodes[0] with
                {
                    AgentId = "gatherer-old",
                    ThreadId = "gatherer-old-thread",
                    TerminalAtUtc = ObservedAt.AddMinutes(-1),
                },
                Roster().Nodes[0] with
                {
                    AgentId = "gatherer-current",
                    TerminalAtUtc = ObservedAt,
                },
            ],
        };

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
            Bootstrap(fixture.Round.Id),
            roster
        );

        manifest.GathererAgentId.Should().Be("gatherer-current");
    }

    [Fact]
    public void Host_evidence_uses_the_latest_completed_exact_gatherer_from_a_cumulative_retry_roster()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var roster = Roster() with
        {
            Nodes =
            [
                Roster().Nodes[0] with
                {
                    AgentId = "gatherer-old",
                    ThreadId = "gatherer-old-thread",
                    TerminalAtUtc = ObservedAt.AddMinutes(-1),
                },
                Roster().Nodes[0] with
                {
                    AgentId = "gatherer-current",
                    TerminalAtUtc = ObservedAt,
                },
            ],
        };

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
            Bootstrap(fixture.Round.Id),
            roster
        );

        manifest.GathererAgentId.Should().Be("gatherer-current");
    }

    [Fact]
    public void Host_evidence_uses_latest_audit_completion_when_a_compatible_host_omits_terminal_times()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult(
            "old-file",
            "gatherer-old-thread",
            "Read",
            "file:src/Foo.cs",
            capturedAt: ObservedAt.AddMinutes(-1)
        );
        _ = fixture.StoreToolResult(
            "old-issue",
            "gatherer-old-thread",
            "mcp__github__get_issue",
            "issue:117",
            capturedAt: ObservedAt.AddMinutes(-1)
        );
        fixture.StoreTwoQualifiedReads();
        var roster = Roster() with
        {
            Nodes =
            [
                Roster().Nodes[0] with
                {
                    AgentId = "z-gatherer-old",
                    ThreadId = "gatherer-old-thread",
                    TerminalAtUtc = null,
                },
                Roster().Nodes[0] with
                {
                    AgentId = "a-gatherer-current",
                    TerminalAtUtc = null,
                },
            ],
        };

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
            Bootstrap(fixture.Round.Id),
            roster
        );

        manifest.GathererAgentId.Should().Be("a-gatherer-current");
    }

    [Fact]
    public void Host_evidence_rejects_a_roster_without_the_exact_gatherer_template()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var roster = Roster() with
        {
            Nodes = [Roster().Nodes[0] with { Template = "code-reviewer:pr-context-gatherer-copy" }],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                roster
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*exact*pr-context-gatherer*");
    }

    [Fact]
    public void Host_evidence_rejects_fewer_than_two_successful_scoped_reads()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "src/Foo.cs");

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_arbitrary_successful_tool_results_as_reads()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "src/Foo.cs");
        _ = fixture.StoreToolResult("source-bash", "gatherer-thread", "Bash", "src/Foo.cs and issue:117");

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_a_read_whose_tool_call_targets_outside_the_bootstrap_scope()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-outside",
            "gatherer-thread",
            "Read",
            "issue:117",
            functionArgs: """{"file_path":"C:/secrets/token.txt"}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Theory]
    [InlineData("/workspace/target/../secret.txt")]
    [InlineData("/workspace/target/src/../../secret.txt")]
    [InlineData("/workspace/targetish/Foo.cs")]
    public void Host_evidence_does_not_count_a_read_that_escapes_the_workspace_after_path_resolution(string filePath)
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-traversal",
            "gatherer-thread",
            "Read",
            "issue:117",
            functionArgs: JsonSerializer.Serialize(new { file_path = filePath })
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_a_result_when_the_latest_matching_call_is_outside_scope()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-ambiguous",
            "gatherer-thread",
            "Read",
            "issue:117",
            functionArgs: """{"file_path":"/etc/shadow"}""",
            precedingFunctionArgs: """{"file_path":"/workspace/target/src/Foo.cs"}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Theory]
    [InlineData("{\"File_Path\":\"/etc/shadow\",\"file_path\":\"/workspace/target/src/Foo.cs\"}")]
    [InlineData("{\"file_path\":\"/etc/shadow\",\"file_path\":\"/workspace/target/src/Foo.cs\"}")]
    public void Host_evidence_rejects_case_colliding_or_duplicate_tool_arguments(string functionArgs)
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-key-collision",
            "gatherer-thread",
            "Read",
            "issue:117",
            functionArgs: functionArgs
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_use_a_github_issue_ref_to_authorize_an_ado_work_item_read()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-cross-provider",
            "gatherer-thread",
            "mcp__azure-devops__getWorkItemById",
            "issue:117",
            functionArgs: """{"id":117}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_allows_a_linked_github_issue_from_its_exact_cross_repository_ref()
    {
        using var fixture = new EvidenceFixture();
        var bootstrap = Bootstrap(fixture.Round.Id) with { LinkedWorkItemRefs = ["github-issue:acme/other-repo#117"] };
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-cross-repo-issue",
            "gatherer-thread",
            "mcp__github__get_issue",
            "issue:117",
            functionArgs: """{"owner":"acme","repo":"other-repo","issue_number":117}"""
        );

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
            bootstrap,
            Roster()
        );

        manifest.ScopedReadCount.Should().Be(2);
    }

    [Fact]
    public void Host_evidence_rejects_a_github_issue_repository_not_named_by_an_exact_linked_ref()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-wrong-issue-repo",
            "gatherer-thread",
            "mcp__github__get_issue",
            "issue:117",
            functionArgs: """{"owner":"acme","repo":"other-repo","issue_number":117}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Theory]
    [InlineData("{\"repository\":\"LmDotnetTools\",\"pullRequestId\":118}")]
    [InlineData("{\"repository\":\"LmDotnetTools\",\"project\":\"\",\"pullRequestId\":118}")]
    [InlineData("{\"repository\":\"LmDotnetTools\",\"project\":123,\"pullRequestId\":118}")]
    public void Host_evidence_requires_the_ado_project_scope(string functionArgs)
    {
        using var fixture = new EvidenceFixture();
        var bootstrap = Bootstrap(fixture.Round.Id) with
        {
            RepoRef = "azure-devops/achieveai/Platform/LmDotnetTools",
            LinkedWorkItemRefs = ["ado-work-item:117"],
        };
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-ado",
            "gatherer-thread",
            "mcp__azure-devops__getPullRequest",
            "issue:117",
            functionArgs: functionArgs
        );

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
                bootstrap,
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_two_results_from_one_tool_call_as_two_reads()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-file-replay",
            "gatherer-thread",
            "Read",
            "issue:117",
            toolCallId: "call-source-file",
            storeCall: false
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_requires_every_claim_citation_to_match_a_qualified_read()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Claims =
            [
                new DynamicContextClaimDraft(
                    "claim-1",
                    "Partly unsupported claim.",
                    ["file:src/Foo.cs", "commit:not-observed"]
                ),
            ],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*claim-1*citation*immutable*");
    }

    [Fact]
    public void Host_evidence_rejects_an_out_of_scope_issue_reference_minted_by_workspace_content()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs refers to issue:999");
        _ = fixture.StoreToolResult("source-issue", "gatherer-thread", "mcp__github__get_issue", "issue:117");
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Claims =
            [
                new DynamicContextClaimDraft("claim-1", "Issue 999 defines the requested behavior.", ["issue:999"]),
            ],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*issue:999*qualified source record*");
    }

    [Fact]
    public void Host_evidence_rejects_a_generic_source_fragment_as_a_material_reference()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "private readonly int _x;");
        _ = fixture.StoreToolResult("source-issue", "gatherer-thread", "mcp__github__get_issue", "issue:117");
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Claims =
            [
                new DynamicContextClaimDraft(
                    "claim-1",
                    "Authentication was deliberately disabled.",
                    ["private readonly int _x;"]
                ),
            ],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*citation*bounded*reference*");
    }

    [Theory]
    [InlineData("mcp__github__list_issues")]
    [InlineData("mcp__github__search_code")]
    [InlineData("mcp__github__search_issues")]
    public void Host_evidence_does_not_count_unbounded_github_list_or_search_results(string toolName)
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-search",
            "gatherer-thread",
            toolName,
            "foreign repository content",
            functionArgs: """{"owner":"achieveai","repo":"LmDotnetTools","query":"org:victim secret"}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_treats_posix_workspace_paths_as_case_sensitive()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-wrong-case",
            "gatherer-thread",
            "Read",
            "issue:117",
            functionArgs: """{"file_path":"/WORKSPACE/TARGET/src/Foo.cs"}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_accepts_windows_workspace_paths_case_insensitively()
    {
        using var fixture = new EvidenceFixture();
        var bootstrap = Bootstrap(fixture.Round.Id) with { WorkspacePath = "C:/Workspace/Target" };
        _ = fixture.StoreToolResult(
            "source-file",
            "gatherer-thread",
            "Read",
            "file:src/Foo.cs",
            functionArgs: """{"file_path":"c:/workspace/target/src/Foo.cs"}"""
        );
        _ = fixture.StoreToolResult("source-issue", "gatherer-thread", "mcp__github__get_issue", "issue:117");

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(SemanticManifest(fixture.Round.Id), "gather-run-1", "parent-thread"),
            bootstrap,
            Roster()
        );

        manifest.ScopedReadCount.Should().Be(2);
    }

    [Fact]
    public void Host_evidence_does_not_count_a_result_without_a_matching_prior_tool_call()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult("source-orphan", "gatherer-thread", "Read", "issue:117", storeCall: false);

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_a_provider_read_for_a_different_repository_or_pull_request()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-wrong-pr",
            "gatherer-thread",
            "mcp__github__get_pull_request",
            "issue:117",
            functionArgs: """{"owner":"achieveai","repo":"OtherRepo","pull_number":999}"""
        );

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_ignores_reads_from_a_different_agent_thread()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "src/Foo.cs");
        _ = fixture.StoreToolResult("source-issue", "other-agent-thread", "mcp__github__get_issue", "issue:117");

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_surfaces_a_corrupt_qualified_audit_record()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        fixture.CorruptAuditRecord("source-file");

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*failed content verification*");
    }

    [Fact]
    public void Host_evidence_rejects_a_material_claim_not_present_in_any_qualified_read()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Claims = [new DynamicContextClaimDraft("claim-1", "Unsupported claim.", ["commit:not-observed"])],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*citation*immutable*source record*");
    }

    [Fact]
    public void Host_evidence_rejects_a_degenerate_substring_citation()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Claims = [new DynamicContextClaimDraft("claim-1", "Fabricated claim.", ["e"])],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*bounded citation*");
    }

    [Fact]
    public void Host_evidence_does_not_count_unscoped_web_tools_as_scoped_reads()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult("source-web", "gatherer-thread", "WebFetch", "issue:117");

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Fact]
    public void Host_evidence_does_not_count_failed_or_deferred_reads()
    {
        using var fixture = new EvidenceFixture();
        _ = fixture.StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
        _ = fixture.StoreToolResult(
            "source-error",
            "gatherer-thread",
            "mcp__github__get_issue",
            "issue:117",
            isError: true
        );
        _ = fixture.StoreToolResult("source-deferred", "gatherer-thread", "Grep", "issue:117", isDeferred: true);

        var act = () => fixture.ValidateDefault();

        act.Should().Throw<InvalidOperationException>().WithMessage("*two*scoped read*");
    }

    [Theory]
    [InlineData("repository", "Failed")]
    [InlineData("head", "Unavailable")]
    [InlineData("workspace", "Truncated")]
    public void Host_evidence_requires_repository_head_and_workspace_scope(string failedScope, string failedState)
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Gaps =
            [
                .. RequiredScopes()
                    .Select(gap =>
                        string.Equals(gap.Scope, failedScope, StringComparison.Ordinal)
                            ? gap with
                            {
                                State = Enum.Parse<DynamicContextGapState>(failedState),
                                Detail = "scope was not established",
                            }
                            : gap
                    ),
            ],
        };

        var act = () =>
            fixture.Validator.Validate(
                new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
                Bootstrap(fixture.Round.Id),
                Roster()
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*required*scope*");
    }

    [Fact]
    public void Host_evidence_preserves_optional_typed_uncertainty()
    {
        using var fixture = new EvidenceFixture();
        fixture.StoreTwoQualifiedReads();
        var semantic = SemanticManifest(fixture.Round.Id) with
        {
            Gaps =
            [
                .. RequiredScopes(),
                new DynamicContextGap("knowledge-base", DynamicContextGapState.Unavailable, false, "not mounted"),
            ],
        };

        var manifest = fixture.Validator.Validate(
            new DynamicContextGatheringResult(semantic, "gather-run-1", "parent-thread"),
            Bootstrap(fixture.Round.Id),
            Roster()
        );

        manifest
            .Gaps.Should()
            .ContainSingle(gap =>
                gap.Scope == "knowledge-base" && gap.State == DynamicContextGapState.Unavailable && !gap.IsRequired
            );
    }

    private static DynamicContextBootstrap Bootstrap(long roundId) =>
        new(
            roundId,
            "github/achieveai/lmdotnettools",
            "118",
            "base-sha",
            "head-sha",
            "merge-base-sha",
            "/workspace/target",
            ["src/Foo.cs", "tests/FooTests.cs"],
            ["github-issue:achieveai/LmDotnetTools#117"],
            ["discussion:10"],
            ["question:3"],
            ["KnowledgeBase/repos/lmdotnettools.md"],
            12
        );

    private static DynamicContextManifestDraft SemanticManifest(long roundId) =>
        new(
            DynamicContextManifest.SchemaVersion,
            roundId,
            [
                new DynamicContextClaimDraft(
                    "claim-1",
                    "The change updates Foo for issue 117.",
                    ["file:src/Foo.cs", "issue:117"]
                ),
            ],
            RequiredScopes()
        );

    private static IReadOnlyList<DynamicContextGap> RequiredScopes() =>
        [
            new DynamicContextGap("repository", DynamicContextGapState.Linked, true, null),
            new DynamicContextGap("head", DynamicContextGapState.Linked, true, null),
            new DynamicContextGap("workspace", DynamicContextGapState.Linked, true, null),
        ];

    private static ReviewSubAgentTreeSnapshot Roster() =>
        new([
            new ReviewSubAgentNode
            {
                AgentId = "gatherer-1",
                ThreadId = "gatherer-thread",
                ParentThreadId = "parent-thread",
                Depth = 1,
                Status = ReviewSubAgentStatus.Completed,
                Name = "context-gatherer",
                Template = "code-reviewer:pr-context-gatherer",
            },
        ]);

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();

        public EvidenceFixture()
        {
            Store = new ReviewStore(_database.ConnectionString);
            Round = SeedRound(Store);
            Validator = new DynamicContextEvidenceValidator(Store);
        }

        public ReviewStore Store { get; }
        public EngagementRound Round { get; }
        public DynamicContextEvidenceValidator Validator { get; }

        public DynamicContextManifest ValidateDefault() =>
            Validator.Validate(
                new DynamicContextGatheringResult(SemanticManifest(Round.Id), "gather-run-1", "parent-thread"),
                Bootstrap(Round.Id),
                Roster()
            );

        public void StoreTwoQualifiedReads()
        {
            _ = StoreToolResult("source-file", "gatherer-thread", "Read", "file:src/Foo.cs");
            _ = StoreToolResult("source-issue", "gatherer-thread", "mcp__github__get_issue", "issue:117");
        }

        public void CorruptAuditRecord(string recordId)
        {
            using var connection = new SqliteConnection(_database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE audit_blob
                SET content = zeroblob(byte_count)
                WHERE sha256 = (
                    SELECT blob_sha256
                    FROM audit_source_chunk
                    WHERE source_record_id = $recordId
                );
                """;
            _ = command.Parameters.AddWithValue("$recordId", recordId);
            command.ExecuteNonQuery().Should().Be(1);
        }

        public AuditSourceRecord StoreToolResult(
            string recordId,
            string threadId,
            string toolName,
            string result,
            bool isError = false,
            bool isDeferred = false,
            string? functionArgs = null,
            bool storeCall = true,
            string? toolCallId = null,
            string? precedingFunctionArgs = null,
            string runId = "gather-run-1",
            DateTimeOffset? capturedAt = null
        )
        {
            toolCallId ??= $"call-{recordId}";
            if (storeCall)
            {
                if (precedingFunctionArgs is not null)
                {
                    StoreToolCall(
                        $"{recordId}-preceding-call",
                        threadId,
                        toolName,
                        toolCallId,
                        precedingFunctionArgs,
                        runId,
                        capturedAt
                    );
                }

                StoreToolCall(
                    $"{recordId}-call",
                    threadId,
                    toolName,
                    toolCallId,
                    functionArgs ?? DefaultFunctionArgs(toolName),
                    runId,
                    capturedAt
                );
            }

            var content = AuditMessageSerializer.SerializeMessage(
                new ToolCallResultMessage
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    Result = result,
                    IsError = isError,
                    IsDeferred = isDeferred,
                    Role = Role.User,
                }
            );
            return Store.StoreAuditRecord(
                new ModelTurnAuditRecord(
                    recordId,
                    new MultiTurnAuditScope("1", Round.Id.ToString()),
                    threadId,
                    runId,
                    "gather-generation-1",
                    null,
                    Store.ListAuditRecordsForRound(Round.Id).Count + 1,
                    MultiTurnAuditRecordTypes.ToolResult,
                    "user",
                    "claude-opus-5",
                    "anthropic",
                    content,
                    AuditMessageSerializer.ComputeSha256(content),
                    content.Length,
                    AuditCaptureOutcome.Complete,
                    null,
                    capturedAt ?? ObservedAt
                )
            );
        }

        private void StoreToolCall(
            string recordId,
            string threadId,
            string toolName,
            string toolCallId,
            string functionArgs,
            string runId,
            DateTimeOffset? capturedAt
        )
        {
            var callContent = AuditMessageSerializer.SerializeMessage(
                new ToolCallMessage
                {
                    ToolCallId = toolCallId,
                    FunctionName = toolName,
                    FunctionArgs = functionArgs,
                    Role = Role.Assistant,
                }
            );
            _ = Store.StoreAuditRecord(
                new ModelTurnAuditRecord(
                    recordId,
                    new MultiTurnAuditScope("1", Round.Id.ToString()),
                    threadId,
                    runId,
                    "gather-generation-1",
                    null,
                    Store.ListAuditRecordsForRound(Round.Id).Count + 1,
                    MultiTurnAuditRecordTypes.ModelResponse,
                    "assistant",
                    "claude-opus-5",
                    "anthropic",
                    callContent,
                    AuditMessageSerializer.ComputeSha256(callContent),
                    callContent.Length,
                    AuditCaptureOutcome.Complete,
                    null,
                    capturedAt ?? ObservedAt
                )
            );
        }

        private static string DefaultFunctionArgs(string toolName) =>
            toolName switch
            {
                "Read" => """{"file_path":"/workspace/target/src/Foo.cs"}""",
                "Grep" => """{"path":"/workspace/target","pattern":"Foo"}""",
                "Glob" => """{"path":"/workspace/target","pattern":"**/*.cs"}""",
                "mcp__github__get_issue" => """{"owner":"achieveai","repo":"LmDotnetTools","issue_number":117}""",
                _ => "{}",
            };

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }
    }

    private static EngagementRound SeedRound(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", ObservedAt, "head:118");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-sha",
                "base-sha",
                null,
                watermark,
                watermark,
                null,
                null,
                null,
                null,
                null,
                ObservedAt
            )
        );
        return store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.CodeReview,
                EngagementRoundStatus.Pending,
                "head-sha",
                "base-sha",
                watermark,
                watermark,
                0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        )!;
    }
}
