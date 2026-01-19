namespace WebChat.Client.Contracts;

public interface ILocalStorageService
{
    ValueTask<string?> GetAsync(string key);
    ValueTask SetAsync(string key, string value);
    ValueTask RemoveAsync(string key);
}