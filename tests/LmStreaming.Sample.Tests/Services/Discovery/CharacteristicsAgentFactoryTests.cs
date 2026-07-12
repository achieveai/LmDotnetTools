using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

public sealed class CharacteristicsAgentFactoryTests
{
    [Theory]
    [InlineData(
        CopilotModelTransport.Anthropic,
        typeof(AchieveAi.LmDotnetTools.AnthropicProvider.Agents.AnthropicAgent)
    )]
    [InlineData(
        CopilotModelTransport.Responses,
        typeof(AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents.OpenAiResponsesAgent)
    )]
    public void ProgramCopilotModelFactory_CreatesTransportCorrectAgent(
        CopilotModelTransport transport,
        Type expectedAgentType
    )
    {
        var model = Model("catalog-model", transport, []);

        var agent = global::Program.CreateCopilotModelAgent(model, NullLoggerFactory.Instance);

        agent.Should().BeOfType(expectedAgentType);
    }

    [Theory]
    [InlineData(CopilotModelTransport.Anthropic)]
    [InlineData(CopilotModelTransport.Responses)]
    public void Create_CatalogModelUsesModelFactoryAndShapesTransportMetadata(CopilotModelTransport transport)
    {
        var model = Model("catalog-model", transport, ["low", "medium", "high"]);
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelAgent = new Mock<IStreamingAgent>().Object;
        CopilotModelInfo? createdFor = null;
        var factory = CreateFactory(
            [model],
            parentAgent,
            catalogModel =>
            {
                createdFor = catalogModel;
                return modelAgent;
            }
        );

        var result = factory.Create(
            new SubAgentCharacteristics(model.Id, ReasoningEffort.Medium) { IsModelExplicitlySelected = true }
        );

        result.Agent.Should().BeSameAs(modelAgent);
        result.OwnsAgent.Should().BeTrue();
        createdFor.Should().BeSameAs(model);
        if (transport == CopilotModelTransport.Anthropic)
        {
            result
                .ExtraProperties["OutputConfig"]
                .Should()
                .BeOfType<AnthropicOutputConfig>()
                .Which.Effort.Should()
                .Be("medium");
        }
        else
        {
            result
                .ExtraProperties["Reasoning"]
                .Should()
                .BeOfType<ResponseReasoningOptions>()
                .Which.Effort.Should()
                .Be("medium");
        }
    }

    [Fact]
    public void Create_InheritedCopilotModelWithEffortShapesMetadataAndReturnsExactParentAgent()
    {
        var inheritedModel = Model("shared-model", CopilotModelTransport.Responses, ["low", "medium", "high"]);
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        var factory = CreateFactory(
            [inheritedModel],
            parentAgent,
            modelFactory.Object,
            parentCopilotModel: inheritedModel
        );

        var result = factory.Create(new SubAgentCharacteristics(inheritedModel.Id, ReasoningEffort.Medium));

        result.Agent.Should().BeSameAs(parentAgent);
        result.OwnsAgent.Should().BeFalse();
        result
            .ExtraProperties["Reasoning"]
            .Should()
            .BeOfType<ResponseReasoningOptions>()
            .Which.Effort.Should()
            .Be("medium");
        modelFactory.Verify(factoryCall => factoryCall(It.IsAny<CopilotModelInfo>()), Times.Never);
    }

    [Fact]
    public void Create_InheritedNonCopilotModelCollisionDoesNotShapeMetadata()
    {
        var collidingCatalogModel = Model("shared-model", CopilotModelTransport.Responses, ["low", "medium", "high"]);
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        var factory = CreateFactory([collidingCatalogModel], parentAgent, modelFactory.Object, logger);

        var result = factory.Create(new SubAgentCharacteristics(collidingCatalogModel.Id, ReasoningEffort.Medium));

        result.Agent.Should().BeSameAs(parentAgent);
        result.ExtraProperties.Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
        modelFactory.Verify(factoryCall => factoryCall(It.IsAny<CopilotModelInfo>()), Times.Never);
    }

