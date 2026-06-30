using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Routes a <c>context_file</c> discovery from the gateway webhook into every live agent thread
/// bound to the same sandbox session: looks up the thread set, dedups gateway retries, and
/// enqueues a user-role <see cref="TextMessage"/> carrying the formatted file body plus a
/// <c>context_discovery</c> metadata key the chat UI uses to render a pill.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is logged and swallowed — context discovery is a best-effort enrichment, never
/// a blocking precondition. The controller always returns 200 to the gateway for an
/// authenticated payload regardless of what happens here, so a transient thread teardown / mode
/// switch in flight cannot translate into a webhook retry storm.
/// </para>
/// <para>
/// Dedup is per <c>(sessionId, kind, path)</c>: redelivery of the same discovery is dropped for
/// the session as a whole, so multi-threaded sessions only inject the file once (on first
/// delivery, fanned out across every registered thread).
/// </para>
/// </remarks>
public sealed class ContextDiscoveryInjector
{
    /// <summary>
    /// Metadata key set on the injected <see cref="TextMessage"/>. The chat UI looks this key up
    /// under <see cref="TextMessage.Metadata"/> to render a "Context loaded" pill — the value is
    /// the metadata object the renderer reads (path, host_path, truncated).
    /// </summary>
    public const string MetadataKey = "context_discovery";

    private readonly SandboxSessionRegistry _registry;
    private readonly MultiTurnAgentPool _pool;
    private readonly ContextDiscoveryFormatter _formatter;
    private readonly ILogger<ContextDiscoveryInjector> _logger;

    public ContextDiscoveryInjector(
        SandboxSessionRegistry registry,
        MultiTurnAgentPool pool,
        ContextDiscoveryFormatter formatter,
        ILogger<ContextDiscoveryInjector> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Best-effort: enqueues the formatted file body into every agent thread that the session is
    /// currently routing. Returns the number of threads the message was actually sent to (0 when
    /// no thread is live, the discovery is a duplicate, or the session is unknown).
    /// </summary>
    public async Task<int> InjectAsync(ContextDiscoveryPayload body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.SessionId))
        {
            _logger.LogInformation("ContextDiscovery context_file: payload missing session_id; dropping.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrEmpty(body.Content))
        {
            // Controller validation rejects null content + missing path upstream; defence-in-depth
            // here keeps the injector safe to call directly from tests / other code paths and also
            // covers the empty-string body case (which validation lets through but the model
            // can't make use of).
            _logger.LogInformation(
                "ContextDiscovery context_file: payload missing path or has empty content; dropping for session {SessionId}.",
                body.SessionId);
            return 0;
        }

        if (!_registry.TryMarkDiscoverySeen(body.SessionId, body.Kind!, body.Path!))
        {
            _logger.LogDebug(
                "ContextDiscovery context_file: duplicate delivery for {Path} in session {SessionId}; dropping.",
                body.Path,
                body.SessionId);
            return 0;
        }

        var threads = _registry.GetThreads(body.SessionId);
        if (threads.Count == 0)
        {
            // No live agent thread for this session yet. Discovery arrived between session
            // creation and the agent path being wired; nothing to inject. The dedup mark above
            // means a redelivery after the thread is up also won't re-inject — by design, so
            // the model isn't surprised by a now-stale file delivery. A later turn that needs
            // the file content can still read it directly via the file-system tool.
            _logger.LogInformation(
                "ContextDiscovery context_file: no live thread for session {SessionId}; nothing to inject.",
                body.SessionId);
            return 0;
        }

        var truncated = body.Truncated ?? false;
        var text = _formatter.BuildInjectedMessage(body.Path!, body.Content!, truncated);
        var metadata = BuildMetadata(body.Path!, truncated);

        var injected = 0;
        foreach (var threadId in threads)
        {
            if (!_pool.TryGet(threadId, out var agent) || agent is null)
            {
                // Thread torn down between GetThreads and now — best-effort skip; the pool's
                // ThreadRemoved notifier is what keeps the registry's thread set fresh.
                continue;
            }

            try
            {
                var message = new TextMessage
                {
                    Role = Role.User,
                    Text = text,
                    Metadata = metadata,
                };

                _ = await agent.SendAsync([message], inputId: null, parentRunId: null, ct).ConfigureAwait(false);
                injected++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ContextDiscovery context_file: SendAsync threw for thread {ThreadId}; continuing with other threads.",
                    threadId);
            }
        }

        _logger.LogInformation(
            "ContextDiscovery context_file: injected {Path} into {Count}/{Total} threads of session {SessionId}.",
            body.Path,
            injected,
            threads.Count,
            body.SessionId);

        return injected;
    }

    private static ImmutableDictionary<string, object> BuildMetadata(string path, bool truncated)
    {
        // The UI reads this key off TextMessage.Metadata; ShadowPropertiesJsonConverter flattens
        // it to a top-level field on the serialised message so the Vue client sees it as
        // `message.context_discovery` without any extra middleware.
        var contextDiscovery = ImmutableDictionary<string, object>.Empty
            .Add("path", path)
            .Add("truncated", truncated);

        return ImmutableDictionary<string, object>.Empty.Add(MetadataKey, contextDiscovery);
    }
}
