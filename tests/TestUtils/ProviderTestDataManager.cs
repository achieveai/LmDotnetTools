using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// Manages test data for provider-specific tests, allowing for data-driven testing
/// with standardized naming conventions and storage formats.
/// </summary>
public class ProviderTestDataManager
{
    private const string DataDirectory = "tests/TestData";
    private const string OpenAIDirectory = "OpenAI";
    private const string AnthropicDirectory = "Anthropic";
    private const string CommonDirectory = "Common";
    
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    /// <summary>
    /// Gets a path to test data files using a standardized naming convention.
    /// </summary>
    /// <param name="testName">The name of the test</param>
    /// <param name="providerType">Which provider this data is for</param>
    /// <param name="dataType">The type of data (requests, responses, etc.)</param>
    /// <returns>A path to the test data file</returns>
    public string GetTestDataPath(string testName, ProviderType providerType, DataType dataType)
    {
        string providerDir = providerType switch
        {
            ProviderType.OpenAI => OpenAIDirectory,
            ProviderType.Anthropic => AnthropicDirectory,
            ProviderType.Common => CommonDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(providerType))
        };
        
        string dataTypeStr = dataType switch
        {
            DataType.LmCoreRequest => "LmCoreRequest",
            DataType.FinalResponse => "FinalResponse",
            _ => throw new ArgumentOutOfRangeException(nameof(dataType))
        };
        
        return Path.Combine(DataDirectory, providerDir, $"{testName}.{dataTypeStr}.json");
    }
    
    /// <summary>
    /// Saves LmCore request data to a file.
    /// </summary>
    public void SaveLmCoreRequest(string testName, ProviderType providerType, TextMessage[] messages, GenerateReplyOptions options)
    {
        var data = new
        {
            Messages = messages,
            Options = options
        };
        
        string filePath = GetTestDataPath(testName, providerType, DataType.LmCoreRequest);
        EnsureDirectoryExists(filePath);
        File.WriteAllText(filePath, JsonSerializer.Serialize(data, _jsonOptions));
    }
    
    /// <summary>
    /// Loads LmCore request data from a file.
    /// </summary>
    public (TextMessage[] Messages, GenerateReplyOptions Options) LoadLmCoreRequest(string testName, ProviderType providerType)
    {
        string filePath = GetTestDataPath(testName, providerType, DataType.LmCoreRequest);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Test data file not found: {filePath}");
        }
        
        var json = File.ReadAllText(filePath);
        var data = JsonSerializer.Deserialize<LmCoreRequestData>(json, _jsonOptions);
        
        return (data?.Messages ?? Array.Empty<TextMessage>(), data?.Options ?? new GenerateReplyOptions());
    }
    
    /// <summary>
    /// Saves a final response to a file.
    /// </summary>
    public void SaveFinalResponse(string testName, ProviderType providerType, TextMessage response)
    {
        string filePath = GetTestDataPath(testName, providerType, DataType.FinalResponse);
        EnsureDirectoryExists(filePath);
        File.WriteAllText(filePath, JsonSerializer.Serialize(response, _jsonOptions));
    }
    
    /// <summary>
    /// Loads a final response from a file.
    /// </summary>
    public TextMessage LoadFinalResponse(string testName, ProviderType providerType)
    {
        string filePath = GetTestDataPath(testName, providerType, DataType.FinalResponse);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Test data file not found: {filePath}");
        }
        
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<TextMessage>(json, _jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize final response from {filePath}");
    }
    
    /// <summary>
    /// Ensures the directory exists for the given file path.
    /// </summary>
    private static void EnsureDirectoryExists(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
    
    /// <summary>
    /// Gets all test cases from test data files in a provider's directory.
    /// </summary>
    public IEnumerable<string> GetTestCaseNames(ProviderType providerType)
    {
        string providerDir = providerType switch
        {
            ProviderType.OpenAI => OpenAIDirectory,
            ProviderType.Anthropic => AnthropicDirectory,
            ProviderType.Common => CommonDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(providerType))
        };
        
        string directoryPath = Path.Combine(DataDirectory, providerDir);
        if (!Directory.Exists(directoryPath))
        {
            return Enumerable.Empty<string>();
        }
        
        return Directory.GetFiles(directoryPath, "*.LmCoreRequest.json")
            .Select(Path.GetFileName)
            .Select(f => f.Replace(".LmCoreRequest.json", string.Empty))
            .Distinct();
    }
}

/// <summary>
/// Types of data stored for tests.
/// </summary>
public enum DataType
{
    /// <summary>
    /// The original LmCore request with messages and options.
    /// </summary>
    LmCoreRequest,
    
    /// <summary>
    /// The final processed response.
    /// </summary>
    FinalResponse
}

/// <summary>
/// Providers supported for testing.
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// OpenAI provider.
    /// </summary>
    OpenAI,
    
    /// <summary>
    /// Anthropic provider.
    /// </summary>
    Anthropic,
    
    /// <summary>
    /// Common data shared between providers.
    /// </summary>
    Common
}

/// <summary>
/// Data structure for LmCore requests.
/// </summary>
internal class LmCoreRequestData
{
    public TextMessage[]? Messages { get; set; }
    public GenerateReplyOptions? Options { get; set; }
} 