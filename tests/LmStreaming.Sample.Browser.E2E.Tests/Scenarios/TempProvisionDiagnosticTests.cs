using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// TEMPORARY diagnostic. Sends one message and then fails on purpose, carrying the browser console
/// and the HTTP traffic in its failure message, because that is the only channel whose text reaches
/// the CI job log. Delete once the #435 provisioning failure on the Linux runner is understood.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class TempProvisionDiagnosticTests
{
    private readonly PlaywrightFixture _fixture;

    public TempProvisionDiagnosticTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dump_what_the_first_send_actually_does()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("First answer."))
            .Build();

        await using var session = await _fixture.OpenAsync("test-anthropic", responder.HandlerFor("test-anthropic"));
        var page = session.Page;

        var log = new StringBuilder();
        var gate = new object();
        void Dump(string line)
        {
            lock (gate)
            {
                _ = log.AppendLine(line);
            }
        }

        page.Console += (_, m) => Dump($"[C:{m.Type}] {m.Text}");
        page.PageError += (_, e) => Dump($"[PAGEERROR] {e}");
        page.RequestFailed += (_, r) => Dump($"[REQFAILED] {r.Method} {r.Url} {r.Failure}");
        page.Response += (_, r) => Dump($"[RESP {r.Status}] {r.Request.Method} {r.Url}");

        await page.SendMessageAsync("hello");
        await Task.Delay(8_000);

        var banner = await page.ErrorBanner().CountAsync() > 0
            ? await page.ErrorBanner().First.InnerTextAsync()
            : "(no error banner)";

        throw new Xunit.Sdk.XunitException(
            $"TEMP DIAGNOSTIC\nerror banner: {banner}\n----- traffic + console -----\n{log}");
    }
}
