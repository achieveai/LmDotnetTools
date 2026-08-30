using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The config contract for #609: both digest audiences ship ON (digests are debounced,
///     net-zero-suppressed information, not budgeted nudges), and a missing/empty/malformed section
///     reads as the defaults — never a throw. The binder is not used precisely because these tests
///     must hold for garbage input.
/// </summary>
public class TodoDigestOptionsTests
{
    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] pairs)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build();
    }

    [Fact]
    public void Defaults_BothAudiencesAreOn()
    {
        var options = new TodoDigestOptions();

        options.PrimaryDigestEnabled.Should().BeTrue("the root always hears is the point of #609");
        options.AssigneeDigestEnabled.Should().BeTrue();
        options.AnyDigestEnabled.Should().BeTrue();
    }

    [Fact]
    public void NullConfiguration_ReadsAsDefaults()
    {
        TodoDigestOptions.FromConfiguration(null).Should().Be(new TodoDigestOptions());
    }

    [Fact]
    public void MissingSection_ReadsAsDefaults()
    {
        var configuration = BuildConfiguration(("Unrelated:Key", "value"));

        TodoDigestOptions.FromConfiguration(configuration).Should().Be(new TodoDigestOptions());
    }

    [Fact]
    public void EmptySection_ReadsAsDefaults()
    {
        // The empty-JSON-array binder trap: `"TodoDigests": []` produces a section that EXISTS but
        // holds nothing. Existence must not change a single default.
        var configuration = BuildConfiguration(("TodoDigests", null));

        TodoDigestOptions.FromConfiguration(configuration).Should().Be(new TodoDigestOptions());
    }

    [Fact]
    public void MalformedValues_ReadAsDefaults_NotAsThrows()
    {
        var configuration = BuildConfiguration(
            ("TodoDigests:PrimaryDigestEnabled", "yes please"),
            ("TodoDigests:AssigneeDigestEnabled", "42")
        );

        TodoDigestOptions.FromConfiguration(configuration).Should().Be(new TodoDigestOptions());
    }

    [Fact]
    public void ExplicitValues_AreRead()
    {
        var configuration = BuildConfiguration(
            ("TodoDigests:PrimaryDigestEnabled", "false"),
            ("TodoDigests:AssigneeDigestEnabled", "false")
        );

        var options = TodoDigestOptions.FromConfiguration(configuration);

        options.PrimaryDigestEnabled.Should().BeFalse();
        options.AssigneeDigestEnabled.Should().BeFalse();
        options.AnyDigestEnabled.Should().BeFalse("both audiences off means the service is not built at all");
    }
}
