using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the contract of <see cref="BuiltInSubAgentTemplates"/> — the shared catalog consumed by
/// both the production path and the test-provider path. Covers the argument guard that the
/// per-call factory must be supplied.
/// </summary>
public class BuiltInSubAgentTemplatesTests
{
    [Fact]
    public void Create_NullProviderFactory_Throws()
    {
        var act = () => BuiltInSubAgentTemplates.Create(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("providerAgentFactory");
    }
}
