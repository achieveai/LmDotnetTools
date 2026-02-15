using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
///     Data-driven tests that exercise the full HTTP stack (record / playback) for reasoning support.
///     The first time <see cref="CreateBasicReasoningTestData" /> is executed it will hit the real provider
///     using the credentials in .env.test, persist the cassette under tests/TestData/OpenAI/, and serialise
///     both the original LmCore request and the translated Core response. Subsequent CI runs replay the
///     interaction offline.
/// </summary>
public class DataDrivenReasoningTests
{
    private const string ManualArtifactCreationEnvVar = "LM_ENABLE_MANUAL_ARTIFACT_CREATION";
    private const string TestBaseUrl = "http://test-mode/v1";
    private static readonly string[] fallbackKeys = ["LLM_API_KEY"];
    private static readonly string[] fallbackKeysArray = ["LLM_API_BASE_URL"];
    private readonly ProviderTestDataManager _testDataManager = new();

    private static string EnvTestPath =>
        Path.Combine(TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

    #region Playback test

    [Theory]
    [MemberData(nameof(GetReasoningTestCases))]
    public async Task Reasoning_RequestAndResponseTransformation(string testName)
    {
        // Arrange – load recorded request/options
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.OpenAI);
        messages = PrepareReasoningInstructionMessages(messages);

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, TestBaseUrl);
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var response = (await agent.GenerateReplyAsync(messages, options)).ToList();

        // Assert – must contain a ReasoningMessage + final answer + UsageMessage
        Assert.True(
            response.Any(m => m is ReasoningMessage or ReasoningUpdateMessage),
            "Expected at least one reasoning message in the provider response."
        );
        Assert.Equal(response.Last(), response.OfType<UsageMessage>().Last()); // ensure UsageMessage is last

        Assert.True(response.Count >= 2, "Expected reasoning response plus usage message.");
    }

    public static IEnumerable<object[]> GetReasoningTestCases()
    {
        var mgr = new ProviderTestDataManager();
        return mgr.GetTestCaseNames(ProviderType.OpenAI)
            .Where(name => name.Contains("Reasoning") && !name.EndsWith("Streaming"))
            .Select(n => new object[] { n });
    }

    #endregion

    private static IMessage[] PrepareReasoningInstructionMessages(IMessage[] messages)
    {
        var userIndex = Array.FindIndex(messages, m => m is TextMessage tm && tm.Role == Role.User);
        if (userIndex < 0 || messages[userIndex] is not TextMessage userMessage)
        {
            return messages;
        }

        var instruction = """
            <|instruction_start|>{"instruction_chain":[{"id_message":"reasoning","reasoning":{"length":30},"messages":[{"text_message":{"length":40}}]}]}<|instruction_end|>
            """;

        var rewritten = messages.ToArray();
        rewritten[userIndex] = userMessage with { Text = $"{userMessage.Text}\n{instruction}" };
        return rewritten;
    }

    #region Test-data creation (one-off)

    /// <summary>
    ///     Executes a real call to the provider (if no cassette exists yet) and stores the artefacts.
    ///     Marked as a Fact so that it can be run manually – CI will skip if the cassette already exists.
    /// </summary>
    [Fact]
    public async Task CreateBasicReasoningTestData()
    {
        if (!ManualArtifactCreationEnabled())
        {
            return;
        }

        const string testName = "BasicReasoning";

        // Short-circuit if data already there
        var lmCoreRequestPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);
        if (File.Exists(lmCoreRequestPath))
        {
            Debug.WriteLine($"Test data already exists at {lmCoreRequestPath}. Skipping creation.");
            return;
        }

        // 1) Build prompt + options
        var messages = new IMessage[]
        {
            new TextMessage
            {
                Role = Role.User,
                Text = "Which number is larger – 9.11 or 9.9? Explain your reasoning.",
            },
        };

        var reasoningDict = new Dictionary<string, object?> { ["effort"] = "medium", ["max_tokens"] = 512 };

        var options = new GenerateReplyOptions
        {
            ModelId = "deepseek/deepseek-r1-0528:free", // model known to emit reasoning field via OpenRouter
            Temperature = 0f,
            ExtraProperties = new Dictionary<string, object?> { ["reasoning"] = reasoningDict }.ToImmutableDictionary(),
        };

        // 2) Save LmCore request artefact
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, [.. messages.OfType<TextMessage>()], options);

        // 3) Configure record/playback handler
        var cassettePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // 4) Execute call and capture response
        var response = await agent.GenerateReplyAsync(messages, options);

        // Sanity – make sure reasoning content present so future assertions make sense
        if (!response.OfType<ReasoningMessage>().Any())
        {
            throw new InvalidOperationException("Provider did not return reasoning content – cannot create test data.");
        }

        // 5) Persist response for future playback
        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    /// <summary>
    ///     Creates OpenAI o-series (gpt-o4-mini) reasoning test data.
    /// </summary>
    [Fact]
    public async Task CreateO4MiniReasoningTestData()
    {
        if (!ManualArtifactCreationEnabled())
        {
            return;
        }

        const string testName = "O4MiniReasoning";

        var lmCoreRequestPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);
        if (File.Exists(lmCoreRequestPath))
        {
            Debug.WriteLine($"Test data already exists at {lmCoreRequestPath}. Skipping creation.");
            return;
        }

        var messages = new IMessage[]
        {
            new TextMessage
            {
                Role = Role.User,
                Text = "Explain why the sky appears blue. Provide your chain-of-thought.",
            },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "o4-mini", // OpenAI o-series small model that supports reasoning_content
            Temperature = 0f,
            ExtraProperties = new Dictionary<string, object?>
            {
                ["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = "medium",
                    ["max_completion_tokens"] = 512,
                },
            }.ToImmutableDictionary(),
        };

        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, [.. messages.OfType<TextMessage>()], options);

        var cassettePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        var response = await agent.GenerateReplyAsync(messages, options);

        if (!response.OfType<ReasoningMessage>().Any())
        {
            throw new InvalidOperationException("Model did not return reasoning content; cannot create test data.");
        }

        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    #endregion

    #region Helpers

    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeys);
    }

    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeysArray);
    }

    private static bool ManualArtifactCreationEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(ManualArtifactCreationEnvVar),
            "1",
            StringComparison.Ordinal
        );
    }

    #endregion
}
