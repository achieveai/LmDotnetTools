using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Oversized tool output is saved and replaced by a preview the model can act on (#930 item 2).
/// </summary>
public sealed class ToolOutputSpillTests
{
    private const string SavedPath = "/workspace/.tool-output/abc/output.txt";
    private const int Inline = 2_000;

    private static string Big => new('x', 50_000);

    private static ToolOutputSpillOptions Options(IToolOutputStore store) =>
        new() { Store = store, MaxInlineBytes = Inline };

    private static Task<ToolCallResult> ApplyAsync(ToolOutputSpillOptions options, ToolCallResult result) =>
        options.ApplyAsync(result, NullLogger.Instance);

    [Fact]
    public async Task Oversized_text_is_saved_whole_and_the_model_gets_a_preview_and_the_path()
    {
        var store = new FakeStore();

        var result = await ApplyAsync(Options(store), new ToolCallResult("c1", Big) { ToolName = "WebFetch" });

        store.Saved.Should().ContainSingle().Which.Should().Be(Big);
        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(Inline);
        result.Result.Should().StartWith("xxx");
        result.Result.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
        result.Result.Should().Contain(SavedPath).And.Contain("Use Read with offset and limit");
        result.IsTruncated.Should().BeTrue();
        result.OriginalBytes.Should().Be(50_000);
    }

    [Fact]
    public async Task Saved_json_stays_valid_json_and_names_the_file()
    {
        var original = JsonSerializer.Serialize(Enumerable.Range(0, 20_000).ToArray());

        var result = await ApplyAsync(Options(new FakeStore()), new ToolCallResult("c2", original));

        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(Inline);
        using var document = JsonDocument.Parse(result.Result);
        document.RootElement.GetProperty("saved_to").GetString().Should().Be(SavedPath);
        document.RootElement.GetProperty("note").GetString().Should().Contain("Use Read with offset and limit");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_save_still_gives_a_preview_and_says_it_was_not_saved(bool throws)
    {
        var store = new FakeStore { Throws = throws, ReturnsNull = !throws };

        var result = await ApplyAsync(Options(store), new ToolCallResult("c3", Big));

        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(Inline);
        result.Result.Should().StartWith("xxx").And.Contain("could not be saved");
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task An_over_long_path_counts_as_not_saved()
    {
        var store = new FakeStore { Path = "/" + new string('p', ToolOutputSpillOptions.MaxSavedPathLength) };

        var result = await ApplyAsync(Options(store), new ToolCallResult("c4", Big));

        result.Result.Should().Contain("could not be saved").And.NotContain("ppp");
    }

    [Fact]
    public async Task Cancellation_during_a_save_is_not_mistaken_for_a_failed_save()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new FakeStore { ThrowCancelled = true };

        var act = () => Options(store).ApplyAsync(new ToolCallResult("c5", Big), NullLogger.Instance, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Bash")]
    public async Task The_sandboxs_own_tools_are_never_saved(string toolName)
    {
        var store = new FakeStore();
        var original = new ToolCallResult("c6", Big) { ToolName = toolName };

        var result = await ApplyAsync(Options(store), original);

        store.Saved.Should().BeEmpty();
        result.Should().Be(original);
    }

    [Fact]
    public async Task Text_at_the_limit_is_left_alone()
    {
        var store = new FakeStore();
        var original = new ToolCallResult("c7", new string('x', Inline));

        var result = await ApplyAsync(Options(store), original);

        store.Saved.Should().BeEmpty();
        result.Should().Be(original);
    }

    [Fact]
    public async Task The_same_text_in_the_result_and_a_block_is_saved_once()
    {
        var store = new FakeStore();
        var image = new ImageToolResultBlock { Data = "AAAA", MimeType = "image/png" };

        var result = await ApplyAsync(
            Options(store),
            new ToolCallResult("c8", Big, [new TextToolResultBlock { Text = Big }, image])
        );

        store.Saved.Should().ContainSingle();
        var block = result.ContentBlocks![0].Should().BeOfType<TextToolResultBlock>().Subject;
        block.Text.Should().Contain(SavedPath);
        result.ContentBlocks[1].Should().BeSameAs(image);
    }

    [Fact]
    public void An_inline_limit_too_small_for_the_notice_is_rejected()
    {
        var act = () =>
            new ToolOutputSpillOptions
            {
                Store = new FakeStore(),
                MaxInlineBytes = ToolOutputSpillOptions.MinInlineBytes - 1,
            };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class FakeStore : IToolOutputStore
    {
        public List<string> Saved { get; } = [];

        public bool Throws { get; init; }

        public bool ThrowCancelled { get; init; }

        public bool ReturnsNull { get; init; }

        public string Path { get; init; } = SavedPath;

        public Task<string?> SaveAsync(string content, CancellationToken ct = default)
        {
            if (ThrowCancelled)
            {
                throw new OperationCanceledException(ct);
            }

            if (Throws)
            {
                throw new IOException("gateway down");
            }

            if (ReturnsNull)
            {
                return Task.FromResult<string?>(null);
            }

            Saved.Add(content);
            return Task.FromResult<string?>(Path);
        }
    }
}
