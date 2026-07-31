using System.Text.Json;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class DaemonProfileConfigurationTests
{
    [Fact]
    public void Mcqdb_listener_and_auth_webhook_use_the_same_origin()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "samples",
            "CodeReviewDaemon.Sample",
            "appsettings.mcqdb.json");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)));
        var root = document.RootElement;

        var listener = new Uri(root.GetProperty("Urls").GetString()!);
        var webhook = new Uri(
            root.GetProperty("Auth").GetProperty("Webhook").GetProperty("PublicBaseUrl").GetString()!);

        listener.GetLeftPart(UriPartial.Authority).Should().Be(
            webhook.GetLeftPart(UriPartial.Authority),
            "sandbox auth callbacks must return to the same daemon that minted each per-session secret");
        listener.Port.Should().Be(5082, "the GitHub daemon owns 5081 and cannot validate MCQdb session secrets");
    }
}
