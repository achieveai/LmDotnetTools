using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Binary-compatibility regression coverage for <see cref="MultiTurnAgentLoop"/>'s public constructor.
/// <c>MultiTurnAgentLoop</c> ships in the packable <c>AchieveAi.LmDotnetTools.LmMultiTurn</c> NuGet
/// package: appending required-looking optional parameters to its ONLY constructor changes the CLR
/// constructor metadata token, so an already-compiled consumer of a prior package version can throw
/// <see cref="MissingMethodException"/> at runtime after upgrading — even though the source still
/// compiles fine. These tests pin, via reflection, that:
/// 1. the exact prior constructor shape — the one that shipped on <c>origin/main</c> before this work,
///    ending at <c>AgentCollaborationSetup? collaboration</c> with NO <c>descendantQuestionSink</c>
///    parameter — still exists with the same parameter types, order, and optionality/defaults, and
/// 2. a distinct overload exposes the new <c>includeAskUserQuestionTool</c>/<c>includeNotifyClientTool</c>
///    controls (plus the new <c>descendantQuestionSink</c> parameter) without creating an
///    ambiguous-call situation for existing source callers.
/// </summary>
public class MultiTurnAgentLoopConstructorCompatibilityTests
{
    // The exact parameter type sequence of the constructor released on origin/main before this work —
    // i.e. before #246 added the includeAskUserQuestionTool/includeNotifyClientTool controls and before
    // descendantQuestionSink existed at all. Nullable annotations do not change the runtime Type for
    // reference types, so `string?` and `string` resolve identically here.
    private static readonly Type[] PriorConstructorParameterTypes =
    [
        typeof(IStreamingAgent),
        typeof(FunctionRegistry),
        typeof(string), // threadId
        typeof(string), // systemPrompt
        typeof(GenerateReplyOptions),
        typeof(int), // maxTurnsPerRun
        typeof(int), // inputChannelCapacity
        typeof(int), // outputChannelCapacity
        typeof(IConversationStore),
        typeof(ILogger<MultiTurnAgentLoop>),
        typeof(SubAgentOptions),
        typeof(MutableSubAgentTemplateSource),
        typeof(ILoggerFactory),
        typeof(bool), // persistRunLedger
        typeof(TriggerOptions),
        typeof(IPricingResolver),
        typeof(IUsageSink),
        typeof(MultiTurnLifecycleServices),
        typeof(MultiTurnLifecycleServices),
        typeof(AgentCollaborationSetup),
    ];

    private static ConstructorInfo? FindConstructor(Type[] parameterTypes) =>
        typeof(MultiTurnAgentLoop)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(ctor =>
            {
                var parameters = ctor.GetParameters();
                return parameters.Length == parameterTypes.Length
                    && parameters.Select(p => p.ParameterType).SequenceEqual(parameterTypes);
            });

    [Fact]
    public void PriorPublicConstructor_StillExists_WithSameParameterTypesAndOrder()
    {
        var ctor = FindConstructor(PriorConstructorParameterTypes);

        ctor.Should()
            .NotBeNull(
                "an already-compiled caller of the prior MultiTurnAgentLoop constructor must still bind "
                    + "to a constructor with this exact CLR signature, or it fails with MissingMethodException"
            );
    }

    [Fact]
    public void PriorPublicConstructor_OnlyThreeLeadingParametersAreRequired()
    {
        var ctor = FindConstructor(PriorConstructorParameterTypes);
        ctor.Should().NotBeNull();

        var parameters = ctor!.GetParameters();

        // providerAgent, functionRegistry, threadId remain required; everything else must remain
        // optional so existing short-form call sites (e.g. `new MultiTurnAgentLoop(agent, registry, id)`)
        // keep compiling and keep resolving to this exact overload.
        parameters.Take(3).Should().OnlyContain(p => !p.IsOptional);
        parameters.Skip(3).Should().OnlyContain(p => p.IsOptional);
    }

    [Fact]
    public void PriorPublicConstructor_DefaultsAreUnchanged()
    {
        var ctor = FindConstructor(PriorConstructorParameterTypes);
        ctor.Should().NotBeNull();

        var parameters = ctor!.GetParameters();
        var byName = parameters.ToDictionary(p => p.Name!);

        byName["maxTurnsPerRun"].DefaultValue.Should().Be(50);
        byName["inputChannelCapacity"].DefaultValue.Should().Be(100);
        byName["outputChannelCapacity"].DefaultValue.Should().Be(1000);
        byName["persistRunLedger"].DefaultValue.Should().Be(false);
        byName["systemPrompt"].DefaultValue.Should().BeNull();

        // This overload has no descendantQuestionSink parameter at all — origin/main never had one.
        byName.Should().NotContainKey("descendantQuestionSink");
    }

    [Fact]
    public void ToolControlOverload_Exists_WithRequiredIncludeFlagsAndDescendantQuestionSink_AndNoAmbiguityWithPriorConstructor()
    {
        // The designated overload: same shape as the prior constructor, plus two REQUIRED (no
        // default) bool parameters carrying the new controls, plus the new optional
        // descendantQuestionSink parameter. Because the bool flags have no default value, C# cannot
        // resolve a short-form call (e.g. 3 positional args) to this overload, so it can never collide
        // with the back-compat constructor above — the two are only ambiguous if both are
        // simultaneously applicable, and a required parameter with no supplied argument makes an
        // overload inapplicable, not merely lower-priority.
        var ctors = typeof(MultiTurnAgentLoop).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var withFlags = ctors.SingleOrDefault(ctor =>
        {
            var parameters = ctor.GetParameters();
            return parameters.Length == PriorConstructorParameterTypes.Length + 3
                && parameters.Any(p =>
                    p.Name == "includeAskUserQuestionTool" && p.ParameterType == typeof(bool) && !p.IsOptional
                )
                && parameters.Any(p =>
                    p.Name == "includeNotifyClientTool" && p.ParameterType == typeof(bool) && !p.IsOptional
                )
                && parameters.Any(p =>
                    p.Name == "descendantQuestionSink"
                    && p.ParameterType == typeof(Func<NotifyMessage, CancellationToken, ValueTask>)
                    && p.IsOptional
                    && p.DefaultValue == null
                );
        });

        withFlags
            .Should()
            .NotBeNull(
                "callers that need to control browser-hosted client tool registration or supply a custom "
                    + "descendant-question sink must have a dedicated overload exposing "
                    + "includeAskUserQuestionTool/includeNotifyClientTool/descendantQuestionSink"
            );
    }

    [Fact]
    public void ExactlyTwoPublicConstructorsExist()
    {
        // Guards against a THIRD overload silently reintroducing an ambiguous or redundant shape.
        typeof(MultiTurnAgentLoop).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().HaveCount(2);
    }
}
