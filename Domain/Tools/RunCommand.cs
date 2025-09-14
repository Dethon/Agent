using System.Text.Json.Nodes;
using Domain.Tools.Shared;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Process = System.Diagnostics.Process;

namespace Domain.Tools;

public class RunCommand
{
    protected const string Name = "RunCommand";

    protected const string Description = """
                                         Executes a command line instruction in the supported shell.
                                         Commands sent in the arguments must be safe and compatible with the server's 
                                         shell environment.
                                         """;

    protected async Task<JsonNode> Run(string command, CancellationToken ct)
    {
        var (shell, commandOption) = await SupportedShell.GetShellAndCommandOption(ct);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"{commandOption} {command}",
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