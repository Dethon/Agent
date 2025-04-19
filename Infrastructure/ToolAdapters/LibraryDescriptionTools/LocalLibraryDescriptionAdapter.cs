using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.LibraryDescriptionTools;

public class LocalLibraryDescriptionAdapter(string baseLibraryPath) : LibraryDescriptionTool
{
    protected override Task<JsonNode> Resolve(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}