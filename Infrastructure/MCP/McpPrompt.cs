using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Role = Domain.DTOs.Role;

namespace Infrastructure.MCP;

// WIP
public class McpPrompt<T> : McpServerPrompt where T : IPrompt
{
    public override Prompt ProtocolPrompt { get; } = new()
    {
        Name = T.Name,
        Description = T.Description,
        Arguments = T.ParamsType?.GetProperties().Select(p => new PromptArgument
        {
            Name = p.Name,
            Required = true
        }).ToList()
    };

    public override async ValueTask<GetPromptResult> GetAsync(
        RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
    {
        var jsonNode = request.Params?.Arguments != null
            ? JsonNode.Parse(JsonSerializer.Serialize(request.Params.Arguments))
            : null;
        var prompt = request.Services!.GetRequiredService<T>();
        return new GetPromptResult
        {
            Messages = (await prompt.Get(jsonNode, cancellationToken)).Select(x => new PromptMessage
            {
                Role = x.Role switch
                {
                    Role.Assistant => ModelContextProtocol.Protocol.Role.Assistant,
                    _ => ModelContextProtocol.Protocol.Role.User
                },
                Content = new Content
                {
                    Type = "text",
                    Text = x.Content
                }
            }).ToList()
        };
    }
}