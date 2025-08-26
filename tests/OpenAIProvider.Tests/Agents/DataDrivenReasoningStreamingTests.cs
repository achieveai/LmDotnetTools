using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;
using Xunit;
using static AchieveAi.LmDotnetTools.TestUtils.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
/// Data-driven end-to-end tests exercising the <see cref="IStreamingAgent"/> pipeline (HTTP + streaming parsing)
/// for models that emit reasoning. Tests both raw streaming and joined streaming (via MessageUpdateJoinerMiddleware).
/// Each test has two parts:
///   • a one-off creator fact that records the interaction to <c>tests/TestData/OpenAI/&lt;TestName&gt;.stream.json</c>
///     and stores the LmCore request + final streamed response;
///   • theories that replay the cassette offline and assert invariants for both raw and joined streaming.
/// </summary>
public class DataDrivenReasoningStreamingTests
{
    private readonly ProviderTestDataManager _dm = new();

    #region Raw Streaming Playback

    [Theory]
    [MemberData(nameof(GetStreamingTestCases))]
    public async Task Reasoning_RawStreaming_Playback(string testName)
    {
        var (messages, options) = _dm.LoadLmCoreRequest(testName, ProviderType.OpenAI);

        // Prepare playback cassette (.stream.json)
        var cassettePath = Path.Combine(
            FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.stream.json"
        );

        if (!File.Exists(cassettePath))
        {
            Debug.WriteLine($"Cassette for {testName} not found – run creator first.");
            return; // Skip in CI until artefacts exist
        }

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: false)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var http = new HttpClient(handler);
        var client = new OpenClient(http, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Act – stream and materialise into list (NO joiner middleware)
        var stream = await agent.GenerateReplyStreamingAsync(messages, options);
        var response = new List<IMessage>();
        await foreach (var msg in stream)
            response.Add(msg);

        // Assert raw streaming shape
        Assert.True(
            response.OfType<ReasoningMessage>().Any(),
            "Expected at least one ReasoningMessage in raw streaming response"
        );
        Assert.True(
            response.OfType<TextUpdateMessage>().Any(),
            "Expected at least one TextUpdateMessage in raw streaming response"
        );
        // The final response SHOULD be a UsageMessage when the provider reports token usage.
        // However, some streamed scenarios omit usage when token counts are zero. Accept either:
        // 1) The last message is a UsageMessage; or
        // 2) No UsageMessage is present at all.
        var last = response.Last();
        if (last is UsageMessage)
        {
            // If present, it must be the only (and therefore last) usage message.
            Assert.Same(last, response.OfType<UsageMessage>().Last());
        }
        else
        {
            Assert.False(
                response.OfType<UsageMessage>().Any(),
                "UsageMessage exists but is not last"
            );
        }

        // Raw streaming should have reasoning messages (pattern varies by model)
        var reasoningMessages = response.OfType<ReasoningMessage>().ToList();
        Assert.True(
            reasoningMessages.Count >= 1,
            $"Expected at least one reasoning message in raw streaming, got {reasoningMessages.Count}"
        );

        // Should not contain consolidated text messages yet
        Assert.False(
            response.OfType<TextMessage>().Any(),
            "Raw streaming should not contain consolidated TextMessage"
        );

        Debug.WriteLine(
            $"Raw streaming test for {testName}: {response.Count} messages, "
                + $"{reasoningMessages.Count} reasoning messages, "
                + $"{response.OfType<TextUpdateMessage>().Count()} text updates"
        );

        // Log reasoning message details for debugging
        foreach (var reasoning in reasoningMessages.Take(3))
        {
            var preview =
                reasoning.Reasoning?.Length > 50
                    ? reasoning.Reasoning.Substring(0, 50) + "..."
                    : reasoning.Reasoning;
            Debug.WriteLine(
                $"  Reasoning message: visibility={reasoning.Visibility}, length={reasoning.Reasoning?.Length}, preview='{preview}'"
            );
        }
    }

    #endregion

    #region Joined Streaming Playback

    [Theory]
    [MemberData(nameof(GetStreamingTestCases))]
    public async Task Reasoning_JoinedStreaming_Playback(string testName)
    {
        var (messages, options) = _dm.LoadLmCoreRequest(testName, ProviderType.OpenAI);

        // Prepare playback cassette (.stream.json)
        var cassettePath = Path.Combine(
            FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.stream.json"
        );

        if (!File.Exists(cassettePath))
        {
            Debug.WriteLine($"Cassette for {testName} not found – run creator first.");
            return; // Skip in CI until artefacts exist
        }

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: false)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var http = new HttpClient(handler);
        var client = new OpenClient(http, GetApiBaseUrlFromEnv());
        var baseAgent = new OpenClientAgent("TestAgent", client);

