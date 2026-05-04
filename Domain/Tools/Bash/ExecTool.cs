using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Bash;

public class ExecTool(ICommandRunner runner)
{
    protected const string Description = """
        Execute a bash command (`bash -lc <command>`) inside the sandbox container.
        The path argument is a relative path under the sandbox root that becomes the CWD.
        Empty string or "." use the home directory; absolute paths are used literally.
        Output is truncated at the configured cap. On timeout the process tree is killed.
        Non-zero exit codes are returned in the result, not as errors.
        """;

    protected Task<JsonNode> Run(
        string path,
        string command,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        return runner.RunAsync(path, command, timeoutSeconds, cancellationToken);
    }
}
