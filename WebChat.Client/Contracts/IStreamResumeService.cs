using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamResumeService
{
    Task TryResumeStreamAsync(StoredTopic topic);
}