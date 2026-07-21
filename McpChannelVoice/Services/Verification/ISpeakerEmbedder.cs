namespace McpChannelVoice.Services.Verification;

public interface ISpeakerEmbedder
{
    // 16 kHz mono S16LE in, L2-normalized speaker embedding out. Throws on
    // failure (model errors, audio too short) — callers treat that as fail-open.
    float[] Embed(ReadOnlySpan<byte> pcmS16Le);
}