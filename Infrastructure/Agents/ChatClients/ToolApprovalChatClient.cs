using System.Diagnostics;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class ToolApprovalChatClient : FunctionInvokingChatClient
{
    private readonly IToolApprovalHandler _approvalHandler;
    private readonly ToolPatternMatcher _patternMatcher;
    private readonly HashSet<string> _dynamicallyApproved;
    private readonly IMetricsPublisher? _metricsPublisher;

    public ToolApprovalChatClient(
        IChatClient innerClient,
        IToolApprovalHandler approvalHandler,
        IEnumerable<string>? whitelistPatterns = null,
        IMetricsPublisher? metricsPublisher = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(approvalHandler);
        _approvalHandler = approvalHandler;
        _patternMatcher = new ToolPatternMatcher(whitelistPatterns);
        _dynamicallyApproved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _metricsPublisher = metricsPublisher;

        IncludeDetailedErrors = true;
        MaximumIterationsPerRequest = 50;
        AllowConcurrentInvocation = true;
        MaximumConsecutiveErrorsPerRequest = 3;
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var toolName = context.Function.Name;
        var request = new ToolApprovalRequest(
            context.Messages.LastOrDefault()?.MessageId,
            toolName,
            ToReadOnlyDictionary(context.CallContent.Arguments));

        if (_patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            await _approvalHandler.NotifyAutoApprovedAsync([request], cancellationToken);
            return await InvokeWithMetricsAsync(context, toolName, cancellationToken);
        }

        var result = await _approvalHandler.RequestApprovalAsync([request], cancellationToken);

        switch (result)
        {
            case ToolApprovalResult.ApprovedAndRemember:
                _dynamicallyApproved.Add(toolName);
                return await InvokeWithMetricsAsync(context, toolName, cancellationToken);

            case ToolApprovalResult.Approved:
            case ToolApprovalResult.AutoApproved:
                return await InvokeWithMetricsAsync(context, toolName, cancellationToken);

            case ToolApprovalResult.Rejected:
            default:
                context.Terminate = true;
                return $"Tool execution was rejected by user: {toolName}. Waiting for new input.";
        }
    }

    private async ValueTask<object?> InvokeWithMetricsAsync(
        FunctionInvocationContext context,
        string toolName,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await base.InvokeFunctionAsync(context, cancellationToken);
            sw.Stop();
            if (_metricsPublisher is not null)
            {
                var (isError, errorMessage) = DetectError(result);
                await _metricsPublisher.PublishAsync(new ToolCallEvent
                {
                    ToolName = toolName,
                    DurationMs = sw.ElapsedMilliseconds,
                    Success = !isError,
                    Error = errorMessage
                }, cancellationToken);
            }
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (_metricsPublisher is not null)
            {
                await _metricsPublisher.PublishAsync(new ToolCallEvent
                {
                    ToolName = toolName,
                    DurationMs = sw.ElapsedMilliseconds,
                    Success = false,
                    Error = ex.Message
                }, cancellationToken);
            }
            throw;
        }
    }

    private static (bool IsError, string? Message) DetectError(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } json)
        {
            return (false, null);
        }

        // MCP tools: CallToolResult with isError: true
        if (json.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            return (true, json.TryGetProperty("content", out var content) ? content.ToString() : null);
        }

        // Domain tools: standard envelope { "ok": false, "errorCode": "...", "message": "..." }
        if (json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            return (true, json.TryGetProperty("message", out var message) ? message.GetString() : null);
        }

        return (false, null);
    }

    private static IReadOnlyDictionary<string, object?> ToReadOnlyDictionary(IDictionary<string, object?>? source)
    {
        return source as IReadOnlyDictionary<string, object?>
               ?? new Dictionary<string, object?>(source ?? new Dictionary<string, object?>());
    }
}