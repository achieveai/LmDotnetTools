using System.Text.Json;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class DaemonProfileConfigurationTests
{
    [Fact]
    public void Mcqdb_listener_and_auth_webhook_use_the_same_origin()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateProfile("appsettings.mcqdb.json")));
        var root = document.RootElement;

        var listener = new Uri(root.GetProperty("Urls").GetString()!);
        var webhook = new Uri(
            root.GetProperty("Auth").GetProperty("Webhook").GetProperty("PublicBaseUrl").GetString()!
        );

        listener
            .GetLeftPart(UriPartial.Authority)
            .Should()
            .Be(
                webhook.GetLeftPart(UriPartial.Authority),
                "sandbox auth callbacks must return to the same daemon that minted each per-session secret"
            );
        listener.Port.Should().Be(5082, "the GitHub daemon owns 5081 and cannot validate MCQdb session secrets");
    }

    /// <summary>
    /// Walks up from the test binary until it finds the daemon's profile, rather than counting a fixed
    /// number of <c>..</c> segments out of <see cref="AppContext.BaseDirectory"/>. The depth is not a
    /// constant: this repo's own convention redirects <c>BaseOutputPath</c> to <c>.logs/tb/bin/</c> to
    /// dodge locked DLLs, which adds two levels and made the fixed walk resolve to a path under the TEST
    /// project. That surfaced as a DirectoryNotFoundException, which reads like a missing profile — a
    /// config regression — when the profile is present and only the arithmetic was wrong.
    /// </summary>
    private static string LocateProfile(string fileName)
    {
        var copiedProfile = Path.Combine(AppContext.BaseDirectory, "ShippedProfiles", fileName);
        if (File.Exists(copiedProfile))
        {
            return copiedProfile;
        }

        var relative = Path.Combine("samples", "CodeReviewDaemon.Sample", fileName);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {relative} in any ancestor of {AppContext.BaseDirectory}.",
            relative
        );
    }
}
