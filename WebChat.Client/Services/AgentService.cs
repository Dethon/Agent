using System.Net.Http.Json;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AgentService(
    ConfigService configService,
    HttpClient httpClient,
    NavigationManager navigationManager) : IAgentService
{
    private string? _baseUrl;

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync()
    {
        var baseUrl = await GetBaseUrlAsync();
        var agents = await httpClient.GetFromJsonAsync<List<AgentInfo>>($"{baseUrl}/api/agents");
        return agents ?? [];
    }

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(string userId)
    {
        var baseUrl = await GetBaseUrlAsync();
        var url = $"{baseUrl}/api/agents?userId={Uri.EscapeDataString(userId)}";
        var agents = await httpClient.GetFromJsonAsync<List<AgentInfo>>(url);
        return agents ?? [];
    }

    public async Task<AgentInfo> RegisterCustomAgentAsync(string userId, CustomAgentRegistration registration)
    {
        var baseUrl = await GetBaseUrlAsync();
        var url = $"{baseUrl}/api/agents?userId={Uri.EscapeDataString(userId)}";
        var response = await httpClient.PostAsJsonAsync(url, registration);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentInfo>()
            ?? throw new InvalidOperationException("Failed to deserialize agent registration response");
    }

    public async Task<bool> UnregisterCustomAgentAsync(string userId, string agentId)
    {
        var baseUrl = await GetBaseUrlAsync();
        var url = $"{baseUrl}/api/agents/{Uri.EscapeDataString(agentId)}?userId={Uri.EscapeDataString(userId)}";
        var response = await httpClient.DeleteAsync(url);
        return response.IsSuccessStatusCode;
    }

    private async Task<string> GetBaseUrlAsync()
    {
        if (_baseUrl is not null)
        {
            return _baseUrl;
        }

        var config = await configService.GetConfigAsync();
        var isHttps = navigationManager.BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // When on HTTPS (through reverse proxy), use same origin so Caddy routes to the agent
        _baseUrl = string.IsNullOrEmpty(config.AgentUrl) || isHttps
            ? navigationManager.BaseUri.TrimEnd('/')
            : config.AgentUrl.TrimEnd('/');

        return _baseUrl;
    }
}