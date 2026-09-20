using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the published <see cref="SubAgentManager" /> construction shape. LmMultiTurn ships as a NuGet
/// package, and an optional parameter is still part of the CLR constructor signature: appending one
/// keeps source compatibility but breaks an already-compiled consumer with
/// <see cref="MissingMethodException" /> after an assembly upgrade. So new construction-time inputs
/// arrive as init properties (see <see cref="SubAgentManager.ParentPromptCaching" />) rather than as
/// extra constructor parameters, and this test fails the moment the constructor grows one.
/// </summary>
public class SubAgentManagerPublicSurfaceTests
{
    /// <summary>The published constructor's parameter types, in order.</summary>
    private static readonly Type[] PublishedConstructorParameters =
    [
        typeof(IMultiTurnAgent),
        typeof(IReadOnlyList<FunctionContract>),
        typeof(IDictionary<string, ToolHandler>),
        typeof(SubAgentOptions),
        typeof(MutableSubAgentTemplateSource),
        typeof(ILogger),
        typeof(string),
        typeof(int?),
        typeof(IUsageSink),
        typeof(Func<Task>),
        typeof(MultiTurnLifecycleServices),
        typeof(AgentCollaborationSetup),
        typeof(Func<NotifyMessage, CancellationToken, ValueTask>),
    ];

    [Fact]
    public void ThePublishedConstructor_KeepsItsExactBinarySignature()
    {
        var constructors = typeof(SubAgentManager).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Exactly one, so a legacy source call that omits optional arguments cannot become ambiguous.
        constructors.Should().ContainSingle();
        constructors[0].GetParameters().Select(p => p.ParameterType).Should().Equal(PublishedConstructorParameters);
    }

    [Fact]
    public async Task ALegacyFormCall_StillConstructs_AndDefaultsCachingToOff()
    {
        // The five required arguments a pre-caching caller passed, with every optional one omitted.
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            [],
            new Dictionary<string, ToolHandler>(),
            new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() },
            new MutableSubAgentTemplateSource()
        );

        manager.ParentPromptCaching.Should().Be(PromptCachingMode.Off);
    }

    [Fact]
    public async Task TheCachingAwareForm_CarriesTheParentMode()
    {
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            [],
            new Dictionary<string, ToolHandler>(),
            new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() },
            new MutableSubAgentTemplateSource()
        )
        {
            ParentPromptCaching = PromptCachingMode.Auto,
        };

        // The value the manager hands ResolveSubAgentOptions on every spawn, whose inheritance rule
        // SubAgentModelResolutionTests pins.
        manager.ParentPromptCaching.Should().Be(PromptCachingMode.Auto);
    }
}
