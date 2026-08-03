using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;

/// <summary>
/// Exposes the <c>AskUserQuestion</c> tool: the model asks a single question, or a batch of up to
/// four, and the run parks (<see cref="ToolHandlerResult.Deferred"/>) until the browser-hosted
/// client submits an answer via the <c>client_tool_result</c> WebSocket frame, which resolves the
/// deferred call through the loop's existing <c>ResolveToolCallAsync</c>/<c>TryResolveToolCallAsync</c>
/// (issue #246). All argument validation is synchronous — a malformed call never parks the run.
/// </summary>
/// <remarks>
/// Registered unconditionally, BEFORE the sub-agent inheritable-tool snapshot in
/// <c>MultiTurnAgentLoop</c>'s constructor, so descendants can ask questions of the same
/// human too. Each loop instance (primary or sub-agent) constructs and registers its own copy —
/// see <c>SubAgentManager</c>'s exclusion of <see cref="ToolName"/> from the
/// parent-tool copy, which would otherwise collide with this fresh registration under
/// <see cref="FunctionRegistry"/>'s default (throwing) conflict resolution.
/// </remarks>
public sealed class AskUserQuestionToolProvider : IFunctionProvider
{
    /// <summary>Tool name — also the deferred-entry function name matched on restart recovery.</summary>
    public const string ToolName = "AskUserQuestion";

    /// <summary>Batch size ceiling: a single call may ask 1 to 4 questions.</summary>
    public const int MaxQuestions = 4;

    public string ProviderName => "ClientTools";

