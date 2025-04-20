using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.Tools;

public abstract class BaseTool
{
    protected static T ParseParams<T>(JsonNode? parameters) where T : class
    {
        var typedParams = parameters?.Deserialize<T>();
        if (typedParams is null)
        {
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileDownloadTool)} cannot have null parameters");
        }

        return typedParams;
    }
}