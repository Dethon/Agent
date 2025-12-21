using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Infrastructure.Clients;

public sealed class TelegramToolApprovalHandler(
    ITelegramBotClient client,
    long chatId,
    int? threadId,
    TimeSpan? timeout = null) : IToolApprovalHandler
{
    private const string ApproveCallbackPrefix = "tool_approve:";
    private const string AlwaysCallbackPrefix = "tool_always:";
    private const string RejectCallbackPrefix = "tool_reject:";

    private static readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    public async Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var approvalId = Guid.NewGuid().ToString("N")[..8];
        var context = new ApprovalContext();
        _pendingApprovals[approvalId] = context;

        try
        {
            var message = FormatApprovalMessage(requests);
            var keyboard = CreateApprovalKeyboard(approvalId);

            await client.SendMessage(
                chatId,
                message,
                ParseMode.Html,
                replyMarkup: keyboard,
                messageThreadId: threadId,
                cancellationToken: cancellationToken);

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                return await context.WaitForApprovalAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                await SendTimeoutMessageAsync(cancellationToken);
                return ToolApprovalResult.Rejected;
            }
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
        }
    }

    public static async Task<bool> HandleCallbackQueryAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        string? approvalId;
        ToolApprovalResult result;

        if (data.StartsWith(ApproveCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[ApproveCallbackPrefix.Length..];
            result = ToolApprovalResult.Approved;
        }
        else if (data.StartsWith(AlwaysCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[AlwaysCallbackPrefix.Length..];
            result = ToolApprovalResult.ApprovedAndRemember;
        }
        else if (data.StartsWith(RejectCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[RejectCallbackPrefix.Length..];
            result = ToolApprovalResult.Rejected;
        }
        else
        {
            return false;
        }

        if (!_pendingApprovals.TryGetValue(approvalId, out var context))
        {
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "This approval request has expired.",
                cancellationToken: cancellationToken);
            return true;
        }

        context.SetResult(result);

        var responseText = result switch
        {
            ToolApprovalResult.Approved => "✅ Approved",
            ToolApprovalResult.ApprovedAndRemember => "✅ Always approved",
            _ => "❌ Rejected"
        };
        await botClient.AnswerCallbackQuery(
            callbackQuery.Id,
            responseText,
            cancellationToken: cancellationToken);

        if (callbackQuery.Message is not null)
        {
            await botClient.EditMessageReplyMarkup(
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                replyMarkup: null,
                cancellationToken: cancellationToken);
        }

        return true;
    }

    public async Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var message = FormatAutoApprovedMessage(requests);

        await client.SendMessage(
            chatId,
            message,
            ParseMode.Html,
            messageThreadId: threadId,
            cancellationToken: cancellationToken);
    }

    private static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🔧 Tool Approval Required</b>");
        sb.AppendLine();

        foreach (var request in requests)
        {
            AppendToolDetails(sb, request);
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatAutoApprovedMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>✅ Tool Auto-Approved</b>");
        sb.AppendLine();

        foreach (var request in requests)
        {
            AppendToolDetails(sb, request);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendToolDetails(StringBuilder sb, ToolApprovalRequest request)
    {
        sb.AppendLine($"<b>Tool:</b> <code>{HtmlEncode(request.ToolName)}</code>");

        if (request.Arguments.Count > 0)
        {
            sb.AppendLine("<b>Arguments:</b>");
            var json = JsonSerializer.Serialize(request.Arguments, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            sb.AppendLine($"<pre><code class=\"language-json\">{HtmlEncode(json)}</code></pre>");
        }

        sb.AppendLine();
    }

    private static InlineKeyboardMarkup CreateApprovalKeyboard(string approvalId)
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"{ApproveCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("🔁 Always", $"{AlwaysCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("❌ Reject", $"{RejectCallbackPrefix}{approvalId}")
            ]
        ]);
    }

    private async Task SendTimeoutMessageAsync(CancellationToken cancellationToken)
    {
        await client.SendMessage(
            chatId,
            "⏱️ Tool approval timed out. Execution rejected.",
            messageThreadId: threadId,
            cancellationToken: cancellationToken);
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private sealed class ApprovalContext
    {
        private readonly TaskCompletionSource<ToolApprovalResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(ToolApprovalResult result)
        {
            _tcs.TrySetResult(result);
        }

        public Task<ToolApprovalResult> WaitForApprovalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
            return _tcs.Task;
        }
    }
}

public sealed class TelegramToolApprovalHandlerFactory(ITelegramBotClient client) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return new TelegramToolApprovalHandler(client, agentKey.ChatId, (int?)agentKey.ThreadId);
    }
}