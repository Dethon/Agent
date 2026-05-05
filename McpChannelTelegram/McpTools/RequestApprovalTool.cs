using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _approvalTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "request_approval")]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests,
        IServiceProvider services)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var registry = services.GetRequiredService<BotRegistry>();
        var router = services.GetRequiredService<ApprovalCallbackRouter>();
        var (chatId, threadId) = ParseConversationId(p.ConversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        var toolRequests = JsonSerializer.Deserialize<List<ToolRequest>>(p.Requests,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        if (p.Mode == ApprovalMode.Notify)
        {
            var toolNames = toolRequests.Select(r => r.ToolName.Split("__").Last());
            var message = $"\u2705 Auto-approved: {string.Join(", ", toolNames)}";

            await botClient.SendMessage(
                chatId,
                message,
                messageThreadId: threadId,
                cancellationToken: CancellationToken.None);

            return "notified";
        }

        // Interactive approval mode
        var (approvalId, resultTask) = router.RegisterApproval(_approvalTimeout, CancellationToken.None);
        var keyboard = ApprovalCallbackRouter.CreateApprovalKeyboard(approvalId);

        var approvalMessage = FormatApprovalMessage(toolRequests);

        await botClient.SendMessage(
            chatId,
            approvalMessage,
            ParseMode.Html,
            replyMarkup: keyboard,
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);

        return await resultTask;
    }

    private static string FormatApprovalMessage(List<ToolRequest> requests)
    {
        var sb = new StringBuilder();
        var toolNames = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));
        sb.AppendLine($"<b>\ud83d\udd27 Approval Required:</b> <code>{HtmlEncode(toolNames)}</code>");

        foreach (var request in requests)
        {
            if (request.Arguments is null || request.Arguments.Count == 0)
            {
                continue;
            }

            var details = new StringBuilder();
            foreach (var (key, value) in request.Arguments)
            {
                var formatted = value?.ToString()?.Replace("\n", " ") ?? "null";
                if (formatted.Length > 100)
                {
                    formatted = formatted[..100] + "...";
                }

                details.AppendLine($"<i>{HtmlEncode(key)}:</i> {HtmlEncode(formatted)}");
            }

            sb.AppendLine($"<blockquote expandable>{details.ToString().TrimEnd()}</blockquote>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static (long ChatId, int? ThreadId) ParseConversationId(string conversationId)
    {
        var parts = conversationId.Split(':');
        var chatId = long.Parse(parts[0]);
        var threadIdVal = long.Parse(parts[1]);

        return threadIdVal == chatId
            ? (chatId, null)
            : (chatId, Convert.ToInt32(threadIdVal));
    }

    private sealed record ToolRequest(
        string ToolName,
        Dictionary<string, object?>? Arguments);
}