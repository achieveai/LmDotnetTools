using System.Reflection;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     Pins issue #153 M1's <c>Program.AddSandboxAuthHeaders</c> helper: every sandbox MCP transport
///     header dictionary must get <c>X-Sbx-App-Id</c> stamped unconditionally, and
///     <c>X-Sbx-App-Key</c> stamped only when <see cref="SandboxCredential.AppKey"/> is non-empty —
///     the keyless <c>AUTH_ENFORCE=off</c> dev path must never send a blank key header.
/// </summary>
public sealed class SandboxAuthHeaderHelperTests
{
    [Fact]
    public void AddSandboxAuthHeaders_WithAppKey_SetsBothHeaders()
    {
        var headers = new Dictionary<string, string> { ["X-Session-ID"] = "sess-1" };
        var cred = new SandboxCredential("lmstreaming-sample", "s3cr3t-app-key");

        Invoke(headers, cred);

        headers.Should().ContainKey("X-Sbx-App-Id").WhoseValue.Should().Be("lmstreaming-sample");
        headers.Should().ContainKey("X-Sbx-App-Key").WhoseValue.Should().Be("s3cr3t-app-key");
        headers.Should().ContainKey("X-Session-ID").WhoseValue.Should().Be("sess-1");
    }

    [Fact]
    public void AddSandboxAuthHeaders_WithEmptyAppKey_OmitsKeyHeader()
    {
        var headers = new Dictionary<string, string>();
        var cred = new SandboxCredential("lmstreaming-sample", string.Empty);

        Invoke(headers, cred);

        headers.Should().ContainKey("X-Sbx-App-Id").WhoseValue.Should().Be("lmstreaming-sample");
        headers.Should().NotContainKey("X-Sbx-App-Key");
    }

    private static void Invoke(Dictionary<string, string> headers, SandboxCredential cred)
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod("AddSandboxAuthHeaders", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("Program must expose the sandbox auth header helper");
        method!.Invoke(null, [headers, cred]);
    }
}
