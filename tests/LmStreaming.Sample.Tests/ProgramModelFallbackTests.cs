using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Reasoning;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     How the sample host reads <c>LmStreaming:ModelFallback:{providerId}</c> and shapes each fallback's
///     request (#930 P1 item 8). The primary is always down here, so what the target agent receives is
///     what a real fallback would send.
/// </summary>
public sealed class ProgramModelFallbackTests : LoggingTestBase
{
    private const string PrimaryId = "primary-x";
    private const string SettingKey = $"LmStreaming:ModelFallback:{PrimaryId}";
    private const string CopilotRawId = "Gpt-Fallback";
    private const string CopilotId = "gpt-fallback";
    private const string CompatId = "compat-chat";
    private const string FakeJinaKey = "fake-key-for-tests";
    private static readonly string[] ResearchTools = ["web_search", WebSearchTool.ToolName, WebFetchTool.ToolName];

    private readonly Dictionary<string, RecordingAgent> _agents = new(StringComparer.Ordinal);
    private readonly List<string> _built = [];
    private readonly FunctionRegistry _functions = new();
    private readonly RecordingLoggerFactory _loggers = new();
    private readonly RecordingAgent _primary;
    private string? _jinaKey = FakeJinaKey;

    public ProgramModelFallbackTests(ITestOutputHelper output)
        : base(output)
    {
        _primary = new RecordingAgent(answers: false, PrimaryId, Logger);
    }

    [Fact]
    public async Task AnAnthropicFallback_GetsTheWebSearchTheModeAllows_EvenWhenThePrimaryHasNoBuiltIns()
    {
        var target = Agent("test-anthropic");

        await FallBackAsync(Build(Registry(), ["web_search"], ("0", "test-anthropic")), new GenerateReplyOptions());

        target.LastOptions!.BuiltInTools.Should().ContainSingle().Which.Should().BeOfType<AnthropicWebSearchTool>();
    }

    [Fact]
    public async Task AFallback_DoesNotGetAWebSearchTheModeDoesNotAllow()
    {
        var target = Agent("test-anthropic");

        await FallBackAsync(
            Build(Registry(), ["calculator"], ("0", "test-anthropic")),
            new GenerateReplyOptions { BuiltInTools = [new AnthropicWebSearchTool()] }
        );

        target.LastOptions!.BuiltInTools.Should().BeNull();
    }

    [Fact]
    public async Task ATargetWhoseProviderHasNoBuiltIns_StaysWithoutAny()
    {
        var target = Agent("test");

        await FallBackAsync(
            Build(Registry(), ["web_search"], ("0", "test")),
            new GenerateReplyOptions { BuiltInTools = [new AnthropicWebSearchTool()] }
        );

        target.LastOptions!.BuiltInTools.Should().BeNull();
    }

    [Theory]
    [InlineData("test-anthropic", true)]
    [InlineData("test", false)]
    public async Task TheFallback_ReplacesThePrimarysReasoning_AndKeepsEverythingElse(
        string targetId,
        bool targetHasThinking
    )
    {
        var target = Agent(targetId);
        var primaryThinking = new AnthropicThinking(99);
        var request = new GenerateReplyOptions
        {
            ExtraProperties = ImmutableDictionary<string, object?>
                .Empty.Add("Thinking", primaryThinking)
                .Add("Reasoning", new ResponseReasoningOptions { Summary = "auto" })
                .Add("OutputConfig", new AnthropicOutputConfig { Effort = "high" })
                .Add("reasoning_effort", "high")
                .Add("unrelated", "kept"),
        };

        await FallBackAsync(Build(Registry(), null, ("0", targetId)), request);

        var sent = target.LastOptions!.ExtraProperties;
        sent.Should().NotContainKey("Reasoning");
        sent.Should().NotContainKey("OutputConfig");
        sent.Should().NotContainKey("reasoning_effort");
        sent["unrelated"].Should().Be("kept");
        if (targetHasThinking)
        {
            sent["Thinking"].Should().BeOfType<AnthropicThinking>().And.NotBeSameAs(primaryThinking);
        }
        else
        {
            sent.Should().NotContainKey("Thinking");
        }
    }

    [Fact]
    public void EveryKeyTheReasoningShapersWrite_IsOneTheFallbackReplaces()
    {
        static CopilotModelInfo Model(CopilotModelTransport transport, bool adaptive = false) =>
            new("m", "M", CopilotModelVendor.OpenAI, transport, adaptive) { ReasoningEfforts = ["high"] };

        ImmutableDictionary<string, object?>[] shapes =
        [
            global::Program.BuildReasoningExtraProperties("anthropic"),
            global::Program.BuildReasoningExtraProperties("x", CopilotModelTransport.Anthropic, true),
            global::Program.BuildReasoningExtraProperties("x", CopilotModelTransport.Responses),
            CopilotReasoningShaper.Shape(Model(CopilotModelTransport.Anthropic, adaptive: true), ReasoningEffort.High),
            CopilotReasoningShaper.Shape(Model(CopilotModelTransport.Responses), ReasoningEffort.High),
            CopilotReasoningShaper.Shape(Model(CopilotModelTransport.ChatCompletions), ReasoningEffort.High),
        ];

        shapes
            .SelectMany(shape => shape.Keys)
            .Distinct()
            .Should()
            .BeEquivalentTo(global::Program.ReasoningExtraPropertyKeys);
    }

