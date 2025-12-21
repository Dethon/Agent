namespace Infrastructure.CommandRunners;

public class ShRunner(string workingDirectory) : BaseCliRunner
{
    public override async Task<string> Run(string command, CancellationToken ct)
    {
        return await Run("sh", $"-c \"cd {workingDirectory} && {command}\"", ct);
    }
}