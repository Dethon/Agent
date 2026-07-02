using Domain.DTOs.Voice;
using Infrastructure.Timers;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class InMemoryTimerStoreTests
{
    private static ArmedTimer Timer(string id, DateTime firesAtUtc) => new()
    {
        Id = id,
        Target = new AnnounceTarget { Room = "Kitchen" },
        DurationSeconds = 300,
        CreatedAtUtc = firesAtUtc.AddSeconds(-300),
        FiresAtUtc = firesAtUtc
    };

    [Fact]
    public async Task TakeDueAsync_RemovesAndReturnsOnlyDueTimers()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("due", now.AddSeconds(-1)));
        await store.ArmAsync(Timer("later", now.AddMinutes(5)));

        var due = await store.TakeDueAsync(now);

        due.ShouldHaveSingleItem();
        due[0].Id.ShouldBe("due");
        (await store.GetAsync("due")).ShouldBeNull();          // removed — fires once
        (await store.GetAsync("later")).ShouldNotBeNull();
        (await store.TakeDueAsync(now)).ShouldBeEmpty();       // second take is empty
    }

    [Fact]
    public async Task CancelAsync_RemovesTimer_AndReportsMisses()
    {
        var store = new InMemoryTimerStore();
        await store.ArmAsync(Timer("pasta", DateTime.UtcNow.AddMinutes(5)));

        (await store.CancelAsync("pasta")).ShouldBeTrue();
        (await store.CancelAsync("pasta")).ShouldBeFalse();
        (await store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_OrdersByFireTime()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("second", now.AddMinutes(10)));
        await store.ArmAsync(Timer("first", now.AddMinutes(1)));

        (await store.ListAsync()).Select(t => t.Id).ShouldBe(["first", "second"]);
    }
}