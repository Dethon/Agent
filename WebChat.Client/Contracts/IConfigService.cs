using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IConfigService
{
    Task<AppConfig> GetConfigAsync();
    Task<SpaceConfig?> GetSpaceAsync(string slug);
}