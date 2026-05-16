using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class DashboardStoreTests
{
    public static TheoryData<string, Func<IDisposable>, Action<object, DateOnly, DateOnly>, Func<object, DateOnly>, Func<object, DateOnly>> StoreFactories =>
        new()
        {
            { "Errors", () => new ErrorsStore(), (s, f, t) => ((ErrorsStore)s).SetDateRange(f, t), s => ((ErrorsStore)s).State.From, s => ((ErrorsStore)s).State.To },
            { "Schedules", () => new SchedulesStore(), (s, f, t) => ((SchedulesStore)s).SetDateRange(f, t), s => ((SchedulesStore)s).State.From, s => ((SchedulesStore)s).State.To },
            { "Tokens", () => new TokensStore(), (s, f, t) => ((TokensStore)s).SetDateRange(f, t), s => ((TokensStore)s).State.From, s => ((TokensStore)s).State.To },
            { "Tools", () => new ToolsStore(), (s, f, t) => ((ToolsStore)s).SetDateRange(f, t), s => ((ToolsStore)s).State.From, s => ((ToolsStore)s).State.To },
            { "Latency", () => new LatencyStore(), (s, f, t) => ((LatencyStore)s).SetDateRange(f, t), s => ((LatencyStore)s).State.From, s => ((LatencyStore)s).State.To },
        };

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void SetDateRange_UpdatesFromAndTo(
        string _,
        Func<IDisposable> factory,
        Action<object, DateOnly, DateOnly> setDateRange,
        Func<object, DateOnly> getFrom,
        Func<object, DateOnly> getTo)
    {
        using var store = factory();
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        setDateRange(store, from, to);

        getFrom(store).ShouldBe(from);
        getTo(store).ShouldBe(to);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1026:Theory methods should use all of their parameters")]
    public void InitialState_DefaultsToToday(
        string _,
        Func<IDisposable> factory,
        Action<object, DateOnly, DateOnly> __,
        Func<object, DateOnly> getFrom,
        Func<object, DateOnly> getTo)
    {
        using var store = factory();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        getFrom(store).ShouldBe(today);
        getTo(store).ShouldBe(today);
    }
}