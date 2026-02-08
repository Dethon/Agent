namespace Infrastructure.CommandRunners;

public class CmdRunner(string workingDirectory) : BaseCliRunner
{
    public override async Task<string> Run(string command, CancellationToken ct)
    {
        return await Run("cmd", $"/C \"cd /d \"{workingDirectory}\" && \"{command}\"", ct);
    }
}