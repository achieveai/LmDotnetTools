using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using Xunit;

namespace LmWorkflow.Tests;

public sealed class TypedWorkflowContractTests
{
    [Theory]
    [InlineData("number", "gte", "10", "9")]
    [InlineData("boolean", "eq", "true", "true")]
    [InlineData("object", "eq", "{\"Value\":true}", "{\"Value\":true}")]
    public void Condition_bindings_use_producer_types_and_fail_when_runtime_operand_is_missing(
        string type,
        string op,
        string left,
        string right
    )
    {
        var yaml = """
            version: 1
            objective: Compare typed operands
            inputType: Input
            types:
              Input:
                type: object
                additionalProperties: false
                required: [Left, Right]
                properties:
                  Left: {type: TYPE}
                  Right: {type: TYPE}
            steps:
              - id: choose
                kind: branch
                branches:
                  - when: {op: OP, path: inputs.Left, value: '{{inputs.Right}}'}
                    goto: yes
                else: no
              - id: yes
                kind: end
              - id: no
                kind: end
            """.Replace("TYPE", type).Replace("OP", op);
        var definition = Read(yaml).ToDefinition();
        var branch = definition.Nodes.OfType<ConditionalNode>().Single().Branches[0];
        var condition = branch.When!.Deserialize<Condition>(WorkflowJson.Options)!;
        var context = new BindingContext
        {
            Inputs = new JsonObject { ["Left"] = JsonNode.Parse(left), ["Right"] = JsonNode.Parse(right) },
        };
        Assert.True(ConditionEvaluator.Evaluate(condition, context, ConditionEvaluationPolicy.StrictWorkflow));
        var runtime = WorkflowRuntime.CreateNew();
        runtime.LoadDefinition(definition);
        runtime.MergeInputs(context.Inputs);
        runtime.AdvanceTo(definition.Nodes.OfType<StartNode>().Single().Id, "choose", null);
        runtime.AdvanceTo("choose", "yes", null);
        Assert.True(runtime.IsComplete);
        Assert.Equal("yes", runtime.CurrentNodeId);
        context.Inputs.Remove("Right");
        Assert.Throws<InvalidOperationException>(() =>
            ConditionEvaluator.Evaluate(condition, context, ConditionEvaluationPolicy.StrictWorkflow)
        );
        Assert.Throws<WorkflowValidationException>(() =>
            Read(yaml.Replace("inputs.Right", "inputs.Missing")).ToDefinition()
        );
        Assert.Throws<WorkflowValidationException>(() =>
            Read(yaml.Replace("Right: {type: " + type + "}", "Right: {type: string}")).ToDefinition()
        );
    }

    internal const string Definition = """
        version: 1
        objective: Typed script and parent interaction
        inputType: Input
        types:
          Input:
            type: object
            additionalProperties: false
            required: [Value]
            properties:
              Value: {type: boolean}
          Result:
            type: object
            additionalProperties: false
            required: [Decision]
            properties:
              Decision: {type: boolean}
        steps:
          - id: prepare
            kind: script
            script: scripts/prepare.ps1
            inputType: Input
            input: {from: inputs}
            outputType: Result
            saveAs: Result
          - id: assess
            kind: agent
            session: review-parent
            skills: [skills/review.md]
            prompt: Assess the input.
            inputType: Result
            input: {Decision: {from: state.Result.Decision}}
            outputType: Result
            saveAs: Assessment
            maxValidationRetries: 1
          - id: route
            kind: branch
            branches:
              - when:
                  all:
                    - "state.Assessment.Decision == true"
                    - not: "state.Result.Decision == false"
                goto: done
            else: done
          - id: done
            kind: end
        """;

    private static SimpleWorkflow Read(string yaml)
    {
        var method = typeof(SimpleWorkflow).GetMethod("DeserializeYaml", [typeof(string)]);
        Assert.NotNull(method);
        try
        {
            return (SimpleWorkflow)method.Invoke(null, [yaml])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException!;
        }
    }

    [Fact]
    public void YamlLowersTypedStepsIntoExistingGraph()
    {
        var definition = Read(Definition).ToDefinition();
        new WorkflowValidator().ValidateAndThrow(definition);
        Assert.Single(definition.Nodes.OfType<StartNode>());
        var task = definition.Nodes.OfType<ProceduralNode>().First().TaskList![0];
        Assert.Equal("Script", task.Delegate.ToString());
        Assert.NotNull(task.OutputSchema);
        Assert.True(definition.StrictContracts);
        Assert.Equal("scripts/prepare.ps1", task.Script);
        var agent = definition.Nodes.OfType<ProceduralNode>().Last().TaskList![0];
        Assert.Equal("review-parent", agent.Session);
        Assert.Equal("skills/review.md", Assert.Single(agent.Skills!));
        Assert.Equal(1, agent.MaxValidationRetries);
        Assert.NotNull(agent.InputSchema);
        Assert.Equal("state.Result", task.Writes!.To);
        Assert.IsType<JsonObject>(definition.Nodes.OfType<ConditionalNode>().Single().Branches[0].When);
    }

