using System.Diagnostics;
using Domain.Contracts;

namespace Infrastructure.Services;

public abstract class BaseCliRunner : ICommandRunner
{
    public abstract Task<string> Run(string command, CancellationToken ct);

    protected async Task<string> Run(string fileName, string args, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var result = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return result;
    }
}