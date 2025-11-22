using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
/// Verifies that we can send reasoning obtained in the previous turn back to the provider in the next turn.
/// Two providers covered: DeepSeek R-series (via OpenRouter) and OpenAI o-series (o4-mini).
/// </summary>
public class DataDrivenMultiTurnReasoningTests
{
    private readonly ProviderTestDataManager _dm = new();
    private static readonly string[] fallbackKeys = ["LLM_API_BASE_URL"];
    private static readonly string[] fallbackKeysArray = ["LLM_API_KEY"];
    private static readonly string[] fallbackKeysArray0 = ["LLM_API_BASE_URL"];

    public static IEnumerable<object[]> GetProviders()
    {
        return new[]
        {
            new object[] { "DeepSeekMultiTurn", "deepseek/deepseek-r1-0528:free" },
            ["O4MiniMultiTurn", "o4-mini"],
        };
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task MultiTurn_Playback(string testName, string _)
    {
        // Skip if artefacts missing (first run must execute creation test)
        var file = _dm.GetTestDataPath(testName + "_Turn2", ProviderType.OpenAI, DataType.FinalResponse);
        if (!File.Exists(file))
        {
            Debug.WriteLine($"Test data for {testName} not found – run creator fact first.");
            return;
        }

        var (turn1Msgs, turn1Opts) = _dm.LoadLmCoreRequest(testName + "_Turn1", ProviderType.OpenAI);
        var turn2 = _dm.LoadLmCoreRequest(testName + "_Turn2", ProviderType.OpenAI);

        // prepare playback handler
        var cassettePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: false)
            .ForwardToApi(
                EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeys, "https://api.openai.com/v1"),
                EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeysArray, "test")
            )
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(
            httpClient,
            EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeys, "https://api.openai.com/v1")
        );
        var agent = new OpenClientAgent("TestAgent", client);

        // execute turn-1 (playback only to verify cassette integrity)
        var dummyResp = await agent.GenerateReplyAsync(turn1Msgs, turn1Opts);

        // execute turn-2 – it includes ReasoningMessage from turn-1
        var (turn2Msgs, turn2Opts) = turn2;
        var resp2 = await agent.GenerateReplyAsync(turn2Msgs, turn2Opts);

        Assert.True(resp2.OfType<TextMessage>().Any());
    }

    // --- Creator facts (run manually) --------------------------------------
    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task CreateMultiTurnArtefacts(string testName, string model)
    {
        var path = _dm.GetTestDataPath(testName + "_Turn2", ProviderType.OpenAI, DataType.FinalResponse);
        if (File.Exists(path))
        {
            Debug.WriteLine("Artefacts already exist. Skipping creation.");
            return;
        }

        // ---- turn 1 ----
        var turn1Msgs = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Which is bigger: 9.11 or 9.9? Explain your reasoning." },
        };
        var opts = new GenerateReplyOptions
        {
            ModelId = model,
            Temperature = 0f,
            ExtraProperties = new Dictionary<string, object?>
            {
                ["reasoning"] = new Dictionary<string, object?> { ["effort"] = "medium", ["max_tokens"] = 512 },
            }.ToImmutableDictionary(),
        };

        _dm.SaveLmCoreRequest(
            testName + "_Turn1",
            ProviderType.OpenAI,
            [.. turn1Msgs.OfType<TextMessage>()],
            opts
        );

        var cassettePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: true)
            .ForwardToApi(
                EnvironmentHelper.GetApiBaseUrlFromEnv(
                    "OPENAI_API_URL",
                    fallbackKeysArray0,
                    "https://api.openai.com/v1"
                ),
                EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeysArray, "test")
            )
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(
            httpClient,
            EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeysArray0, "https://api.openai.com/v1")
        );
        var agent = new OpenClientAgent("Recorder", client);

        var turn1Resp = (await agent.GenerateReplyAsync(turn1Msgs, opts)).ToList();
        if (!turn1Resp.OfType<ReasoningMessage>().Any())
        {
            throw new InvalidOperationException("Provider did not return reasoning, cannot record multi-turn test.");
        }

        _dm.SaveFinalResponse(testName + "_Turn1", ProviderType.OpenAI, turn1Resp);

        // ---- turn 2 (send reasoning back) ----
        var turn2Prompt = new List<IMessage>();
        turn2Prompt.AddRange(turn1Msgs);
        turn2Prompt.AddRange(turn1Resp.Where(m => m is TextMessage or ReasoningMessage));
        turn2Prompt.Add(new TextMessage { Role = Role.User, Text = "Thanks!" });

        _dm.SaveLmCoreRequest(
            testName + "_Turn2",
            ProviderType.OpenAI,
            [.. turn2Prompt.OfType<TextMessage>()],
            opts
        );

        var turn2Resp = await agent.GenerateReplyAsync(turn2Prompt, opts);
        _dm.SaveFinalResponse(testName + "_Turn2", ProviderType.OpenAI, turn2Resp);
    }
}
