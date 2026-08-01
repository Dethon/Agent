using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class LocalCommandDispatcherTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static VoiceCommandMatcher Matcher() =>
        new(new CommandSettings
        {
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        });

    private sealed class FakeHandler(IReadOnlySet<VoiceCommand> commands, bool result = true) : ILocalCommandHandler
    {
        public List<VoiceCommand> Handled { get; } = [];
        public IReadOnlySet<VoiceCommand> Commands => commands;

        public Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct)
        {
            Handled.Add(command);
            return Task.FromResult(result);
        }
    }

    private static readonly IReadOnlySet<VoiceCommand> _volumeCommands =
        new HashSet<VoiceCommand> { VoiceCommand.LocalVolumeUp, VoiceCommand.LocalVolumeDown };

    private static readonly IReadOnlySet<VoiceCommand> _muteCommands =
        new HashSet<VoiceCommand> { VoiceCommand.LocalMute, VoiceCommand.LocalUnmute };

    [Fact]
    public void Ctor_DuplicateCommandOwnership_Throws()
    {
        var all = Enum.GetValues<VoiceCommand>().ToHashSet();

        Should.Throw<InvalidOperationException>(
            () => new LocalCommandDispatcher(Matcher(), [new FakeHandler(all), new FakeHandler(_muteCommands)]));
    }

    [Fact]
    public void Ctor_UncoveredCommand_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => new LocalCommandDispatcher(Matcher(), [new FakeHandler(_volumeCommands)]));
    }

    [Fact]
    public async Task TryHandleAsync_NonCommandTranscript_ReturnsNull()
    {
        var sut = new LocalCommandDispatcher(
            Matcher(), [new FakeHandler(_volumeCommands), new FakeHandler(_muteCommands)]);

        var result = await sut.TryHandleAsync("sube el volumen", Session(), default);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryHandleAsync_MatchedCommand_RoutesToItsOwningHandler()
    {
        var volume = new FakeHandler(_volumeCommands);
        var mute = new FakeHandler(_muteCommands);
        var sut = new LocalCommandDispatcher(Matcher(), [volume, mute]);

        var result = await sut.TryHandleAsync("silencia el altavoz", Session(), default);

        result.ShouldNotBeNull();
        result.Command.ShouldBe(VoiceCommand.LocalMute);
        result.Sent.ShouldBeTrue();
        mute.Handled.ShouldBe([VoiceCommand.LocalMute]);
        volume.Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_HandlerReportsFailure_ResultSentIsFalse()
    {
        var sut = new LocalCommandDispatcher(
            Matcher(),
            [new FakeHandler(_volumeCommands, result: false), new FakeHandler(_muteCommands)]);

        var result = await sut.TryHandleAsync("sube el volumen local", Session(), default);

        result.ShouldNotBeNull();
        result.Sent.ShouldBeFalse();
    }
}