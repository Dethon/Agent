using System.Collections.Concurrent;
using Domain.Contracts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace McpChannelTelegram.Services;

public sealed class ApprovalCallbackRouter
{
    private const string ApproveCallbackPrefix = "tool_approve:";
    private const string AlwaysCallbackPrefix = "tool_always:";
    private const string RejectCallbackPrefix = "tool_reject:";

    private readonly ConcurrentDictionary<string, ApprovalWaiter> _pending = new();

    public (string ApprovalId, Task<string> ResultTask) RegisterApproval(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var approvalId = Guid.NewGuid().ToString("N")[..8];
        var waiter = new ApprovalWaiter(timeout, cancellationToken);
        _pending[approvalId] = waiter;

        var resultTask = WaitAndCleanupAsync(approvalId, waiter);
        return (approvalId, resultTask);
    }

    public async Task<bool> HandleCallbackQueryAsync(
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
        string result;

        if (data.StartsWith(ApproveCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[ApproveCallbackPrefix.Length..];
            result = "approved";
        }
        else if (data.StartsWith(AlwaysCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[AlwaysCallbackPrefix.Length..];
            result = "approved_and_remember";
        }
        else if (data.StartsWith(RejectCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[RejectCallbackPrefix.Length..];
            result = "denied";
        }
        else
        {
            return false;
        }

        if (!_pending.TryGetValue(approvalId, out var waiter))
        {
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "This approval request has expired.",
                cancellationToken: cancellationToken);
            return true;
        }

        waiter.SetResult(result);

        var responseText = result == "denied" ? "Rejected" : "Approved";
        var icon = result == "denied" ? "\u274c" : "\u2705";
        await botClient.AnswerCallbackQuery(
            callbackQuery.Id,
            $"{icon} {responseText}",
            cancellationToken: cancellationToken);

        if (callbackQuery.Message is not null)
        {
            try
            {
                await botClient.EditMessageText(
                    callbackQuery.Message.Chat.Id,
                    callbackQuery.Message.MessageId,
                    $"{icon} {responseText}",
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Best effort — message may have been deleted
            }
        }

        return true;
    }

    public static InlineKeyboardMarkup CreateApprovalKeyboard(string approvalId)
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("\u2705 Approve", $"{ApproveCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("\ud83d\udd01 Always", $"{AlwaysCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("\u274c Reject", $"{RejectCallbackPrefix}{approvalId}")
            ]
        ]);
    }

    private async Task<string> WaitAndCleanupAsync(string approvalId, ApprovalWaiter waiter)
    {
        try
        {
            return await waiter.Task;
        }
        catch (OperationCanceledException)
        {
            return "denied";
        }
        finally
        {
            _pending.TryRemove(approvalId, out _);
            waiter.Dispose();
        }
    }

    private sealed class ApprovalWaiter : IDisposable
    {
        private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenSource _linkedCts;
        private readonly CancellationTokenRegistration _registration;

        public ApprovalWaiter(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _timeoutCts = new CancellationTokenSource(timeout);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeoutCts.Token);
            _registration = _linkedCts.Token.Register(() => _tcs.TrySetCanceled(_linkedCts.Token));
        }

        public Task<string> Task => _tcs.Task;

        public void SetResult(string result) => _tcs.TrySetResult(result);

        public void Dispose()
        {
            _registration.Dispose();
            _linkedCts.Dispose();
            _timeoutCts.Dispose();
        }
    }
}
