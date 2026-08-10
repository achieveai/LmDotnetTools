using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;

/// <summary>
///     The single definition of how these tests launch the real <c>McpSampleServer</c> over stdio.
/// </summary>
/// <remarks>
///     <para>
///         There were two constructions of this one rule and they drifted, which is the whole reason this
///         type exists. <c>McpServerTests.ServerLocation</c> hardcoded a <c>.exe</c> suffix, so the four
///         tests reaching it died on Linux with <c>Win32Exception: No such file or directory</c> before any
///         MCP traffic — while <c>McpClientFunctionProviderPrefixTests</c> kept a private, platform-aware
///         copy and passed. The divergence was even documented in a comment rather than fixed, so the
///         codebase knew about it and still failed on it.
///     </para>
///     <para>
///         Launching via the muxer (<c>dotnet Server.dll</c>) rather than the apphost is deliberate: it has
///         strictly fewer preconditions. The managed <c>.dll</c> is the build output of any .NET project and
///         is always present, whereas the apphost is conditional on <c>UseAppHost</c> (unset today, so
///         defaulting to true) AND on getting the per-platform extension right — which is precisely the
///         precondition that failed. Neither of those can quietly stop being true here.
///     </para>
/// </remarks>
internal static class McpSampleServerProcess
{
    private const string ServerAssemblyName = "AchieveAi.LmDotnetTools.McpSampleServer.dll";

    /// <summary>
    ///     Transport options pointing at the sample server, ready to hand to <see cref="StdioClientTransport" />.
    /// </summary>
    public static StdioClientTransportOptions TransportOptions(string name = "test-server") =>
        new()
        {
            Name = name,
            Command = DotnetMuxer,
            Arguments = [ServerAssemblyPath],
        };

    /// <summary>
    ///     The sample server assembly, copied next to the test assembly by its ProjectReference.
    /// </summary>
    private static string ServerAssemblyPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, ServerAssemblyName);
            if (!File.Exists(path))
            {
                // Fail with the reason rather than letting the process spawn throw a bare Win32Exception,
                // which is what made the original defect read as an environment problem instead of a
                // missing build output.
                throw new FileNotFoundException(
                    $"The MCP sample server assembly was not found at '{path}'. It is copied there by the "
                        + "ProjectReference to McpSampleServer; a missing file means the reference was "
                        + "dropped or the test assembly was run from an unexpected directory.",
                    path);
            }

            return path;
        }
    }

    /// <summary>
    ///     The dotnet muxer. The SDK sets <c>DOTNET_HOST_PATH</c> for test hosts it launches, which is exact;
    ///     the bare name is a fallback for runs where it is absent and PATH resolution has to do.
    /// </summary>
    private static string DotnetMuxer =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } muxer && File.Exists(muxer)
            ? muxer
            : "dotnet";
}
