using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Infrastructure.Clients;

public sealed class TelegramToolApprovalHandler : IToolApprovalHandler
{
    private const string ApproveCallbackPrefix = "tool_approve:";
    private const string RejectCallbackPrefix = "tool_reject:";

    private readonly ITelegramBotClient _client;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    private long? _activeChatId;
    private int? _activeThreadId;

    public TelegramToolApprovalHandler(ITelegramBotClient client, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public void SetActiveChat(long chatId, int? threadId)
    {
        _activeChatId = chatId;
        _activeThreadId = threadId;
    }

    public async Task<bool> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        if (_activeChatId is null)
        {
            return false;
        }

        var approvalId = Guid.NewGuid().ToString("N")[..8];
        var context = new ApprovalContext();
        _pendingApprovals[approvalId] = context;

        try
        {
            var message = FormatApprovalMessage(requests);
            var keyboard = CreateApprovalKeyboard(approvalId);

            await _client.SendMessage(
                _activeChatId.Value,
                message,
                ParseMode.Html,
                replyMarkup: keyboard,
                messageThreadId: _activeThreadId,
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
                return false;
            }
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
        }
    }

    public async Task<bool> HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        string? approvalId;
        bool approved;

        if (data.StartsWith(ApproveCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[ApproveCallbackPrefix.Length..];
            approved = true;
        }
        else if (data.StartsWith(RejectCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[RejectCallbackPrefix.Length..];
            approved = false;
        }
        else
        {
            return false;
        }

        if (!_pendingApprovals.TryGetValue(approvalId, out var context))
        {
            await _client.AnswerCallbackQuery(
                callbackQuery.Id,
                "This approval request has expired.",
                cancellationToken: cancellationToken);
            return true;
        }

        context.SetResult(approved);

        var responseText = approved ? "✅ Approved" : "❌ Rejected";
        await _client.AnswerCallbackQuery(
            callbackQuery.Id,
            responseText,
            cancellationToken: cancellationToken);

        if (callbackQuery.Message is not null)
        {
            await _client.EditMessageReplyMarkup(
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                replyMarkup: null,
                cancellationToken: cancellationToken);
        }

        return true;
    }

    private static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🔧 Tool Approval Required</b>");
        sb.AppendLine();

        foreach (var request in requests)
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

        return sb.ToString().TrimEnd();
    }

    private static InlineKeyboardMarkup CreateApprovalKeyboard(string approvalId)
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"{ApproveCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("❌ Reject", $"{RejectCallbackPrefix}{approvalId}")
            ]
        ]);
    }

    private async Task SendTimeoutMessageAsync(CancellationToken cancellationToken)
    {
        if (_activeChatId is null)
        {
            return;
        }

        await _client.SendMessage(
            _activeChatId.Value,
            "⏱️ Tool approval timed out. Execution rejected.",
            messageThreadId: _activeThreadId,
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
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(bool approved)
        {
            _tcs.TrySetResult(approved);
        }

        public Task<bool> WaitForApprovalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
            return _tcs.Task;
        }
    }
}