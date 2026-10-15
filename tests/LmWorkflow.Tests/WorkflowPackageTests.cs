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
    [InlineData("new_head", "prepare-independent,review,grade,final-review,publish,retain-review")]
    [InlineData("discussion", "prepare-discussion,discussion,retain-discussion")]
    [InlineData(
        "merged",
        "prepare-history,merged-self-assessment,learnings,merged-judgment,collect-statistics,retain-merged,close-artifact-branch"
    )]
    [InlineData(
        "abandoned",
        "prepare-history,merged-self-assessment,learnings,merged-judgment,collect-statistics,retain-merged"
    )]
    public void Shipped_routes_follow_the_declared_operations(string route, string expected)
    {
        var definition = Load();
        var context = new BindingContext
        {
            Inputs = new JsonObject { ["Route"] = route },
            State = new JsonObject
            {
                ["PreparedReview"] = new JsonObject { ["PublicationMode"] = "post" },
                ["PreparedHistory"] = new JsonObject
                {
                    ["TerminalState"] = route == "abandoned" ? "Abandoned" : "Merged",
                },
            },
        };
        var current = "route";
        var visited = new List<string>();
        var transitions = 0;
        while (definition.Nodes.Single(node => node.Id == current) is not TerminalNode)
        {
            var node = definition.Nodes.Single(node => node.Id == current);
            if (node is ConditionalNode branch)
                current =
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
            else
            {
                var step = (ProceduralNode)node;
                visited.Add(step.Id);
                current = step.Next.Single();
            }
            (++transitions).Should().BeLessThan(40, "the production routes have no unbounded cycle");
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
        tasks["publish"].Delegate.Should().Be(DelegateKind.Script);
        tasks["publish"].Script.Should().Be("scripts/publish-review.py");
        tasks["final-review"].Session.Should().Be(tasks["review"].Session);
        tasks["discussion"].Session.Should().Be(tasks["review"].Session);
        tasks["discussion"].Tools.Should().Equal("review_reply");
        tasks["grade"].Session.Should().NotBe(tasks["review"].Session);
        tasks
            .Where(pair => pair.Value.Tools?.Count > 0)
            .Select(pair => pair.Key)
            .Should()
            .BeEquivalentTo(["discussion"]);
        tasks["publish"].Tools.Should().BeNullOrEmpty();
        tasks["final-review"].Tools.Should().BeNullOrEmpty();
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
                    ["FinalReview"] = new JsonObject { ["Format"] = "markdown", ["Markdown"] = "Final report" },
                    ["Publication"] = new JsonObject
                    {
                        ["Outcome"] = "no_op",
                        ["Description"] = "None",
                        ["Actions"] = new JsonArray(),
                    },
                    ["MergedJudgment"] = new JsonObject
                    {
                        ["Verdict"] = "approved",
                        ["Reviewed"] = extraction.DeepClone(),
                        ["Description"] = "Reviewed",
                    },
                    ["Learnings"] = extraction.DeepClone(),
                    ["MergedSelfAssessment"] = new JsonObject { ["Format"] = "markdown", ["Markdown"] = "Assessment" },
                },
            }
        );
        JsonNode.DeepEquals(bound!["Admission"], admission).Should().BeTrue();
        bound["Extractions"]!.AsArray().Should().HaveCount(expectedGroups);
        if (expectedGroups > 0)
        {
            JsonNode.DeepEquals(bound["Extractions"]![0], extraction).Should().BeTrue();
            JsonNode.DeepEquals(bound["KnowledgeReview"]!["Reviewed"], extraction).Should().BeTrue();
            bound["Canonical"]!
                .AsObject()
                .Select(pair => pair.Key)
                .Should()
                .BeEquivalentTo("SelfAssessment", "Learnings", "Judgment");
            bound["Canonical"]!["Judgment"]!["Verdict"]!.GetValue<string>().Should().Be("approved");
        }
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
