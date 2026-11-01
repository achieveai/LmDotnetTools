using System.Text.Json;

namespace AchieveAi.LmDotnetTools.MockProviderHost;

/// <summary>
/// Front of the host's chat-completions and Anthropic-messages routes: a request whose latest
/// human text carries an instruction prompt (<c>&lt;|instruction_start|&gt;</c>) goes to the
/// instruction-chain handler; every other request goes to the scripted scenario. The Responses
/// route needs no router: its scripted handler resolves instruction chains itself.
/// </summary>
/// <remarks>
/// <para>
/// The human text is the newest text part of a user-role message that is not an envelope. Tool
/// results (an OpenAI <c>tool</c> message, an Anthropic message of <c>tool_result</c> parts) carry
/// no text part, so they keep a running chain on the chain handler, which then picks the step.
/// A new plain message after a chain goes back to the scenario.
/// </para>
/// <para>
/// Envelopes are text the agent loop or the CLI injects as a user turn: LmMultiTurn notifications,
/// Wait fires, agent messages and elapsed-time notices, and the <c>&lt;system-reminder&gt;</c> parts
/// Claude Code puts before (and after) the human text. Copilot CLI prefixes the human text with
/// <c>&lt;current_datetime&gt;</c> in the same string, so the check is a substring match.
/// </para>
/// </remarks>
internal sealed class InstructionChainRouter : HttpMessageHandler
{
    internal const string ChainStart = "<|instruction_start|>";

    private static readonly string[] EnvelopePrefixes =
    [
        "<notification",
        "<trigger>",
        "<agent-message",
        "<elapsed-time-notice>",
        "<system-reminder>",
    ];

    private readonly HttpMessageInvoker _chains;
    private readonly HttpMessageInvoker _scenario;

    public InstructionChainRouter(HttpMessageHandler chains, HttpMessageHandler scenario)
    {
        _chains = new HttpMessageInvoker(chains ?? throw new ArgumentNullException(nameof(chains)));
        _scenario = new HttpMessageInvoker(scenario ?? throw new ArgumentNullException(nameof(scenario)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // ReadAsStringAsync buffers the content, so the chosen handler can read it again.
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var target = CarriesInstructionChain(body) ? _chains : _scenario;
        return await target.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static bool CarriesInstructionChain(string body)
    {
        // No raw-text shortcut: serializers may escape the tag's angle brackets (<), so only
        // the parsed text shows it.
        try
        {
            using var document = JsonDocument.Parse(body);
            if (
                !document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
            )
            {
                return false;
            }

            foreach (var message in messages.EnumerateArray().Reverse())
            {
                if (
                    message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("role", out var role)
                    || role.GetString() != "user"
                )
                {
                    continue;
                }

                var texts = TextParts(message);
                for (var i = texts.Count - 1; i >= 0; i--)
                {
                    var trimmed = texts[i].TrimStart();
                    if (trimmed.Length == 0 || IsEnvelope(trimmed))
                    {
                        continue;
                    }

                    return trimmed.Contains(ChainStart, StringComparison.Ordinal);
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEnvelope(string text)
    {
        foreach (var prefix in EnvelopePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A message's texts: its string content, or each part's <c>text</c>.</summary>
    private static List<string> TextParts(JsonElement message)
    {
        var texts = new List<string>();
        if (!message.TryGetProperty("content", out var content))
        {
            return texts;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            texts.Add(content.GetString()!);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (
                    part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var value)
                    && value.ValueKind == JsonValueKind.String
                )
                {
                    texts.Add(value.GetString()!);
                }
            }
        }

        return texts;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chains.Dispose();
            _scenario.Dispose();
        }

        base.Dispose(disposing);
    }
}
