namespace Infrastructure.Wrappers;

public interface ISshClientWrapper : IDisposable
{
    bool IsConnected { get; }
    void Connect();
    void Disconnect();
    ICommandWrapper CreateCommand(string commandText);
    ICommandWrapper RunCommand(string commandText);
}

public interface ICommandWrapper
{
    void Execute();
    string Result { get; }
    string Error { get; }
}