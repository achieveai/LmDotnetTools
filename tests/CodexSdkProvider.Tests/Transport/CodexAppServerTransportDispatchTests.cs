using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Transport;
using AchieveAi.LmDotnetTools.Testing.Transport;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Transport;

/// <summary>
/// Pins that a request the app-server makes of us cannot stall the loop that reads its output.
/// </summary>
/// <remarks>
/// This matters because a tool call can now be held at an approval gate until a person answers.
/// Handled on the read loop, that wait would stop the only stream carrying the rest of the turn —
/// including the cancellation that would end the wait — so the run could not fail, only time out.
/// Every assertion below is written to time out rather than pass if the handler runs inline.
/// </remarks>
public class CodexAppServerTransportDispatchTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AParkedRequest_BlocksNeitherTheReadLoopNorALaterRequest()
    {
        using var fake = new FakeCliProcess();
        await using var transport = new CodexAppServerTransport(
            new CodexSdkOptions
            {
                CodexCliPath = "codex-cli-mock",
                ProcessLauncher = fake.Launcher,
            });

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
                    if (method == "tool/parked")
                    {
                        _ = parkedEntered.TrySetResult();
                        await parked.Task;
                        _ = parkedFinished.TrySetResult();
                    }

                    return JsonDocument.Parse($$"""{"answered":"{{method}}"}""").RootElement.Clone();
                },
                notificationHandler: (method, _) => notified.TrySetResult(method),
                ct: CancellationToken.None);

            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tool/parked"}""");
            await parkedEntered.Task.WaitAsync(Generous);

            // The reader is now past the parked request. Inline handling would never get here.
            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","method":"session/notice"}""");
            Assert.Equal("session/notice", await notified.Task.WaitAsync(Generous));

            // And a second request is not merely read but answered while the first still waits,
            // which is what the JSON-RPC id is for.
            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tool/prompt"}""");
            var second = await fake.ReadResponseAsync(Generous);
            Assert.Equal(2, second.Id);
            Assert.Equal("tool/prompt", second.Result.GetProperty("answered").GetString());
            Assert.False(
                parkedFinished.Task.IsCompleted,
                "the first handler had already returned, so nothing was proven");

            // Releasing the approval answers the first request, out of arrival order.
            _ = parked.TrySetResult();
            var first = await fake.ReadResponseAsync(Generous);
            Assert.Equal(1, first.Id);
            Assert.Equal("tool/parked", first.Result.GetProperty("answered").GetString());

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
        await using var transport = new CodexAppServerTransport(
            new CodexSdkOptions
            {
                CodexCliPath = "codex-cli-mock",
                ProcessLauncher = fake.Launcher,
            });

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
                ct: CancellationToken.None);

            await fake.WriteStdoutLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tool/never"}""");
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
