using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Integration.WebChat.Client.Adapters;

public sealed class HubConnectionApprovalService(HubConnection connection) : IApprovalService
{
    public async Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        return await connection.InvokeAsync<bool>("RespondToApprovalAsync", approvalId, result);
    }

    public async Task<ToolApprovalRequestMessage?> GetPendingApprovalForTopicAsync(string topicId)
    {
        return await connection.InvokeAsync<ToolApprovalRequestMessage?>(
            "GetPendingApprovalForTopic", topicId);
    }
}