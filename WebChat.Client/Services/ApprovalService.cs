using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class ApprovalService(IChatLiveConnection liveConnection) : IApprovalService
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

    public Task<HubResult<ToolApprovalRequestMessage>> GetPendingApprovalForTopicAsync(string topicId) =>
        liveConnection.InvokeAsync<ToolApprovalRequestMessage>("GetPendingApprovalForTopic", topicId);
}