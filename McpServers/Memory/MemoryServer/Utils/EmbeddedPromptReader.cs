using AchieveAi.LmDotnetTools.LmCore.Prompts;
using System.Reflection;

namespace MemoryServer.Utils;

/// <summary>
/// PromptReader implementation that loads prompts from embedded resources.
/// </summary>
public class EmbeddedPromptReader : IPromptReader
{
    private readonly ILogger<EmbeddedPromptReader> _logger;
    private readonly PromptReader _promptReader;

    public EmbeddedPromptReader(ILogger<EmbeddedPromptReader> logger)
    {
        _logger = logger;
        _promptReader = CreatePromptReader();
    }

    private PromptReader CreatePromptReader()
    {
        try
        {
            // Try to load from embedded resources first
            if (EmbeddedResourceHelper.TryLoadEmbeddedResource("Prompts.graph-extraction.yaml", out var content))
            {
                _logger.LogDebug("Loading prompts from embedded resource");
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                return new PromptReader(stream);
            }

            // Fallback to file system
            var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "graph-extraction.yaml");
            if (File.Exists(promptPath))
            {
                _logger.LogDebug("Loading prompts from file system at {PromptPath}", promptPath);
                return new PromptReader(promptPath);
            }

            throw new FileNotFoundException("Prompt file 'graph-extraction.yaml' not found in embedded resources or file system");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize prompt reader");
            throw;
        }
    }

    public Prompt GetPrompt(string promptName, string version = "latest")
    {
        return _promptReader.GetPrompt(promptName, version);
    }

    public PromptChain GetPromptChain(string promptName, string version = "latest")
    {
        return _promptReader.GetPromptChain(promptName, version);
    }
}