extern alias lmstreaming;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ConversationDescendantScanner = lmstreaming::LmStreaming.Sample.Services.ConversationDescendantScanner;
using ConversationReviewScope = lmstreaming::LmStreaming.Sample.Services.ConversationReviewScope;
using LmStreamingProgram = lmstreaming::Program;
using ReviewAuditBridge = lmstreaming::LmStreaming.Sample.Services.ReviewAuditBridge;
using ReviewAuditDeliveryStatus = lmstreaming::LmStreaming.Sample.Services.ReviewAuditDeliveryStatus;
using ReviewConversationScope = lmstreaming::LmStreaming.Sample.Models.ReviewConversationScope;
using SubAgentProvenance = lmstreaming::LmStreaming.Sample.Persistence.SubAgentProvenance;
using SubAgentTreeResponse = lmstreaming::LmStreaming.Sample.Models.SubAgentTreeResponse;
using SystemChatModes = lmstreaming::LmStreaming.Sample.Persistence.SystemChatModes;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class GathererAuditIdentityTests : IDisposable
{
    private const string BridgeSecret = "gatherer-audit-identity-secret";
    private const string ParentThreadId = "parent-thread";
    private const string WorkspacePath = "/workspace/target";
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 17, 0, 0, TimeSpan.Zero);
    private readonly string _conversationRoot = Path.Combine(
        Path.GetTempPath(),
        $"gatherer-audit-identity-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task Spawned_gatherer_audit_reaches_the_daemon_under_the_roster_thread_id()
    {
        using var daemon = new DaemonWebAppFactory(enableReviewAuditIngestion: true, reviewBridgeSecret: BridgeSecret);
        var store = daemon.Services.GetRequiredService<ReviewStore>();
        var round = SeedRound(store);
        using var daemonClient = daemon.CreateClient();
        var bridge = new ReviewAuditBridge(daemonClient, BridgeSecret, maxAttempts: 2, retryDelay: TimeSpan.Zero);
        var lifecycle = ConversationReviewScope.DeriveLifecycleServices(
            new MultiTurnLifecycleServices(),
            bridge,
            new ReviewConversationScope("1", round.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "anthropic",
            SystemChatModes.CodeReviewDaemonModeId
        );
        var conversationStore = new FileConversationStore(_conversationRoot);
        var child = new ScriptedStreamingAgent([
            [
                new ToolCallMessage
                {
                    FunctionName = "Read",
                    FunctionArgs = """{"file_path":"/workspace/target/src/Foo.cs"}""",
                    ToolCallId = "call-read-file",
                    Role = Role.Assistant,
                },
            ],
            [
                new ToolCallMessage
                {
                    FunctionName = "Grep",
                    FunctionArgs = """{"path":"/workspace/target","pattern":"issue 117"}""",
                    ToolCallId = "call-read-issue",
                    Role = Role.Assistant,
                },
            ],
            [new TextMessage { Text = "gatherer complete", Role = Role.Assistant }],
        ]);
        var parent = new ScriptedStreamingAgent([
            [
                new ToolCallMessage
                {
                    FunctionName = "Agent",
                    FunctionArgs = JsonSerializer.Serialize(
                        new
                        {
                            subagent_type = DynamicContextEvidenceValidator.GathererTemplate,
                            prompt = "Gather the frozen PR context.",
                        }
                    ),
                    ToolCallId = "call-spawn-gatherer",
                    Role = Role.Assistant,
                },
            ],
            [new TextMessage { Text = "parent complete", Role = Role.Assistant }],
        ]);
        var options = LmStreamingProgram.ApplyDefaultSubAgentStore(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    [DynamicContextEvidenceValidator.GathererTemplate] = new SubAgentTemplate
                    {
                        Name = "context-gatherer",
                        SystemPrompt = "Gather context.",
                        AgentFactory = () => child,
                    },
                },
            },
            conversationStore,
            stampProvenance: true
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "Read",
                Description = "Read a file",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("file:src/Foo.cs"))
        );
        registry.AddFunction(
            new FunctionContract
            {
                Name = "Grep",
                Description = "Search files",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("issue:117"))
        );
        await using var loop = new MultiTurnAgentLoop(
            parent,
            registry,
            ParentThreadId,
            store: conversationStore,
            subAgentOptions: options,
            lifecycleServices: lifecycle
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput(
                    [new TextMessage { Text = "Dispatch the exact gatherer.", Role = Role.User }],
                    InputId: "gatherer-audit-input"
                ),
                cts.Token
            )
        ) { }

        var liveChild = loop.SubAgentManager!.ListAgents().Should().ContainSingle().Subject;
        _ = await loop.SubAgentManager.ObserveCompletionAsync(liveChild.AgentId, cts.Token);
        loop.SubAgentManager.ListAgents().Should().ContainSingle().Subject.Status.Should().Be(SubAgentStatus.Completed);

        var childMetadata = await conversationStore.LoadMetadataAsync(liveChild.ThreadId, cts.Token);
        childMetadata.Should().NotBeNull();
        childMetadata!.Properties.Should().ContainKey(SubAgentProvenance.StatusKey);
        childMetadata
            .Properties![SubAgentProvenance.StatusKey]
            .ToString()
            .Should()
            .Be(
                "completed",
                "terminal metadata is written before completion observers are released; metadata: {0}",
                JsonSerializer.Serialize(childMetadata)
            );

        var projected = await new ConversationDescendantScanner(
            conversationStore,
            NullLogger<ConversationDescendantScanner>.Instance
        ).ScanAsync(ParentThreadId, cts.Token);
        var rosterBody = JsonSerializer.Serialize(
            new SubAgentTreeResponse(1, projected),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        var rosterHandler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            $"conversations/{ParentThreadId}/subagents",
            rosterBody,
            HttpStatusCode.OK
        );
        using var rosterHttp = new HttpClient(rosterHandler) { BaseAddress = new Uri("http://review-host/") };
        var roster = await new LmStreamingS2SClient(rosterHttp, null, null, null).GetSubAgentTreeAsync(
            ParentThreadId,
            cts.Token
        );

        var node = roster.Nodes.Should().ContainSingle().Subject;
        node.Template.Should().Be(DynamicContextEvidenceValidator.GathererTemplate);
        node.ParentThreadId.Should().Be(ParentThreadId);
        node.Status.Should().Be(ReviewSubAgentStatus.Completed);
        node.ThreadId.Should().Be(SubAgentThreadIds.For(ParentThreadId, node.AgentId));

        var records = store.ListAuditRecordsForRound(round.Id);
        var childRecords = records.Where(record => record.ThreadId == node.ThreadId).ToArray();
        var parentRecords = records.Where(record => record.ThreadId == ParentThreadId).ToArray();
        childRecords.Should().NotBeEmpty();
        parentRecords.Should().NotBeEmpty();
        childRecords.Select(record => record.Id).Should().NotIntersectWith(parentRecords.Select(record => record.Id));
        records.Should().OnlyContain(record => record.EngagementRoundId == round.Id);
        records.Should().OnlyContain(record => record.ProviderId == "anthropic");
        records.Should().OnlyContain(record => record.CaptureOutcome == AuditSourceCaptureOutcome.Complete);
        records.All(record => record.CompletedAtUtc.HasValue).Should().BeTrue();
        bridge
            .GetRoundStatus(new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditScope("1", round.Id.ToString()))
            .Should()
            .Be(ReviewAuditDeliveryStatus.Complete);

        var childAuditShape = childRecords
            .OrderBy(record => record.Sequence)
            .Select(record => new
            {
                record.Sequence,
                record.RecordType,
                record.RunId,
                record.GenerationId,
                Content = System.Text.Encoding.UTF8.GetString(store.ReadAuditContent(record.Id)),
            })
            .ToArray();
        childAuditShape
            .Count(record => record.RecordType == MultiTurnAuditRecordTypes.ToolResult)
            .Should()
            .Be(
                2,
                "the real child must audit both tool results before the validator can qualify them; child audit: {0}",
                JsonSerializer.Serialize(childAuditShape)
            );

        var manifest = new DynamicContextEvidenceValidator(store).Validate(
            new DynamicContextGatheringResult(SemanticManifest(round.Id), "parent-run", ParentThreadId),
            Bootstrap(round.Id),
            roster
        );

        manifest.ScopedReadCount.Should().Be(2);
        manifest.GathererAgentId.Should().Be(node.AgentId);
        manifest.Claims.Should().ContainSingle();
        manifest
            .Claims[0]
            .SourceRecordRefs.Select(source => source.SourceRecordId)
            .Should()
            .BeEquivalentTo(
                childRecords
                    .Where(record => record.RecordType == MultiTurnAuditRecordTypes.ToolResult)
                    .Where(record =>
                        Encoding
                            .UTF8.GetString(store.ReadAuditContent(record.Id))
                            .Contains("file:src/Foo.cs", StringComparison.Ordinal)
                    )
                    .Select(record => record.Id)
            );

        await cts.CancelAsync();
    }

    private static DynamicContextBootstrap Bootstrap(long roundId) =>
        new(
            roundId,
            "github:achieveai/LmDotnetTools",
            "118",
            "base-sha",
            "head-sha",
            "base-sha",
            WorkspacePath,
            ["src/Foo.cs"],
            ["github-issue:LmDotnetTools#117"],
            [],
            [],
            [],
            0
        );

    private static DynamicContextManifestDraft SemanticManifest(long roundId) =>
        new(
            DynamicContextManifest.SchemaVersion,
            roundId,
            [new DynamicContextClaimDraft("claim-1", "The change updates Foo for issue 117.", ["file:src/Foo.cs"])],
            [
                new DynamicContextGap("repository", DynamicContextGapState.Linked, true, null),
                new DynamicContextGap("head", DynamicContextGapState.Linked, true, null),
                new DynamicContextGap("workspace", DynamicContextGapState.Linked, true, null),
            ]
        );

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

    public void Dispose()
    {
        if (Directory.Exists(_conversationRoot))
        {
            Directory.Delete(_conversationRoot, recursive: true);
        }
    }

    private sealed class ScriptedStreamingAgent(IReadOnlyList<IReadOnlyList<IMessage>> turns) : IStreamingAgent
    {
        private int _turn;

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The multi-turn loop uses streaming replies.");

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var index = Interlocked.Increment(ref _turn) - 1;
            if (index >= turns.Count)
            {
                throw new InvalidOperationException("The scripted provider received an unexpected turn.");
            }

            return Task.FromResult(Emit(turns[index], cancellationToken));
        }

        private static async IAsyncEnumerable<IMessage> Emit(
            IReadOnlyList<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }
        }
    }
}
