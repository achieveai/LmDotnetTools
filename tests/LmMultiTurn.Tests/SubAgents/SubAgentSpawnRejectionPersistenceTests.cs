using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
///     #691: a spawn that is REJECTED by tool-set validation (a <c>remove_tools</c>-only request with no
///     base set) must leave no conversation row behind. Before the fix, <c>CreateSubAgentAsync</c> stamped
///     the child's tenant/owner metadata (<see cref="AgentThreadOwnership.InheritAsync"/>, a durable write
///     that creates the thread directory) BEFORE <c>BuildEnabledToolSet</c> threw, and the failure path
///     never removed the row — nine <c>metadata.json</c>-only ghost directories in production.
/// </summary>
public sealed class SubAgentSpawnRejectionPersistenceTests : IAsyncLifetime
{
    private const string ParentThreadId = "parent-thread";

    private readonly string _baseDirectory = Path.Combine(
        Path.GetTempPath(),
        "lmmt-691-" + Guid.NewGuid().ToString("N")
    );

    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
        }

        try
        {
            if (Directory.Exists(_baseDirectory))
            {
                Directory.Delete(_baseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task RemoveToolsOnlySpawn_IsRejected_AndPersistsNoChildThread()
    {
        // A tenanted parent is what makes InheritAsync write at all — with a null parent tenant it is a
        // no-op and the ghost could never form, so the pin would be vacuous.
        var store = new FileConversationStore(_baseDirectory);
        await store.SaveMetadataAsync(
            ParentThreadId,
            new ThreadMetadata
            {
                ThreadId = ParentThreadId,
                TenantId = "tenant-a",
                OwnerUserId = "user-a",
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );

        var manager = CreateManager(store);

        var act = () => manager.SpawnAsync("worker", "work", runInBackground: true, removeTools: ["get_weather"]);

        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*removeTools*");

        var threadDirectories = Directory
            .GetDirectories(_baseDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToList();

        threadDirectories
            .Should()
            .BeEquivalentTo(
                [ParentThreadId],
                "a rejected spawn must not leave a metadata-only ghost conversation behind (#691)"
            );
        manager.ListAgents().Should().BeEmpty();
    }

    private SubAgentManager CreateManager(IConversationStore store)
    {
        var parentMock = new Mock<IMultiTurnAgent>();
        _ = parentMock.SetupGet(p => p.ThreadId).Returns(ParentThreadId);
        _ = parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var provider = new Mock<IStreamingAgent>();

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => provider.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
            DefaultConversationStoreFactory = _ => store,
        };

        var manager = new SubAgentManager(
            parentAgent: parentMock.Object,
            parentContracts: [new FunctionContract { Name = "get_weather", Description = "weather" }],
            parentHandlers: new Dictionary<string, ToolHandler>(StringComparer.Ordinal)
            {
                ["get_weather"] = (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
            },
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
        _managers.Add(manager);
        return manager;
    }
}
