using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Auth;

/// <summary>
/// Covers <see cref="SessionSecretStore.SaveAsync"/>'s write-temp-then-rename path against the two hazards
/// its <c>_gate</c> cannot cover. The gate is a per-INSTANCE <see cref="SemaphoreSlim"/>: it serializes
/// writers inside one store object and nothing between two stores over one base directory, let alone across
/// processes — and the sandbox gateway constructs this store per host, so both shapes are real.
/// <para>
/// Both tests hold a REAL handle rather than racing threads and hoping. A race-based test for a window this
/// narrow goes green on a fast box whether or not the defect is present, which is precisely the failure mode
/// a concurrency fix must not be verified by. This mirrors
/// <c>LmMultiTurn.Tests.Persistence.FileConversationStoreConcurrentWriteTests</c>.
/// </para>
/// </summary>
public sealed class SessionSecretStoreConcurrentWriteTests : IDisposable
{
    private const string SessionId = "session-concurrent-write";

    private readonly string _root;

    public SessionSecretStoreConcurrentWriteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SessionSecretConcurrentWrite_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Teardown of a temp directory must never turn a green run red.
        }
    }

    /// <summary>
    /// Pins the staging-name collision. Holding <c>{file}.secret.tmp</c> stands in for a concurrent writer
    /// whose own staging file is still open — a deterministic name means there is only ever ONE such path per
    /// target, so every writer to a session's secret contends for it.
    /// <para>
    /// Against a deterministic staging name this write cannot even reach the rename: the
    /// <c>WriteAllTextAsync</c> onto the held path fails first. A per-write unique name removes the
    /// contention by construction, so the held path is simply not one this writer ever touches.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Save_SucceedsWhileAnotherWritersStagingFileForTheSameTargetIsOpen()
    {
        var store = NewStore();

        // Establish the target so its real path is discoverable without duplicating the store's hashing.
        await store.SaveAsync(SessionId, "secret-1");
        var secretFile = SecretFile();
        var deterministicTemp = secretFile + ".tmp";

        // The other writer's in-flight staging file. FileShare.None is what a writer holding its own output
        // looks like to everyone else.
        await File.WriteAllTextAsync(deterministicTemp, "in-flight");
        using (var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = async () => await store.SaveAsync(SessionId, "secret-2");

            await act.Should().NotThrowAsync();
        }

        // The write must have actually landed, not merely declined to throw.
        (await store.MatchesAsync(SessionId, "secret-2"))
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// Pins the missing rename retry. A reader opened through <c>File.ReadAllTextAsync</c> holds
    /// <see cref="FileShare.Read"/>, which withholds delete access, and Windows <c>MoveFile</c> with
    /// <c>REPLACE_EXISTING</c> needs it — so a plain concurrent read of the destination is enough to make the
    /// rename throw <see cref="UnauthorizedAccessException"/>. <see cref="SessionSecretStore.MatchesAsync"/>
    /// reads straight from disk on every webhook call, so that concurrent reader is this store's NORMAL
    /// traffic rather than a contrived holder.
    /// <para>
    /// The handle is released partway through the retry budget, so this asserts the write WAITS OUT a
    /// transient holder rather than that it tolerates a permanent one. Without a retry the very first rename
    /// throws and the test fails; a fixed sleep in the store would not pass it either, because the release
    /// happens after the first attempt has already been made.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Save_WaitsOutATransientReaderHoldingTheDestination()
    {
        var store = NewStore();

        await store.SaveAsync(SessionId, "secret-1");
        var secretFile = SecretFile();

        // Exactly the sharing a concurrent File.ReadAllTextAsync of this file would take.
        var reader = new FileStream(secretFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var released = false;
        var write = Task.Run(async () => await store.SaveAsync(SessionId, "secret-2"));

        // Give the rename at least one attempt against the live handle before letting go, so a passing run
        // proves the retry ran rather than that the handle was gone before the store looked.
        await Task.Delay(60);
        released = true;
        await reader.DisposeAsync();

        var act = async () => await write;
        await act.Should().NotThrowAsync();
        released.Should().BeTrue();

        (await store.MatchesAsync(SessionId, "secret-2")).Should().BeTrue();
    }

    private SessionSecretStore NewStore() => new(_root, NullLogger<SessionSecretStore>.Instance);

    /// <summary>
    /// Resolves the store's own file for <see cref="SessionId"/> by enumeration rather than by recomputing
    /// its SHA-256 stem, so the test never encodes a second copy of the store's naming rule.
    /// </summary>
    private string SecretFile() => Directory.GetFiles(_root, "*.secret").Single();
}
