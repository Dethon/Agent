using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class ToolApprovalChatClient : FunctionInvokingChatClient
{
    private readonly IToolApprovalHandler _approvalHandler;
    private readonly HashSet<string> _whitelistedTools;

    public ToolApprovalChatClient(
        IChatClient innerClient,
        IToolApprovalHandler approvalHandler,
        IEnumerable<string>? whitelistedTools = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(approvalHandler);
        _approvalHandler = approvalHandler;
        _whitelistedTools = whitelistedTools is not null
            ? new HashSet<string>(whitelistedTools, StringComparer.OrdinalIgnoreCase)
            : [];

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
            toolName,
            ToReadOnlyDictionary(context.CallContent.Arguments));

        if (_whitelistedTools.Contains(toolName))
        {
            await _approvalHandler.NotifyAutoApprovedAsync([request], cancellationToken);
            return await base.InvokeFunctionAsync(context, cancellationToken);
        }

        var approved = await _approvalHandler.RequestApprovalAsync([request], cancellationToken);
        if (approved)
        {
            return await base.InvokeFunctionAsync(context, cancellationToken);
        }

        context.Terminate = true;
        return $"Tool execution was rejected by user: {toolName}. Waiting for new input.";
    }

    private static IReadOnlyDictionary<string, object?> ToReadOnlyDictionary(IDictionary<string, object?>? source)
    {
        return source as IReadOnlyDictionary<string, object?>
               ?? new Dictionary<string, object?>(source ?? new Dictionary<string, object?>());
    }
}