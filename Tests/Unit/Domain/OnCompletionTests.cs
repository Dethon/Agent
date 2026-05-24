using Domain.Extensions;
using Shouldly;

namespace Tests.Unit.Domain;

public class OnCompletionTests
{
    private static readonly int[] _source = [1, 2, 3];

    [Fact]
    public async Task OnCompletion_PassesThroughAllElementsUnchanged()
    {
        var result = await _source.ToAsyncEnumerable()
            .OnCompletion(seed: 0, fold: (acc, _) => acc, onCompletion: (_, _) => ValueTask.CompletedTask)
            .ToListAsync();

        result.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task OnCompletion_InvokesCallbackWithFoldedState()
    {
        var captured = -1;

        await _source.ToAsyncEnumerable()
            .OnCompletion(
                seed: 0,
                fold: (acc, item) => acc + item,
                onCompletion: (acc, _) =>
                {
                    captured = acc;
                    return ValueTask.CompletedTask;
                })
            .ToListAsync();

        captured.ShouldBe(6);
    }

    [Fact]
    public async Task OnCompletion_EmptySource_InvokesCallbackWithSeed()
    {
        var captured = -1;

        var result = await AsyncEnumerable.Empty<int>()
            .OnCompletion(
                seed: 42,
                fold: (acc, item) => acc + item,
                onCompletion: (acc, _) =>
                {
                    captured = acc;
                    return ValueTask.CompletedTask;
                })
            .ToListAsync();

        result.ShouldBeEmpty();
        captured.ShouldBe(42);
    }

    [Fact]
    public async Task OnCompletion_InvokesCallbackAfterAllElements()
    {
        var order = new List<string>();

        await _source.ToAsyncEnumerable()
            .OnCompletion(
                seed: 0,
                fold: (acc, item) =>
                {
                    order.Add($"item:{item}");
                    return acc;
                },
                onCompletion: (_, _) =>
                {
                    order.Add("complete");
                    return ValueTask.CompletedTask;
                })
            .ToListAsync();

        order.ShouldBe(["item:1", "item:2", "item:3", "complete"]);
    }

    [Fact]
    public async Task OnCompletion_ConsumerStopsEarly_DoesNotInvokeCallback()
    {
        var completed = false;

        await foreach (var item in _source.ToAsyncEnumerable()
                           .OnCompletion(
                               seed: 0,
                               fold: (acc, _) => acc,
                               onCompletion: (_, _) =>
                               {
                                   completed = true;
                                   return ValueTask.CompletedTask;
                               }))
        {
            if (item == 2)
            {
                break;
            }
        }

        completed.ShouldBeFalse();
    }
}