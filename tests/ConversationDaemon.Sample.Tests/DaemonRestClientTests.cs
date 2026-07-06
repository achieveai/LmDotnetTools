using System.Net.Sockets;

namespace ConversationDaemon.Sample.Tests;

/// <summary>
/// AC7 — the daemon's own connection-refused handling. <see cref="DaemonRestClient"/> must surface a
/// clean, actionable <see cref="DaemonConnectionException"/> (carrying
/// <see cref="DaemonMessages.ConnectionRefused(string)"/>) when the server is not listening, and must
/// leave genuine HTTP-level failures unwrapped so they are not mistaken for "server not running".
/// </summary>
public sealed class DaemonRestClientTests
{
    private const string BaseUrl = "http://localhost:5000";

    // HttpClient normalizes BaseAddress to include a trailing slash; the daemon reads BaseAddress.ToString()
    // verbatim, so the message it builds names the URL in exactly this form.
    private const string NormalizedBaseUrl = "http://localhost:5000/";

    [Fact]
    public void ConnectionRefused_message_names_the_base_url_and_how_to_start_the_server()
    {
        var message = DaemonMessages.ConnectionRefused(BaseUrl);

        message.Should().Contain(BaseUrl, "the operator needs to know which endpoint was unreachable");
        message.Should().Contain("LmStreaming.Sample");
        message.Should().Contain("Start it first");
        message.Should().Contain("dotnet run");
    }

    [Fact]
    public async Task ProvisionAsync_wraps_an_HttpRequestException_with_a_socket_inner_as_DaemonConnectionException()
    {
        var client = ClientThatThrows(
            () => new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused)));

        var act = () => client.ProvisionAsync("ws", "provider", "mode", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<DaemonConnectionException>();
        thrown.Which.Message.Should().Be(DaemonMessages.ConnectionRefused(NormalizedBaseUrl));
    }

    [Fact]
    public async Task ProvisionAsync_wraps_a_bare_SocketException_as_DaemonConnectionException()
    {
        var client = ClientThatThrows(() => new SocketException((int)SocketError.ConnectionRefused));

        var act = () => client.ProvisionAsync("ws", "provider", "mode", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<DaemonConnectionException>();
        thrown.Which.Message.Should().Be(DaemonMessages.ConnectionRefused(NormalizedBaseUrl));
        thrown.Which.InnerException.Should().BeOfType<SocketException>();
    }

    [Fact]
    public async Task SendMessageAsync_wraps_connection_refused_the_same_way()
    {
        // A request method other than Provision routes through the same transport guard.
        var client = ClientThatThrows(() => new SocketException((int)SocketError.ConnectionRefused));

        var act = () => client.SendMessageAsync("thread-1", "hello", CancellationToken.None);

        await act.Should().ThrowAsync<DaemonConnectionException>();
    }

    [Fact]
    public async Task ProvisionAsync_does_not_wrap_an_HttpRequestException_without_a_socket_inner()
    {
        // A plain HTTP-layer failure (no SocketException inside) is NOT a "server not running" condition,
        // so it must propagate untouched rather than be relabeled as a connection-refused.
        var client = ClientThatThrows(() => new HttpRequestException("transient 502"));

        var act = () => client.ProvisionAsync("ws", "provider", "mode", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Should().NotBeOfType<DaemonConnectionException>();
    }

    private static DaemonRestClient ClientThatThrows(Func<Exception> exceptionFactory)
    {
        var handler = new FakeHttpMessageHandler().OnAnyThrow(exceptionFactory);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        return new DaemonRestClient(httpClient);
    }
}
