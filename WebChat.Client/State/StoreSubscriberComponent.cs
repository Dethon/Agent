using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;

namespace WebChat.Client.State;

/// <summary>
/// Base component that manages IObservable subscriptions with automatic disposal.
/// Components inherit this to subscribe to store state with proper lifecycle management.
/// </summary>
public abstract class StoreSubscriberComponent : ComponentBase, IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    private bool _disposed;

    /// <summary>
    /// Subscribe to an observable and re-render on each emission.
    /// Subscription is automatically disposed when component is disposed.
    /// </summary>
    protected void Subscribe<T>(IObservable<T> observable, Action<T> onNext)
    {
        var subscription = observable.Subscribe(value =>
        {
            InvokeAsync(() =>
            {
                if (_disposed) return;
                onNext(value);
                StateHasChanged();
            });
        });
        _subscriptions.Add(subscription);
    }

    /// <summary>
    /// Subscribe with a selector - only re-render when selected value changes.
    /// Uses DistinctUntilChanged to prevent unnecessary re-renders.
    /// </summary>
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
                    if (_disposed) return;
                    onNext(value);
                    StateHasChanged();
                });
            });
        _subscriptions.Add(subscription);
    }

    /// <summary>
    /// Subscribe with a selector and custom equality comparer.
    /// Use when default equality is insufficient (e.g., comparing collections).
    /// </summary>
    protected void Subscribe<TState, TSelected>(
        IObservable<TState> stateObservable,
        Func<TState, TSelected> selector,
        IEqualityComparer<TSelected> comparer,
        Action<TSelected> onNext)
    {
        var subscription = stateObservable
            .Select(selector)
            .DistinctUntilChanged(comparer)
            .Subscribe(value =>
            {
                InvokeAsync(() =>
                {
                    if (_disposed) return;
                    onNext(value);
                    StateHasChanged();
                });
            });
        _subscriptions.Add(subscription);
    }

    /// <summary>
    /// Subscribe to an already-throttled observable (e.g., from RenderCoordinator).
    /// Only marshals to UI thread via InvokeAsync - does NOT apply additional throttling.
    /// Use this for observables that already have Sample applied by RenderCoordinator.
    /// </summary>
    protected void SubscribeWithInvoke<T>(IObservable<T> throttledObservable, Action<T> onNext)
    {
        var subscription = throttledObservable.Subscribe(value =>
        {
            InvokeAsync(() =>
            {
                if (_disposed) return;
                onNext(value);
                StateHasChanged();
            });
        });
        _subscriptions.Add(subscription);
    }

    /// <summary>
    /// Clear all current subscriptions. Use when component needs to re-subscribe
    /// (e.g., when TopicId parameter changes).
    /// </summary>
    protected void ClearSubscriptions()
    {
        _subscriptions.Clear();
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        GC.SuppressFinalize(this);
    }
}
