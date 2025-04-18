using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Domain.DTOs;

namespace Infrastructure.LLMAdapters.OpenRouter;

public static class MessageExtensions
{
    public static OpenRouterMessage ToOpenRouterMessage(this Message message)
    {
        return new OpenRouterMessage
        {
            Role = message.Role.ToString().ToLowerInvariant(),
            Content = message.Content,
            ToolCalls = message.ToolCalls.Select(tc => new OpenRouterToolCall
            {
                Id = tc.Id,
                Type = tc.Name,
                Function = new OpenRouterFunctionCall
                {
                    Name = tc.Name,
                    Arguments = tc.Parameters
                }
            }).ToArray()
        };
    }
}

public static class ToolDefinitionExtensions
{
    public static OpenRouterTool ToOpenRouterTool(this ToolDefinition tool)
    {
        return new OpenRouterTool
        {
            Function = new OpenRouterFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = CreateParametersSchema(tool.ParamsType)
            }
        };
    }

    private static JsonNode CreateParametersSchema(Type paramsType)
    {
        var options = JsonSerializerOptions.Default;
        var schema = options.GetJsonSchemaAsNode(paramsType);
        schema["type"] = "object";
        return schema;
    }
}