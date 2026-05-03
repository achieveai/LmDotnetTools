using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.MockProviderHost;

/// <summary>
/// Loads <see cref="ScriptedSseResponder"/> instances from a JSON scenario document. The schema
/// is intentionally minimal — it mirrors the fluent builder one-to-one — so scenarios can be
/// edited without recompiling the host.
/// </summary>
/// <remarks>
/// <para>
/// Document shape:
/// <code>
/// {
///   "roles": [
///     {
///       "key": "demo",
///       "match": { "type": "always" },
///       "turns": [
///         { "thinking": 32, "messages": [
///             { "kind": "text", "text": "Hello!" },
///             { "kind": "tool_call", "name": "echo", "args": { "msg": "hi" } }
///         ] }
///       ]
///     }
///   ]
/// }
/// </code>
/// Match types: <c>always</c>, <c>system_contains</c> (value), <c>tool</c> (name),
/// <c>user_contains</c> (value).
/// </para>
/// </remarks>
public static class JsonScenarioLoader
{
    private const string BuiltinScenarioPrefix = "AchieveAi.LmDotnetTools.MockProviderHost.scenarios.";

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads a scenario from a file path or built-in name. If <paramref name="nameOrPath"/> is
    /// a path that exists on disk, the file is read; otherwise it is treated as a built-in
    /// scenario name (e.g. <c>"demo"</c>) and resolved from the assembly's embedded resources
    /// or the on-disk <c>scenarios/</c> folder beside the host binary.
    /// </summary>
    public static ScriptedSseResponder Load(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);

