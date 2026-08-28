using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class AgentOutputTokenPolicyTests
{
    private static AgentOutputTokenPolicy Policy(int primary = 24_576, int delegated = 16_384)
    {
        var options = new AgentOutputTokenOptions { Primary = primary, Delegated = delegated };
        options.Validate().Succeeded.Should().BeTrue();
        return new AgentOutputTokenPolicy(options);
    }

    private static SubAgentTemplate Template(GenerateReplyOptions? defaultOptions)
    {
        return new SubAgentTemplate
        {
            SystemPrompt = "Test prompt",
            AgentFactory = () => throw new NotImplementedException(),
            DefaultOptions = defaultOptions,
        };
    }

    [Fact]
    public void ApplyPrimary_UsesConfiguredPrimaryWhenUnset()
    {
        var policy = Policy(primary: 30_000, delegated: 18_000);

        policy.ApplyPrimary(new GenerateReplyOptions()).MaxToken.Should().Be(30_000);
    }

    [Fact]
    public void ApplyPrimary_UsesDelegatedFallback_WhenRequested()
    {
        Policy().ApplyPrimary(new GenerateReplyOptions(), useDelegatedFallback: true).MaxToken.Should().Be(16_384);
    }

    [Fact]
    public void ApplyPrimary_PreservesExplicitValue()
    {
        var policy = Policy(primary: 30_000, delegated: 18_000);

        policy.ApplyPrimary(new GenerateReplyOptions { MaxToken = 4_096 }).MaxToken.Should().Be(4_096);
    }

    [Fact]
    public void ApplyDelegatedOptions_UsesConfiguredDelegatedWhenUnset()
    {
        Policy().ApplyDelegated((GenerateReplyOptions?)null).MaxToken.Should().Be(16_384);
    }

    [Fact]
    public void ApplyDelegatedTemplate_FillsMissingBudget()
    {
        var template = Template(defaultOptions: null);

        var result = Policy().ApplyDelegated(template);

        result.DefaultOptions!.MaxToken.Should().Be(16_384);
    }

    [Fact]
    public void ApplyDelegatedTemplates_FillsOnlyMissingBudgets()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["unset"] = Template(defaultOptions: null),
                ["explicit"] = Template(defaultOptions: new GenerateReplyOptions { MaxToken = 7_000 }),
            },
        };

        var result = Policy().ApplyDelegated(options);

        result.Templates["unset"].DefaultOptions!.MaxToken.Should().Be(16_384);
        result.Templates["explicit"].DefaultOptions!.MaxToken.Should().Be(7_000);
    }

    [Fact]
    public void ApplyDelegatedTemplates_PreservesAllOtherTemplateFields()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test"] = Template(defaultOptions: null) with
                {
                    Name = "TestName",
                    Description = "Test description",
                    WhenToUse = "When needed",
                    MaxTurnsPerRun = 25,
                },
            },
        };

        var result = Policy().ApplyDelegated(options);

        result.Templates["test"].Name.Should().Be("TestName");
        result.Templates["test"].Description.Should().Be("Test description");
        result.Templates["test"].WhenToUse.Should().Be("When needed");
        result.Templates["test"].MaxTurnsPerRun.Should().Be(25);
    }

    [Fact]
    public void ApplyDelegatedTemplates_PreservesAllOtherSubAgentOptionsFields()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test"] = Template(defaultOptions: null) },
            MaxConcurrentSubAgents = 3,
            MaxQueuedSubAgents = 50,
        };

        var result = Policy().ApplyDelegated(options);

        result.MaxConcurrentSubAgents.Should().Be(3);
        result.MaxQueuedSubAgents.Should().Be(50);
    }
}
