using System.Net;
using System.Net.Sockets;
using System.Text;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Isolation pins for the eval host launcher: readiness can never bless a process that is not our
/// child (F-001), and the child never inherits the parent's <c>LMSTREAMING_ENV_FILE</c> (F-002).
/// </summary>
public class EvalHostProcessTests
{
    // ── F-001: occupied port must hard-fail, never report ready ─────────────────────────────────

    [Fact]
    public async Task StartAsync_PortAlreadyOccupied_FailsInsteadOfBlessingTheForeignListener()
    {
        // A stub that answers 200 to EVERY request — including GET /api/providers — stands in for
        // a live LmStreaming deployment already listening on the configured port.
        var (stub, port) = StartStub200Server();
        var scratch = Path.Combine(Path.GetTempPath(), $"todo-eval-f001-{Guid.NewGuid():N}");
        var publishDir = Path.Combine(scratch, "publish");
        Directory.CreateDirectory(publishDir);
        // Launch plumbing only ever checks this file exists; it must never become a live host here.
        await File.WriteAllTextAsync(Path.Combine(publishDir, "LmStreaming.Sample.dll"), "not a real assembly");

        try
        {
            var config = new HostConfig
            {
                PublishDir = publishDir,
                Port = port,
                ReadinessTimeoutSeconds = 10,
                ShutdownGraceSeconds = 0,
            };

            var act = () =>
                EvalHostProcess.StartAsync(
                    config,
                    scratch,
                    Path.Combine(scratch, "instance"),
                    Path.Combine(scratch, "logs"),
                    TextWriter.Null,
                    CancellationToken.None
                );

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already in use*");
        }
        finally
        {
            stub.Stop();
            TryDelete(scratch);
        }
    }

    [Fact]
    public void EnsurePortIsFree_OccupiedPort_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var act = () => EvalHostProcess.EnsurePortIsFree(port);
            act.Should().Throw<InvalidOperationException>().WithMessage("*already in use*");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void EnsurePortIsFree_FreePort_DoesNotThrow()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var act = () => EvalHostProcess.EnsurePortIsFree(port);
        act.Should().NotThrow();
    }

    // ── F-002: LMSTREAMING_ENV_FILE must not leak from the parent into the child ────────────────

    [Fact]
    public void BuildStartInfo_NoConfiguredEnvFile_DoesNotInheritTheParentsEnvFileVariable()
    {
        var original = Environment.GetEnvironmentVariable("LMSTREAMING_ENV_FILE");
        Environment.SetEnvironmentVariable(
            "LMSTREAMING_ENV_FILE",
            Path.Combine(Path.GetTempPath(), "live-deployment.env")
        );
        try
        {
            var startInfo = EvalHostProcess.BuildStartInfo(
                new HostConfig(),
                Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "LmStreaming.Sample.dll"),
                54321
            );

            startInfo
                .Environment.ContainsKey("LMSTREAMING_ENV_FILE")
                .Should()
                .BeFalse("an 'isolated' host must never read the live deployment's .env by inheritance");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LMSTREAMING_ENV_FILE", original);
        }
    }

    [Fact]
    public void BuildStartInfo_ConfiguredEnvFile_IsHandedToTheChildAsAFullPath()
    {
        var envFile = Path.Combine(Path.GetTempPath(), "eval.env");
        var startInfo = EvalHostProcess.BuildStartInfo(
            new HostConfig { EnvFile = envFile },
            Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "LmStreaming.Sample.dll"),
            54321
        );

        startInfo.Environment["LMSTREAMING_ENV_FILE"].Should().Be(Path.GetFullPath(envFile));
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Raw TCP server answering HTTP 200 with a tiny JSON body to whatever connects.</summary>
    private static (TcpListener Listener, int Port) StartStub200Server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var socket = await listener.AcceptSocketAsync();
                    _ = Task.Run(async () =>
                    {
                        using (socket)
                        await using (var stream = new NetworkStream(socket, ownsSocket: false))
                        {
                            var buffer = new byte[4096];
                            _ = await stream.ReadAsync(buffer);
                            var response =
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                                + "Content-Length: 2\r\nConnection: close\r\n\r\n{}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                        }
                    });
                }
            }
            catch (SocketException)
            {
                // Listener stopped — normal end of the stub.
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed — normal end of the stub.
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup under %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup under %TEMP%.
        }
    }
}
