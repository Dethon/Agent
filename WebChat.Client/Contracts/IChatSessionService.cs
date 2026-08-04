using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IChatSessionService
{
    StoredTopic? CurrentTopic { get; }

    event Action? OnSessionChanged;

    Task<HubResult<bool>> StartSessionAsync(StoredTopic topic);

    // The same concern as starting a session: who the server thinks this client is. It lived
    // as the one hub call with no service in front of it until this feature.
    Task<HubResult<Nothing>> RegisterUserAsync(string userId);

    void ClearSession();
}