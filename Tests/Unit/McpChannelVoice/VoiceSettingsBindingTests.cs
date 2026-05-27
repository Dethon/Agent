using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSettingsBindingTests
{
    [Fact]
    public void VoiceSettings_BindsFromJson()
    {
        var json = """
        {
          "Voice": {
            "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
            "Stt": {
              "Provider": "Wyoming",
              "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base", "Language": "es" }
            },
            "Tts": {
              "Provider": "Wyoming",
              "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" }
            },
            "ConfidenceThreshold": 0.4,
            "Announce": {
              "Enabled": true,
              "Token": "secret",
              "BindToLoopbackOnly": false,
              "QueueMaxDepth": 8,
              "DefaultPriority": "Normal"
            },
            "Satellites": {
              "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis" }
            }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.GetSection("Voice").Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.WyomingServer.Port.ShouldBe(10700);
        settings.Stt.Provider.ShouldBe("Wyoming");
        settings.Stt.Wyoming!.Model.ShouldBe("base");
        settings.Tts.Wyoming!.Voice.ShouldBe("es_ES-davefx-medium");
        settings.ConfidenceThreshold.ShouldBe(0.4);
        settings.Announce.Token.ShouldBe("secret");
        settings.Announce.DefaultPriority.ShouldBe(AnnouncePriorityDefault.Normal);
        settings.Satellites.Count.ShouldBe(1);
        settings.Satellites["kitchen-01"].Identity.ShouldBe("household");
    }
}