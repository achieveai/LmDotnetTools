using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The host's record of each conversation's sandboxes, passed through, with every write it receives noted and the
/// ability to hold writes (to act while a release records its retirement) or fail writes or reads. A release route
/// sends the gateway nothing now, so its record write is where a test catches one in flight.
/// </summary>
internal sealed class HeldOwnedSandboxStore(IOwnedSandboxStore inner) : IOwnedSandboxStore
{
    private static readonly TimeSpan S_patience = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<string> _writes = [];
    private readonly SemaphoreSlim _writesStarted = new(0);
    private TaskCompletionSource? _hold;

    /// <summary>When set, every write fails, writing nothing.</summary>
    public volatile bool FailWrites;

    /// <summary>When set, every read fails.</summary>
    public volatile bool FailLoads;

    /// <summary>The thread of every write that reached the store, held, failed or done, in order.</summary>
    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    /// <summary>Installs this over the host's own store; read it back from the host's services.</summary>
    public static void Install(IServiceCollection services)
    {
        _ = services.AddSingleton(sp => new HeldOwnedSandboxStore(sp.GetRequiredService<ConversationOwnedSandboxes>()));
        _ = services.AddSingleton<IOwnedSandboxStore>(sp => sp.GetRequiredService<HeldOwnedSandboxStore>());
    }

    /// <summary>Holds every write from now on until the returned gate is completed.</summary>
    public TaskCompletionSource HoldWrites()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only writes from now on count: earlier ones (a create's own record) must not satisfy the next wait.
        while (_writesStarted.Wait(0)) { }

        Volatile.Write(ref _hold, gate);
        return gate;
    }

    /// <summary>Waits until one more write has reached the store (held or not).</summary>
    public async Task WaitForWriteStartedAsync() =>
        (await _writesStarted.WaitAsync(S_patience)).Should().BeTrue("a sandbox record write was expected");

    public Task<IReadOnlyList<OwnedSandboxRecord>> LoadAsync(string threadId, CancellationToken ct = default) =>
        FailLoads
            ? Task.FromException<IReadOnlyList<OwnedSandboxRecord>>(new IOException("The sandbox record store failed."))
            : inner.LoadAsync(threadId, ct);

    public async Task UpdateAsync(
        string threadId,
        Func<IReadOnlyList<OwnedSandboxRecord>, IReadOnlyList<OwnedSandboxRecord>> update,
        CancellationToken ct = default
    )
    {
        lock (_gate)
        {
            _writes.Add(threadId);
        }

        _ = _writesStarted.Release();
        if (Volatile.Read(ref _hold) is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        if (FailWrites)
        {
            throw new IOException("The sandbox record store failed.");
        }

        await inner.UpdateAsync(threadId, update, ct);
    }
}
