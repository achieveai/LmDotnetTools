namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

/// <summary>
///     Tries a sequence of <see cref="ICopilotTokenProvider"/>s in order and returns the first token
///     that resolves, caching it in memory for the provider's lifetime. The default sequence reuses
///     existing CLI credentials first, then falls back to the device flow.
/// </summary>
public sealed class CompositeCopilotTokenProvider : ICopilotTokenProvider
{
    private readonly IReadOnlyList<ICopilotTokenProvider> _providers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;

    /// <summary>Creates a composite over the supplied providers (evaluated in order).</summary>
    public CompositeCopilotTokenProvider(params ICopilotTokenProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Length == 0)
        {
            throw new ArgumentException("At least one token provider is required.", nameof(providers));
        }

        _providers = providers;
    }

    /// <summary>
    ///     Builds the default chain: existing CLI/env credentials first, then GitHub device flow.
    /// </summary>
    public static CompositeCopilotTokenProvider CreateDefault(DeviceFlowCopilotTokenProvider? deviceFlow = null)
    {
        return new CompositeCopilotTokenProvider(
            new CliCredentialCopilotTokenProvider(),
            deviceFlow ?? new DeviceFlowCopilotTokenProvider()
        );
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken is not null)
        {
            return _cachedToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null)
            {
                return _cachedToken;
            }

            var failures = new List<Exception>();
            foreach (var provider in _providers)
            {
                try
                {
                    var token = await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _cachedToken = token;
                        return token;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Preserve every failure — the first provider's error is often the most
                    // informative, so don't let a later one overwrite it.
                    failures.Add(ex);
                }
            }

            throw new InvalidOperationException(
                "No GitHub Copilot token could be resolved from any configured provider.",
                failures.Count > 0 ? new AggregateException(failures) : null
            );
        }
        finally
        {
            _ = _gate.Release();
        }
    }
}
