using System.Text.Json;

namespace Agent.Services;

public record UserConfig(string Id, string AvatarUrl);

public class UserConfigService(IWebHostEnvironment env)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Lazy<IReadOnlyList<UserConfig>> _users = new(() =>
    {
        var fileInfo = env.WebRootFileProvider.GetFileInfo("users.json");
        if (!fileInfo.Exists)
        {
            return [];
        }

        using var stream = fileInfo.CreateReadStream();
        return JsonSerializer.Deserialize<List<UserConfig>>(stream, _jsonOptions) ?? [];
    });

    public UserConfig? GetUserById(string userId)
    {
        return _users.Value.FirstOrDefault(u => u.Id == userId);
    }

    public IReadOnlyList<UserConfig> GetAllUsers()
    {
        return _users.Value;
    }
}