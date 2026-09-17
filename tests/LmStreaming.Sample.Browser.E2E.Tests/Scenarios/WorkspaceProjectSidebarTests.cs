using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser journey for the project-shaped conversation sidebar and the composer project picker.
/// The scenario crosses persisted workspace/conversation metadata, keyboard disclosure behavior,
/// the phone layout, first-send provisioning, and the newly-created sidebar row.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class WorkspaceProjectSidebarTests
{
    private const int PhoneWidth = 390;
    private const int PhoneHeight = 700;

    private readonly PlaywrightFixture _fixture;

    public WorkspaceProjectSidebarTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Project_folders_start_a_bound_chat_and_keep_the_phone_composer_on_screen()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("Beacon is ready."))
            .Build();

        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;
        await page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);

        var (atlasId, beaconId) = await SeedProjectsAndConversationsAsync(session.Factory);
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();

        // The sidebar is a phone-width overlay. Open it and prove the saved chats stayed under their
        // persisted project rather than flattening into one recency list.
        await SetSidebarExpandedAsync(page, expanded: true);
        var atlasPanel = page.GetByTestId($"project-conversations-{atlasId}");
        var beaconPanel = page.GetByTestId($"project-conversations-{beaconId}");
        await Assertions.Expect(atlasPanel.Locator("[data-thread-id='atlas-saved']")).ToHaveCountAsync(1);
        await Assertions.Expect(beaconPanel.Locator("[data-thread-id='beacon-saved']")).ToHaveCountAsync(1);

        // Native buttons supply Enter/Space activation. The ARIA state and controlled panel must
        // change together, so keyboard users get the same folder behavior as pointer users.
        var atlasToggle = page.GetByTestId($"project-toggle-{atlasId}");
        await atlasToggle.FocusAsync();
        await atlasToggle.PressAsync("Enter");
        await Assertions.Expect(atlasToggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(atlasPanel).ToBeHiddenAsync();
        await atlasToggle.PressAsync("Space");
        await Assertions.Expect(atlasToggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(atlasPanel).ToBeVisibleAsync();

        // Legacy and orphaned metadata remain reachable but cannot start an ambiguously-bound chat.
        await Assertions.Expect(page.GetByTestId("project-toggle-legacy")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("project-toggle-missing-retired-project")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("start-conversation-legacy")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("start-conversation-retired-project")).ToHaveCountAsync(0);

        var provisionBodies = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/api/conversations")
            {
                provisionBodies.Enqueue(request.PostData ?? string.Empty);
            }
        };

        // Start from Beacon's own folder. This creates only an unreserved draft and automatically
        // dismisses the mobile sidebar; the server must not mint anything until the first send.
        await page.GetByTestId($"start-conversation-{beaconId}").ClickAsync();
        await Assertions.Expect(page.GetByTestId("sidebar-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        provisionBodies.Should().BeEmpty("choosing a project folder must not eagerly reserve a conversation");

        var picker = page.GetByTestId("workspace-selector-button");
        await Assertions.Expect(picker).ToContainTextAsync("Beacon Project");
        await Assertions.Expect(picker).ToHaveAttributeAsync("aria-expanded", "false");

        var pickerBox = await picker.BoundingBoxAsync();
        var textareaBox = await page.Textarea().BoundingBoxAsync();
        pickerBox.Should().NotBeNull("the selected project strip must be visible above the composer");
        textareaBox.Should().NotBeNull("the composer must remain measurable at phone width");
        pickerBox!.X.Should().BeGreaterThanOrEqualTo(0);
        (pickerBox.X + pickerBox.Width).Should().BeLessThanOrEqualTo(PhoneWidth);
        textareaBox!.X.Should().BeGreaterThanOrEqualTo(0, "the composer must not slide left off-screen");
        (textareaBox.X + textareaBox.Width)
            .Should()
            .BeLessThanOrEqualTo(PhoneWidth, "the composer must not be clipped at the phone viewport edge");
        (pickerBox.Y + pickerBox.Height)
            .Should()
            .BeLessThanOrEqualTo(textareaBox.Y, "the project strip belongs above the message field");

        // Exercise the tallest picker state at 390x700. It opens upward, stays inside the viewport,
        // and scrolls instead of pushing create/edit controls off-screen.
        await picker.ClickAsync();
        await page.GetByTestId("workspace-create-open").ClickAsync();
        var menu = page.Locator("#workspace-selector-menu");
        var menuBox = await menu.BoundingBoxAsync();
        menuBox.Should().NotBeNull("the create form must render in the open project popup");
        menuBox!.Y.Should().BeGreaterThanOrEqualTo(0);
        (menuBox.Y + menuBox.Height)
            .Should()
            .BeLessThanOrEqualTo(pickerBox.Y + 1, "the popup must open upward without covering the trigger");
        (await menu.EvaluateAsync<double>("el => el.scrollHeight - el.clientHeight"))
            .Should()
            .BeGreaterThan(0, "the tall create form must remain reachable through popup scrolling");
        await page.Keyboard.PressAsync("Escape");

        await page.SendMessageAsync("Start this Beacon conversation.");
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        provisionBodies.Should().ContainSingle("the first send reserves exactly one conversation");
        using var provision = JsonDocument.Parse(provisionBodies.Single());
        provision.RootElement.GetProperty("workspaceId").GetString().Should().Be(beaconId);

        // The optimistic local row carries the same binding immediately, before a reload can mask a
        // client-side grouping mistake with a fresh server projection.
        await SetSidebarExpandedAsync(page, expanded: true);
        await Assertions
            .Expect(
                page.GetByTestId($"project-conversations-{beaconId}")
                    .Locator("[data-thread-id]")
                    .Filter(new LocatorFilterOptions { HasText = "Start this Beacon conversation." })
            )
            .ToHaveCountAsync(1);

        await session.SaveSuccessScreenshotAsync("WorkspaceProjectSidebar.bound_phone_journey");
    }

    private static async Task<(string AtlasId, string BeaconId)> SeedProjectsAndConversationsAsync(
        BrowserWebAppFactory factory
    )
    {
        var seed = Guid.NewGuid().ToString("N")[..8];
        var workspaces = factory.AppServices.GetRequiredService<IWorkspaceStore>();
        var atlas = await workspaces.CreateAsync(
            new WorkspaceCreate
            {
                Name = "Atlas Project",
                DirectoryRelPath = $"atlas-{seed}",
                Marketplaces = [],
            }
        );
        var beacon = await workspaces.CreateAsync(
            new WorkspaceCreate
            {
                Name = "Beacon Project",
                DirectoryRelPath = $"beacon-{seed}",
                Marketplaces = [],
            }
        );

        var conversations = factory.AppServices.GetRequiredService<IConversationStore>();
        await SaveConversationAsync(conversations, "atlas-saved", "Atlas saved chat", atlas.Id, 4);
        await SaveConversationAsync(conversations, "beacon-saved", "Beacon saved chat", beacon.Id, 3);
        await SaveConversationAsync(conversations, "missing-saved", "Retired project chat", "retired-project", 2);
        await SaveConversationAsync(conversations, "legacy-saved", "Legacy unbound chat", workspaceId: null, 1);

        return (atlas.Id, beacon.Id);
    }

    private static async Task SetSidebarExpandedAsync(IPage page, bool expanded)
    {
        var sidebar = page.Locator(".conversation-sidebar");
        var isCollapsed = await sidebar.EvaluateAsync<bool>("el => el.classList.contains('collapsed')");
        if (expanded == isCollapsed)
        {
            await page.GetByTestId("sidebar-toggle").ClickAsync();
        }

        await Assertions
            .Expect(page.GetByTestId("sidebar-toggle"))
            .ToHaveAttributeAsync("aria-expanded", expanded ? "true" : "false");
    }

    private static Task SaveConversationAsync(
        IConversationStore store,
        string threadId,
        string title,
        string? workspaceId,
        long lastUpdated
    )
    {
        var properties = ImmutableDictionary<string, object>.Empty.Add("title", title);
        if (workspaceId is not null)
        {
            properties = properties.Add("workspace", workspaceId);
        }

        return store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = lastUpdated,
                Properties = properties,
            }
        );
    }
}
