namespace Infrastructure.Services;

public class BashRunner(string workingDirectory) : BaseCliRunner
{
    public override async Task<string> Run(string command, CancellationToken ct)
    {
        return await Run("bash", $"-c \"cd {workingDirectory} && {command}\"", ct);
    }
}