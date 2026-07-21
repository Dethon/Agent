using McpChannelVoice.Services.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class SpeakerProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"voices-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        { Directory.Delete(_dir, true); }
    }

    // Embeds every WAV to a vector derived from its first sample so tests can
    // predict profile math without a real model.
    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Calls;
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le)
        {
            Calls++;
            var first = (short)(pcmS16Le[0] | (pcmS16Le[1] << 8));
            return OnnxSpeakerEmbedder.L2Normalize([first, 1f]);
        }
    }

    // Minimal valid 16 kHz mono S16LE RIFF file whose samples all carry `value`.
    private static byte[] Wav(short value, int samples = 1600)
    {
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            data[2 * i] = (byte)(value & 0xFF);
            data[2 * i + 1] = (byte)((value >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(16_000);        // sample rate
        w.Write(16_000 * 2);    // byte rate
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    private void WriteVoice(string name, params byte[][] wavs)
    {
        var d = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        for (var i = 0; i < wavs.Length; i++)
        { File.WriteAllBytes(Path.Combine(d, $"sample-{i}.wav"), wavs[i]); }
    }

    [Fact]
    public void Load_TwoIdentities_BuildsNormalizedMeanProfiles()
    {
        WriteVoice("fran", Wav(1000), Wav(2000));
        WriteVoice("ana", Wav(-500));
        var store = new SpeakerProfileStore(_dir, new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance);

        var profiles = store.Load();

        profiles.Count.ShouldBe(2);
        var fran = profiles.Single(p => p.Name == "fran");
        // mean of normalized [1000,1] and [2000,1], re-normalized => unit length
        Math.Sqrt(fran.Embedding.Sum(x => (double)x * x)).ShouldBe(1.0, 1e-5);
        var ana = profiles.Single(p => p.Name == "ana");
        ana.Embedding[0].ShouldBeLessThan(0); // sign of the -500 sample survives
    }

    [Fact]
    public void Load_SecondCall_UsesCacheInsteadOfReEmbedding()
    {
        WriteVoice("fran", Wav(1000), Wav(2000));
        var embedder = new FakeEmbedder();
        var store = new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance);

        var first = store.Load();
        var again = new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        embedder.Calls.ShouldBe(2); // only the first Load embedded
        again.Single().Embedding.ShouldBe(first.Single().Embedding);
    }

    [Fact]
    public void Load_WavChanged_InvalidatesCache()
    {
        WriteVoice("fran", Wav(1000));
        var embedder = new FakeEmbedder();
        new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        File.WriteAllBytes(Path.Combine(_dir, "fran", "sample-0.wav"), Wav(3000, samples: 3200));
        new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        embedder.Calls.ShouldBe(2);
    }

    [Fact]
    public void Load_WrongFormatWav_IsSkippedWithoutFailing()
    {
        var stereo = Wav(1000);
        stereo[22] = 2; // channels = 2
        WriteVoice("fran", stereo, Wav(2000));

        var profiles = new SpeakerProfileStore(_dir, new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance).Load();

        profiles.Single().Name.ShouldBe("fran"); // built from the one valid file
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmpty()
    {
        new SpeakerProfileStore(Path.Combine(_dir, "nope"), new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance)
            .Load().ShouldBeEmpty();
    }
}