using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IChatSessionService
{
    StoredTopic? CurrentTopic { get; }

    event Action? OnSessionChanged;

    Task<bool> StartSessionAsync(StoredTopic topic);
    void ClearSession();
}