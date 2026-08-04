using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IChatSessionService
{
    StoredTopic? CurrentTopic { get; }

    event Action? OnSessionChanged;

    Task<HubResult<bool>> StartSessionAsync(StoredTopic topic);

    // Here because it is the same concern as starting a session — who the server thinks this
    // client is — and because session recovery needs a dependency it can fake.
    Task<HubResult<Nothing>> RegisterUserAsync(string userId);

    void ClearSession();
}