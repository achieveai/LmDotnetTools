using System.Text;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Sets <c>CODEX_HOME</c> to a fresh temp directory containing an API-key <c>auth.json</c>
/// for the duration of the test. Without this, a developer's cached <c>~/.codex/auth.json</c>
/// (typically ChatGPT-account auth) takes precedence over the <c>OPENAI_BASE_URL</c> /
/// <c>OPENAI_API_KEY</c> env-var bridge and the CLI silently bypasses the mock host.
///
/// Disposes by deleting the temp directory and restoring the previous <c>CODEX_HOME</c> value
/// so concurrent / sequential tests do not leak state.
/// </summary>
internal sealed class IsolatedCodexHome : IDisposable
{
    private const string CodexHomeEnvVar = "CODEX_HOME";

    private readonly string _previousValue;
    private readonly string _directory;
    private bool _disposed;

    public IsolatedCodexHome(string apiKey)
    {
        _previousValue = Environment.GetEnvironmentVariable(CodexHomeEnvVar) ?? string.Empty;
        _directory = Path.Combine(Path.GetTempPath(), "lmdotnet-codex-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        // Minimal API-key auth.json — forces Codex into apikey mode, ignoring any persisted
        // ChatGPT tokens. The actual key is irrelevant; the mock host accepts any value.
        var authJson = $$"""
            {
              "auth_mode": "apikey",
              "OPENAI_API_KEY": "{{apiKey}}",
              "tokens": null
            }
            """;
        File.WriteAllText(Path.Combine(_directory, "auth.json"), authJson, new UTF8Encoding(false));

        Environment.SetEnvironmentVariable(CodexHomeEnvVar, _directory);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Environment.SetEnvironmentVariable(
            CodexHomeEnvVar,
            string.IsNullOrEmpty(_previousValue) ? null : _previousValue);

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }
}
