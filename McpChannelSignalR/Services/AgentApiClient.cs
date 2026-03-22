using System.Net.Http.Json;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

public sealed class AgentApiClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(string? userId = null)
    {
        var url = userId is not null ? $"/api/agents?userId={Uri.EscapeDataString(userId)}" : "/api/agents";
        var agents = await httpClient.GetFromJsonAsync<List<AgentInfo>>(url);
        return agents ?? [];
    }

    public async Task<AgentInfo> RegisterCustomAgentAsync(string userId, CustomAgentRegistration registration)
    {
        var url = $"/api/agents?userId={Uri.EscapeDataString(userId)}";
        var response = await httpClient.PostAsJsonAsync(url, registration);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentInfo>()
            ?? throw new InvalidOperationException("Failed to deserialize agent registration response");
    }

    public async Task<bool> UnregisterCustomAgentAsync(string userId, string agentId)
    {
        var url = $"/api/agents/{Uri.EscapeDataString(agentId)}?userId={Uri.EscapeDataString(userId)}";
        var response = await httpClient.DeleteAsync(url);
        return response.IsSuccessStatusCode;
    }
}
