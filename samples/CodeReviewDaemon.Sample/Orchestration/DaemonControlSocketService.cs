using System.Net.Sockets;
using System.Text;

namespace CodeReviewDaemon.Sample.Orchestration;

internal sealed class DaemonControlSocketService(
    DaemonAdmissionCoordinator admission,
    IConfiguration configuration,
    ILogger<DaemonControlSocketService> logger
) : BackgroundService
{
    private Socket? _listener;
    private string? _path;

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The secure daemon control socket requires Linux.");
        }

        _path = configuration["CodeReviewDaemon:ControlSocketPath"];
        if (string.IsNullOrWhiteSpace(_path))
        {
            logger.LogWarning("Daemon control socket is disabled because no path is configured.");
            return;
        }

        _path = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.Delete(_path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(4);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var client = await _listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            await HandleAsync(client, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(Socket client, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        switch (command)
        {
            case "activate":
                admission.Activate();
                await writer.WriteLineAsync("ok").ConfigureAwait(false);
                break;
            case "drain":
                await admission.BeginDrainAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync("ok").ConfigureAwait(false);
                break;
            case "status":
                await writer.WriteLineAsync(admission.State.ToString().ToLowerInvariant()).ConfigureAwait(false);
                break;
            default:
                await writer.WriteLineAsync("error:unknown-command").ConfigureAwait(false);
                break;
        }
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        if (_path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the next same-user start replaces the stale socket.
            }
        }
        base.Dispose();
    }
}
