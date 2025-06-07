using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Infrastructure.Utils;

public static class JsonSchema
{
    public static JsonNode? CreateParametersSchema(Type? paramsType)
    {
        if (paramsType is null)
        {
            return null;
        }

        var options = JsonSerializerOptions.Default;
        var schema = options.GetJsonSchemaAsNode(paramsType);
        schema["type"] = "object";
        return schema;
    }
}