using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class ReviewSlotPoolTests
{
    private static ReviewSlotPool CreatePool(int count = 2) =>
        new(["Nova", "Widgets"], count, NullLogger<ReviewSlotPool>.Instance);

    [Fact]
    public async Task Mixed_repository_counts_allocate_eighteen_unique_case_insensitive_slots()
    {
        var pool = new ReviewSlotPool(
            ["Nova", "NovaClient", "Astra", "WeveNova", "MODISService"],
            3,
            NullLogger<ReviewSlotPool>.Instance,
            new Dictionary<string, int> { ["nova"] = 6 }
        );
        pool.Slots.Should().HaveCount(18).And.OnlyHaveUniqueItems();
        pool.Slots.Count(slot => slot.RepositoryName == "Nova").Should().Be(6);
        foreach (var repository in new[] { "Nova", "NovaClient", "Astra", "WeveNova", "MODISService" })
        {
            var expected = repository == "Nova" ? 6 : 3;
            for (var index = 0; index < expected; index++)
                (await pool.LeaseAsync(repository.ToLowerInvariant(), default))
                    .Should()
                    .Be(new ReviewSlot(repository, index));
            (await pool.TryLeaseAsync(repository, default)).Should().BeNull();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    public void Invalid_repository_slot_override_is_rejected(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReviewSlotPool(
                ["Nova"],
                3,
                NullLogger<ReviewSlotPool>.Instance,
                new Dictionary<string, int> { ["Nova"] = count }
            )
        );

    [Fact]
    public void Unknown_or_case_colliding_slot_overrides_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReviewSlotPool(
                ["Nova"],
                3,
                NullLogger<ReviewSlotPool>.Instance,
                new Dictionary<string, int> { ["Other"] = 3 }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new ReviewSlotPool(
                ["Nova"],
                3,
                NullLogger<ReviewSlotPool>.Instance,
                new Dictionary<string, int> { ["Nova"] = 6, ["nova"] = 3 }
            )
        );
        new ReviewSlotPool(["Nova", "Astra"], 3, NullLogger<ReviewSlotPool>.Instance).Slots.Should().HaveCount(6);
    }

    [Fact]
    public async Task Repository_identity_selects_independent_bounded_slots()
    {
        var pool = CreatePool(1);
        var nova = await pool.LeaseAsync("nova", default);
        var widgets = await pool.LeaseAsync("Widgets", default);
        nova.Should().Be(new ReviewSlot("Nova", 0));
        widgets.Should().Be(new ReviewSlot("Widgets", 0));
        (await pool.TryLeaseAsync("Nova", default)).Should().BeNull();
        nova.WorktreeRelativePath.Should().Be(".worktrees/Nova-0");
        nova.SourceRelativePath.Should().Be(".worktrees/Nova-0/repos/Nova");
        S2SReviewWorkspacePreparer.BuildWorktreeCwd(nova).Should().Be(nova.WorktreeRelativePath);
    }

    [Fact]
    public async Task Return_wakes_waiter_and_reuses_exact_address()
    {
        var pool = CreatePool(1);
        var first = await pool.LeaseAsync("Nova", default);
        var next = pool.LeaseAsync("Nova", default);
        next.IsCompleted.Should().BeFalse();
        await pool.ReturnAsync(first, default);
        (await next.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(first);
    }

    [Fact]
    public async Task Retirement_permanently_spends_capacity_even_after_return()
    {
        var pool = CreatePool(1);
        var slot = await pool.LeaseAsync("Nova", default);
        await pool.RetireAsync(slot, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.ReturnAsync(slot, default));
        (await pool.TryLeaseAsync("Nova", default)).Should().BeNull();
        (await pool.TryLeasePreferredAsync(slot, default)).Should().BeNull();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RecoverLeaseAsync(slot, default));
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.LeaseAsync("Nova", ct.Token));
    }

    [Fact]
    public async Task Retirement_never_invents_a_seventh_slot()
    {
        var pool = CreatePool(6);
        for (var index = 0; index < 6; index++)
        {
            var slot = await pool.LeaseAsync("Nova", default);
            slot.Index.Should().Be(index);
            await pool.RetireAsync(slot, default);
        }
        (await pool.TryLeaseAsync("Nova", default)).Should().BeNull();
    }

    [Fact]
    public async Task Recovery_reserves_exact_address_and_rejects_duplicate()
    {
        var pool = CreatePool();
        var slot = new ReviewSlot("Nova", 1);
        (await pool.RecoverLeaseAsync(slot, default)).Should().Be(slot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RecoverLeaseAsync(slot, default));
        (await pool.LeaseAsync("Nova", default)).Index.Should().Be(0);
    }

    [Fact]
    public async Task Preferred_lease_waits_for_exact_address_not_another_free_slot()
    {
        var pool = CreatePool();
        var slot = await pool.LeaseAsync("Nova", default);
        var pending = pool.LeasePreferredAsync(slot, default);
        pending.IsCompleted.Should().BeFalse();
        await pool.ReturnAsync(slot, default);
        (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(slot);
    }

    [Theory]
    [InlineData("Other", 0)]
    [InlineData("Nova", 6)]
    [InlineData("", 0)]
    public async Task Recovery_rejects_foreign_or_legacy_addresses(string repo, int index)
    {
        var pool = CreatePool();
        await Assert.ThrowsAsync<SlotAddressUnusableException>(() => pool.RecoverLeaseAsync(new(repo, index), default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Unsupported_slot_count_is_rejected(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePool(count));

    [Theory]
    [InlineData("../Nova")]
    [InlineData("Nova/other")]
    [InlineData("Nova\\other")]
    [InlineData(".")]
    public void Noncanonical_repo_names_cannot_become_conversation_cwd(string repo) =>
        Assert.Throws<ArgumentException>(() => S2SReviewWorkspacePreparer.BuildWorktreeCwd(new(repo, 0)));
}
