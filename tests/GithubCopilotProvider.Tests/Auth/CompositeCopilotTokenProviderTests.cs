using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Auth;

public sealed class CompositeCopilotTokenProviderTests
{
    private sealed class StubProvider(Func<CancellationToken, Task<string>> behavior) : ICopilotTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return behavior(cancellationToken);
        }
    }

    private static StubProvider Returns(string token) => new(_ => Task.FromResult(token));

    private static StubProvider Throws(Exception ex) => new(_ => Task.FromException<string>(ex));

    [Fact]
    public async Task First_provider_wins_and_later_providers_are_not_called()
    {
        var first = Returns("gho_first");
        var second = Returns("gho_second");
        var composite = new CompositeCopilotTokenProvider(first, second);

        (await composite.GetTokenAsync()).Should().Be("gho_first");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Falls_back_to_next_provider_when_earlier_one_fails()
    {
        var first = Throws(new InvalidOperationException("no creds"));
        var second = Returns("gho_fallback");
        var composite = new CompositeCopilotTokenProvider(first, second);

        (await composite.GetTokenAsync()).Should().Be("gho_fallback");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolved_token_is_cached_in_memory()
    {
        var first = Returns("gho_cached");
        var composite = new CompositeCopilotTokenProvider(first);

        (await composite.GetTokenAsync()).Should().Be("gho_cached");
        (await composite.GetTokenAsync()).Should().Be("gho_cached");

        first.CallCount.Should().Be(1, "the cached token should be reused without re-invoking the provider");
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_swallowed_as_a_provider_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var provider = new StubProvider(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        });
        var composite = new CompositeCopilotTokenProvider(provider);

        var act = async () => await composite.GetTokenAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task When_all_providers_fail_all_failures_are_preserved()
    {
        var first = Throws(new InvalidOperationException("first failure"));
        var second = Throws(new IOException("second failure"));
        var composite = new CompositeCopilotTokenProvider(first, second);

        var act = async () => await composite.GetTokenAsync();

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeOfType<AggregateException>();
        var aggregate = (AggregateException)ex.InnerException!;
        aggregate.InnerExceptions.Should().HaveCount(2);
        aggregate.InnerExceptions.Select(e => e.Message).Should().Contain(["first failure", "second failure"]);
    }
}
