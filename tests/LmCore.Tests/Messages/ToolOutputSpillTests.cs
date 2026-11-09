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
    public async Task The_limit_counts_utf8_bytes_not_characters()
    {
        var snowmen = new string('☃', 1_000);
        var store = new FakeStore();

        var result = await ApplyAsync(Options(store), new ToolCallResult("c9", snowmen));

        store.Saved.Should().ContainSingle().Which.Should().Be(snowmen);
        result.OriginalBytes.Should().Be(3_000);
        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(Inline);
    }

    [Fact]
    public async Task Multibyte_text_of_exactly_the_limit_is_left_alone()
    {
        var exact = new string('☃', 666) + "ab";
        Encoding.UTF8.GetByteCount(exact).Should().Be(Inline);
        var store = new FakeStore();
        var original = new ToolCallResult("c10", exact);

        var result = await ApplyAsync(Options(store), original);

        store.Saved.Should().BeEmpty();
        result.Should().Be(original);
    }

    [Fact]
    public async Task An_oversized_block_is_saved_and_cut_without_touching_a_short_result()
    {
        var block = new string('b', 40_000);
        var store = new FakeStore();

        var result = await ApplyAsync(
            Options(store),
            new ToolCallResult("c11", "short") { ContentBlocks = [new TextToolResultBlock { Text = block }] }
        );

        store.Saved.Should().ContainSingle().Which.Should().Be(block);
        result.Result.Should().Be("short");
        var cut = result.ContentBlocks![0].Should().BeOfType<TextToolResultBlock>().Subject;
        Encoding.UTF8.GetByteCount(cut.Text).Should().BeLessThanOrEqualTo(Inline);
        cut.Text.Should().StartWith("bbb").And.Contain(SavedPath);
    }

    [Fact]
    public async Task Cancelling_ends_a_save_that_is_waiting_on_its_token()
    {
        using var cts = new CancellationTokenSource();
        var store = new WaitingStore();

        var saving = Options(store).ApplyAsync(new ToolCallResult("c12", Big), NullLogger.Instance, cts.Token);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var act = () => saving.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_save_that_never_finishes_counts_as_not_saved_once_its_time_is_up()
    {
        var options = Options(new WaitingStore()) with { SaveTimeout = TimeSpan.FromMilliseconds(20) };

        var result = await ApplyAsync(options, new ToolCallResult("c13", Big)).WaitAsync(TimeSpan.FromSeconds(10));

        result.Result.Should().Contain("could not be saved");
    }

    [Fact]
    public async Task A_saved_path_too_long_for_the_preview_counts_as_not_saved()
    {
        var store = new FakeStore { Path = "/" + new string('☃', 200) };
        var options = new ToolOutputSpillOptions
        {
            Store = store,
            MaxInlineBytes = ToolOutputSpillOptions.MinInlineBytes,
        };
        var json = JsonSerializer.Serialize(Enumerable.Range(0, 2_000).ToArray());

        var result = await ApplyAsync(options, new ToolCallResult("c14", json));

        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(ToolOutputSpillOptions.MinInlineBytes);
        result.Result.Should().Contain("could not be saved").And.NotContain("☃");
        using var document = JsonDocument.Parse(result.Result);
        document.RootElement.TryGetProperty("saved_to", out _).Should().BeFalse();
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

    private sealed class WaitingStore : IToolOutputStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> SaveAsync(string content, CancellationToken ct = default)
        {
            _ = Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return SavedPath;
        }
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
