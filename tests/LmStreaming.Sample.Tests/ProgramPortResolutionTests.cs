using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace LmStreaming.Sample.Tests;

public class ProgramPortResolutionTests
{
    [Fact]
    public void ResolveCodexMcpPort_FallsBack_WhenConfiguredPortIsBusy()
    {
        var previousPort = Environment.GetEnvironmentVariable("CODEX_MCP_PORT");
        try
        {
            using var busyListener = new TcpListener(IPAddress.Loopback, 0);
            busyListener.Start();
            var busyPort = ((IPEndPoint)busyListener.LocalEndpoint).Port;
            Environment.SetEnvironmentVariable("CODEX_MCP_PORT", busyPort.ToString());

            var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
            programType.Should().NotBeNull();
            var method = programType!.GetMethod(
                "ResolveCodexMcpPort",
                BindingFlags.NonPublic | BindingFlags.Static);

            method.Should().NotBeNull();
            var resolved = (int)(method!.Invoke(null, null) ?? 0);

            resolved.Should().NotBe(busyPort);
            resolved.Should().BeGreaterThan(0);
            resolved.Should().BeLessThanOrEqualTo(65535);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_MCP_PORT", previousPort);
        }
    }
}
