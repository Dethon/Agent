using Domain.Contracts;

namespace Infrastructure.Services;

public static class CommandRunnerFactory
{
    public static async Task<ICommandRunner> Create(string workingDirectory, CancellationToken ct)
    {
        return await AvailableShell.GetShell(ct) switch
        {
            Shell.PowerShell => new PowerShellRunner(workingDirectory),
            Shell.Bash => new BashRunner(workingDirectory),
            Shell.Sh => new ShRunner(workingDirectory),
            Shell.Cmd => new CmdRunner(workingDirectory),
            _ => throw new NotSupportedException("No supported shell found (Powershell, Bash, Sh, Cmd)")
        };
    }
}