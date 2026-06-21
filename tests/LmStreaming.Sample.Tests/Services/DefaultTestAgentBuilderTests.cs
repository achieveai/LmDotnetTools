using AchieveAi.LmDotnetTools.LmCore.Agents;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the test-mode sub-agent contract for <see cref="DefaultTestAgentBuilder"/>. The default
/// builder must hand the parent loop a non-null <c>SubAgentOptions</c> carrying the built-in
/// template catalog so the test providers (<c>test</c> / <c>test-anthropic</c>) register the
/// <c>Agent</c> sub-agent tool — the same middleware the real middleware providers get. Without
/// this, <c>tools_list</c> (a test-only directive) can never surface the <c>Agent</c> tool.
/// </summary>
public class DefaultTestAgentBuilderTests
{
    private static readonly Func<IStreamingAgent> SentinelFactory = () => null!;

    [Fact]
    public void CreateSubAgentOptions_ExposesBuiltInTemplates()
    {
        var builder = new DefaultTestAgentBuilder();

        var options = builder.CreateSubAgentOptions(NullLoggerFactory.Instance, SentinelFactory);

        options.Should().NotBeNull("test providers must expose the Agent sub-agent tool");
        options!.MaxConcurrentSubAgents.Should().BeGreaterThan(0);
        options.Templates.Should().ContainKey("general-purpose");
        options.Templates.Should().ContainKey("researcher");
    }

    [Fact]
    public void CreateSubAgentOptions_TemplatesReuseProvidedProviderFactory()
    {
        var builder = new DefaultTestAgentBuilder();

        var options = builder.CreateSubAgentOptions(NullLoggerFactory.Instance, SentinelFactory);

        options.Should().NotBeNull();
        foreach (var template in options!.Templates.Values)
        {
            template.AgentFactory.Should().BeSameAs(SentinelFactory, "spawned test sub-agents must reuse the parent's test transport");
        }
    }
}
