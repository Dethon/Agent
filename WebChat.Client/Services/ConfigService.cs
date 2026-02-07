using System.Net.Http.Json;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ConfigService(HttpClient httpClient)
{
    private AppConfig? _config;

    public async Task<AppConfig> GetConfigAsync()
    {
        return _config ??= await httpClient.GetFromJsonAsync<AppConfig>("/api/config")
            ?? new AppConfig(null, []);
    }
}

public record AppConfig(string? AgentUrl, UserConfig[]? Users);
