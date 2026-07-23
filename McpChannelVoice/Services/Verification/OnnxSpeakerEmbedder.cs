using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace McpChannelVoice.Services.Verification;

// Runs a WeSpeaker/CAM++-family speaker-embedding ONNX model: input [1, T, 80] RAW
// log-mel fbank features, output [1, D] speaker embedding. This model family applies
// its own normalization internally, so feeding externally mean-normalized fbank
// double-normalizes and collapses embeddings into a narrow cosine-similarity cone
// (verified empirically against the sherpa-onnx reference implementation on the
// project's fixture WAVs). InferenceSession is thread-safe for concurrent Run
// calls; one instance serves the whole hub.
public sealed class OnnxSpeakerEmbedder(string modelPath) : ISpeakerEmbedder, IDisposable
{
    private readonly InferenceSession _session = new(modelPath);

    public float[] Embed(ReadOnlySpan<byte> pcmS16Le)
    {
        var frames = new FbankExtractor().Extract(pcmS16Le);
        if (frames.Length == 0)
        {
            throw new InvalidOperationException("Audio too short to embed");
        }

        var tensor = new DenseTensor<float>([1, frames.Length, 80]);
        for (var f = 0; f < frames.Length; f++)
        {
            for (var b = 0; b < 80; b++)
            { tensor[0, f, b] = frames[f][b]; }
        }

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        return L2Normalize(results.First().AsEnumerable<float>().ToArray());
    }

    public static float[] L2Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b) =>
        a.Zip(b, (x, y) => (double)x * y).Sum();

    public void Dispose() => _session.Dispose();
}