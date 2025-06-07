using Domain.DTOs;
using Infrastructure.Utils;

namespace Infrastructure.LLMAdapters.OpenRouter;

public static class MessageExtensions
{
    public static OpenRouterMessage ToOpenRouterMessage(this Message message)
    {
        return message switch
        {
            ToolMessage toolMessage => toolMessage.ToOpenRouterMessage(),
            ToolRequestMessage toolRequestMessage => toolRequestMessage.ToOpenRouterMessage(),
            _ => new OpenRouterMessage
            {
                Role = message.Role.ToOpenRouterRoleString(),
                Reasoning = message.Reasoning,
                Content = message.Content
            }
        };
    }

    private static OpenRouterMessage ToOpenRouterMessage(this ToolMessage message)
    {
        return new OpenRouterMessage
        {
            Role = message.Role.ToOpenRouterRoleString(),
            Reasoning = message.Reasoning,
            Content = message.Content,
            ToolCallId = message.ToolCallId
        };
    }

    private static OpenRouterMessage ToOpenRouterMessage(this ToolRequestMessage message)
    {
        return new OpenRouterMessage
        {
            Role = message.Role.ToOpenRouterRoleString(),
            Content = message.Content,
            Reasoning = message.Reasoning,
            ToolCalls = message.ToolCalls.Select(tc => new OpenRouterToolCall
            {
                Id = tc.Id,
                Type = "function",
                Function = new OpenRouterFunctionCall
                {
                    Name = tc.Name,
                    Arguments = tc.Parameters?.ToJsonString()
                }
            }).ToArray()
        };
    }

    private static string ToOpenRouterRoleString(this Role role)
    {
        return role switch
        {
            Role.User => "user",
            Role.Assistant => "assistant",
            Role.System => "system",
            Role.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, $"Unexpected message role: {role}")
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
                Parameters = JsonSchema.CreateParametersSchema(tool.ParamsType)
            }
        };
    }
}