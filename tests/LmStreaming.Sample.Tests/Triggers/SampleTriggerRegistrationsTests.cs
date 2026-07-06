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
}
