using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace LmWorkflow.Tests;

/// <summary>Loads the shipped workspace package, rather than a separate demonstration definition.</summary>
public sealed class WorkflowPackageTests
{
    [Fact]
    public void An_authored_model_is_carried_to_the_host_invocation_contract()
    {
        var yaml = File.ReadAllText(Path.Combine(PackageDirectory(), "workflow.yaml"))
            .Replace(
                "skills: [skills/grade.md]",
                "skills: [skills/grade.md]\n    model: independent-model",
                StringComparison.Ordinal
            );
        var definition = SimpleWorkflow.DeserializeYaml(yaml).ToDefinition();
        definition
            .Nodes.OfType<ProceduralNode>()
            .Single(node => node.Id == "grade")
            .TaskList!.Single()
            .ModelId.Should()
            .Be("independent-model");
    }

    [Theory]
    [InlineData("new_head", "prepare-review,review,grade,publish,retain-review")]
    [InlineData("discussion", "prepare-discussion,discussion,retain-discussion")]
    [InlineData(
        "merged",
        "prepare-history,learnings,process-judge,additional-extraction,knowledge-safety-review,collect-statistics,retain-merged,close-artifact-branch"
    )]
    public void Shipped_routes_follow_the_declared_operations(string route, string expected)
    {
        var definition = Load();
        var context = new BindingContext { Inputs = new JsonObject { ["Route"] = route } };
        var branch = definition.Nodes.OfType<ConditionalNode>().Single();
        var current =
            branch
                .Branches.FirstOrDefault(item =>
                    ConditionEvaluator.Evaluate(
                        item.StructuredCondition!,
                        context,
                        ConditionEvaluationPolicy.StrictWorkflow
                    )
                )
                ?.To
            ?? branch.Else;
        var visited = new List<string>();
        while (definition.Nodes.Single(node => node.Id == current) is ProceduralNode step)
        {
            visited.Add(step.Id);
            current = step.Next.Single();
            visited.Count.Should().BeLessThan(30, "the production routes have no unbounded cycle");
        }
        visited.Should().Equal(expected.Split(','));
        current.Should().Be("complete");
    }

    [Fact]
    public void Publication_continues_reviewer_parent_and_all_declared_assets_exist()
    {
        var definition = Load();
        var tasks = definition
            .Nodes.OfType<ProceduralNode>()
            .ToDictionary(node => node.Id, node => node.TaskList!.Single());
        tasks["publish"].Session.Should().Be(tasks["review"].Session);
        tasks["discussion"].Session.Should().Be(tasks["review"].Session);
        tasks["discussion"].Tools.Should().Equal("review_reply");
        tasks["grade"].Session.Should().NotBe(tasks["review"].Session);
        tasks
            .Where(pair => pair.Value.Tools?.Count > 0)
            .Select(pair => pair.Key)
            .Should()
            .BeEquivalentTo(["publish", "discussion"]);
        tasks["publish"]
            .Tools.Should()
            .BeEquivalentTo(["review_publish_summary", "review_publish_inline", "review_reply"]);
        foreach (var task in tasks.Values)
        {
            task.InputSchema.Should().NotBeNull();
            task.OutputSchema.Should().NotBeNull();
            foreach (var asset in (task.Skills ?? []).Concat(task.Script is { } script ? [script] : []))
            {
                File.Exists(Path.Combine(PackageDirectory(), asset))
                    .Should()
                    .BeTrue($"declared asset {asset} must ship");
            }
        }
    }

    [Theory]
    [InlineData("retain-review", 0)]
    [InlineData("retain-discussion", 0)]
    [InlineData("retain-merged", 1)]
    public void Retention_receives_only_explicit_validated_extraction_bindings(string stepId, int expectedGroups)
    {
        var task = Load().Nodes.OfType<ProceduralNode>().Single(node => node.Id == stepId).TaskList!.Single();
        var admission = new JsonObject
        {
            ["Route"] = "merged",
            ["PrId"] = "7",
            ["HeadSha"] = "head",
            ["WindowId"] = "window",
        };
        var extraction = new JsonObject { ["Edits"] = new JsonArray(), ["Description"] = "No durable edit" };
        var bound = TypedInputBinder.Resolve(
            task.Input,
            new BindingContext
            {
                Inputs = admission,
                State = new JsonObject
                {
                    ["Review"] = new JsonObject { ["Findings"] = new JsonArray(), ["ReviewText"] = "Draft" },
                    ["Grade"] = new JsonObject { ["Assessments"] = new JsonArray(), ["Description"] = "Grade" },
                    ["Publication"] = new JsonObject
                    {
                        ["Outcome"] = "no_op",
                        ["Description"] = "None",
                        ["Actions"] = new JsonArray(),
                    },
                    ["KnowledgeSafety"] = new JsonObject
                    {
                        ["Verdict"] = "approved",
                        ["Reviewed"] = extraction.DeepClone(),
                        ["Description"] = "Reviewed",
                    },
                    ["Learnings"] = extraction.DeepClone(),
                    ["AdditionalExtraction"] = extraction.DeepClone(),
                },
            }
        );
        JsonNode.DeepEquals(bound!["Admission"], admission).Should().BeTrue();
        bound["Extractions"]!.AsArray().Should().HaveCount(expectedGroups);
    }

    private static WorkflowDefinition Load() =>
        SimpleWorkflow
            .DeserializeYaml(File.ReadAllText(Path.Combine(PackageDirectory(), "workflow.yaml")))
            .ToDefinition();

    private static string PackageDirectory([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../samples/CodeReviewDaemon.Sample/.review")
        );
}
