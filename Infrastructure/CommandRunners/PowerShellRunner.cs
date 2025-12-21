namespace Infrastructure.CommandRunners;

public class PowerShellRunner(string workingDirectory) : BaseCliRunner
{
    public override async Task<string> Run(string command, CancellationToken ct)
    {
        return await Run("pwsh", $"-WorkingDirectory \"{workingDirectory}\" -Command \"{command}\"", ct);
    }
}