    [Fact]
    public async Task ATargetThatCannotBeBuilt_IsSkippedWithAWarning_AndALaterOneAnswers()
    {
        var later = Agent(CopilotId);
        // No agent is registered for test-anthropic, so building it throws.
        var agent = Build(Registry(), null, ("0", "test-anthropic"), ("1", CopilotId));

        await FallBackAsync(agent, new GenerateReplyOptions());

        later.Calls.Should().Be(1);
        _loggers.Warnings.Should().Contain(w => w.Contains("test-anthropic") && w.Contains("skipped"));
    }

    [Fact]
    public async Task AnUnavailableTarget_IsSkippedBeforeItIsBuilt_SoItsRejectionCannotEndTheChain()
    {
        _ = Agent(CopilotId);
        var later = Agent("test-anthropic");
        var agent = Build(Registry(copilotTokenAvailable: false), null, ("0", CopilotId), ("1", "test-anthropic"));

        await FallBackAsync(agent, new GenerateReplyOptions());

        _built.Should().Equal("test-anthropic");
        later.Calls.Should().Be(1);
        _loggers.Warnings.Should().Contain(w => w.Contains(CopilotId) && w.Contains("skipped"));
    }

    [Theory]
    [InlineData(CopilotId, CopilotRawId)]
    [InlineData(CompatId, "vendor/raw-chat")]
    public async Task ADiscoveredTarget_RunsTheModelIdTheRegistryHolds(string targetId, string expectedModelId)
    {
        var target = Agent(targetId);

        await FallBackAsync(Build(Registry(), null, ("0", targetId)), new GenerateReplyOptions());

        target.LastOptions!.ModelId.Should().Be(expectedModelId);
    }

