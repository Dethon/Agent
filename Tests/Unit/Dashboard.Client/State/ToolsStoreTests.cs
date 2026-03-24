using Dashboard.Client.State.Tools;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class ToolsStoreTests : IDisposable
{
    private readonly ToolsStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SetDateRange_UpdatesFromAndTo()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        _store.SetDateRange(from, to);

        _store.State.From.ShouldBe(from);
        _store.State.To.ShouldBe(to);
    }

    [Fact]
    public void InitialState_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _store.State.From.ShouldBe(today);
        _store.State.To.ShouldBe(today);
    }
}
