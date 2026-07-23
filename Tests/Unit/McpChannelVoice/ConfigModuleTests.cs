using Domain.Contracts;
using McpChannelVoice.Modules;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfigModuleTests
{
    // Registration-only smoke: nothing here starts hosted services — resolving the STT/TTS graph
    // must work from settings alone. The STT graph now runs through the TSE metrics decorator, so
    // stub IMetricsPublisher to keep the Redis-backed one out and avoid a live connection.
    private static ServiceProvider Build(VoiceSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(settings);
        services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingEnabled_ResolvesSegmentedOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings
        {
            Stt = new SttSettings { Streaming = new SegmentedSttConfig { Enabled = true } }
        });

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<SegmentedSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingDisabled_ResolvesBareOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<OpenAiSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimEnabled_ResolvesSilenceTrimmedOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<SilenceTrimmingTextToSpeech>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimDisabled_ResolvesBareOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings
        {
            Tts = new TtsSettings { OpenAi = new OpenAiTtsConfig { TrailingSilenceTrimThreshold = 0 } }
        });

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<OpenAiTextToSpeech>();
    }
}