    /// <summary>Low priority (high number) so domain tools take precedence on key conflicts.</summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return new FunctionDescriptor
        {
            Contract = BuildContract(),
            Handler = HandleAsync,
            ProviderName = ProviderName,
        };
    }

    private static FunctionContract BuildContract()
    {
        var optionSchema = new JsonSchemaObject
        {
            Type = new("object"),
            AdditionalProperties = false,
            Required = ["label"],
            Properties = new Dictionary<string, JsonSchemaObject>
            {
                ["label"] = JsonSchemaObject.String("Display text for this choice."),
                ["value"] = JsonSchemaObject.String(
                    "Stable identifier echoed back in the answer. Defaults to 'label' when omitted."),
                ["description"] = JsonSchemaObject.String("Optional short explanation shown under the label."),
                ["preview"] = JsonSchemaObject.String(
                    "Optional markdown preview (code, diagram, mockup) shown when this option is focused."),
            },
        };

        var questionSchema = new JsonSchemaObject
        {
            Type = new("object"),
            AdditionalProperties = false,
            Required = ["prompt", "options"],
            Properties = new Dictionary<string, JsonSchemaObject>
            {
                ["id"] = JsonSchemaObject.String(
                    "Optional stable id echoed back in the answer. Defaults to \"q{index}\" (0-based)."),
                ["prompt"] = JsonSchemaObject.String("The question text."),
                ["description"] = JsonSchemaObject.String("Optional additional context for this question."),
                ["allowMultiple"] = JsonSchemaObject.Boolean("Allow selecting more than one option. Default false."),
                ["allowOther"] = JsonSchemaObject.Boolean(
                    "Allow a free-text \"Other\" answer alongside/instead of the listed options. Default false."),
                ["options"] = JsonSchemaObject.Array(optionSchema, "One or more selectable choices."),
            },
        };

        return new FunctionContract
        {
            Name = ToolName,
            Description =
                "Ask the human one question, or a batch of up to 4 related questions, and pause for their "
                + "answer. The run parks after this call and resumes automatically once the client submits "
                + "an answer — the result becomes this tool's return value.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "context",
                    Description = "Required rationale shown to the human explaining why you're asking.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "questions",
                    Description = "1 to 4 questions to ask in this batch.",
                    ParameterType = JsonSchemaObject.Array(questionSchema),
                    IsRequired = true,
                },
            ],
        };
    }

    private Task<ToolHandlerResult> HandleAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ToolCallId))
        {
            return FromError(
                "missing_tool_call_id",
                "AskUserQuestion requires a tool_call_id to correlate the deferred answer.");
        }

        if (!TryParseArgs(argsJson, out var parsed, out var errorCode, out var errorMessage))
        {
            return FromError(errorCode!, errorMessage!);
        }

        return Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred());
    }

    /// <summary>
    /// Parses and validates <c>{ context, questions[] }</c>. On failure sets <paramref name="errorCode"/>/
    /// <paramref name="errorMessage"/> to one of: <c>invalid_args</c> (malformed JSON/shape),
    /// <c>missing_context</c>, <c>invalid_question_count</c> (0 or &gt;4 questions), <c>no_options</c>
    /// (a question with zero options), <c>duplicate_option_values</c> (two options in the same question
    /// share a value after label-defaulting).
    /// </summary>
    internal static bool TryParseArgs(
        string? argsJson,
        out AskUserQuestionArgs? parsed,
        out string? errorCode,
        out string? errorMessage)
    {
        parsed = null;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(argsJson))
        {
            errorCode = "invalid_args";
            errorMessage = "AskUserQuestion requires 'context' and 'questions'.";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argsJson);
        }
        catch (JsonException)
        {
            errorCode = "invalid_args";
            errorMessage = "AskUserQuestion arguments are not valid JSON.";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_args";
                errorMessage = "AskUserQuestion arguments must be a JSON object.";
                return false;
            }

            var contextText = root.TryGetProperty("context", out var contextEl)
                && contextEl.ValueKind == JsonValueKind.String
                ? contextEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(contextText))
            {
                errorCode = "missing_context";
                errorMessage = "AskUserQuestion requires a non-empty 'context'.";
                return false;
            }

            if (!root.TryGetProperty("questions", out var questionsEl)
                || questionsEl.ValueKind != JsonValueKind.Array)
            {
                errorCode = "invalid_question_count";
                errorMessage = "AskUserQuestion requires a non-empty 'questions' array (1-4 entries).";
                return false;
            }

            var questionEls = questionsEl.EnumerateArray().ToList();
            if (questionEls.Count is 0 or > MaxQuestions)
            {
                errorCode = "invalid_question_count";
                errorMessage = $"AskUserQuestion requires 1 to {MaxQuestions} questions; got {questionEls.Count}.";
                return false;
            }

            var questions = new List<AskUserQuestionSpec>(questionEls.Count);
            for (var i = 0; i < questionEls.Count; i++)
            {
                var qEl = questionEls[i];
                if (qEl.ValueKind != JsonValueKind.Object)
                {
                    errorCode = "invalid_args";
                    errorMessage = $"Question at index {i} must be a JSON object.";
                    return false;
                }

                var prompt = GetString(qEl, "prompt");
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    errorCode = "invalid_args";
                    errorMessage = $"Question at index {i} requires a non-empty 'prompt'.";
                    return false;
                }

                if (!qEl.TryGetProperty("options", out var optionsEl)
                    || optionsEl.ValueKind != JsonValueKind.Array
                    || optionsEl.GetArrayLength() == 0)
                {
                    errorCode = "no_options";
                    errorMessage = $"Question at index {i} ('{prompt}') requires at least one option.";
                    return false;
                }

                var options = new List<AskUserQuestionOptionSpec>();
                var seenValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (var optEl in optionsEl.EnumerateArray())
                {
                    var label = GetString(optEl, "label");
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        errorCode = "invalid_args";
                        errorMessage = $"Question at index {i} has an option with no 'label'.";
                        return false;
                    }

                    var value = GetString(optEl, "value");
                    var effectiveValue = string.IsNullOrWhiteSpace(value) ? label : value;
                    if (!seenValues.Add(effectiveValue))
                    {
                        errorCode = "duplicate_option_values";
                        errorMessage =
                            $"Question at index {i} ('{prompt}') has duplicate option value '{effectiveValue}'.";
                        return false;
                    }

                    options.Add(new AskUserQuestionOptionSpec(
                        label,
                        effectiveValue,
                        GetString(optEl, "description"),
                        GetString(optEl, "preview")));
                }

                var id = GetString(qEl, "id");
                questions.Add(new AskUserQuestionSpec(
                    string.IsNullOrWhiteSpace(id) ? $"q{i}" : id,
                    prompt,
                    GetString(qEl, "description"),
                    GetBool(qEl, "allowMultiple"),
                    GetBool(qEl, "allowOther"),
                    options));
            }

            parsed = new AskUserQuestionArgs(contextText, questions);
            return true;
        }
    }

    private static Task<ToolHandlerResult> FromError(string code, string message) =>
        Task.FromResult<ToolHandlerResult>(
            ToolHandlerResult.FromError(
                JsonSerializer.Serialize(new { status = "rejected", reason = code, message }),
                code));

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Parsed, validated shape of an <c>AskUserQuestion</c> call.</summary>
internal sealed record AskUserQuestionArgs(string Context, IReadOnlyList<AskUserQuestionSpec> Questions);

/// <summary>One question within an <c>AskUserQuestion</c> batch.</summary>
internal sealed record AskUserQuestionSpec(
    string Id,
    string Prompt,
    string? Description,
    bool AllowMultiple,
    bool AllowOther,
    IReadOnlyList<AskUserQuestionOptionSpec> Options);

/// <summary>One selectable option within a question.</summary>
internal sealed record AskUserQuestionOptionSpec(
    string Label,
    string Value,
    string? Description,
    string? Preview);
