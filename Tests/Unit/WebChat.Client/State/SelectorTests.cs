using WebChat.Client.State;

namespace Tests.Unit.WebChat.Client.State;

public class SelectorTests
{
    private sealed record TestState(int Value, string Name);

    [Fact]
    public void Select_ReturnsCachedValue_WhenStateUnchanged()
    {
        // Arrange
        var computeCount = 0;
        var selector = Selector.Create((TestState s) =>
        {
            computeCount++;
            return s.Value * 2;
        });
        var state = new TestState(5, "test");

        // Act
        var result1 = selector.Select(state);
        var result2 = selector.Select(state);

        // Assert
        Assert.Equal(10, result1);
        Assert.Equal(10, result2);
        Assert.Equal(1, computeCount); // Only computed once
    }

    [Fact]
    public void Select_RecomputesValue_WhenStateChanges()
    {
        // Arrange
        var computeCount = 0;
        var selector = Selector.Create((TestState s) =>
        {
            computeCount++;
            return s.Value * 2;
        });
        var state1 = new TestState(5, "test");
        var state2 = state1 with { Value = 10 };

        // Act
        var result1 = selector.Select(state1);
        var result2 = selector.Select(state2);

        // Assert
        Assert.Equal(10, result1);
        Assert.Equal(20, result2);
        Assert.Equal(2, computeCount); // Computed twice
    }

    [Fact]
    public void Invalidate_ForcesRecomputation()
    {
        // Arrange
        var computeCount = 0;
        var selector = Selector.Create((TestState s) =>
        {
            computeCount++;
            return s.Value;
        });
        var state = new TestState(5, "test");

        // Act
        selector.Select(state);
        selector.Invalidate();
        selector.Select(state);

        // Assert
        Assert.Equal(2, computeCount);
    }

    [Fact]
    public void Compose_CombinesSelectors()
    {
        // Arrange
        var firstSelector = Selector.Create((TestState s) => s.Value);
        var composedSelector = Selector.Compose(firstSelector, v => v * 3);
        var state = new TestState(4, "test");

        // Act
        var result = composedSelector.Select(state);

        // Assert
        Assert.Equal(12, result);
    }
}
