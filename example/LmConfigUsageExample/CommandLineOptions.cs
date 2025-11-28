using CommandLine;

namespace LmConfigUsageExample;

/// <summary>
/// Command line options for the LmConfigUsageExample application.
/// Supports verb-based commands for different example modes.
/// </summary>
public class CommandLineOptions
{
    [Option('p', "prompt", Required = false, Default = "Hello! Can you help me with a task?",
        HelpText = "The prompt to send to the model.")]
    public string Prompt { get; set; } = "Hello! Can you help me with a task?";

    [Option('m', "model", Required = false,
        HelpText = "The model ID to use (e.g., grok-4.1, claude-sonnet-4-5, gpt-4.1, gemini-2.5-pro).")]
    public string? Model { get; set; }

    [Option("grok", Required = false, Default = false,
        HelpText = "Run the OpenAI Grok agentic loop example.")]
    public bool RunGrok { get; set; }

    [Option("background", Required = false, Default = false,
        HelpText = "Run the background agentic loop example with event queues.")]
    public bool RunBackground { get; set; }

    [Option("claude", Required = false, Default = false,
        HelpText = "Run the ClaudeAgentSDK one-shot example.")]
    public bool RunClaude { get; set; }

    [Option("claude-background", Required = false, Default = false,
        HelpText = "Run the ClaudeAgentSDK background loop example (same interface as BackgroundAgenticLoop).")]
    public bool RunClaudeBackground { get; set; }

    [Option("all", Required = false, Default = false,
        HelpText = "Run all configuration examples (excludes interactive modes).")]
    public bool RunAll { get; set; }

    [Option("list-models", Required = false, Default = false,
        HelpText = "List all available models from the configuration.")]
    public bool ListModels { get; set; }

    [Option("list-providers", Required = false, Default = false,
        HelpText = "List all available providers and their status.")]
    public bool ListProviders { get; set; }

    [Option('t', "temperature", Required = false, Default = 0.7f,
        HelpText = "Temperature for model generation (0.0-2.0).")]
    public float Temperature { get; set; } = 0.7f;

    [Option("max-turns", Required = false, Default = 10,
        HelpText = "Maximum turns for agentic loop (default: 10).")]
    public int MaxTurns { get; set; } = 10;

    [Option('v', "verbose", Required = false, Default = false,
        HelpText = "Enable verbose logging output.")]
    public bool Verbose { get; set; }
}
