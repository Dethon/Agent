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
            "WyomingClient": { "TrailingSilenceMs": 800, "MaxUtteranceMs": 15000 },
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
              "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis", "Address": "tcp://host.docker.internal:10800" }
            }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.GetSection("Voice").Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.WyomingClient.TrailingSilenceMs.ShouldBe(800);
        settings.WyomingClient.MaxUtteranceMs.ShouldBe(15000);
        settings.Stt.Provider.ShouldBe("Wyoming");
        settings.Stt.Wyoming!.Model.ShouldBe("base");
        settings.Tts.Wyoming!.Voice.ShouldBe("es_ES-davefx-medium");
        settings.ConfidenceThreshold.ShouldBe(0.4);
        settings.Announce.Token.ShouldBe("secret");
        settings.Announce.DefaultPriority.ShouldBe(AnnouncePriorityDefault.Normal);
        settings.Satellites.Count.ShouldBe(1);
        settings.Satellites["kitchen-01"].Identity.ShouldBe("household");
        settings.Satellites["kitchen-01"].Address.ShouldBe("tcp://host.docker.internal:10800");
    }

    [Fact]
    public void ConversationLifetime_DefaultsToFiveMinutes()
    {
        var settings = new VoiceSettings();

        settings.ConversationLifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void VoiceSettings_BindsSatelliteFromEnvironmentVariables()
    {
        // Mirrors how docker-compose.override.debug.yml delivers the satellite topology:
        // appsettings.Development.json is excluded from the image by .dockerignore, so the
        // hub must pick up satellites from env. Verifies the exact key shape works, including
        // the hyphen in the satellite id and the "__" segment separator.
        var vars = new Dictionary<string, string>
        {
            ["Voice__Satellites__kitchen-01__Identity"] = "household",
            ["Voice__Satellites__kitchen-01__Room"] = "Kitchen",
            ["Voice__Satellites__kitchen-01__WakeWord"] = "hey_jarvis",
            ["Voice__Satellites__kitchen-01__Address"] = "tcp://host.docker.internal:10800"
        };

        foreach (var (k, v) in vars)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var settings = config.GetSection("Voice").Get<VoiceSettings>();

            settings.ShouldNotBeNull();
            settings!.Satellites.Count.ShouldBe(1);
            settings.Satellites.ContainsKey("kitchen-01").ShouldBeTrue();
            settings.Satellites["kitchen-01"].Room.ShouldBe("Kitchen");
            settings.Satellites["kitchen-01"].Address.ShouldBe("tcp://host.docker.internal:10800");
        }
        finally
        {
            foreach (var k in vars.Keys)
            {
                Environment.SetEnvironmentVariable(k, null);
            }
        }
    }
}