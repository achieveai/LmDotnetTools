using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;
using Xunit;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
/// Data-driven tests that exercise the full HTTP stack (record / playback) for reasoning support.
/// The first time <see cref="CreateBasicReasoningTestData"/> is executed it will hit the real provider
/// using the credentials in .env.test, persist the cassette under tests/TestData/OpenAI/, and serialise
/// both the original LmCore request and the translated Core response. Subsequent CI runs replay the
/// interaction offline.
/// </summary>
public class DataDrivenReasoningTests
{
    private readonly ProviderTestDataManager _testDataManager = new();

    private static string EnvTestPath =>
        Path.Combine(
            AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(
                AppDomain.CurrentDomain.BaseDirectory
            ),
            ".env.test"
        );

    #region Playback test

    [Theory]
    [MemberData(nameof(GetReasoningTestCases))]
    public async Task Reasoning_RequestAndResponseTransformation(string testName)
    {
        // Arrange – load recorded request/options
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.OpenAI);

        // Wire HTTP client for playback only (allowAdditional = false)
        var cassettePath = Path.Combine(
            AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(
                AppDomain.CurrentDomain.BaseDirectory
            ),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: false)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var response = (await agent.GenerateReplyAsync(messages, options)).ToList();

        // Assert – must contain a ReasoningMessage + final answer + UsageMessage
        Assert.True(
            response.OfType<ReasoningMessage>().Any(),
            "Expected at least one ReasoningMessage in the provider response."
        );
        Assert.Equal(response.Last(), response.OfType<UsageMessage>().Last()); // ensure UsageMessage is last

        // Cross-check with frozen expectation (if available)
        var expected = _testDataManager.LoadFinalResponse(testName, ProviderType.OpenAI);
        if (expected != null)
        {
            Assert.Equal(expected.Count, response.Count);
        }
    }

    public static IEnumerable<object[]> GetReasoningTestCases()
    {
        var mgr = new ProviderTestDataManager();
        return mgr.GetTestCaseNames(ProviderType.OpenAI)
            .Where(name => name.Contains("Reasoning") && !name.EndsWith("Streaming"))
            .Select(n => new object[] { n });
    }

    #endregion

    #region Test-data creation (one-off)

    /// <summary>
    /// Executes a real call to the provider (if no cassette exists yet) and stores the artefacts.
    /// Marked as a Fact so that it can be run manually – CI will skip if the cassette already exists.
    /// </summary>
    [Fact]
    public async Task CreateBasicReasoningTestData()
    {
        const string testName = "BasicReasoning";

        // Short-circuit if data already there
        string lmCoreRequestPath = _testDataManager.GetTestDataPath(
            testName,
            ProviderType.OpenAI,
            DataType.LmCoreRequest
        );
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

        var reasoningDict = new Dictionary<string, object?>
        {
            ["effort"] = "medium",
            ["max_tokens"] = 512,
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "deepseek/deepseek-r1-0528:free", // model known to emit reasoning field via OpenRouter
            Temperature = 0f,
            ExtraProperties = new Dictionary<string, object?>
            {
                ["reasoning"] = reasoningDict,
            }.ToImmutableDictionary(),
        };

        // 2) Save LmCore request artefact
        _testDataManager.SaveLmCoreRequest(
            testName,
            ProviderType.OpenAI,
            messages.OfType<TextMessage>().ToArray(),
            options
        );

        // 3) Configure record/playback handler
        var cassettePath = Path.Combine(
            AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(
                AppDomain.CurrentDomain.BaseDirectory
            ),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: true)
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
            throw new InvalidOperationException(
                "Provider did not return reasoning content – cannot create test data."
            );
        }

        // 5) Persist response for future playback
        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    /// <summary>
    /// Creates OpenAI o-series (gpt-o4-mini) reasoning test data.
    /// </summary>
    [Fact]
    public async Task CreateO4MiniReasoningTestData()
    {
        const string testName = "O4MiniReasoning";

        string lmCoreRequestPath = _testDataManager.GetTestDataPath(
            testName,
            ProviderType.OpenAI,
            DataType.LmCoreRequest
        );
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

        _testDataManager.SaveLmCoreRequest(
            testName,
            ProviderType.OpenAI,
            messages.OfType<TextMessage>().ToArray(),
            options
        );

        var cassettePath = Path.Combine(
            AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(
                AppDomain.CurrentDomain.BaseDirectory
            ),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        var response = await agent.GenerateReplyAsync(messages, options);

        if (!response.OfType<ReasoningMessage>().Any())
        {
            throw new InvalidOperationException(
                "Model did not return reasoning content; cannot create test data."
            );
        }

        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    #endregion

    #region Helpers

    private static string GetApiKeyFromEnv() =>
        EnvironmentHelper.GetApiKeyFromEnv(
            "OPENAI_API_KEY",
            new[] { "LLM_API_KEY" },
            "test-api-key"
        );

    private static string GetApiBaseUrlFromEnv() =>
        EnvironmentHelper.GetApiBaseUrlFromEnv(
            "OPENAI_API_URL",
            new[] { "LLM_API_BASE_URL" },
            "https://api.openai.com/v1"
        );

    #endregion
}
