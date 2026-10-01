using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The sample's dual-layer composition branch, driven through the real host. The pair tests build
/// the two loops by hand; nothing there proves that <c>Program.cs</c> builds them the same way when a
/// conversation selects a preset: that the executor gets the real tools and its own thread in the
/// store, that the planner's call reaches it, and that the executor's spend lands in the planner's
/// ledger as the executor's.
/// </summary>
/// <remarks>
/// Both members are the scripted providers. The planner (<c>test</c>) follows the user's instruction
/// chain and calls the mirrored <c>calculate</c>; its required <c>rationale</c> carries a nested chain,
/// which is what the executor (<c>test-anthropic</c>) sees as its delegation and follows: run the real
/// <c>calculate</c>, then report. The chain parser finds a chain anywhere in the text, so the nested
/// one is read out of the delegation block.
/// </remarks>
public sealed class DualLayerCompositionTests
{
    private const string PresetId = "testpair";
    private const string PlannerAnswer = "Planner done: the executor reported.";
    private const string ExecutorReport = "report: 5";

    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    private sealed class DualLayerWebAppFactory(InMemoryConversationStore store) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Keeps startup provider discovery side-effect-free (no API key, no network), and avoids
            // the Vite dev-server auto-spawn: the same arrangement every other in-process host test uses.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
            builder.UseEnvironment("Production");

            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"DualLayerModels:{PresetId}:Planner"] = "test",
                        [$"DualLayerModels:{PresetId}:Executor"] = "test-anthropic",
                    }
                )
            );

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileSystemProbe>();
                services.AddSingleton<IFileSystemProbe>(new FakeFileSystemProbe());
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(store);
                services.RemoveAll<ITestAgentBuilder>();
                services.AddSingleton<ITestAgentBuilder>(new UndelayedTestAgentBuilder());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    /// <summary>
    /// The host's test providers stream a few words every 300 ms to look like a model. The planner's
    /// tool call carries the executor's chain in its arguments, so at that pace the one call takes
    /// most of a minute. Same scripted handlers, no pause between chunks.
    /// </summary>
    private sealed class UndelayedTestAgentBuilder : ITestAgentBuilder
    {
        private readonly DefaultTestAgentBuilder _inner = new();

        public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory) =>
            string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase)
                ? new AnthropicTestSseMessageHandler(loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>())
                {
                    ChunkDelayMs = 0,
                }
                : new TestSseMessageHandler(loggerFactory.CreateLogger<TestSseMessageHandler>()) { ChunkDelayMs = 0 };

        public SubAgentOptions? CreateSubAgentOptions(
            ILoggerFactory loggerFactory,
            Func<IStreamingAgent> providerAgentFactory
        ) => _inner.CreateSubAgentOptions(loggerFactory, providerAgentFactory);
    }

    [Fact]
    public async Task SelectingAPreset_BuildsThePair_AndThePlannersCallIsCarriedOutByTheExecutor()
    {
        var store = new InMemoryConversationStore();
        await using var host = new DualLayerWebAppFactory(store);
        var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();
        var threadId = $"dual-layer-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        host.Services.GetRequiredService<ProviderRegistry>()
            .TryGetDualLayerPreset(PresetId, out _)
            .Should()
            .BeTrue("guard: the preset must have loaded from configuration");

        var planner = pool.GetOrCreateAgent(threadId, DefaultMode, PresetId, requestResponseDumpFileName: null)
            .Should()
            .BeOfType<MultiTurnAgentLoop>()
            .Subject;
        planner.ThreadId.Should().Be(threadId, "the planner is the conversation's agent");

        var executorThread = DualLayerThreadIds.ExecutorFor(threadId);
        AgentTextResult answer;
        var run = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            answer = await AgentTextCollector.CollectAsync(planner, Prompt(), cts.Token);
            run.Stop();
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                "The planner's run did not complete in time.\nPlanner transcript:\n"
                    + await DumpAsync(store, threadId)
                    + "\nExecutor transcript:\n"
                    + await DumpAsync(store, executorThread)
            );
        }

        answer.Text.Should().Be(PlannerAnswer);

        // The executor is its own loop on its own thread, with the real tool: the delegation reached
        // it, it ran calculate, and its report is what the planner got back.
        var executorTranscript = await store.LoadMessagesAsync(executorThread);
        executorTranscript
            .Should()
            .Contain(
                m => IsRole(m, "user") && (Field(m, "text") ?? "").StartsWith("<planner-tool-call>"),
                "the planner's transcript was:\n{0}",
                await DumpAsync(store, threadId)
            )
            .And.Contain(m => m.MessageType == nameof(ToolCallMessage) && Field(m, "function_name") == "calculate")
            .And.Contain(m => IsRole(m, "assistant") && Field(m, "text") == ExecutorReport);

        // The planner saw the executor's report as its tool result, never the raw tool output.
        var plannerTranscript = await store.LoadMessagesAsync(threadId);
        var plannerResult = plannerTranscript
            .Where(m => m.MessageType == nameof(ToolCallResultMessage))
            .Should()
            .ContainSingle()
            .Subject;
        Field(plannerResult, "result").Should().Be(ExecutorReport);
        // With the streaming pause removed this is well under a second of work; a long wait here
        // means something in the pair is waiting on a timeout rather than on the executor.
        run.Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(15),
                "two undelayed scripted providers and one tool call. Planner:\n{0}\nExecutor:\n{1}",
                await DumpAsync(store, threadId),
                await DumpAsync(store, executorThread)
            );

        // One ledger for the conversation: every row is rooted at the planner's thread, the executor's
        // rows are owned by the executor's thread and stamped as the executor's, and anything the
        // planner reports stays the primary. (The executor's mock reports usage on every request; the
        // planner's reports it only on the non-streaming path, so its rows may be absent.)
        var ledger = planner.UsageSink.Should().BeOfType<UsageLedger>().Subject;
        var records = ledger.SnapshotRecords();
        records.Should().OnlyContain(r => r.RootConversationId == threadId);
        records
            .Where(r => r.ParentExecutionId == executorThread)
            .Should()
            .NotBeEmpty("the executor's usage is rolled into the planner's ledger")
            .And.OnlyContain(r => r.ExecutionKind == UsageExecutionKind.Executor);
        records
            .Where(r => r.ParentExecutionId == threadId && r.ExecutionKind != UsageExecutionKind.Primary)
            .Should()
            .BeEmpty("the planner's own rows are the conversation's primary spend");
    }

    /// <summary>
    /// The user's chain for the planner. Its calculate call carries the executor's chain as the rationale.
    /// </summary>
    private static string Prompt()
    {
        var executorChain = Chain(
            new
            {
                id = "executor-runs-the-tool",
                messages = new object[]
                {
                    new
                    {
                        tool_call = new[]
                        {
                            new
                            {
                                name = "calculate",
                                args = new
                                {
                                    a = 2,
                                    operation = "add",
                                    b = 3,
                                },
                            },
                        },
                    },
                },
            },
            new { id = "executor-reports", messages = new object[] { new { text = ExecutorReport } } }
        );
        return Chain(
            new
            {
                id = "planner-delegates",
                messages = new object[]
                {
                    new
                    {
                        tool_call = new[]
                        {
                            new
                            {
                                name = "calculate",
                                args = new
                                {
                                    a = 2,
                                    operation = "add",
                                    b = 3,
                                    // The mirror wants a rationale of a few plain words; the chain rides behind them.
                                    rationale = "Add the two numbers and report the sum. " + executorChain,
                                },
                            },
                        },
                    },
                },
            },
            new { id = "planner-wraps-up", messages = new object[] { new { text = PlannerAnswer } } }
        );
    }

    private static async Task<string> DumpAsync(IConversationStore store, string threadId)
    {
        var rows = await store.LoadMessagesAsync(threadId);
        var first = rows.Count == 0 ? 0 : rows.Min(m => m.Timestamp);
        return string.Join(
            "\n",
            rows.Select(m =>
                $"+{(m.Timestamp - first) / 1000.0:F1}s {m.MessageType}/{m.Role}: {m.MessageJson[..Math.Min(160, m.MessageJson.Length)]}"
            )
        );
    }

    /// <summary>A string property of the persisted message's JSON, or null when absent.</summary>
    private static string? Field(PersistedMessage message, string name)
    {
        using var doc = JsonDocument.Parse(message.MessageJson);
        return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsRole(PersistedMessage message, string role) =>
        string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase);

    private static string Chain(params object[] steps) =>
        "<|instruction_start|>" + JsonSerializer.Serialize(new { instruction_chain = steps }) + "<|instruction_end|>";
}
