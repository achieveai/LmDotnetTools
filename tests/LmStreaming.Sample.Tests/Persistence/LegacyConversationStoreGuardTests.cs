namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// The SQLite host must not start silently over conversations the former file store left behind, since
/// nothing imports them; an empty or absent legacy directory, or an operator acknowledgement, lets it start.
/// </summary>
public sealed class LegacyConversationStoreGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"legacy-store-guard-{Guid.NewGuid():N}");

    private string LegacyDirectory => Path.Combine(_root, "conversations");

    [Fact]
    public void A_legacy_thread_directory_refuses_startup_and_names_both_ways_forward()
    {
        _ = Directory.CreateDirectory(Path.Combine(LegacyDirectory, "thread-1"));

        var start = () =>
            LegacyConversationStoreGuard.EnsureNoUnimportedStore(LegacyDirectory, "conversations.db", false);

        start
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"*{LegacyDirectory}*conversations.legacy*{LegacyConversationStoreGuard.AcknowledgeKey}=true*"
            );
    }

    [Fact]
    public void An_acknowledged_legacy_directory_starts()
    {
        _ = Directory.CreateDirectory(Path.Combine(LegacyDirectory, "thread-1"));

        var start = () =>
            LegacyConversationStoreGuard.EnsureNoUnimportedStore(LegacyDirectory, "conversations.db", true);

        start.Should().NotThrow();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_absent_or_threadless_legacy_directory_starts(bool createEmpty)
    {
        if (createEmpty)
        {
            _ = Directory.CreateDirectory(LegacyDirectory);
            File.WriteAllText(Path.Combine(LegacyDirectory, "stray.txt"), "not a thread");
        }

        var start = () =>
            LegacyConversationStoreGuard.EnsureNoUnimportedStore(LegacyDirectory, "conversations.db", false);

        start.Should().NotThrow();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
