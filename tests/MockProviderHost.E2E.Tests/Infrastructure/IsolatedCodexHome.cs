using System.Globalization;
using System.Text;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Sets <c>CODEX_HOME</c> to a fresh temp directory containing an API-key <c>auth.json</c>
/// and (when a mock <c>baseUrl</c> is supplied) a <c>config.toml</c> that declares a
/// <c>mock</c> model provider pointing at the test's mock host. Without this isolation:
/// <list type="bullet">
///   <item><description>A developer's cached <c>~/.codex/auth.json</c> (typically ChatGPT-account auth)
///         takes precedence over the env-var bridge and the CLI silently bypasses the mock host.</description></item>
///   <item><description>Codex CLI 0.125+ uses a hardcoded <c>wss://api.openai.com/v1/responses</c> WebSocket
///         endpoint for the default <c>openai</c> provider and ignores <c>OPENAI_BASE_URL</c>.
///         Declaring a custom <c>model_providers.mock</c> entry with <c>wire_api = "chat"</c> and
///         setting <c>model_provider = "mock"</c> redirects the CLI to plain HTTP
///         <c>/v1/chat/completions</c>, which is what the mock host already serves.</description></item>
/// </list>
/// Disposes by deleting the temp directory and restoring the previous <c>CODEX_HOME</c> value
/// so concurrent / sequential tests do not leak state.
/// </summary>
internal sealed class IsolatedCodexHome : IDisposable
{
    private const string CodexHomeEnvVar = "CODEX_HOME";

    private readonly string _previousValue;
    private readonly string _directory;
    private bool _disposed;

    /// <summary>
    /// Initialises an isolated Codex home with API-key auth and (optionally) a mock model
    /// provider pointing at the supplied base URL. The base URL should already include the
    /// <c>/v1</c> suffix (matching the OpenAI Chat Completions wire shape the mock host serves).
    /// </summary>
    public IsolatedCodexHome(string apiKey, string? mockBaseUrl = null)
    {
        _previousValue = Environment.GetEnvironmentVariable(CodexHomeEnvVar) ?? string.Empty;
        _directory = Path.Combine(Path.GetTempPath(), "lmdotnet-codex-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        // Minimal API-key auth.json — forces Codex into apikey mode, ignoring any persisted
        // ChatGPT tokens. The actual key is irrelevant; the mock host accepts any value.
        var authJson = $$"""
            {
              "auth_mode": "apikey",
              "OPENAI_API_KEY": "{{JsonStringInner(apiKey)}}",
              "tokens": null
            }
            """;
        File.WriteAllText(Path.Combine(_directory, "auth.json"), authJson, new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(mockBaseUrl))
        {
            var configToml = BuildMockProviderConfigToml(mockBaseUrl);
            File.WriteAllText(Path.Combine(_directory, "config.toml"), configToml, new UTF8Encoding(false));
        }

        Environment.SetEnvironmentVariable(CodexHomeEnvVar, _directory);
    }

    /// <summary>
    /// Path to the isolated Codex home directory. Exposed so tests can assert on the files we
    /// wrote (e.g. <c>config.toml</c>) without hard-coding the layout.
    /// </summary>
    public string HomePath => _directory;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Environment.SetEnvironmentVariable(
            CodexHomeEnvVar,
            string.IsNullOrEmpty(_previousValue) ? null : _previousValue
        );

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup. Surface a Debug trace so a leaked-temp diagnosis isn't silent
            // on machines where /tmp permissions or antivirus locks interfere with deletion.
            System.Diagnostics.Debug.WriteLine(
                $"IsolatedCodexHome cleanup failed for '{_directory}': {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    /// <summary>
    /// Builds a Codex <c>config.toml</c> that declares a <c>mock</c> model provider on plain HTTP
    /// (<c>wire_api = "chat"</c>) and selects it as the default. The CLI then appends
    /// <c>/chat/completions</c> to <paramref name="mockBaseUrl"/> for each turn, so the caller is
    /// expected to pass a URL that already includes the <c>/v1</c> suffix (matching the standard
    /// <c>OPENAI_BASE_URL=https://api.openai.com/v1</c> convention the mock host's
    /// <c>POST /v1/chat/completions</c> route shadows).
    /// </summary>
    private static string BuildMockProviderConfigToml(string mockBaseUrl)
    {
        // TOML strings are double-quoted; escape backslashes and quotes per the spec. We don't
        // expect either in a fixture URL, but defensive escaping keeps the writer correct under
        // unusual fixture configurations (e.g. a future Windows pipe URL).
        var quotedBaseUrl = TomlString(mockBaseUrl);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            # Generated by IsolatedCodexHome — see tests/MockProviderHost.E2E.Tests.
            model_provider = "mock"

            [model_providers.mock]
            name = "Mock"
            base_url = {0}
            wire_api = "chat"
            env_key = "OPENAI_API_KEY"
            """,
            quotedBaseUrl
        );
    }

    private static string TomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Escapes <paramref name="value"/> for inclusion inside a JSON string literal (the surrounding
    /// quotes are supplied by the caller). Mirrors RFC 8259 §7: backslash, quote, and the C0 control
    /// range require escaping.
    /// </summary>
    private static string JsonStringInner(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
