using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The host's record of each conversation's sandboxes, passed through, with every write it receives noted and the
/// ability to hold writes (to act while a release records its retirement) or reads (to act while a conversation
/// delete reads its records), or fail either. A release route
/// sends the gateway nothing now, so its record write is where a test catches one in flight.
/// </summary>
internal sealed class HeldOwnedSandboxStore(IOwnedSandboxStore inner) : IOwnedSandboxStore
{
    private static readonly TimeSpan S_patience = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<string> _writes = [];
    private readonly SemaphoreSlim _writesStarted = new(0);
    private readonly SemaphoreSlim _loadsStarted = new(0);
    private TaskCompletionSource? _hold;
    private TaskCompletionSource? _loadHold;

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

    /// <summary>Holds every read from now on until the returned gate is completed; the read then sees the store as
    /// it is at that moment.</summary>
    public TaskCompletionSource HoldLoads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (_loadsStarted.Wait(0)) { }

        Volatile.Write(ref _loadHold, gate);
        return gate;
    }

    /// <summary>Waits until one more read has reached the store (held or not).</summary>
    public async Task WaitForLoadStartedAsync() =>
        (await _loadsStarted.WaitAsync(S_patience)).Should().BeTrue("a sandbox record read was expected");

    public async Task<IReadOnlyList<OwnedSandboxRecord>> LoadAsync(string threadId, CancellationToken ct = default)
    {
        _ = _loadsStarted.Release();
        if (Volatile.Read(ref _loadHold) is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return FailLoads
            ? throw new IOException("The sandbox record store failed.")
            : await inner.LoadAsync(threadId, ct);
    }

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
