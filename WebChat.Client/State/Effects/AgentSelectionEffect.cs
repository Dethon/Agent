using WebChat.Client.Contracts;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentSelectionEffect : IDisposable
{
    private readonly ILocalStorageService _localStorage;
    private readonly IChatSessionService _sessionService;
    private readonly IDisposable _subscription;
    private string? _previousAgentId;

    public AgentSelectionEffect(
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        ILocalStorageService localStorage)
    {
        _sessionService = sessionService;
        _localStorage = localStorage;

        // Subscribe to store to detect agent changes
        _subscription = topicsStore.StateObservable.Subscribe(HandleStateChange);
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }

    private void HandleStateChange(TopicsState state)
    {
        if (state.SelectedAgentId != _previousAgentId && _previousAgentId is not null)
        {
            // Agent changed - clear session and save
            _sessionService.ClearSession();
            _ = _localStorage.SetAsync("selectedAgentId", state.SelectedAgentId ?? "");
        }

        _previousAgentId = state.SelectedAgentId;
    }
}