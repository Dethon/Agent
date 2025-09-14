using System.Text.Json.Nodes;
using Domain.Tools.Shared;

namespace Domain.Tools;

public class GetCliPlatform
{
    protected const string Name = "GetCliPlatform";

    protected const string Description = """
                                         Retrieves the platform information of the command line interface (CLI) on the 
                                         server. 
                                         Must be used before any other CLI-related tools to ensure compatibility.
                                         Values can be either either bash, pwsh, cmd or sh.
                                         """;

    protected async Task<JsonNode> Run(CancellationToken ct)
    {
        return new JsonObject
        {
            ["platform"] = await SupportedShell.GetShell(ct)
        };
    }
}