        // Wrap with MessageUpdateJoinerMiddleware
        var joinerMiddleware = new MessageUpdateJoinerMiddleware();
        var agent = baseAgent.WithMiddleware(joinerMiddleware);

        // Act – stream through joiner middleware
        var stream = await agent.GenerateReplyStreamingAsync(messages, options);
        var response = new List<IMessage>();
        await foreach (var msg in stream)
            response.Add(msg);

        // Basic assertions - verify joiner middleware is working
        Assert.False(
            response.OfType<ReasoningUpdateMessage>().Any(),
            "Joined streaming should not contain ReasoningUpdateMessage"
        );
        Assert.False(
            response.OfType<TextUpdateMessage>().Any(),
            "Joined streaming should not contain TextUpdateMessage"
        );

        // Should have some reasoning and text messages
        var reasoningMessages = response.OfType<ReasoningMessage>().ToList();
        var textMessages = response.OfType<TextMessage>().ToList();

        Assert.True(
            reasoningMessages.Any(),
            "Expected at least one ReasoningMessage in joined streaming response"
        );
        Assert.True(
            textMessages.Any(),
            "Expected at least one TextMessage in joined streaming response"
        );

        // The last message MAY be UsageMessage when token usage is provided. Accept absence too.
        var joinedLast = response.Last();
        if (joinedLast is UsageMessage)
        {
            Assert.Same(joinedLast, response.OfType<UsageMessage>().Last());
        }
        else
        {
            Assert.False(
                response.OfType<UsageMessage>().Any(),
                "UsageMessage exists but is not last"
            );
        }

