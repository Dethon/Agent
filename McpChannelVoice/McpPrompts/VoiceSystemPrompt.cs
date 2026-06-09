using System.ComponentModel;
using Domain.Prompts;
using McpChannelVoice.Services;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpPrompts;

[McpServerPromptType]
public class VoiceSystemPrompt(SatelliteRegistry registry)
{
    [McpServerPrompt(Name = VoicePrompt.Name)]
    [Description(VoicePrompt.Description)]
    public string GetVoicePrompt()
    {
        var satellites = registry.GetAllIds()
            .Select(id => (Id: id, Config: registry.GetById(id)))
            .Where(x => x.Config is not null)
            .Select(x => (x.Id, x.Config!.DisplayLocation))
            .ToList();

        return VoicePrompt.Build(satellites);
    }
}