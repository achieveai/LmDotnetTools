using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The host's <see cref="IChatModeStore"/>, plus a one-shot callback that runs on the next read of a given mode,
/// before that read answers. Lets a test land a change at the exact point a route reads a thread's mode.
/// </summary>
internal sealed class ModeReadHookStore(IChatModeStore inner) : IChatModeStore
{
    private Tuple<string, Func<Task>>? _hook;
    private int _skip;

    /// <summary>Replaces the registered mode store with one that wraps it.</summary>
    public static ModeReadHookStore Install(IServiceCollection services)
    {
        var registered =
            services
                .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IChatModeStore))
                ?.ImplementationInstance as IChatModeStore
            ?? throw new InvalidOperationException("The host registers no IChatModeStore instance to wrap.");
        var hooked = new ModeReadHookStore(registered);
        services.RemoveAll<IChatModeStore>();
        services.AddSingleton<IChatModeStore>(hooked);
        return hooked;
    }

    /// <summary>
    /// Runs <paramref name="callback"/> once, on a read of <paramref name="modeId"/>: the next one, or the one after
    /// <paramref name="skip"/> more.
    /// </summary>
    public void BeforeNextRead(string modeId, Func<Task> callback, int skip = 0)
    {
        Volatile.Write(ref _skip, skip);
        Volatile.Write(ref _hook, Tuple.Create(modeId, callback));
    }

    public async Task<ChatMode?> GetModeAsync(string modeId, CancellationToken ct = default)
    {
        var hook = Volatile.Read(ref _hook);
        if (
            hook is not null
            && string.Equals(hook.Item1, modeId, StringComparison.Ordinal)
            && Interlocked.Decrement(ref _skip) < 0
            && ReferenceEquals(Interlocked.CompareExchange(ref _hook, null, hook), hook)
        )
        {
            await hook.Item2();
        }

        return await inner.GetModeAsync(modeId, ct);
    }

    public Task<IReadOnlyList<ChatMode>> GetAllModesAsync(CancellationToken ct = default) => inner.GetAllModesAsync(ct);

    public Task<ChatMode> CreateModeAsync(ChatModeCreateUpdate mode, CancellationToken ct = default) =>
        inner.CreateModeAsync(mode, ct);

    public Task<ChatMode> UpdateModeAsync(string modeId, ChatModeCreateUpdate mode, CancellationToken ct = default) =>
        inner.UpdateModeAsync(modeId, mode, ct);

    public Task DeleteModeAsync(string modeId, CancellationToken ct = default) => inner.DeleteModeAsync(modeId, ct);

    public Task<ChatMode> CopyModeAsync(string modeId, string newName, CancellationToken ct = default) =>
        inner.CopyModeAsync(modeId, newName, ct);
}
