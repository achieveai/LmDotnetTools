using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;
using AchieveAi.LmDotnetTools.Testing.Transport;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Transport;

/// <summary>
/// Pins that a request the agent makes of us cannot stall the loop that reads its output.
/// </summary>
/// <remarks>
/// The Copilot and Codex transports read stdio JSON-RPC the same way, so they share the same
/// hazard: a tool call held at an approval gate, handled on the read loop, would block the stream
/// carrying the rest of the turn and the cancellation that would end the wait. Pinning it here as
/// well is what stops the two transports drifting apart on the property that matters.
/// </remarks>
public class CopilotAcpTransportDispatchTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AParkedRequest_BlocksNeitherTheReadLoopNorALaterRequest()
    {
        using var fake = new FakeCliProcess();
        await using var transport = new CopilotAcpTransport(
            new CopilotSdkOptions { CopilotCliPath = "copilot-cli-mock", ProcessLauncher = fake.Launcher }
        );

        // Stands in for an approval nobody has answered yet.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var workingDirectory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await transport.StartAsync(
                workingDirectory,
                apiKey: null,
                baseUrl: null,
                requestHandler: async (method, _, _) =>
                {
                    if (method == "session/request_permission")
                    {
                        _ = parkedEntered.TrySetResult();
                        await parked.Task;
                        _ = parkedFinished.TrySetResult();
                    }

                    return JsonDocument.Parse($$"""{"answered":"{{method}}"}""").RootElement.Clone();
                },
                notificationHandler: (method, _) => notified.TrySetResult(method),
                ct: CancellationToken.None
            );

            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":1,"method":"session/request_permission"}""");
            await parkedEntered.Task.WaitAsync(Generous);

            // The reader is now past the parked request. Inline handling would never get here.
            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","method":"session/update"}""");
            Assert.Equal("session/update", await notified.Task.WaitAsync(Generous));

            // And a second request is not merely read but answered while the first still waits,
            // which is what the JSON-RPC id is for.
            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":2,"method":"fs/read_text_file"}""");
            var second = await fake.ReadResponseAsync(Generous);
            Assert.Equal(2, second.Id);
            Assert.Equal("fs/read_text_file", second.Result.GetProperty("answered").GetString());
            Assert.False(
                parkedFinished.Task.IsCompleted,
                "the first handler had already returned, so nothing was proven"
            );

            // Releasing the approval answers the first request, out of arrival order.
            _ = parked.TrySetResult();
            var first = await fake.ReadResponseAsync(Generous);
            Assert.Equal(1, first.Id);
            Assert.Equal("session/request_permission", first.Result.GetProperty("answered").GetString());

            await transport.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Generous);
        }
        finally
        {
            // A failed assertion above must not leave the handler parked: if the request were
            // handled inline it would still own the read loop, and shutdown waits on that loop.
            _ = parked.TrySetResult();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StoppingWhileARequestIsParked_DoesNotHangShutdown()
    {
        using var fake = new FakeCliProcess();
        await using var transport = new CopilotAcpTransport(
            new CopilotSdkOptions { CopilotCliPath = "copilot-cli-mock", ProcessLauncher = fake.Launcher }
        );

        var parkedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workingDirectory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await transport.StartAsync(
                workingDirectory,
                apiKey: null,
                baseUrl: null,
                requestHandler: async (_, _, ct) =>
                {
                    _ = parkedEntered.TrySetResult();
                    try
                    {
                        // An approval nobody ever answers. Only cancellation ends this.
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = handlerCancelled.TrySetResult();
                        throw;
                    }

                    return default;
                },
                notificationHandler: (_, _) => { },
                ct: CancellationToken.None
            );

            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":1,"method":"session/request_permission"}""");
            await parkedEntered.Task.WaitAsync(Generous);

            // Shutdown cancels the handler rather than waiting on an answer that is not coming.
            await transport.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Generous);
            await handlerCancelled.Task.WaitAsync(Generous);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}
