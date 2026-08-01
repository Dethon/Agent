namespace WebChat.Client.Contracts;

public interface IHubConnectionFactory
{
    Task<IChatHubConnection> CreateAsync();
}