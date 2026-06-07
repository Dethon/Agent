using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSettingsBindingTests
{
    [Fact]
    public void VoiceSettings_BindsFromJson()
    {
        // Binds at the configuration root, matching the other channels (ChannelSettings).
        var json = """
        {
          "WyomingClient": { "TrailingSilenceMs": 800, "MaxUtteranceMs": 15000 },
          "Stt": {
            "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Language": "es" }
          },
          "Tts": {
            "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" }
          },
          "ConfidenceThreshold": 0.4,
          "Announce": {
            "Enabled": true,
            "Token": "secret",
            "BindToLoopbackOnly": false,
            "QueueMaxDepth": 8,
            "MaxTextLength": 500
          },
          "Satellites": {
            "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis", "Address": "tcp://host.docker.internal:10800" }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.WyomingClient.TrailingSilenceMs.ShouldBe(800);
        settings.WyomingClient.MaxUtteranceMs.ShouldBe(15000);
        settings.Stt.Wyoming.Host.ShouldBe("wyoming-whisper");
        settings.Tts.Wyoming.Voice.ShouldBe("es_ES-davefx-medium");
        settings.ConfidenceThreshold.ShouldBe(0.4);
        settings.Announce.Token.ShouldBe("secret");
        settings.Announce.MaxTextLength.ShouldBe(500);
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
    public void VoiceSettings_BindsGlobalAndPerSatelliteLocality()
    {
        var json = """
        {
          "Locality": "Madrid, Spain",
          "Satellites": {
            "kitchen-01": { "Identity": "household", "Room": "Kitchen" },
            "office-01": { "Identity": "household", "Room": "Office", "Locality": "Barcelona, Spain" }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.Locality.ShouldBe("Madrid, Spain");
        settings.Satellites["kitchen-01"].Locality.ShouldBeNull();
        settings.Satellites["office-01"].Locality.ShouldBe("Barcelona, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_SatelliteWithoutLocality_InheritsGlobal()
    {
        var settings = new VoiceSettings
        {
            Locality = "Madrid, Spain",
            Satellites = new() { ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen" } }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["kitchen-01"].Locality.ShouldBe("Madrid, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_SatelliteWithLocality_KeepsOwn()
    {
        var settings = new VoiceSettings
        {
            Locality = "Madrid, Spain",
            Satellites = new()
            {
                ["office-01"] = new() { Identity = "household", Room = "Office", Locality = "Barcelona, Spain" }
            }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["office-01"].Locality.ShouldBe("Barcelona, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_NoGlobalDefault_LeavesSatelliteNull()
    {
        var settings = new VoiceSettings
        {
            Satellites = new() { ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen" } }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["kitchen-01"].Locality.ShouldBeNull();
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
            ["Satellites__kitchen-01__Identity"] = "household",
            ["Satellites__kitchen-01__Room"] = "Kitchen",
            ["Satellites__kitchen-01__WakeWord"] = "hey_jarvis",
            ["Satellites__kitchen-01__Address"] = "tcp://host.docker.internal:10800"
        };

        foreach (var (k, v) in vars)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var settings = config.Get<VoiceSettings>();

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