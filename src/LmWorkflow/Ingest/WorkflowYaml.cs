using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

/// <summary>Loads the JSON-compatible YAML subset and normalizes conditions before flat translation.</summary>
public static class WorkflowYaml
{
    private static readonly JsonSerializerOptions Options = new(SimpleWorkflow.JsonOptions)
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SimpleWorkflow Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        try
        {
            // Aliases and explicit tags can change scalar interpretation or create recursive graphs.
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                if (
                    parser.Current is AnchorAlias
                    || (parser.Current is NodeEvent node && (!node.Tag.IsEmpty || !node.Anchor.IsEmpty))
                )
                    throw Invalid("YAML tags, anchors and aliases are not supported.");
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || Convert(stream.Documents[0].RootNode) is not JsonObject document)
                throw Invalid("Expected exactly one workflow mapping.");
            ValidateFields(document, ["version", "objective", "types", "inputType", "steps"]);
            var conditions = new Dictionary<(int, int), JsonNode>();
            if (document["steps"] is JsonArray steps)
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step)
                        throw Invalid("Each step must be a mapping.");
                    ValidateFields(
                        step,
                        [
                            "id",
                            "title",
                            "kind",
                            "next",
                            "agent",
                            "model",
                            "modelIntelligence",
                            "prompt",
                            "forEach",
                            "saveAs",
                            "agents",
                            "branches",
                            "else",
                            "maxVisits",
                            "onMaxVisits",
                            "inputType",
                            "input",
                            "outputType",
                            "session",
                            "skills",
                            "tools",
                            "script",
                            "maxValidationRetries",
                        ]
                    );
                    if (step["branches"] is not JsonArray branches)
                        continue;
                    for (var j = 0; j < branches.Count; j++)
                    {
                        if (branches[j] is not JsonObject branch || branch["when"] is not { } when)
                            throw Invalid("Each branch requires a condition.");
                        ValidateFields(branch, ["when", "goto"]);
                        conditions[(i, j)] = JsonSerializer.SerializeToNode(
                            ParseCondition(when),
                            WorkflowJson.Options
                        )!;
                        branch["when"] = "";
                    }
                }
            }
            var workflow = document.Deserialize<SimpleWorkflow>(Options) ?? throw Invalid("Workflow is empty.");
            return workflow with
            {
                StrictContracts = true,
                Steps =
                [
                    .. workflow.Steps.Select(
                        (step, i) =>
                            step with
                            {
                                Branches = step
                                    .Branches?.Select(
                                        (branch, j) => branch with { StructuredWhen = conditions[(i, j)] }
                                    )
                                    .ToArray(),
                            }
                    ),
                ],
            };
        }
        catch (WorkflowValidationException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is YamlException
                        or JsonException
                        or FormatException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            throw Invalid($"Invalid workflow YAML: {ex.Message}");
        }
    }

    private static Condition ParseCondition(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var expression))
            return NaturalConditionParser.Parse(expression);
        if (node is not JsonObject obj)
            throw Invalid("A condition must be a comparison or structured mapping.");
        foreach (var key in new[] { "all", "any", "not" })
        {
            if (!obj.TryGetPropertyValue(key, out var child))
                continue;
            if (obj.Count != 1)
                throw Invalid("A composite condition must contain exactly one of all, any or not.");
            if (key == "not")
                return new Condition { Not = ParseCondition(child ?? throw Invalid("Missing not condition.")) };
            if (child is not JsonArray { Count: > 0 } children)
                throw Invalid($"{key} must contain conditions.");
            var parsed = children.Select(c => ParseCondition(c ?? throw Invalid("Null condition."))).ToArray();
            return key == "all" ? new Condition { All = parsed } : new Condition { Any = parsed };
        }
        if (obj.Any(p => p.Key is not ("op" or "path" or "value")))
            throw Invalid("Unknown structured condition field.");
        var condition = obj.Deserialize<Condition>(WorkflowJson.Options) ?? throw Invalid("Missing condition.");
        if (condition.Op is null || string.IsNullOrWhiteSpace(condition.Path))
            throw Invalid("A condition requires a supported op and path.");
        if (condition.Op is not (ConditionOp.Empty or ConditionOp.NonEmpty) && !obj.ContainsKey("value"))
            throw Invalid("Comparison requires a value.");
        return condition;
    }

    private static JsonNode? Convert(YamlNode node) =>
        node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping),
            YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => throw Invalid("Unsupported YAML node."),
        };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: { } name } || result.ContainsKey(name))
                throw Invalid("Mappings require unique string keys.");
            result.Add(name, Convert(value));
        }
        return result;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain)
            return JsonValue.Create(value);
        if (value is "null" or "~" or "")
            return null;
        if (value is "true" or "false")
            return JsonValue.Create(value == "true");
        if (
            value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
        )
            throw Invalid("Use explicit lower-case true or false for Boolean values.");
        var unsigned = value.TrimStart('+', '-');
        if (
            unsigned.Equals(".inf", StringComparison.OrdinalIgnoreCase)
            || unsigned.Equals(".nan", StringComparison.OrdinalIgnoreCase)
        )
            throw Invalid("Numbers must be finite JSON numbers.");
        if (char.IsDigit(value[0]) || value[0] is '-' or '+')
        {
            try
            {
                var parsed = JsonNode.Parse(value);
                if (parsed?.GetValueKind() == JsonValueKind.Number)
                {
                    if (
                        !double.TryParse(value, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number)
                    )
                        throw Invalid("Numbers must be finite.");
                    return parsed;
                }
            }
            catch (JsonException) { }
        }
        return JsonValue.Create(value);
    }

    private static void ValidateFields(JsonObject mapping, string[] allowed)
    {
        foreach (var key in mapping.Select(p => p.Key))
            if (!allowed.Contains(key, StringComparer.Ordinal))
                throw Invalid($"Unknown workflow configuration field '{key}'.");
    }

    private static WorkflowValidationException Invalid(string message) => new([message]);
}