        var json = ReadScenarioJson(nameOrPath);
        return Parse(json);
    }

    /// <summary>
    /// Parses a JSON document into a <see cref="ScriptedSseResponder"/>. Exposed for tests
    /// that want to verify the parser without touching the filesystem.
    /// </summary>
    public static ScriptedSseResponder Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ScenarioDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ScenarioDocument>(json, ParseOptions)
                ?? throw new JsonScenarioFormatException("Scenario JSON deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new JsonScenarioFormatException($"Scenario JSON is malformed: {ex.Message}", ex);
        }

        if (document.Roles is null || document.Roles.Count == 0)
        {
            throw new JsonScenarioFormatException("Scenario must declare at least one role.");
        }

        var builder = ScriptedSseResponder.New();
        foreach (var role in document.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Key))
            {
                throw new JsonScenarioFormatException("Every role must declare a non-empty 'key'.");
            }

            var matcher = BuildMatcher(role.Key, role.Match);
            var roleBuilder = builder.ForRole(role.Key, matcher);

            var turns = role.Turns ?? [];
            foreach (var turn in turns)
            {
                roleBuilder = roleBuilder.Turn(plan => ApplyTurn(plan, turn));
            }
        }

        return builder.Build();
    }

    private static string ReadScenarioJson(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
        {
            return File.ReadAllText(nameOrPath);
        }

        // Try alongside the host binary first — `samples/MockProviderHost/scenarios/<name>.json`
        // is copied to the build output by the .csproj.
        var sideloadPath = Path.Combine(
            AppContext.BaseDirectory,
            "scenarios",
            EnsureJsonExtension(nameOrPath));
        if (File.Exists(sideloadPath))
        {
            return File.ReadAllText(sideloadPath);
        }

        // Embedded fallback so consuming applications (e.g. LmStreaming.Sample) can resolve the
        // built-in `demo` scenario without copying the JSON next to their own binary.
        var resourceName = BuiltinScenarioPrefix + EnsureJsonExtension(nameOrPath);
        var assembly = typeof(JsonScenarioLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        throw new FileNotFoundException(
            $"Scenario '{nameOrPath}' was not found on disk or as an embedded resource.",
            nameOrPath);
    }

    private static string EnsureJsonExtension(string nameOrPath) =>
        nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? nameOrPath
            : nameOrPath + ".json";

    private static Func<ScriptedRequestContext, bool> BuildMatcher(string roleKey, ScenarioMatch? match)
    {
        if (match is null || string.IsNullOrWhiteSpace(match.Type)
            || match.Type.Equals("always", StringComparison.OrdinalIgnoreCase)
            || match.Type.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return _ => true;
        }

        if (match.Type.Equals("system_contains", StringComparison.OrdinalIgnoreCase))
        {
            var value = match.Value
                ?? throw new JsonScenarioFormatException(
                    $"Role '{roleKey}': system_contains match requires a 'value' field.");
            return ctx => ctx.SystemPromptContains(value);
        }

        if (match.Type.Equals("user_contains", StringComparison.OrdinalIgnoreCase))
        {
            var value = match.Value
                ?? throw new JsonScenarioFormatException(
                    $"Role '{roleKey}': user_contains match requires a 'value' field.");
            return ctx => ctx.LatestUserMessage is not null
                && ctx.LatestUserMessage.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        if (match.Type.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            var name = match.Name
                ?? throw new JsonScenarioFormatException(
                    $"Role '{roleKey}': tool match requires a 'name' field.");
            return ctx => ctx.HasTool(name);
        }

        throw new JsonScenarioFormatException(
            $"Role '{roleKey}': unknown match type '{match.Type}'.");
    }

    private static void ApplyTurn(InstructionPlanBuilder plan, ScenarioTurn turn)
    {
        if (turn.Thinking is { } reasoning && reasoning > 0)
        {
            _ = plan.Thinking(reasoning);
        }

        var messages = turn.Messages ?? [];
        foreach (var message in messages)
        {
            ApplyMessage(plan, message);
        }
    }

    private static void ApplyMessage(InstructionPlanBuilder plan, ScenarioMessage message)
    {
        var kind = message.Kind ?? string.Empty;

        if (kind.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Text is null)
            {
                throw new JsonScenarioFormatException("Text message requires a 'text' field.");
            }

            _ = plan.Text(message.Text);
            return;
        }

        if (kind.Equals("text_len", StringComparison.OrdinalIgnoreCase))
        {
            if (message.WordCount is not { } wordCount || wordCount <= 0)
            {
                throw new JsonScenarioFormatException(
                    "text_len message requires a positive 'wordCount' field.");
            }

            _ = plan.TextLen(wordCount);
            return;
        }

        if (kind.Equals("tool_call", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(message.Name))
            {
                throw new JsonScenarioFormatException("tool_call message requires a 'name' field.");
            }

            // ToolCall expects a CLR object that JsonSerializer can serialise. Pass the parsed
            // JsonElement straight through — it serialises losslessly.
            object args = message.Args.HasValue ? message.Args.Value : new { };
            _ = plan.ToolCall(message.Name, args);
            return;
        }

        throw new JsonScenarioFormatException($"Unknown message kind '{kind}'.");
    }

    /// <summary>Returns the names of built-in scenarios shipped as embedded resources.</summary>
    public static IReadOnlyList<string> ListBuiltinScenarios()
    {
        var names = typeof(JsonScenarioLoader).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(BuiltinScenarioPrefix, StringComparison.Ordinal)
                && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(n => Path.GetFileNameWithoutExtension(n[BuiltinScenarioPrefix.Length..]))
            .ToArray();

        return names;
    }

    private sealed record ScenarioDocument
    {
        public List<ScenarioRole>? Roles { get; init; }
    }

    private sealed record ScenarioRole
    {
        public string? Key { get; init; }
        public ScenarioMatch? Match { get; init; }
        public List<ScenarioTurn>? Turns { get; init; }
    }

    private sealed record ScenarioMatch
    {
        public string? Type { get; init; }
        public string? Value { get; init; }
        public string? Name { get; init; }
    }

    private sealed record ScenarioTurn
    {
        public int? Thinking { get; init; }
        public List<ScenarioMessage>? Messages { get; init; }
    }

    private sealed record ScenarioMessage
    {
        public string? Kind { get; init; }
        public string? Text { get; init; }
        public int? WordCount { get; init; }
        public string? Name { get; init; }
        public JsonElement? Args { get; init; }
    }
}

/// <summary>
/// Thrown when a JSON scenario document cannot be parsed. The exception message names the
/// offending role/field so authors can correct the document without consulting the source.
/// </summary>
public sealed class JsonScenarioFormatException : Exception
{
    public JsonScenarioFormatException(string message) : base(message) { }

    public JsonScenarioFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
