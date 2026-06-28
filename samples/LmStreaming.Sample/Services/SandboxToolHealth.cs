using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Classifies a sandbox MCP tool result as a "container unhealthy" infrastructure failure and
/// rewrites it into a single, actionable message.
/// </summary>
/// <remarks>
/// <para>
/// When the sandbox gateway runs on a Docker backend whose container has no <c>sandbox</c> user in
/// <c>/etc/passwd</c>, the gateway's <c>docker exec -u sandbox …</c> fails for EVERY tool call with a
/// Docker exec 400 — surfaced through the shared MCP middleware as
/// <c>"Error executing MCP tool &lt;name&gt;: … no matching entries in passwd file"</c> (or
/// <c>"Timed out waiting for 'sandbox' user in container …"</c>). This is an environment/container
/// defect (owned by the gateway image, e.g. <c>SandboxedOstoolsMcpServer</c>), NOT something a retry
/// can fix.
/// </para>
/// <para>
/// Left untouched, the model treats each failure as a transient tool error and retries the same call
/// hundreds of times, ballooning the conversation and burning tokens. <see cref="Wrap"/> intercepts
/// the sandbox tool handlers (sample-local, only the middleware-provider path where the app executes
/// the tool) and collapses this error class into one clear message that tells the model to stop and
/// the user how to recover. It does not — and cannot — fix the root cause; that belongs in the gateway
/// image / its container entrypoint (provision the <c>sandbox</c> user) or by running the gateway on
/// the local (non-Docker) backend.
/// </para>
/// </remarks>
internal static class SandboxToolHealth
{
    /// <summary>Error code stamped on the rewritten tool result (lets providers flag it as an error).</summary>
    internal const string UnhealthyErrorCode = "sandbox_container_unhealthy";

    /// <summary>
    /// Single, actionable message substituted for the raw gateway exec error. Phrased to (a) stop the
    /// model retrying and (b) tell the user exactly how to recover.
    /// </summary>
    internal const string UnhealthyMessage =
        "The sandbox workspace is unavailable: the gateway's container is unhealthy — its 'sandbox' "
        + "user account is missing (the container's /etc/passwd has no 'sandbox' entry), so no file or "
        + "shell tools can run. This is an infrastructure failure that retrying will NOT fix. Stop "
        + "calling sandbox tools and tell the user to restart the sandbox gateway/container (or run it "
        + "on the local backend) to recover.";

    /// <summary>
    /// Signatures the gateway emits when the container lacks the <c>sandbox</c> user. Matched
    /// case-insensitively against the tool result text. Kept narrow so only this specific
    /// container-provisioning failure is rewritten — ordinary tool errors pass through untouched.
    /// </summary>
    private static readonly string[] UnhealthySignatures =
    [
        "no matching entries in passwd file",
        "waiting for 'sandbox' user",
        "unable to find user sandbox",
    ];

    /// <summary>
    /// True when <paramref name="toolResultText"/> carries one of the known
    /// "container has no sandbox user" gateway signatures.
    /// </summary>
    internal static bool IsContainerUnhealthy(string? toolResultText)
    {
        if (string.IsNullOrEmpty(toolResultText))
        {
            return false;
        }

        foreach (var signature in UnhealthySignatures)
        {
            if (toolResultText.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decorates a sandbox tool handler so that a result matching the "container unhealthy" signature
    /// is replaced with the single actionable <see cref="UnhealthyMessage"/> (flagged as an error).
    /// Healthy results — and deferrals — pass through unchanged.
    /// </summary>
    internal static ToolHandler Wrap(ToolHandler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return async (argsJson, context, cancellationToken) =>
        {
            var result = await inner(argsJson, context, cancellationToken).ConfigureAwait(false);

            return result is ToolHandlerResult.Resolved resolved
                && IsContainerUnhealthy(resolved.Payload.Text)
                ? ToolHandlerResult.FromError(UnhealthyMessage, UnhealthyErrorCode)
                : result;
        };
    }
}
