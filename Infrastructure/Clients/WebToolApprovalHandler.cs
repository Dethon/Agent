using System.Text;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public sealed class WebToolApprovalHandler(
    WebChatMessengerClient messengerClient,
    long chatId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return NotifyAndApproveAsync(requests, cancellationToken);
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return NotifyAndApproveAsync(requests, cancellationToken);
    }

    private async Task<ToolApprovalResult> NotifyAndApproveAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var toolCallsText = FormatToolCalls(requests);

        var responseMessage = new ChatResponseMessage
        {
            CalledTools = toolCallsText
        };

        await messengerClient.SendResponse(chatId, responseMessage, 0, null, cancellationToken);

        return ToolApprovalResult.AutoApproved;
    }

    private static string FormatToolCalls(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();

        foreach (var request in requests)
        {
            var toolName = request.ToolName.Split(':').Last();
            sb.AppendLine($"🔧 {toolName}");

            if (request.Arguments.Count <= 0)
            {
                continue;
            }

            foreach (var (key, value) in request.Arguments)
            {
                var formattedValue = FormatArgumentValue(value);
                if (formattedValue.Length > 100)
                {
                    formattedValue = formattedValue[..100] + "...";
                }

                sb.AppendLine($"  {key}: {formattedValue}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatArgumentValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => s.Replace("\n", " ").Replace("\r", ""),
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString()?.Replace("\n", " ") ?? "",
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? ""
        };
    }
}

public sealed class WebToolApprovalHandlerFactory(WebChatMessengerClient messengerClient) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return new WebToolApprovalHandler(messengerClient, agentKey.ChatId);
    }
}