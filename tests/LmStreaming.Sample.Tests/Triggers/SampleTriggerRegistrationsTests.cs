using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

namespace LmStreaming.Sample.Tests.Triggers;

public class SampleTriggerRegistrationsTests
{
    [Fact]
    public void Build_ReturnsTriggerOptions_WithNoDuplicateKinds()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        var kinds = options.AdditionalRegistrations.Select(r => r.Kind).ToList();
        kinds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_OmitsSubAgentKind_WhenAccessorNotSupplied()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: false);
        options.AdditionalRegistrations.Select(r => r.Kind)
            .Should().NotContain(SubAgentCompletionTriggerSource.KindName);
    }

    [Fact]
    public void Build_IncludesSubAgentKind_WhenAccessorSupplied()
    {
        var options = SampleTriggerRegistrations.Build(
            sandboxEnabled: false,
            subAgentManagerAccessor: () => null);
        options.AdditionalRegistrations.Select(r => r.Kind)
            .Should().Contain(SubAgentCompletionTriggerSource.KindName);
    }
}
