using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// The unified sandbox webhook (gateway PR #205, <c>Docs/webhooks.md</c>): the create block and the GET summary,
/// which never carries the auth header value.
/// </summary>
public sealed class SandboxClientWebhookTests
{
    [Fact]
    public async Task CreateAsync_Webhook_IsSentWithItsAllowlist()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"s-1"}""", HttpStatusCode.Created);

        _ = await client.CreateAsync(
            new SandboxCreateRequest(
                "ws",
                webhook: new SandboxWebhookSettings(
                    "https://app.example.com/sbx/events",
                    "Bearer secret",
                    [SandboxWebhookEvents.TaskCompleted, SandboxWebhookEvents.ContextDiscovery]
                )
            )
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var webhook = sent.RootElement.GetProperty("webhook");
        webhook.GetProperty("url").GetString().Should().Be("https://app.example.com/sbx/events");
        webhook.GetProperty("auth_header").GetString().Should().Be("Bearer secret");
        webhook
            .GetProperty("events")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("task_completed", "context_discovery");
    }

    [Fact]
    public async Task CreateAsync_WebhookWithoutAllowlistOrAuth_OmitsBothSoEveryEventIsDelivered()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"s-1"}""", HttpStatusCode.Created);

        _ = await client.CreateAsync(
            new SandboxCreateRequest("ws", webhook: new SandboxWebhookSettings("https://app.example.com/e"))
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var webhook = sent.RootElement.GetProperty("webhook");
        webhook
            .TryGetProperty("events", out _)
            .Should()
            .BeFalse("an absent list means every event, including future ones");
        webhook.TryGetProperty("auth_header", out _).Should().BeFalse();
    }

    [Fact]
    public void SandboxWebhookSettings_EmptyAllowlist_IsRejected_AndToStringRedactsTheAuthHeader()
    {
        var act = () => new SandboxWebhookSettings("https://h.example/e", events: []);
        act.Should().Throw<ArgumentOutOfRangeException>();

        new SandboxWebhookSettings("https://h.example/e", "Bearer secret").ToString().Should().NotContain("secret");
    }

    [Fact]
    public async Task GetAsync_ReportsTheRegisteredWebhookAndProperties()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1",
            """
            {"session_id":"s-1","properties":{"tenant_id":"acme"},"partition":["tenant_id"],
             "webhook":{"url":"https://app.example.com/sbx/events","events":null,"auth_header_set":true},
             "image":"llm-sandbox-agent:latest","image_digest":null}
            """
        );

        var info = await client.GetAsync("s-1");

        info.Webhook.Should().Be(new SandboxWebhookInfo("https://app.example.com/sbx/events", null, true));
        info.Properties.Should().Equal(new Dictionary<string, string> { ["tenant_id"] = "acme" });
        info.Partition.Should().Equal("tenant_id");
        info.Image.Should().Be("llm-sandbox-agent:latest");
    }

    [Fact]
    public async Task GetAsync_WebhookSummaryWithoutAuthFlag_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1",
            """{"session_id":"s-1","webhook":{"url":"https://app.example.com/e","events":null}}"""
        );

        var act = () => client.GetAsync("s-1");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }
}
