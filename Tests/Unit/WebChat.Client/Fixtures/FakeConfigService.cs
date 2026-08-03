using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeConfigService(CallRecorder? recorder = null) : IConfigService
{
    private readonly Dictionary<string, SpaceConfig> _spaces = new();

    public AppConfig Config { get; set; } = new(null, []);

    public int ConfigCalls { get; private set; }

    public FakeConfigService WithSpace(string slug, string name = "Main", string accentColor = "#112233")
    {
        _spaces[slug] = new SpaceConfig(slug, name, accentColor);
        return this;
    }

    // Not recorded: the app config is read from the detached push path as well, so its
    // position in the call log would depend on when that task happens to run.
    public Task<AppConfig> GetConfigAsync()
    {
        ConfigCalls++;
        return Task.FromResult(Config);
    }

    public Task<SpaceConfig?> GetSpaceAsync(string slug)
    {
        recorder?.Record($"space:{slug}");
        return Task.FromResult(_spaces.GetValueOrDefault(slug));
    }
}