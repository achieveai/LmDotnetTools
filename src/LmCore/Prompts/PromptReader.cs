using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Scriban;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AchieveAi.LmDotnetTools.LmCore.Prompts;

public class PromptReader : IPromptReader
{
    private readonly Dictionary<string, Dictionary<string, object>> _prompts;

    /// <summary>
    /// Initializes a new instance of the PromptReader class using a file path.
    /// </summary>
    /// <param name="filePath">The path to the YAML file containing prompts.</param>
    public PromptReader(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"The prompt file '{filePath}' was not found.");
        }

        string fullPath = Path.GetFullPath(filePath);
        _prompts = ParseYamlFile(File.ReadAllText(fullPath));
    }

    /// <summary>
    /// Initializes a new instance of the PromptReader class using a stream.
    /// </summary>
    /// <param name="stream">The stream containing the YAML data.</param>
    public PromptReader(Stream stream)
    {
        using var reader = new StreamReader(stream);
        _prompts = ParseYamlFile(reader.ReadToEnd());
    }

    /// <summary>
    /// Initializes a new instance of the PromptReader class using an embedded resource.
    /// </summary>
    /// <param name="assembly">The assembly containing the embedded resource.</param>
    /// <param name="resourceName">The name of the embedded resource.</param>
    public PromptReader(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Resource '{resourceName}' not found in assembly '{assembly.FullName}'.");

        using var reader = new StreamReader(stream);
        _prompts = ParseYamlFile(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses the YAML content and returns a dictionary of prompts.
    /// </summary>
    /// <param name="yamlContent">The YAML content to parse.</param>
    /// <returns>A dictionary of prompts with their versions.</returns>
    private Dictionary<string, Dictionary<string, object>> ParseYamlFile(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var result = deserializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(yamlContent);

        foreach (var promptName in result.Keys)
        {
            var versions = result[promptName];
            var latestVersion = FindLatestVersion(versions.Keys);
            versions["latest"] = versions[latestVersion];
        }

        return result;
    }

    /// <summary>
    /// Finds the latest version from a collection of version strings.
    /// </summary>
    /// <param name="versions">The collection of version strings.</param>
    /// <returns>The latest version string.</returns>
    private string FindLatestVersion(IEnumerable<string> versions)
    {
        Version latest = new Version(0, 0);
        string latestString = "";

        foreach (var version in versions)
        {
            if (Version.TryParse(version.TrimStart('v'), out Version? current) && current != null)
            {
                if (current > latest)
                {
                    latest = current;
                    latestString = version;
                }
            }
        }

        return latestString;
    }

    /// <summary>
    /// Parses a prompt chain from the given chain data.
    /// </summary>
    /// <param name="chainData">The list of dictionaries representing the chain data.</param>
    /// <returns>A list of Message objects.</returns>
    private List<IMessage> ParsePromptChain(List<Dictionary<string, string>> chainData)
    {
        var allowedRoles = new HashSet<string> { "system", "user", "assistant" };

        return chainData.Select(m =>
        {
            var role = m.Keys.First().ToLower();
            var content = m.Values.First();

            if (!allowedRoles.Contains(role))
            {
                throw new ArgumentException($"Invalid role '{role}' in prompt chain. Allowed roles are: {string.Join(", ", allowedRoles)}");
            }

            return new TextMessage
            {
                Role = role switch
                {
                    "system" => Role.System,
                    "user" => Role.User,
                    "assistant" => Role.Assistant
                },
                Text = content
            } as IMessage;
        }).ToList();
    }

    /// <summary>
    /// Retrieves a prompt by name and version.
    /// </summary>
    /// <param name="promptName">The name of the prompt.</param>
    /// <param name="version">The version of the prompt (default is "latest").</param>
    /// <returns>A Prompt object.</returns>
    public Prompt GetPrompt(string promptName, string version = "latest")
    {
        if (!_prompts.ContainsKey(promptName))
            throw new KeyNotFoundException($"Prompt '{promptName}' not found.");

        var promptVersions = _prompts[promptName];

        if (!promptVersions.ContainsKey(version))
            throw new KeyNotFoundException($"Version '{version}' not found for prompt '{promptName}'.");

        var promptContent = promptVersions[version];

        if (promptContent is string)
        {
            return new Prompt(promptName, version, (string)promptContent);
        }
        else if (promptContent is List<object> chainData)
        {
            var rv = chainData.Where(val => val is Dictionary<object, object>)
                .Select(val => (Dictionary<object, object>)val)
                .Where(d => d.Count == 1 && d.Keys.First() is string && d.Values.First() is string)
                .Select(d => new Dictionary<string, string> { { d.Keys.First()!.ToString()!, d.Values.First()!.ToString()! } })
                .ToList();
            try
            {
                var messages = ParsePromptChain(rv);
                return new PromptChain(promptName, version, messages);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Error parsing prompt chain '{promptName}' version '{version}': {ex.Message}");
            }
        }
        else
        {
            throw new InvalidOperationException($"Invalid prompt content for '{promptName}' version '{version}'.");
        }
    }

    /// <summary>
    /// Retrieves a prompt chain by name and version.
    /// </summary>
    /// <param name="promptName">The name of the prompt chain.</param>
    /// <param name="version">The version of the prompt chain (default is "latest").</param>
    /// <returns>A PromptChain object.</returns>
    public PromptChain GetPromptChain(string promptName, string version = "latest")
    {
        var prompt = GetPrompt(promptName, version);
        if (prompt is PromptChain promptChain)
        {
            return promptChain;
        }
        throw new InvalidOperationException($"Prompt '{promptName}' version '{version}' is not a PromptChain.");
    }
}

/// <summary>
/// Represents a single prompt with a name, version, and value.
/// </summary>
public record Prompt(string Name, string Version, string Value)
{
    /// <summary>
    /// Applies variables to the prompt text if provided.
    /// </summary>
    /// <param name="variables">Optional dictionary of variables to apply to the prompt.</param>
    /// <returns>The prompt text with variables applied if provided, otherwise the original text.</returns>
    public virtual string PromptText(Dictionary<string, object>? variables = null)
    {
        if (variables == null)
        {
            return Value;
        }

        var template = Template.Parse(Value);
        return template.Render(variables);
    }
}

/// <summary>
/// Represents a chain of prompts with a name, version, and a list of messages.
/// </summary>
public record PromptChain(string Name, string Version, List<IMessage> Messages) : Prompt(Name, Version, string.Empty)
{
    /// <summary>
    /// Overrides the PromptText method to throw an exception, as it's not applicable for PromptChain.
    /// </summary>
    public override string PromptText(Dictionary<string, object>? variables = null)
    {
        throw new NotSupportedException("PromptChain does not support PromptText method. Use PromptMessages method instead.");
    }

    /// <summary>
    /// Returns the list of messages with variables applied if provided.
    /// </summary>
    /// <param name="variables">Optional dictionary of variables to apply to the message content.</param>
    /// <returns>A list of messages with variables applied if provided, otherwise the original messages.</returns>
    public List<IMessage> PromptMessages(Dictionary<string, object>? variables = null)
    {
        if (variables == null)
        {
            return Messages;
        }

        return Messages.Select<IMessage, IMessage>(
            m => new TextMessage
            {
                Role = m.Role,
                Text = ApplyVariables(((ICanGetText)m).GetText()!, variables)
            }).ToList();
    }

    /// <summary>
    /// Applies variables to the given content using Scriban templating.
    /// </summary>
    /// <param name="content">The content to apply variables to.</param>
    /// <param name="variables">The dictionary of variables to apply.</param>
    /// <returns>The content with variables applied.</returns>
    private string ApplyVariables(string content, Dictionary<string, object> variables)
    {
        var template = Template.Parse(content);
        return template.Render(variables);
    }
}

/// <summary>
/// Defines the interface for a prompt reader.
/// </summary>
public interface IPromptReader
{
    /// <summary>
    /// Retrieves a prompt by name and version.
    /// </summary>
    /// <param name="promptName">The name of the prompt.</param>
    /// <param name="version">The version of the prompt (default is "latest").</param>
    /// <returns>A Prompt object.</returns>
    Prompt GetPrompt(string promptName, string version = "latest");

    /// <summary>
    /// Retrieves a prompt chain by name and version.
    /// </summary>
    /// <param name="promptName">The name of the prompt chain.</param>
    /// <param name="version">The version of the prompt chain (default is "latest").</param>
    /// <returns>A PromptChain object.</returns>
    PromptChain GetPromptChain(string promptName, string version = "latest");
}