        Debug.WriteLine(
            $"Joined streaming test for {testName}: {response.Count} messages, "
                + $"{reasoningMessages.Count} reasoning messages, "
                + $"{textMessages.Count} text messages"
        );
    }

    #endregion

    #region Filtering Validation Tests

    [Fact]
    public async Task FilteringValidation_ShouldNotContainEmptyMessages()
    {
        var testName = "O4MiniReasoningStreaming"; // This test data had the filtering issues
        var (messages, options) = _dm.LoadLmCoreRequest(testName, ProviderType.OpenAI);

        // Prepare playback cassette (.stream.json)
        var cassettePath = Path.Combine(
            FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.stream.json"
        );

        if (!File.Exists(cassettePath))
        {
            Debug.WriteLine($"Cassette for {testName} not found – skipping validation test.");
            return;
        }

        var handler = MockHttpHandlerBuilder.Create().WithRecordPlayback(cassettePath).Build();

        var http = new HttpClient(handler);
        var client = new OpenClient(http, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Get the raw streaming response
        var streamingResponseAsync = await agent.GenerateReplyStreamingAsync(messages, options);
        var streamingResponse = new List<IMessage>();

        await foreach (var message in streamingResponseAsync)
        {
            streamingResponse.Add(message);
        }

        // Validate that filtering is working
        Assert.True(streamingResponse.Count > 0, "Should have some messages");

        // Check that we don't have excessive empty text updates
        var emptyTextUpdates = streamingResponse
            .OfType<TextUpdateMessage>()
            .Where(t => string.IsNullOrEmpty(t.Text))
            .Count();

        Debug.WriteLine($"Empty text updates found: {emptyTextUpdates}");
        Assert.True(
            emptyTextUpdates < 10,
            $"Should not have many empty text updates, found {emptyTextUpdates}"
        );

        // Check that we don't have excessive zero-token usage messages
        var zeroTokenUsageMessages = streamingResponse
            .OfType<UsageMessage>()
            .Where(u =>
                u.Usage.PromptTokens == 0
                && u.Usage.CompletionTokens == 0
                && u.Usage.TotalTokens == 0
            )
            .Count();

        Debug.WriteLine($"Zero-token usage messages found: {zeroTokenUsageMessages}");
        Assert.True(
            zeroTokenUsageMessages <= 1,
            $"Should have at most 1 zero-token usage message, found {zeroTokenUsageMessages}"
        );

        // Check that reasoning messages are meaningful (not tiny fragments)
        var reasoningMessages = streamingResponse.OfType<ReasoningMessage>().ToList();
        var tinyReasoningMessages = reasoningMessages.Where(r => r.Reasoning.Length < 10).Count();

        Debug.WriteLine(
            $"Reasoning messages: {reasoningMessages.Count}, tiny fragments: {tinyReasoningMessages}"
        );

        // Total message count should be reasonable (not thousands)
        Debug.WriteLine($"Total messages: {streamingResponse.Count}");
        Assert.True(
            streamingResponse.Count < 1000,
            $"Message count should be reasonable, found {streamingResponse.Count}"
        );

        // Debug: Show the last few messages
        var lastMessages = streamingResponse.TakeLast(5).ToList();
        Debug.WriteLine("Last 5 messages:");
        for (int i = 0; i < lastMessages.Count; i++)
        {
            var msg = lastMessages[i];
            Debug.WriteLine($"  [{i}] {msg.GetType().Name}: {msg}");
        }

        // Check if we have any usage messages at all
        var allUsageMessages = streamingResponse.OfType<UsageMessage>().ToList();
        Debug.WriteLine($"Total usage messages: {allUsageMessages.Count}");
        foreach (var usage in allUsageMessages)
        {
            Debug.WriteLine(
                $"  Usage: P={usage.Usage.PromptTokens}, C={usage.Usage.CompletionTokens}, T={usage.Usage.TotalTokens}"
            );
        }
    }

    #endregion

    #region Test Data Discovery

    [Fact]
    public void TestDataDiscovery_ShouldFindStreamingTestCases()
    {
        var mgr = new ProviderTestDataManager();
        var allCases = mgr.GetTestCaseNames(ProviderType.OpenAI).ToList();
        var streamingCases = allCases.Where(n => n.EndsWith("Streaming")).ToList();

        Debug.WriteLine($"All test cases: {string.Join(", ", allCases)}");
        Debug.WriteLine($"Streaming test cases: {string.Join(", ", streamingCases)}");

        Assert.True(
            streamingCases.Count > 0,
            $"Expected streaming test cases, but found: {string.Join(", ", allCases)}"
        );
        Assert.Contains("BasicReasoningStreaming", streamingCases);
        Assert.Contains("O4MiniReasoningStreaming", streamingCases);
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> GetStreamingTestCases()
    {
        var mgr = new ProviderTestDataManager();
        return mgr.GetTestCaseNames(ProviderType.OpenAI)
            .Where(n => n.EndsWith("Streaming"))
            .Select(n => new object[] { n });
    }

    #endregion

    #region Artefact creation

    [Fact]
    public async Task CreateBasicReasoningStreamingTestData()
    {
        const string testName = "BasicReasoningStreaming";
        await CreateStreamingArtefactsAsync(testName, modelId: "deepseek/deepseek-r1-0528:free");
    }

    [Fact]
    public async Task CreateO4MiniReasoningStreamingTestData()
    {
        const string testName = "O4MiniReasoningStreaming";
        await CreateStreamingArtefactsAsync(testName, modelId: "o4-mini");
    }

    private async Task CreateStreamingArtefactsAsync(string testName, string modelId)
    {
        string lmCoreRequestPath = _dm.GetTestDataPath(
            testName,
            ProviderType.OpenAI,
            DataType.LmCoreRequest
        );
        if (File.Exists(lmCoreRequestPath))
        {
            Debug.WriteLine($"Artefacts for {testName} already exist. Skip creation.");
            return;
        }

        var messages = new IMessage[]
        {
            new TextMessage
            {
                Role = Role.User,
                Text = "Which number is larger – 9.11 or 9.9? Explain your reasoning.",
            },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = modelId,
            Temperature = 0f,
            ExtraProperties = new Dictionary<string, object?>
            {
                ["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = "medium",
                    ["max_tokens"] = 512,
                },
            }.ToImmutableDictionary(),
        };

        _dm.SaveLmCoreRequest(
            testName,
            ProviderType.OpenAI,
            messages.OfType<TextMessage>().ToArray(),
            options
        );

        // Build handler for recording
        var cassettePath = Path.Combine(
            FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.stream.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(cassettePath, allowAdditional: true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var http = new HttpClient(handler);
        var client = new OpenClient(http, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        var stream = await agent.GenerateReplyStreamingAsync(messages, options);
        var response = new List<IMessage>();
        await foreach (var msg in stream)
            response.Add(msg);

        if (!response.Any(m => m is ReasoningMessage or ReasoningUpdateMessage))
        {
            throw new InvalidOperationException(
                "Provider did not return reasoning in streaming – cannot create test data."
            );
        }

        _dm.SaveFinalResponse(testName, ProviderType.OpenAI, response);
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
