using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;

namespace WebChat.Client.State;

public abstract class StoreSubscriberComponent : ComponentBase, IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    private bool _disposed;

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscriptions.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void Subscribe<TState, TSelected>(
        IObservable<TState> stateObservable,
        Func<TState, TSelected> selector,
        Action<TSelected> onNext)
    {
        var subscription = stateObservable
            .Select(selector)
            .DistinctUntilChanged()
            .Subscribe(value =>
            {
                InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    onNext(value);
                    StateHasChanged();
                });
            });
        _subscriptions.Add(subscription);
    }

    protected void SubscribeWithInvoke<T>(IObservable<T> throttledObservable, Action<T> onNext)
    {
        var subscription = throttledObservable.Subscribe(value =>
        {
            InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                onNext(value);
                StateHasChanged();
            });
        });
        _subscriptions.Add(subscription);
    }


    protected void ClearSubscriptions()
    {
        _subscriptions.Clear();
    }
}