    [Fact]
    public async Task ASingleStringSetting_IsAOneEntryList()
    {
        var target = Agent("test-anthropic");
        var agent = Build(Registry(), null, SettingKey, "test-anthropic");

        await FallBackAsync(agent, new GenerateReplyOptions());

        target.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AListWhoseParentKeyReadsBlank_IsStillAList()
    {
        var target = Agent("test-anthropic");
        var agent = BuildFrom(
            Registry(),
            null,
            [
                new KeyValuePair<string, string?>(SettingKey, ""),
                new KeyValuePair<string, string?>($"{SettingKey}:0", "test-anthropic"),
            ]
        );

        await FallBackAsync(agent, new GenerateReplyOptions());

        target.Calls.Should().Be(1);
    }

    [Fact]
    public async Task BlankDuplicateAndSelfEntries_LeaveEachTargetOnce()
    {
        _ = Agent("test-anthropic");
        var agent = Build(
            Registry(),
            null,
            ("0", ""),
            ("1", "  TEST-anthropic "),
            ("2", "test-anthropic"),
            ("3", PrimaryId)
        );

        await FallBackAsync(agent, new GenerateReplyOptions());

        _built.Should().Equal("test-anthropic");
    }

    [Fact]
    public async Task ATargetWithoutNativeSearch_GetsTheFunctionSearch_AndThePrimaryDoesNot()
    {
        var target = Agent("test");

        await FallBackAsync(
            Build(Registry(), ResearchTools, ("0", "test")),
            new GenerateReplyOptions { BuiltInTools = [new AnthropicWebSearchTool()] }
        );

        Names(_primary.LastOptions!.Functions).Should().BeEmpty();
        Names(target.LastOptions!.Functions).Should().BeEquivalentTo(WebSearchTool.ToolName, WebFetchTool.ToolName);
        target.LastOptions.BuiltInTools.Should().BeNull();
    }

    [Fact]
    public async Task ATargetWithNativeSearch_KeepsItsOwn_AndLosesTheFunctionWebTools()
    {
        var target = Agent("test-anthropic");

        await FallBackAsync(
            Build(Registry(), ResearchTools, ("0", "test-anthropic")),
            new GenerateReplyOptions
            {
                Functions = [Tool(WebSearchTool.ToolName), Tool(WebFetchTool.ToolName), Tool("calculate")],
            }
        );

        Names(target.LastOptions!.Functions).Should().Equal("calculate");
        target.LastOptions.BuiltInTools.Should().ContainSingle().Which.Should().BeOfType<AnthropicWebSearchTool>();
    }

    [Fact]
    public async Task AToolTheRequestAlreadyLists_IsNotListedTwice()
    {
        var target = Agent("test");

        await FallBackAsync(
            Build(Registry(), ResearchTools, ("0", "test")),
            new GenerateReplyOptions { Functions = [Tool(WebSearchTool.ToolName)] }
        );

        Names(target.LastOptions!.Functions).Should().OnlyHaveUniqueItems().And.Contain(WebSearchTool.ToolName);
    }

    [Fact]
    public async Task WithoutAJinaKey_AModeThatListsSearch_SkipsTheTargetThatCouldNotSearch()
    {
        _jinaKey = null;
        _ = Agent("test");
        var later = Agent("test-anthropic");
        var agent = Build(Registry(), ResearchTools, ("0", "test"), ("1", "test-anthropic"));

        await FallBackAsync(agent, new GenerateReplyOptions());

        _built.Should().Equal("test-anthropic");
        later.Calls.Should().Be(1);
        _loggers
            .Warnings.Should()
            .ContainSingle(w => w.Contains("JINA_API_KEY") && w.Contains("skipped") && w.Contains("test"));
    }

    [Fact]
    public async Task WithoutAJinaKey_AModeWithNoToolList_StillFallsBack_AndWarns()
    {
        _jinaKey = null;
        var target = Agent("test");

        await FallBackAsync(Build(Registry(), null, ("0", "test")), new GenerateReplyOptions());

        target.Calls.Should().Be(1);
        Names(target.LastOptions!.Functions).Should().NotContain(WebSearchTool.ToolName);
        _loggers.Warnings.Should().ContainSingle(w => w.Contains("without web search") && w.Contains("JINA_API_KEY"));
    }

    [Fact]
    public async Task ACopilotTarget_GetsItsOwnReasoningShape_AfterAFallback()
    {
        var target = Agent(CopilotId);

        await FallBackAsync(
            Build(Registry(), null, ("0", CopilotId)),
            new GenerateReplyOptions
            {
                ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("Thinking", new AnthropicThinking(99)),
            }
        );

        var properties = target.LastOptions!.ExtraProperties;
        properties.Should().NotContainKey("Thinking");
        properties["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which.Summary.Should().Be("auto");
    }

    [Fact]
    public async Task TwoTargets_EachKeepTheirOwnBuiltInTools()
    {
        var native = new RecordingAgent(answers: false, "test-anthropic", Logger);
        _agents["test-anthropic"] = native;
        var plain = Agent("test");

        await FallBackAsync(
            Build(Registry(), ResearchTools, ("0", "test-anthropic"), ("1", "test")),
            new GenerateReplyOptions()
        );

        native.LastOptions!.BuiltInTools.Should().ContainSingle().Which.Should().BeOfType<AnthropicWebSearchTool>();
        Names(native.LastOptions.Functions).Should().BeEmpty();
        plain.LastOptions!.BuiltInTools.Should().BeNull();
        Names(plain.LastOptions.Functions).Should().Contain(WebSearchTool.ToolName);
    }

    [Fact]
    public async Task ANativeAnthropicPrimaryDown_TheFallbackSearches_AndTheRecoveredPrimaryReadsTheResult()
    {
        const string searchResponse =
            """{"data":[{"title":"Found on the web","url":"https://example.test/a","description":"result text"}]}""";
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler(searchResponse, out var search);
        var options = new WebToolsOptions { JinaApiKey = FakeJinaKey };
        var primary = new ScriptedProvider(null, [new TextMessage { Text = "final", Role = Role.Assistant }]);
        var fallback = new ScriptedProvider([SearchCall()]);
        var agent = global::Program.WithConfiguredModelFallback(
            Services(Registry(), [new KeyValuePair<string, string?>("LmStreaming:ModelFallback:anthropic:0", "test")]),
            "anthropic",
            primary,
            _ => fallback,
            ResearchTools,
            new FallbackWebTools(
                _functions,
                ResearchTools,
                new JinaWebProvider(new HttpClient(handler), options),
                options,
                _loggers
            )
        );
        await using var loop = new MultiTurnAgentLoop(
            agent,
            _functions,
            "native-primary-down",
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = "claude",
                BuiltInTools = [new AnthropicWebSearchTool()],
            }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "search it", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }

        await cts.CancelAsync();
        fallback.Options.Should().ContainSingle();
        Names(fallback.Options[0]!.Functions).Should().Contain(WebSearchTool.ToolName);
        fallback.Options[0]!.BuiltInTools.Should().BeNull();
        search.RequestBody.Should().Contain("dotnet release");
        primary.Options.Should().HaveCount(2, "the next model call goes to the primary again");
        primary.Options.Should().OnlyContain(o => !Names(o!.Functions).Contains(WebSearchTool.ToolName));
        primary
            .Requests[1]
            .OfType<ToolsCallResultMessage>()
            .SelectMany(m => m.ToolCallResults)
            .Should()
            .ContainSingle(r => r.ToolCallId == "search-1")
            .Which.Result.Should()
            .Contain("Found on the web");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoFallbackConfigured_LeavesThePrimaryAlone(string? setting)
    {
        var agent = setting is null ? Build(Registry(), null) : Build(Registry(), null, SettingKey, setting);

        agent.Should().BeSameAs(_primary);
    }

    private RecordingAgent Agent(string providerId)
    {
        var agent = new RecordingAgent(answers: true, providerId, Logger);
        _agents[providerId] = agent;
        return agent;
    }

    private static ProviderRegistry Registry(bool copilotTokenAvailable = true) =>
        new(
            new FakeFileSystemProbe(),
            () => false,
            [
                new CopilotModelInfo(
                    CopilotRawId,
                    "Fallback",
                    CopilotModelVendor.OpenAI,
                    CopilotModelTransport.Responses
                ),
            ],
            () => copilotTokenAvailable,
            [new AnthropicCompatModel(CompatId, "Compat", "vendor/raw-chat", "http://localhost", "key", "family")]
        );

    private IStreamingAgent Build(
        ProviderRegistry registry,
        IReadOnlyList<string>? allowList,
        params (string Index, string Id)[] targets
    ) =>
        BuildFrom(
            registry,
            allowList,
            targets.Select(t => new KeyValuePair<string, string?>($"{SettingKey}:{t.Index}", t.Id))
        );

    private IStreamingAgent Build(
        ProviderRegistry registry,
        IReadOnlyList<string>? allowList,
        string key,
        string value
    ) => BuildFrom(registry, allowList, [new KeyValuePair<string, string?>(key, value)]);

    private IStreamingAgent BuildFrom(
        ProviderRegistry registry,
        IReadOnlyList<string>? allowList,
        IEnumerable<KeyValuePair<string, string?>> settings
    )
    {
        var options = new WebToolsOptions { JinaApiKey = _jinaKey };
        return global::Program.WithConfiguredModelFallback(
            Services(registry, settings),
            PrimaryId,
            _primary,
            id =>
            {
                _built.Add(id);
                return _agents.TryGetValue(id, out var agent)
                    ? agent
                    : throw new InvalidOperationException($"No agent can be built for {id}.");
            },
            allowList,
            new FallbackWebTools(_functions, allowList, new JinaWebProvider(options), options, _loggers)
        );
    }

    private ServiceProvider Services(ProviderRegistry registry, IEnumerable<KeyValuePair<string, string?>> settings) =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddSingleton(registry)
            .AddSingleton<ILoggerFactory>(_loggers)
            .BuildServiceProvider();

    private static FunctionContract Tool(string name) => new() { Name = name, Description = name };

    private static string[] Names(FunctionContract[]? functions) => [.. (functions ?? []).Select(f => f.Name)];

    private static ToolCallMessage SearchCall() =>
        new()
        {
            FunctionName = WebSearchTool.ToolName,
            FunctionArgs = """{"query":"dotnet release"}""",
            ToolCallId = "search-1",
            Role = Role.Assistant,
        };

    /// <summary>A provider whose calls follow a script: each entry is one call's reply, null for an outage.</summary>
    private sealed class ScriptedProvider(params IMessage[]?[] script) : IStreamingAgent
    {
        public List<GenerateReplyOptions?> Options { get; } = [];

        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Options.Add(options);
            Requests.Add([.. messages]);
            return Task.FromResult(ReadAsync(script[Options.Count - 1], cancellationToken));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static async IAsyncEnumerable<IMessage> ReadAsync(
            IMessage[]? turn,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (turn is null)
            {
                throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable);
            }

            foreach (var message in turn)
            {
                yield return message;
            }
        }
    }

    private static async Task FallBackAsync(IStreamingAgent agent, GenerateReplyOptions request)
    {
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Text = "hi", Role = Role.User }],
            request
        );
        await foreach (var _ in stream) { }
    }

    private sealed class RecordingAgent(bool answers, string providerId, ILogger logger) : IStreamingAgent
    {
        public int Calls { get; private set; }

        public GenerateReplyOptions? LastOptions { get; private set; }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            LastOptions = options;
            logger.LogDebug(
                "Agent {ProviderId} called for model {ModelId} with {BuiltInToolCount} built-in tools",
                providerId,
                options?.ModelId,
                options?.BuiltInTools?.Count ?? 0
            );
            return Task.FromResult(ReadAsync(answers, cancellationToken));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static async IAsyncEnumerable<IMessage> ReadAsync(
            bool answers,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (!answers)
            {
                throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable);
            }

            yield return new TextMessage { Text = "answer", Role = Role.Assistant };
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<string> Warnings { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
