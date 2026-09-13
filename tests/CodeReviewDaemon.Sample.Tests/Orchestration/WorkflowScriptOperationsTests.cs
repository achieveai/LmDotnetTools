using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class WorkflowScriptOperationsTests
{
    [Fact]
    public void Child_operation_inherits_resolved_destination_and_identity_with_canonical_paths()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeReviewDaemon:CrossRepoStoreUrl"] = "https://github.com/selected/store",
                    ["CodeReviewDaemon:DatabasePath"] = "relative.db",
                    ["SandboxGateway:AppId"] = "selected-daemon",
                    ["Auth:Github:ClientSecret"] = "test-only-secret",
                    ["WorkflowPublication:SharedSecret"] = "test-only-callback",
                    ["CodeReviewDaemon:_comment"] = "Operator documentation",
                    ["Unrelated:Value"] = "not-needed",
                }
            )
            .Build();
        var environment = WorkflowScriptHost.BuildEnvironment(
            configuration,
            new Dictionary<string, string> { ["CodeReviewDaemon__DatabasePath"] = "/canonical/review.db" }
        );
        environment["CodeReviewDaemon__CrossRepoStoreUrl"].Should().Be("https://github.com/selected/store");
        environment["CodeReviewDaemon__DatabasePath"].Should().Be("/canonical/review.db");
        environment["SandboxGateway__AppId"].Should().Be("selected-daemon");
        environment["Auth__Github__ClientSecret"].Should().Be("test-only-secret");
        environment["WorkflowPublication__SharedSecret"].Should().Be("test-only-callback");
        environment.Should().NotContainKey("Unrelated__Value").And.NotContainKey("CodeReviewDaemon___comment");
    }

    [Theory]
    [InlineData("{\"Context\":{},\"Input\":{}}")]
    [InlineData(
        "{\"Context\":{\"RunId\":\"1\",\"StepId\":\"x\",\"Attempt\":1,\"RunDirectory\":\"/tmp\"},\"Input\":{\"x\":1,\"x\":2}}"
    )]
    [InlineData(
        "{\"Context\":{\"RunId\":\"1\",\"StepId\":\"x\",\"Attempt\":1,\"RunDirectory\":\"/tmp\"},\"Input\":{},\"Extra\":1}"
    )]
    public async Task Invalid_envelope_never_dispatches(string json)
    {
        var output = new StringWriter();
        var diagnostics = new StringWriter();
        var called = false;
        var code = await WorkflowScriptHost.RunAsync(
            "retain-artifacts",
            new StringReader(json),
            output,
            diagnostics,
            (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult<JsonNode>(new JsonObject());
            },
            default
        );
        code.Should().Be(2);
        called.Should().BeFalse();
        output.ToString().Should().BeEmpty();
        diagnostics.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Valid_envelope_preserves_types_and_emits_one_json_result()
    {
        var output = new StringWriter();
        var diagnostics = new StringWriter();
        var code = await WorkflowScriptHost.RunAsync(
            "collect-statistics",
            new StringReader(
                """{"Context":{"RunId":"1","StepId":"stats","Attempt":1,"RunDirectory":"/tmp"},"Input":{"Decision":true}}"""
            ),
            output,
            diagnostics,
            (operation, context, input, _) =>
            {
                operation.Should().Be("collect-statistics");
                context["RunId"]!.GetValue<string>().Should().Be("1");
                input["Decision"]!.GetValue<bool>().Should().BeTrue();
                return Task.FromResult<JsonNode>(new JsonObject { ["Count"] = 2 });
            },
            default
        );
        code.Should().Be(0);
        JsonNode.Parse(output.ToString())!["Count"]!.GetValue<int>().Should().Be(2);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Closure_requires_durable_retention_and_never_touches_git_before_it()
    {
        using var fixture = new Fixture();
        await fixture
            .Operations.Invoking(x => x.CloseArtifactBranchAsync(default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_push_cannot_authorize_closure()
    {
        using var fixture = new Fixture();
        fixture.Runner.OnArgvContains("push", new SandboxCommandResult(1, "", "rejected"));
        await fixture
            .Operations.Invoking(x => x.RetainArtifactsAsync(fixture.Files, default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        fixture
            .Store.GetOutboxForRun(fixture.Run.Id)
            .Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(OutboxStatus.Pending);
        var commandCount = fixture.Runner.Commands.Count;
        await fixture
            .Operations.Invoking(x => x.CloseArtifactBranchAsync(default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        fixture.Runner.Commands.Should().HaveCount(commandCount);
    }

    [Fact]
    public async Task Retention_receipt_survives_recreation_and_closure_uses_only_own_branch()
    {
        using var fixture = new Fixture();
        var retained = await fixture.Operations.RetainArtifactsAsync(fixture.Files, default);
        retained["RetainedSha"]!.GetValue<string>().Should().Be(Fixture.Sha);
        fixture
            .Store.GetOutboxForRun(fixture.Run.Id)
            .Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(OutboxStatus.Posted);
        var commands = fixture.Runner.Commands.Count;
        await fixture.CreateOperations().RetainArtifactsAsync(fixture.Files, default);
        fixture.Runner.Commands.Should().HaveCount(commands, "an acknowledged retention is not blindly replayed");
        var closed = await fixture.CreateOperations().CloseArtifactBranchAsync(default);
        closed["ArtifactBranch"]!.GetValue<string>().Should().Be("review/widgets-7");
        fixture
            .Runner.Commands.Should()
            .Contain(c => c.Argv.Contains("--delete") && c.Argv.Contains("review/widgets-7"));
        fixture.Runner.Commands.Should().NotContain(c => c.Argv.Contains("--delete") && c.Argv.Contains("main"));
    }

    [Fact]
    public async Task Closure_does_not_claim_success_when_remote_branch_deletion_failed()
    {
        using var fixture = new Fixture();
        await fixture.Operations.RetainArtifactsAsync(fixture.Files, default);
        fixture.Runner.OnArgvContainsFirst(
            "ls-remote",
            new SandboxCommandResult(0, $"{Fixture.Sha}\trefs/heads/review/widgets-7", "")
        );
        fixture.Runner.OnArgvContains("--delete", new SandboxCommandResult(1, "", "deletion refused"));
        await fixture
            .Operations.Invoking(x => x.CloseArtifactBranchAsync(default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Closure_requires_retained_commit_in_remote_default_even_if_branch_is_absent()
    {
        using var fixture = new Fixture();
        await fixture.Operations.RetainArtifactsAsync(fixture.Files, default);
        fixture.Runner.OnArgvContains("merge-base", new SandboxCommandResult(1, "", ""));
        fixture.Runner.OnArgvContains("diff --quiet", new SandboxCommandResult(1, "", ""));
        await fixture
            .Operations.Invoking(x => x.CloseArtifactBranchAsync(default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("../elsewhere.json")]
    [InlineData("PRs/widgets-7/../other/notes.json")]
    [InlineData("PRs/other-7/notes.json")]
    [InlineData("/etc/passwd")]
    public async Task Retention_rejects_artifacts_outside_own_pr(string path)
    {
        using var fixture = new Fixture();
        await fixture
            .Operations.Invoking(x => x.RetainArtifactsAsync([new(path, "{}")], default))
            .Should()
            .ThrowAsync<ArgumentException>();
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public void Statistics_count_records_without_interpreting_payload_prose()
    {
        using var fixture = new Fixture();
        foreach (var payload in new[] { "{\"Text\":\"critical critical\"}", "{\"Text\":\"looks fine\"}" })
        {
            fixture.Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = fixture.Run.Id,
                    ArtifactSchemaVersion = 1,
                    ArtifactKind = "review",
                    Provider = "github",
                    Payload = payload,
                }
            );
        }
        var stats = fixture.Operations.CollectStatistics();
        stats["ArtifactCount"]!.GetValue<int>().Should().Be(2);
        stats["ArtifactCountsByKind"]!["review"]!.GetValue<int>().Should().Be(2);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Same_head_windows_have_independent_retention_receipts()
    {
        using var fixture = new Fixture();
        await fixture.Operations.RetainArtifactsAsync(fixture.Files, default, "window-1");
        fixture.Operations.ReconcileRetention("window-2").Should().BeNull();
        await fixture.Operations.RetainArtifactsAsync(
            [new("PRs/widgets-7/summary.json", Summary(fixture.Run.Id))],
            default,
            "window-2"
        );
        fixture.Store.GetOutboxForRun(fixture.Run.Id).Should().HaveCount(2);
        fixture.Operations.ReconcileRetention("window-1").Should().NotBeNull();
        fixture.Operations.ReconcileRetention("window-2").Should().NotBeNull();
    }

    [Fact]
    public async Task Knowledge_bundle_reads_the_target_branch_instead_of_the_previous_pr_checkout()
    {
        using var fixture = new Fixture();
        var branch = "review/other-8";
        fixture.Runner.On(
            command =>
            {
                var checkout = command.Argv.ToList().IndexOf("checkout");
                if (checkout >= 0)
                    branch = command.Argv[checkout + 1];
                return false;
            },
            new SandboxCommandResult(0, "", "")
        );
        fixture.FileSystem.ListFault = _ =>
            branch == "review/widgets-7"
                ? null
                : new InvalidOperationException("Knowledge was read from the previous PR branch.");
        var extractions = new JsonArray(
            new JsonObject
            {
                ["Edits"] = new JsonArray(
                    new JsonObject
                    {
                        ["Path"] = "KnowledgeBase/widgets/contract.md",
                        ["Content"] = "---\ntitle: Contract\nscope: widgets\n---\nEvidence",
                    }
                ),
                ["Description"] = "Lesson",
            }
        );
        var files = await fixture.Operations.PrepareKnowledgeFilesAsync(
            extractions,
            default,
            ReviewedKnowledgeTestExtensions.Approved(extractions)
        );
        branch.Should().Be("review/widgets-7");
        files.Should().Contain(file => file.RelativePath == "KnowledgeBase/_index.jsonl");
        fixture
            .FileSystem.Writes.Should()
            .BeEmpty("bundle preparation computes future files without modifying entries");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_lock_blocks_retention_and_readonly_closure_verification(bool reconcile)
    {
        using var fixture = new Fixture();
        if (reconcile)
            await fixture.Operations.RetainArtifactsAsync(fixture.Files, default);
        var count = fixture.Runner.Commands.Count;
        await using var repositoryLock = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
            fixture.RepoRoot,
            default
        );
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> invoke = async () =>
        {
            if (reconcile)
                await fixture.CreateOperations().ReconcileClosureAsync(canceled.Token);
            else
                await fixture.CreateOperations().RetainArtifactsAsync(fixture.Files, canceled.Token);
        };
        await invoke.Should().ThrowAsync<OperationCanceledException>();
        fixture.Runner.Commands.Should().HaveCount(count, "the independent OS lease protects Git reads and writes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Origin_refusal_prevents_retention_and_readonly_closure_verification(bool reconcile)
    {
        using var fixture = new Fixture();
        if (reconcile)
            await fixture.Operations.RetainArtifactsAsync(fixture.Files, default);
        var count = fixture.Runner.Commands.Count;
        var operations = fixture.CreateOperations(_ => throw new InvalidOperationException("wrong origin"));
        Func<Task> invoke = async () =>
        {
            if (reconcile)
                await operations.ReconcileClosureAsync(default);
            else
                await operations.RetainArtifactsAsync(fixture.Files, default);
        };
        await invoke.Should().ThrowAsync<InvalidOperationException>().WithMessage("wrong origin");
        fixture.Runner.Commands.Should().HaveCount(count);
    }

    private static string Summary(long runId) =>
        new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["ReviewRunId"] = runId,
            ["Route"] = "merged",
            ["KnowledgeEntryCount"] = 0,
        }.ToJsonString();

    private sealed class Fixture : IDisposable
    {
        public const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly TempSqliteDatabase _db = new();
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "workflow-artifacts-" + Guid.NewGuid().ToString("N")
        );
        public string RepoRoot => Path.Combine(_root, "store");
        private readonly SemaphoreSlim _gitGate = new(1, 1);
        private readonly RepoIdentity _repo = new()
        {
            Provider = "github",
            OrgOrOwner = "example",
            RepoName = "widgets",
        };
        public ReviewStore Store { get; }
        public ReviewRun Run { get; }
        public FakeSandboxCommandRunner Runner { get; } = new();
        public FakeSandboxFileSystem FileSystem { get; } = new();
        public IReadOnlyList<ReviewArtifactFile> Files { get; } = [new("PRs/widgets-7/summary.json", Summary(1))];
        public WorkflowArtifactOperations Operations { get; }

        public Fixture()
        {
            Store = new ReviewStore(_db.ConnectionString);
            Run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = Store.EnsureRepo(_repo),
                    PrId = "7",
                    HeadSha = Sha,
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "merged",
                    VariantId = "primary",
                    Mode = "auto",
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Pending,
                    PrLifecycleState = PrLifecycleState.Merged,
                }
            );
            Runner.OnArgvContains("rev-parse", new SandboxCommandResult(0, Sha, ""));
            Runner.OnArgvContains("ls-remote", new SandboxCommandResult(2, "", ""));
            Operations = CreateOperations();
        }

        public WorkflowArtifactOperations CreateOperations(Func<CancellationToken, Task>? verifyOrigin = null) =>
            new(
                Store,
                Run,
                _repo,
                RepoRoot,
                "main",
                new ReviewBranchManager(new GitRunner(Runner), FileSystem, NullLogger<ReviewBranchManager>.Instance),
                _gitGate,
                new WorkflowKnowledgeEdits(RepoRoot, _repo, FileSystem, NullLogger.Instance),
                verifyOrigin
            );

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
            _gitGate.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
