using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using Json.Schema;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

/// <summary>Resolves named contracts and rejects unsupported or inconsistent declarations before execution.</summary>
internal static class WorkflowContractValidator
{
    internal static WorkflowDefinition Apply(SimpleWorkflow workflow, WorkflowDefinition definition)
    {
        try
        {
            return ApplyCore(workflow, definition);
        }
        catch (WorkflowValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Invalid(ex.Message);
        }
    }

    private static WorkflowDefinition ApplyCore(SimpleWorkflow workflow, WorkflowDefinition definition)
    {
        if (workflow.Version != 1)
        {
            throw Invalid("YAML workflow version must be 1.");
        }

        var types = workflow.Types ?? throw Invalid("Workflow requires named types.");
        foreach (var (name, schema) in types)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('~'))
            {
                throw Invalid($"Unsupported schema name '{name}'.");
            }

            if (schema is not JsonObject || !MetaSchemas.Draft202012.Evaluate(schema).IsValid)
            {
                throw Invalid($"Type '{name}' is not a valid JSON Schema object.");
            }

            ValidateReferences(schema, types);
        }
        var inputSchema = NamedSchema(workflow.InputType, types);
        var stateSchemas = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var nodes = definition.Nodes.ToList();
        foreach (var step in workflow.Steps)
        {
            ValidateStepShape(step);
            if (step.Kind is "agent" or "script")
            {
                var input = NamedSchema(step.InputType, types);
                var output = NamedSchema(step.OutputType, types);
                if (step.Input is null)
                {
                    throw Invalid($"Step '{step.Id}' requires typed input bindings.");
                }

                ValidateBinding(step.Input, input, inputSchema, stateSchemas, types);
                var index = nodes.FindIndex(n => n.Id == step.Id);
                var procedural = (ProceduralNode)nodes[index];
                nodes[index] = procedural with
                {
                    TaskList = [procedural.TaskList![0] with { InputSchema = input, OutputSchema = output }],
                };
                if (step.SaveAs is { } saveAs)
                {
                    if (TypedInputBinder.ParsePath("state." + saveAs).Count != 2)
                    {
                        throw Invalid("saveAs must be a single state property name.");
                    }

                    stateSchemas[saveAs] = output;
                }
            }
            foreach (var branch in step.Branches ?? [])
            {
                var condition =
                    branch.StructuredWhen?.Deserialize<Condition>(WorkflowJson.Options)
                    ?? throw Invalid("Strict branch requires a structured condition.");
                ValidateCondition(condition, inputSchema, stateSchemas, types);
            }
        }
        if (!nodes.OfType<StartNode>().Any())
        {
            var startId = "start";
            while (nodes.Any(n => n.Id == startId))
            {
                startId = "_" + startId;
            }

            nodes.Insert(
                0,
                new StartNode
                {
                    Id = startId,
                    Title = startId,
                    Next = [nodes[0].Id],
                }
            );
        }
        var result = definition with
        {
            Nodes = nodes,
            Defs = (JsonObject)types.DeepClone(),
            InputSchema = inputSchema,
            StrictContracts = true,
        };
        new WorkflowValidator().ValidateAndThrow(result);
        return result;
    }

    private static void ValidateStepShape(SimpleStep step)
    {
        if (step.Kind is not ("start" or "agent" or "script" or "branch" or "end"))
        {
            throw Invalid($"Unsupported strict step kind '{step.Kind}'.");
        }

        if (step.ForEach is not null || step.Agents is not null)
        {
            throw Invalid($"Step '{step.Id}': fan-out is not supported by automatic contracts.");
        }

        if (step.MaxValidationRetries is < 0 or > 1)
        {
            throw Invalid("maxValidationRetries must be 0 or 1.");
        }

        if (step.ModelIntelligence is not null)
        {
            throw Invalid("Automatic steps use a host-resolved model identifier; modelIntelligence is not supported.");
        }

        if (step.Model is not null && (step.Kind != "agent" || string.IsNullOrWhiteSpace(step.Model)))
        {
            throw Invalid("Only agent steps can declare a nonempty model identifier.");
        }

        if (
            step.Tools is { } tools
            && (
                step.Kind != "agent"
                || tools.Any(string.IsNullOrWhiteSpace)
                || tools.Distinct(StringComparer.Ordinal).Count() != tools.Count
            )
        )
        {
            throw Invalid("Only agent steps can declare unique, nonempty host action tool grants.");
        }

        if (step.Kind == "script")
        {
            ValidateWorkspacePath(step.Script, "script");
            if (
                step.Session is not null
                || step.Skills is not null
                || step.Agent is not null
                || step.Prompt is not null
            )
            {
                throw Invalid($"Script step '{step.Id}' cannot declare agent configuration.");
            }
        }
        else if (step.Script is not null)
        {
            throw Invalid($"Only a script step can declare script.");
        }

        if (step.Kind == "agent")
        {
            if (string.IsNullOrWhiteSpace(step.Session))
            {
                throw Invalid($"Agent step '{step.Id}' requires a host-bound session.");
            }

            foreach (var skill in step.Skills ?? [])
            {
                ValidateWorkspacePath(skill, "skill");
            }
        }
        if (
            step.Kind is not ("agent" or "script")
            && (
                step.InputType is not null
                || step.OutputType is not null
                || step.Input is not null
                || step.SaveAs is not null
                || step.Session is not null
                || step.Skills is not null
                || step.Prompt is not null
                || step.Agent is not null
                || step.MaxValidationRetries != 0
            )
        )
        {
            throw Invalid($"Control step '{step.Id}' cannot declare invocation configuration.");
        }

        if (step.Kind != "branch" && (step.Branches is not null || step.Else is not null))
        {
            throw Invalid("Only branches declare branches and else.");
        }

        if (step.Kind is "branch" or "end" && step.Next is not null)
        {
            throw Invalid($"Step kind '{step.Kind}' does not use next.");
        }
    }

    private static void ValidateWorkspacePath(string? path, string field)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':')
            || path.Split('/', '\\').Any(p => p is ".." or "")
        )
        {
            throw Invalid($"{field} must be a contained workspace-relative path.");
        }
    }

    private static JsonObject NamedSchema(string? name, JsonObject types)
    {
        if (name is null || types[name] is not JsonObject schema)
        {
            throw Invalid($"Unknown type '{name ?? "<missing>"}'.");
        }

        var result = (JsonObject)schema.DeepClone();
        result["$defs"] = types.DeepClone();
        return result;
    }

    private static void ValidateReferences(JsonNode? schema, JsonObject types)
    {
        if (schema is JsonObject obj)
        {
            if (obj["$ref"] is { } reference)
            {
                var path = reference.GetValue<string>();
                if (!path.StartsWith("#/$defs/", StringComparison.Ordinal) || types[path[8..]] is null)
                {
                    throw Invalid($"Unknown or external schema reference '{path}'.");
                }
            }
            foreach (var child in obj)
            {
                ValidateReferences(child.Value, types);
            }
        }
        else if (schema is JsonArray array)
        {
            foreach (var child in array)
            {
                ValidateReferences(child, types);
            }
        }
    }

    private static JsonNode Dereference(JsonNode schema, JsonObject types)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (schema is JsonObject obj && obj["$ref"] is { } reference)
        {
            var path = reference.GetValue<string>();
            if (!seen.Add(path))
            {
                throw Invalid("Cyclic schema aliases cannot resolve a binding type.");
            }

            schema = types[path[8..]] ?? throw Invalid($"Unknown reference '{path}'.");
        }
        return schema;
    }

    private static JsonNode ResolveSchema(
        string path,
        JsonNode input,
        IReadOnlyDictionary<string, JsonNode> state,
        JsonObject types
    )
    {
        var segments = TypedInputBinder.ParsePath(path);
        JsonNode schema;
        var offset = 1;
        if (segments[0].Name == "inputs")
        {
            schema = input;
        }
        else
        {
            if (segments.Count < 2 || segments[1].IsIndex || !state.TryGetValue(segments[1].Name!, out schema!))
            {
                throw Invalid($"Unknown producer path '{path}'.");
            }

            offset = 2;
        }
        for (var i = offset; i < segments.Count; i++)
        {
            schema = Dereference(schema, types);
            schema =
                (segments[i].IsIndex ? schema["items"] : schema["properties"]?[segments[i].Name!])
                ?? throw Invalid($"Path '{path}' is inconsistent with its producer schema.");
        }
        return Dereference(schema, types);
    }

    private static void ValidateBinding(
        JsonNode? binding,
        JsonNode target,
        JsonNode inputs,
        IReadOnlyDictionary<string, JsonNode> state,
        JsonObject types
    )
    {
        target = Dereference(target, types);
        if (binding is JsonObject obj)
        {
            if (obj.ContainsKey("from"))
            {
                if (obj.Count != 1 || obj["from"] is not JsonValue value || !value.TryGetValue<string>(out var path))
                {
                    throw Invalid("A reference must contain only a string from field.");
                }

                var source = ResolveSchema(path, inputs, state, types);
                ValidateAssignable(source, target, types, path);
                return;
            }
            if (obj.ContainsKey("literal"))
            {
                if (obj.Count != 1)
                {
                    throw Invalid("A literal must contain only a literal field.");
                }

                ValidateLiteral(obj["literal"], target, types);
                return;
            }
            if (SchemaType(target) is { } targetType && targetType != "object")
            {
                throw Invalid($"Object binding does not match '{targetType}'.");
            }

            var properties = target["properties"] as JsonObject;
            foreach (var required in target["required"] as JsonArray ?? [])
            {
                if (!obj.ContainsKey(required!.GetValue<string>()))
                {
                    throw Invalid($"Input binding is missing required '{required.GetValue<string>()}'.");
                }
            }

            foreach (var (name, child) in obj)
            {
                if (properties?[name] is { } property)
                {
                    ValidateBinding(child, property, inputs, state, types);
                }
                else if (target["additionalProperties"]?.ToJsonString() == "false")
                {
                    throw Invalid($"Unknown input field '{name}'.");
                }
                else
                {
                    ValidateBinding(child, target["additionalProperties"] as JsonObject ?? [], inputs, state, types);
                }
            }
            return;
        }
        if (binding is JsonArray array)
        {
            if (SchemaType(target) is { } targetType && targetType != "array")
            {
                throw Invalid("Array binding requires an array schema.");
            }

            foreach (var item in array)
            {
                ValidateBinding(item, target["items"] ?? new JsonObject(), inputs, state, types);
            }

            return;
        }
        ValidateLiteral(binding, target, types);
    }

    private static void ValidateAssignable(JsonNode source, JsonNode target, JsonObject types, string path)
    {
        source = Dereference(source, types);
        target = Dereference(target, types);
        var sourceType = SchemaType(source);
        var targetType = SchemaType(target);
        if (targetType is not null && sourceType != targetType && !(sourceType == "integer" && targetType == "number"))
        {
            throw Invalid($"Binding '{path}' has type '{sourceType ?? "unspecified"}', expected '{targetType}'.");
        }

        if (targetType == "object")
        {
            var sourceProperties = source["properties"] as JsonObject;
            var targetProperties = target["properties"] as JsonObject;
            if (target["additionalProperties"]?.ToJsonString() == "false")
            {
                foreach (var property in sourceProperties ?? [])
                {
                    if (targetProperties?.ContainsKey(property.Key) != true)
                    {
                        throw Invalid($"Binding '{path}' contains forbidden property '{property.Key}'.");
                    }
                }
            }
            foreach (var property in targetProperties ?? [])
            {
                if (sourceProperties?[property.Key] is { } sourceProperty && property.Value is { } targetProperty)
                {
                    ValidateAssignable(sourceProperty, targetProperty, types, path + "." + property.Key);
                }
            }

            foreach (var required in target["required"] as JsonArray ?? [])
            {
                var name = required!.GetValue<string>();
                if (sourceProperties?[name] is null)
                {
                    throw Invalid($"Binding '{path}' lacks required property '{name}'.");
                }
            }
        }
        if (targetType == "array" && target["items"] is { } items && source["items"] is { } sourceItems)
        {
            ValidateAssignable(sourceItems, items, types, path + "[0]");
        }
    }

    private static void ValidateLiteral(JsonNode? literal, JsonNode schema, JsonObject types)
    {
        var resolved = schema.DeepClone();
        if (resolved is JsonObject obj)
        {
            obj["$defs"] = types.DeepClone();
        }

        var result = new JsonSchemaValidator().ValidateDetailed(
            literal?.ToJsonString() ?? "null",
            resolved.ToJsonString()
        );
        if (!result.IsValid)
        {
            throw Invalid("Literal binding does not satisfy its input schema: " + string.Join("; ", result.Errors));
        }
    }

    private static string? SchemaType(JsonNode schema) =>
        schema is JsonObject obj && obj["type"] is JsonValue value && value.TryGetValue<string>(out var type)
            ? type
            : null;

    private static void ValidateCondition(
        Condition condition,
        JsonNode inputs,
        IReadOnlyDictionary<string, JsonNode> state,
        JsonObject types
    )
    {
        if (condition.All is { } all)
        {
            foreach (var child in all)
            {
                ValidateCondition(child, inputs, state, types);
            }

            return;
        }
        if (condition.Any is { } any)
        {
            foreach (var child in any)
            {
                ValidateCondition(child, inputs, state, types);
            }

            return;
        }
        if (condition.Not is { } not)
        {
            ValidateCondition(not, inputs, state, types);
            return;
        }
        var schema = ResolveSchema(condition.Path!, inputs, state, types);
        if (condition.Op is ConditionOp.Empty or ConditionOp.NonEmpty)
        {
            return;
        }

        var type = SchemaType(schema);
        if (ConditionEvaluator.GetValueBindingPath(condition.Value) is { } valuePath)
        {
            var rightType = SchemaType(ResolveSchema(valuePath, inputs, state, types));
            var numericPair = type is "number" or "integer" && rightType is "number" or "integer";
            if (condition.Op is ConditionOp.Lt or ConditionOp.Lte or ConditionOp.Gt or ConditionOp.Gte)
            {
                if (!numericPair)
                {
                    throw Invalid("Ordered comparison requires numeric operand schemas.");
                }
            }
            else if (condition.Op is ConditionOp.Eq or ConditionOp.Ne)
            {
                if (type != rightType && !numericPair)
                {
                    throw Invalid($"Condition binding '{valuePath}' has type '{rightType}', expected '{type}'.");
                }
            }
            else if (condition.Op == ConditionOp.In && type != "array" && rightType != "array")
            {
                throw Invalid("Condition membership requires an array operand schema.");
            }
            return;
        }
        var kind = condition.Value?.GetValueKind();
        if (condition.Op is ConditionOp.Lt or ConditionOp.Lte or ConditionOp.Gt or ConditionOp.Gte)
        {
            if (type is not ("number" or "integer") || kind != JsonValueKind.Number)
            {
                throw Invalid("Ordered comparison requires numeric schema and literal.");
            }
        }
        else if (condition.Op is ConditionOp.Eq or ConditionOp.Ne)
        {
            if (
                (type == "boolean" && kind is not (JsonValueKind.True or JsonValueKind.False))
                || (type == "string" && kind != JsonValueKind.String)
                || (type is "number" or "integer" && kind != JsonValueKind.Number)
            )
            {
                throw Invalid($"Condition literal has wrong type for '{condition.Path}'.");
            }
        }
    }

    private static WorkflowValidationException Invalid(string message) => new([message]);
}
