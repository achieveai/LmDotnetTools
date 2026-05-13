using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmMultiTurn.Tests;

public class CopilotEventTranslatorUnmappedKindTests
{
    [Fact]
    public void ConvertEventToMessages_UnmappedSessionUpdateKind_LogsInformationDroppedWithStructuredProps()
    {
        var logger = new ListLogger();
        var options = new CopilotSdkOptions { Provider = "copilot", ProviderMode = "copilot" };
        var translator = new CopilotEventTranslator(options, logger);

        const string unmappedKind = "some_unknown_kind";
        var envelope = JsonDocument.Parse($$"""
            {
              "type": "session/update",
              "sessionId": "sess_x",
              "update": { "sessionUpdate": "{{unmappedKind}}" }
            }
            """).RootElement;

        var result = translator.ConvertEventToMessages(envelope, runId: "run_1", generationId: "gen_1");

        result.Should().BeEmpty("unmapped kinds must not produce messages");

        var matching = logger.Entries
            .Where(e =>
                e.Level == LogLevel.Information
                && string.Equals(GetProp(e.State, "event_type"), "copilot.session_update.unmapped"))
            .ToList();

        matching.Should().HaveCount(1, "exactly one Information-level diagnostic must fire for an unmapped kind");

        var entry = matching[0];
        GetProp(entry.State, "event_status").Should().Be("dropped");
        GetProp(entry.State, "provider").Should().Be("copilot");
        GetProp(entry.State, "provider_mode").Should().Be("copilot");
        GetProp(entry.State, "session_update_kind").Should().Be(unmappedKind);
    }

    private static string? GetProp(IReadOnlyList<KeyValuePair<string, object?>> state, string key)
    {
        foreach (var kv in state)
        {
            if (string.Equals(kv.Key, key, StringComparison.Ordinal))
            {
                return kv.Value?.ToString();
            }
        }

        return null;
    }

    private sealed class ListLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var props = new List<KeyValuePair<string, object?>>();
            if (state is IEnumerable<KeyValuePair<string, object?>> structured)
            {
                props.AddRange(structured);
            }

            Entries.Add(new LogEntry(logLevel, formatter(state, exception), props));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State);
}
