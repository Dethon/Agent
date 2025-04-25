using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.Tools;

public abstract class BaseTool
{
    protected T ParseParams<T>(JsonNode? parameters) where T : class
    {
        try
        {
            var typedParams = parameters?.Deserialize<T>();
            if (typedParams is null)
            {
                throw new ArgumentNullException(
                    nameof(parameters), $"{GetType().Name} cannot have null parameters");
            }

            return typedParams;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Failed to deserialize parameters for {GetType().Name}", nameof(parameters), ex);
        }
    }
}