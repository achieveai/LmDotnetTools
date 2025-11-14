using System.Net;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Generates test data for provider testing
/// Provides standardized test data patterns for all LmDotnetTools providers
/// </summary>
public static class ProviderTestDataGenerator
{
    /// <summary>
    /// Common test API keys for different scenarios
    /// </summary>
    public static class ApiKeys
    {
        public const string Valid = "test-api-key-12345";
        public const string ValidAlternate = "sk-test-abcdef123456";
        public const string Empty = "";
        public const string WhitespaceOnly = "   ";
        public const string TooShort = "abc";
        public static readonly string TooLong = new string('x', 1000);
    }

    /// <summary>
    /// Common test base URLs for different scenarios
    /// </summary>
    public static class BaseUrls
    {
        public const string ValidHttps = "https://api.test.com";
        public const string ValidHttp = "http://api.test.com";
        public const string ValidWithPath = "https://api.test.com/v1";
        public const string ValidWithTrailingSlash = "https://api.test.com/";
        public const string Invalid = "not-a-url";
        public const string Empty = "";
        public const string WhitespaceOnly = "   ";
    }

    /// <summary>
    /// Common test model names for different scenarios
    /// </summary>
    public static class Models
    {
        public const string Valid = "test-model-v1";
        public const string ValidAlternate = "gpt-4";
        public const string ValidClaude = "claude-3-sonnet";
        public const string Empty = "";
        public const string WhitespaceOnly = "   ";
        public const string WithSpecialChars = "model-with-special@chars!";
    }

    /// <summary>
    /// Generates test data for API key validation scenarios
    /// </summary>
    /// <returns>Test data for API key validation</returns>
    public static IEnumerable<object[]> GetApiKeyValidationTestCases()
    {
        return new List<object[]>
        {
            new object[] { ApiKeys.Valid, true, "Valid API key should pass" },
            new object[] { ApiKeys.ValidAlternate, true, "Valid alternate API key should pass" },
            new object[] { ApiKeys.Empty, false, "Empty API key should fail" },
            new object[] { ApiKeys.WhitespaceOnly, false, "Whitespace-only API key should fail" },
            new object[] { ApiKeys.TooShort, false, "Too short API key should fail" },
            new object[] { null!, false, "Null API key should fail" },
        };
    }

    /// <summary>
    /// Generates test data for base URL validation scenarios
    /// </summary>
    /// <returns>Test data for base URL validation</returns>
    public static IEnumerable<object[]> GetBaseUrlValidationTestCases()
    {
        return new List<object[]>
        {
            new object[] { BaseUrls.ValidHttps, true, "Valid HTTPS URL should pass" },
            new object[] { BaseUrls.ValidHttp, true, "Valid HTTP URL should pass" },
            new object[] { BaseUrls.ValidWithPath, true, "Valid URL with path should pass" },
            new object[] { BaseUrls.ValidWithTrailingSlash, true, "Valid URL with trailing slash should pass" },
            new object[] { BaseUrls.Invalid, false, "Invalid URL should fail" },
            new object[] { BaseUrls.Empty, false, "Empty URL should fail" },
            new object[] { BaseUrls.WhitespaceOnly, false, "Whitespace-only URL should fail" },
            new object[] { null!, false, "Null URL should fail" },
        };
    }

    /// <summary>
    /// Generates test data for model validation scenarios
    /// </summary>
    /// <returns>Test data for model validation</returns>
    public static IEnumerable<object[]> GetModelValidationTestCases()
    {
        return new List<object[]>
        {
            new object[] { Models.Valid, true, "Valid model name should pass" },
            new object[] { Models.ValidAlternate, true, "Valid alternate model name should pass" },
            new object[] { Models.ValidClaude, true, "Valid Claude model name should pass" },
            new object[] { Models.Empty, false, "Empty model name should fail" },
            new object[] { Models.WhitespaceOnly, false, "Whitespace-only model name should fail" },
            new object[] { null!, false, "Null model name should fail" },
        };
    }

    /// <summary>
    /// Generates common HTTP status code test scenarios
    /// </summary>
    /// <returns>Test data for HTTP status code scenarios</returns>
    public static IEnumerable<object[]> GetHttpStatusCodeTestCases()
    {
        return HttpTestHelpers.GetHttpStatusCodeTestCases();
    }

    /// <summary>
    /// Generates test data for retry scenarios
    /// </summary>
    /// <returns>Test data for retry testing</returns>
    public static IEnumerable<object[]> GetRetryTestCases()
    {
        return new List<object[]>
        {
            new object[] { 0, "Should succeed immediately with no retries" },
            new object[] { 1, "Should succeed after 1 retry" },
            new object[] { 2, "Should succeed after 2 retries" },
            new object[] { 3, "Should succeed after 3 retries (max retries)" },
        };
    }

    /// <summary>
    /// Generates test data for timeout scenarios
    /// </summary>
    /// <returns>Test data for timeout testing</returns>
    public static IEnumerable<object[]> GetTimeoutTestCases()
    {
        return new List<object[]>
        {
            new object[] { TimeSpan.FromMilliseconds(100), "Short timeout" },
            new object[] { TimeSpan.FromSeconds(1), "Medium timeout" },
            new object[] { TimeSpan.FromSeconds(5), "Long timeout" },
            new object[] { TimeSpan.FromMinutes(1), "Very long timeout" },
        };
    }

    /// <summary>
    /// Creates a simple test message for chat completion
    /// </summary>
    /// <param name="role">Message role (user, assistant, system)</param>
    /// <param name="content">Message content</param>
    /// <returns>Generic message object</returns>
    public static object CreateTestMessage(string role, string content)
    {
        return new { role = role, content = content };
    }

    /// <summary>
    /// Creates a list of test messages for chat completion
    /// </summary>
    /// <returns>List of test messages</returns>
    public static List<object> CreateTestMessages()
    {
        return new List<object>
        {
            CreateTestMessage("system", "You are a helpful assistant."),
            CreateTestMessage("user", "Hello, how are you?"),
            CreateTestMessage("assistant", "I'm doing well, thank you for asking!"),
        };
    }

    /// <summary>
    /// Creates test usage data
    /// </summary>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>Usage object</returns>
    public static object CreateTestUsage(int promptTokens = 10, int completionTokens = 20)
    {
        return new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens,
        };
    }
}
