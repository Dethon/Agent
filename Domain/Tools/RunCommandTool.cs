using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools;

public class RunCommandTool(ICommandRunner commandRunner)
{
    protected const string Name = "RunCommand";

    protected const string Description = """
                                         Executes a command line instruction in the supported shell.
                                         Commands sent in the arguments must be safe and compatible with the server's 
                                         shell environment.
                                         """;

    protected async Task<JsonNode> Run(string command, CancellationToken ct)
    {
        var output = await commandRunner.Run(command, ct);
        return new JsonObject
        {
            ["output"] = output
        };
    }
}