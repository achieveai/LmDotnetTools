using CodeReviewDaemon.Sample.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class DaemonPromptCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"daemon-prompt-catalog-{Guid.NewGuid():N}");

    [Fact]
    public void CaptureSnapshot_reloads_a_valid_file_without_mutating_an_existing_snapshot()
    {
        var path = WriteCatalog("first prompt");
        var catalog = new DaemonPromptCatalog(path, NullLogger<DaemonPromptCatalog>.Instance);

        var first = catalog.CaptureSnapshot();
        _ = WriteCatalog("second prompt");
        var second = catalog.CaptureSnapshot();

        first.GetPrompt("review").Text.Should().Be("first prompt");
        second.GetPrompt("review").Text.Should().Be("second prompt");
        second.GetPrompt("review").Version.Should().Be("v1.0");
        second.GetPrompt("review").ContentHash.Should().NotBe(first.GetPrompt("review").ContentHash);
    }

    [Fact]
    public void CaptureSnapshot_retains_the_last_valid_snapshot_when_an_update_is_invalid()
    {
        var path = WriteCatalog("valid prompt");
        var catalog = new DaemonPromptCatalog(path, NullLogger<DaemonPromptCatalog>.Instance);
        var valid = catalog.CaptureSnapshot();
        File.WriteAllText(path, "not: [valid yaml");

        var retained = catalog.CaptureSnapshot();

        retained.Should().BeSameAs(valid);
        retained.GetPrompt("review").Text.Should().Be("valid prompt");
    }

    [Fact]
    public void CaptureSnapshot_fails_closed_when_the_initial_catalog_is_invalid()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "daemon-prompts.yaml");
        File.WriteAllText(path, "review:\n  v1.0: missing active selection\n");
        var catalog = new DaemonPromptCatalog(path, NullLogger<DaemonPromptCatalog>.Instance);

        var act = catalog.CaptureSnapshot;

        act.Should().Throw<InvalidOperationException>().WithMessage("*active*");
    }

    private string WriteCatalog(string reviewPrompt)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "daemon-prompts.yaml");
        File.WriteAllText(
            path,
            $$"""
            _active:
              review: v1.0
            review:
              v1.0: |
                {{reviewPrompt}}
            """
        );
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
