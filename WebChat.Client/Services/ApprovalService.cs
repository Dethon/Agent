using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class ApprovalService(ChatLiveConnection liveConnection) : IApprovalService
{
    public async Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return false;
        }

        return await hubConnection.InvokeAsync<bool>("RespondToApprovalAsync", approvalId, result);
    }

    public async Task<ToolApprovalRequestMessage?> GetPendingApprovalForTopicAsync(string topicId)
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return null;
        }

        return await hubConnection.InvokeAsync<ToolApprovalRequestMessage?>("GetPendingApprovalForTopic", topicId);
    }
}