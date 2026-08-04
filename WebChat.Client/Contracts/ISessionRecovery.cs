namespace WebChat.Client.Contracts;

public interface ISessionRecovery
{
    Task RecoverAsync();
}