    [Theory]
    [InlineData("outputType: Result", "outputType: Missing")]
    [InlineData("state.Result.Decision}", "state.Result.decision}")]
    [InlineData("goto: done", "goto: nowhere")]
    [InlineData("type: boolean", "type: booolean")]
    [InlineData("kind: script", "kind: script\n    unrecognized: true")]
    [InlineData("version: 1", "version: 1\nversion: 1")]
    [InlineData("{from: inputs}", "{from: inputs, literal: true}")]
    public void InvalidContractsFailBeforeExecution(string before, string after)
    {
        Assert.Throws<WorkflowValidationException>(() =>
            Read(Definition.Replace(before, after.Replace("\\n", "\n"))).ToDefinition()
        );
    }

    [Fact]
    public void BindingCopiesNestedValuesWithoutStringCoercionOrAliasing()
    {
        var context = new BindingContext
        {
            State = JsonNode.Parse("""{"Result":{"items":[{"Value":true}],"nothing":null}}""")!.AsObject(),
        };
        var binding = JsonNode.Parse(
            """{"flag":{"from":"state.Result.items[0].Value"},"constant":{"literal":42},"empty":{"from":"state.Result.nothing"},"copy":{"from":"state.Result"}}"""
        );
        var result = TypedInputBinder.Resolve(binding, context)!;
        Assert.True(result["flag"]!.GetValue<bool>());
        Assert.Equal(42, result["constant"]!.GetValue<int>());
        Assert.True(result.AsObject().ContainsKey("empty"));
        result["copy"]!["items"]![0]!["Value"] = false;
        Assert.True(context.State["Result"]!["items"]![0]!["Value"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("state.Result.missing")]
    [InlineData("state.result.items")]
    [InlineData("state.Result.items[9]")]
    [InlineData("state..Result")]
    public void MissingAndMalformedRequiredBindingsFail(string path)
    {
        var context = new BindingContext { State = JsonNode.Parse("""{"Result":{"items":[true]}}""")!.AsObject() };
        Assert.Throws<InvalidOperationException>(() =>
            TypedInputBinder.Resolve(new JsonObject { ["from"] = path }, context)
        );
    }

    [Theory]
    [InlineData("version: 1", "version: 2")]
    [InlineData("version: 1", "version: !!int 1")]
    [InlineData("{from: inputs}", "&input {from: inputs}")]
    [InlineData("{type: boolean}", "{type: boolean, type: string}")]
    [InlineData("state.Result.Decision == false", "state.Result.Decision == 'false'")]
    [InlineData("scripts/prepare.ps1", "../outside.ps1")]
    [InlineData("maxValidationRetries: 1", "maxValidationRetries: 2")]
    [InlineData("input: {from: inputs}", "input: {Value: {literal: 'true'}}")]
    public void UnsupportedYamlAndMismatchedLiteralsFail(string before, string after)
    {
        Assert.Throws<WorkflowValidationException>(() => Read(Definition.Replace(before, after)).ToDefinition());
    }

    [Fact]
    public void TypedLiteralBooleanPreservesItsJsonType()
    {
        var workflow = Read(Definition.Replace("input: {from: inputs}", "input: {Value: {literal: true}}"));
        var definition = workflow.ToDefinition();
        var task = definition.Nodes.OfType<ProceduralNode>().First().TaskList![0];
        Assert.True(TypedInputBinder.Resolve(task.Input, new BindingContext())!["Value"]!.GetValue<bool>());
    }

    [Fact]
    public void WholeObjectBindingRejectsKnownAdditionalProperties()
    {
        var workflow = Read(Definition);
        workflow.Types!["Narrow"] = workflow.Types["Input"]!.DeepClone();
        workflow.Types["Input"]!["properties"]!["Extra"] = new JsonObject { ["type"] = "string" };
        workflow = workflow with
        {
            Steps = [workflow.Steps[0] with { InputType = "Narrow" }, .. workflow.Steps.Skip(1)],
        };
        Assert.Throws<WorkflowValidationException>(() => workflow.ToDefinition());
    }

    [Fact]
    public void NamedNestedReferencesAreResolvedForBindings()
    {
        var yaml = Definition
            .Replace("Decision: {type: boolean}", "Decision: {$ref: '#/$defs/Flag'}")
            .Replace("types:", "types:\n  Flag: {type: boolean}");
        Assert.NotNull(Read(yaml).ToDefinition().InputSchema);
    }

    [Fact]
    public void ReusingSaveAsReadsThePreviousTypedValue()
    {
        var yaml = Definition
            .Replace("saveAs: Assessment", "saveAs: Result")
            .Replace("state.Assessment.Decision", "state.Result.Decision");
        Assert.Equal(
            2,
            Read(yaml)
                .ToDefinition()
                .Nodes.OfType<ProceduralNode>()
                .Count(p => p.TaskList![0].Writes!.To == "state.Result")
        );
    }

    [Fact]
    public void LegacyValidatorStillRejectsScriptDelegates()
    {
        var definition = Read(Definition).ToDefinition() with { StrictContracts = false };
        Assert.False(new WorkflowValidator().Validate(definition).IsValid);
    }

    [Fact]
    public void LegacyProseRemainsUnchanged()
    {
        var definition = SimpleWorkflow
            .Deserialize(
                """{"objective":"legacy","steps":[{"id":"route","kind":"branch","branches":[{"when":"the review looks good","goto":"done"}],"else":"done"},{"id":"done","kind":"end"}]}"""
            )
            .ToDefinition();
        Assert.Equal(
            "the review looks good",
            definition.Nodes.OfType<ConditionalNode>().Single().Branches[0].When!.GetValue<string>()
        );
    }
}
