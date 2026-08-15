namespace LmStreaming.Sample.Tests.Models;

public class PluginRefTests
{
    [Fact]
    public void RoundTrips_ThroughJson_WithCamelCaseFields()
    {
        var pluginRef = new PluginRef("official", "code-review");

        var json = JsonSerializer.Serialize(pluginRef, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.Should().Contain("\"marketplace\":\"official\"");
        json.Should().Contain("\"plugin\":\"code-review\"");

        var roundTripped = JsonSerializer.Deserialize<PluginRef>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        roundTripped.Should().Be(pluginRef);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new PluginRef("official", "code-review");
        var b = new PluginRef("official", "code-review");

        a.Should().Be(b);
    }
}
