namespace WebChat.Client.Contracts;

public interface IHubEventBinder
{
    void Bind(IChatHubConnection connection);

    void Unbind();
}