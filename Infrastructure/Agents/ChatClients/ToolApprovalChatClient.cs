using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class ToolApprovalChatClient : FunctionInvokingChatClient
{
    private readonly IToolApprovalHandler _approvalHandler;
    private readonly ToolPatternMatcher _patternMatcher;
    private readonly HashSet<string> _dynamicallyApproved;

    public ToolApprovalChatClient(
        IChatClient innerClient,
        IToolApprovalHandler approvalHandler,
        IEnumerable<string>? whitelistPatterns = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(approvalHandler);
        _approvalHandler = approvalHandler;
        _patternMatcher = new ToolPatternMatcher(whitelistPatterns);
        _dynamicallyApproved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        if (_patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            await _approvalHandler.NotifyAutoApprovedAsync([request], cancellationToken);
            return await base.InvokeFunctionAsync(context, cancellationToken);
        }

        var result = await _approvalHandler.RequestApprovalAsync([request], cancellationToken);

        switch (result)
        {
            case ToolApprovalResult.ApprovedAndRemember:
                _dynamicallyApproved.Add(toolName);
                return await base.InvokeFunctionAsync(context, cancellationToken);

            case ToolApprovalResult.Approved:
                return await base.InvokeFunctionAsync(context, cancellationToken);

            case ToolApprovalResult.Rejected:
            default:
                context.Terminate = true;
                return $"Tool execution was rejected by user: {toolName}. Waiting for new input.";
        }
    }

    private static IReadOnlyDictionary<string, object?> ToReadOnlyDictionary(IDictionary<string, object?>? source)
    {
        return source as IReadOnlyDictionary<string, object?>
               ?? new Dictionary<string, object?>(source ?? new Dictionary<string, object?>());
    }
}