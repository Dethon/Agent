using System.Diagnostics.CodeAnalysis;
using Renci.SshNet;

namespace Infrastructure.Wrappers;

public sealed class SshClientWrapper : ISshClientWrapper
{
    private readonly SshClient _sshClient;
    public bool IsConnected => _sshClient.IsConnected;

    public SshClientWrapper(string host, string username, string keyFile, string passPhrase)
    {
        var privateKeyFile = new PrivateKeyFile(keyFile, passPhrase);
        _sshClient = new SshClient(host, username, privateKeyFile);
    }

    public void Connect()
    {
        if (!_sshClient.IsConnected)
        {
            _sshClient.Connect();
        }
    }

    public void Disconnect()
    {
        if (_sshClient.IsConnected)
        {
            _sshClient.Disconnect();
        }
    }

    public ICommandWrapper CreateCommand(string commandText)
    {
        var command = _sshClient.CreateCommand(commandText);
        return new CommandWrapper(command);
    }

    public ICommandWrapper RunCommand(string commandText)
    {
        var command = _sshClient.RunCommand(commandText);
        return new CommandWrapper(command);
    }

    public void Dispose()
    {
        _sshClient.Dispose();
    }

    private class CommandWrapper(SshCommand command) : ICommandWrapper
    {
        public string Result => command.Result;
        public string Error => command.Error;

        public void Execute()
        {
            command.Execute();
        }
    }
}