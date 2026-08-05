using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// <see cref="ILoggerFactory"/> that hands every category the SAME <see cref="Capturing"/> logger, so a test
/// can assert on what a component under test actually logged. Used where the log line IS the deliverable —
/// the daemon's proof-of-use and rejection lines exist so that a silent retrieval failure cannot pass for a
/// healthy review, and a test that only checks the behaviour would let the line rot away unnoticed.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public CapturingLogger<object> Capturing { get; } = new();

    public ILogger CreateLogger(string categoryName) => Capturing;

    public void AddProvider(ILoggerProvider provider)
    {
        // Nothing to fan out to: this factory is the sink.
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
