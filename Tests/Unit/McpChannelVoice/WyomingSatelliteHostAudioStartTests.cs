using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// audio-start is where the satellite learns which sink a stream belongs on: an alert (timer or
// alarm) plays on the non-attenuated route, everything else on the calibrated voice level. The
// format fields must survive unchanged — the satellite's playback sink is fixed at 22050 Hz and a
// wrong rate here would be a silent pitch bug.
public class WyomingSatelliteHostAudioStartTests
{
    private static readonly AudioFormat _playback = new()
    {
        SampleRateHz = 22_050,
        SampleWidthBytes = 2,
        Channels = 1
    };

    [Fact]
    public void BuildAudioStart_AlertStream_MarksTheFrame()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: true);

        data["alert"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void BuildAudioStart_NormalStream_MarksTheFrameFalse()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: false);

        data["alert"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void BuildAudioStart_CarriesTheAudioFormatUnchanged()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: true);

        data["rate"]!.GetValue<int>().ShouldBe(22_050);
        data["width"]!.GetValue<int>().ShouldBe(2);
        data["channels"]!.GetValue<int>().ShouldBe(1);
        data["timestamp"]!.GetValue<int>().ShouldBe(0);
    }

    // Documented as ONE number with satellite/src/wyoming/event.rs PROTOCOL_VERSION, which has its
    // own test; the alert field on audio-start is the 1.5 change, so the two move together.
    [Fact]
    public void ProtocolVersion_MatchesTheSatellite()
    {
        WyomingWriter.ProtocolVersion.ShouldBe("1.5");
    }
}