    [Theory]
    [InlineData(ReasoningEffort.Xhigh, "high")]
    [InlineData(ReasoningEffort.Low, "medium")]
    public void Create_AdjustedEffortWarnsOnceWithTransitionAndModel(ReasoningEffort requested, string selected)
    {
        var model = Model("adjusted-model", CopilotModelTransport.Responses, [selected]);
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var factory = CreateFactory([model], parentAgent, _ => new Mock<IStreamingAgent>().Object, logger);
        var characteristics = new SubAgentCharacteristics(model.Id, requested) { IsModelExplicitlySelected = true };

        factory.Create(characteristics);
        factory.Create(characteristics);

        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(requested.ToString());
        warning.Message.Should().Contain(selected);
        warning.Message.Should().Contain(model.Id);
    }

    [Fact]
    public void Create_AdjustedEffortDiagnosticsRemainDistinctPerModel()
    {
        var firstModel = Model("first-model", CopilotModelTransport.Responses, ["low"]);
        var secondModel = Model("second-model", CopilotModelTransport.Responses, ["low"]);
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var factory = CreateFactory(
            [firstModel, secondModel],
            new Mock<IStreamingAgent>().Object,
            _ => new Mock<IStreamingAgent>().Object,
            logger
        );

        factory.Create(
            new SubAgentCharacteristics(firstModel.Id, ReasoningEffort.High) { IsModelExplicitlySelected = true }
        );
        factory.Create(
            new SubAgentCharacteristics(secondModel.Id, ReasoningEffort.High) { IsModelExplicitlySelected = true }
        );

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("max")]
    [InlineData("unknown,max")]
    public void Create_UnsupportedAdvertisedEffortDebugLogsOmissionOnce(string advertised)
    {
        var efforts = string.IsNullOrEmpty(advertised) ? [] : advertised.Split(',');
        var model = Model("unsupported-effort-model", CopilotModelTransport.Responses, efforts);
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var factory = CreateFactory([model], parentAgent, _ => new Mock<IStreamingAgent>().Object, logger);
        var characteristics = new SubAgentCharacteristics(model.Id, ReasoningEffort.High)
        {
            IsModelExplicitlySelected = true,
        };

        var first = factory.Create(characteristics);
        var second = factory.Create(characteristics);

        first.ExtraProperties.Should().BeEmpty();
        second.ExtraProperties.Should().BeEmpty();
        var debug = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Debug).Which;
        debug.Message.Should().Contain(ReasoningEffort.High.ToString());
        debug.Message.Should().Contain(model.Id);
    }

    [Fact]
    public void Create_InheritedCopilotModelWithoutEffortReturnsEmptyMetadata()
    {
        var inheritedModel = Model("shared-model", CopilotModelTransport.Responses, ["low", "medium", "high"]);
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        var factory = CreateFactory(
            [inheritedModel],
            parentAgent,
            modelFactory.Object,
            parentCopilotModel: inheritedModel
        );

        var result = factory.Create(new SubAgentCharacteristics(inheritedModel.Id, null));

        result.Agent.Should().BeSameAs(parentAgent);
        result.ExtraProperties.Should().BeEmpty();
        modelFactory.Verify(factoryCall => factoryCall(It.IsAny<CopilotModelInfo>()), Times.Never);
    }

    [Fact]
    public void Create_ExplicitModelEqualToParentModelUsesCopilotFactory()
    {
        var sharedModel = Model("shared-model", CopilotModelTransport.Responses, []);
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var copilotAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        modelFactory.Setup(factoryCall => factoryCall(sharedModel)).Returns(copilotAgent);
        var factory = CreateFactory([sharedModel], parentAgent, modelFactory.Object);

        var result = factory.Create(
            new SubAgentCharacteristics(sharedModel.Id, null) { IsModelExplicitlySelected = true }
        );

        result.Agent.Should().BeSameAs(copilotAgent);
        modelFactory.Verify(factoryCall => factoryCall(sharedModel), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-in-catalog")]
    public void Create_MissingCatalogModelReturnsExactParentAndWarnsOnce(string? modelId)
    {
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        var factory = CreateFactory([], parentAgent, modelFactory.Object, logger);
        var characteristics = new SubAgentCharacteristics(modelId, ReasoningEffort.High)
        {
            IsModelExplicitlySelected = true,
        };

        var first = factory.Create(characteristics);
        var second = factory.Create(characteristics);

        first.Agent.Should().BeSameAs(parentAgent);
        second.Agent.Should().BeSameAs(parentAgent);
        first.ExtraProperties.Should().BeEmpty();
        modelFactory.Verify(factoryCall => factoryCall(It.IsAny<CopilotModelInfo>()), Times.Never);
        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void Create_DistinctUnknownExplicitModelsFallbackAndWarnOnceForBoundedCategory()
    {
        var logger = new CapturingLogger<CharacteristicsAgentFactory>();
        var parentAgent = new Mock<IStreamingAgent>().Object;
        var modelFactory = new Mock<Func<CopilotModelInfo, IStreamingAgent>>();
        var factory = CreateFactory([], parentAgent, modelFactory.Object, logger);

        var first = factory.Create(
            new SubAgentCharacteristics("unknown-one", null) { IsModelExplicitlySelected = true }
        );
        var second = factory.Create(
            new SubAgentCharacteristics("unknown-two", null) { IsModelExplicitlySelected = true }
        );

        first.Agent.Should().BeSameAs(parentAgent);
        second.Agent.Should().BeSameAs(parentAgent);
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("unknown-one");
        modelFactory.Verify(factoryCall => factoryCall(It.IsAny<CopilotModelInfo>()), Times.Never);
    }

    [Fact]
    public async Task Create_UnknownExplicitModelRestoresParentModelBeforeParentProviderRequest()
    {
        GenerateReplyOptions? receivedOptions = null;
        var parentAgent = new Mock<IStreamingAgent>();
        parentAgent
            .Setup(agent =>
                agent.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, options, _) => receivedOptions = options
            )
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));
        var factory = CreateFactory([], parentAgent.Object, _ => throw new InvalidOperationException());
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            DefaultOptions = new GenerateReplyOptions { ModelId = "unknown-explicit-model" },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        await using var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            parentModelId: "parent-model"
        );

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedOptions.Should().NotBeNull();
        receivedOptions!.ModelId.Should().Be("parent-model");
    }

    [Fact]
    public async Task Spawn_DisposesOwnedExplicitProviderExactlyOnce()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var ownedAgent = CreateRespondingDisposableAgent();
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        _ = await manager.SpawnAsync("test-agent", "test task");
        ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Spawn_ContinuationRecreatesAndDisposesOwnedProviderPerCompletedRun()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var firstOwnedAgent = CreateRespondingDisposableAgent();
        var secondOwnedAgent = CreateRespondingDisposableAgent();
        var createdAgents = new Queue<IStreamingAgent>([firstOwnedAgent.Object, secondOwnedAgent.Object]);
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => createdAgents.Dequeue()
        );
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        await using var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );

        _ = await manager.SpawnAsync("test-agent", "first task", name: "owned");
        _ = await manager.SendMessageAsync("owned", "continued task");

        firstOwnedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
        secondOwnedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
        createdAgents.Should().BeEmpty();
    }

    [Fact]
    public async Task Spawn_CompletionDisposesOwnedProviderBeforeBackgroundRelayCompletes()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var ownedAgent = CreateRespondingDisposableAgent();
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object
        );
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var relayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeRelay = new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var parent = new Mock<IMultiTurnAgent>();
        parent
            .Setup(agent => agent.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => relayStarted.TrySetResult())
            .Returns(new ValueTask<SendReceipt>(completeRelay.Task));
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        await using var manager = new SubAgentManager(
            parent.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );

        _ = await manager.SpawnAsync("test-agent", "test task", runInBackground: true);
        await relayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
        }
        finally
        {
            completeRelay.TrySetResult(new SendReceipt("receipt", null, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task Spawn_ConstructionFailureDisposesOwnedProvider()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var ownedAgent = CreateRespondingDisposableAgent();
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object
        );
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
            ConversationStoreFactory = _ => throw new InvalidOperationException("Store creation failed."),
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        await using var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );

        var act = () => manager.SpawnAsync("test-agent", "test task");

        await act.Should().ThrowAsync<InvalidOperationException>();
        ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Spawn_NeverDisposesBorrowedParentProvider()
    {
        var parentAgent = CreateRespondingDisposableAgent();
        var factory = CreateFactory([], parentAgent.Object, _ => throw new InvalidOperationException());
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        _ = await manager.SpawnAsync("test-agent", "test task");
        await manager.DisposeAsync();

        parentAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Spawn_DisposesOwnedSynchronousProviderExactlyOnce()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var ownedAgent = CreateRespondingSyncDisposableAgent();
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        _ = await manager.SpawnAsync("test-agent", "test task");
        ownedAgent.As<IDisposable>().Verify(agent => agent.Dispose(), Times.Once);
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        ownedAgent.As<IDisposable>().Verify(agent => agent.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Spawn_RetriesOwnedProviderDisposalAfterFirstAttemptThrows()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var disposeCalls = 0;
        var ownedAgent = CreateRespondingDisposableAgent();
        ownedAgent
            .As<IAsyncDisposable>()
            .Setup(agent => agent.DisposeAsync())
            .Returns(() =>
            {
                disposeCalls++;
                return disposeCalls == 1
                    ? throw new InvalidOperationException("owned provider dispose boom")
                    : ValueTask.CompletedTask;
            });
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        // The completion-time disposal throws, but must not permanently latch the guard: a later
        // cleanup (manager dispose) retries and succeeds, so the provider is not leaked.
        _ = await manager.SpawnAsync("test-agent", "test task");
        await manager.DisposeAsync();

        ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Spawn_ConstructionRollbackDisposesProviderWhenStoreDisposeThrowsAndPreservesOriginalError()
    {
        var model = Model("owned-model", CopilotModelTransport.Responses, []);
        var ownedAgent = CreateRespondingDisposableAgent();
        var store = new Mock<IConversationStore>();
        store
            .As<IAsyncDisposable>()
            .Setup(disposable => disposable.DisposeAsync())
            .Throws(new InvalidOperationException("store dispose boom"));
        var factory = CreateFactory(
            [model],
            new Mock<IStreamingAgent>().Object,
            _ => ownedAgent.Object);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "Test",
            AgentFactory = () => throw new InvalidOperationException(),
            DefaultOptions = new GenerateReplyOptions { ModelId = model.Id },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = factory.Create,
            ConversationStoreFactory = _ => store.Object,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };
        await using var manager = new SubAgentManager(
            new Mock<IMultiTurnAgent>().Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );

        // removeTools without a base set makes BuildEnabledToolSet throw AFTER both the owned provider
        // and the store are constructed, exercising the construction rollback with a throwing store.
        var act = () => manager.SpawnAsync("test-agent", "test task", removeTools: ["missing-tool"]);

        // The ORIGINAL construction error surfaces, not the store-disposal failure that masks it.
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("removeTools");
        // Provider disposal is attempted independently even though store disposal threw first.
        ownedAgent.As<IAsyncDisposable>().Verify(agent => agent.DisposeAsync(), Times.Once);
        store.As<IAsyncDisposable>().Verify(disposable => disposable.DisposeAsync(), Times.Once);
    }

    private static CharacteristicsAgentFactory CreateFactory(
        IReadOnlyList<CopilotModelInfo> models,
        IStreamingAgent parentAgent,
        Func<CopilotModelInfo, IStreamingAgent> modelFactory,
        ILogger<CharacteristicsAgentFactory>? logger = null,
        CopilotModelInfo? parentCopilotModel = null
    )
    {
        var registry = new ProviderRegistry(models, new Mock<IFileSystemProbe>().Object);
        return new CharacteristicsAgentFactory(
            registry,
            parentAgent,
            modelFactory,
            logger ?? new CapturingLogger<CharacteristicsAgentFactory>(),
            parentCopilotModel
        );
    }

    private static Mock<IStreamingAgent> CreateRespondingDisposableAgent()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(candidate =>
                candidate.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));
        agent.As<IAsyncDisposable>().Setup(candidate => candidate.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return agent;
    }

    private static Mock<IStreamingAgent> CreateRespondingSyncDisposableAgent()
    {
        // Deliberately implements ONLY IDisposable (not IAsyncDisposable) so the synchronous disposal
        // branch in SubAgentState.DisposeOwnedProviderAgentAsync is exercised.
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(candidate =>
                candidate.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));
        agent.As<IDisposable>().Setup(candidate => candidate.Dispose());
        return agent;
    }

    private static CopilotModelInfo Model(string id, CopilotModelTransport transport, IReadOnlyList<string> efforts) =>
        new(id, id, CopilotModelVendor.OpenAI, transport) { ReasoningEfforts = efforts };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IReadOnlyList<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
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
