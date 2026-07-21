using System.Text.Json;

namespace McpChannelVoice.Services.Verification;

// Builds one profile per voices/<identity>/ directory: each 16 kHz mono S16LE WAV is
// embedded, embeddings are averaged and re-normalized. Embeddings are cached in
// profile.json beside the WAVs, keyed by file name/length/mtime, so startup does not
// re-run the model when nothing changed. Wrong-format WAVs are skipped with a warning.
public sealed class SpeakerProfileStore(string voicesPath, ISpeakerEmbedder embedder, ILogger<SpeakerProfileStore> logger)
{
    private sealed record CacheEntry(List<CachedFile> Files, float[] Embedding);
    private sealed record CachedFile(string Name, long Length, DateTime ModifiedUtc);

    public IReadOnlyList<SpeakerProfile> Load()
    {
        if (!Directory.Exists(voicesPath))
        {
            logger.LogInformation("Voices path {Path} does not exist; no speaker profiles", voicesPath);
            return [];
        }

        return Directory.EnumerateDirectories(voicesPath)
            .Select(dir => BuildProfile(dir))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    private SpeakerProfile? BuildProfile(string dir)
    {
        var name = Path.GetFileName(dir);
        var files = Directory.EnumerateFiles(dir, "*.wav")
            .OrderBy(f => f)
            .Select(f => new FileInfo(f))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var manifest = files
            .Select(f => new CachedFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
        var cachePath = Path.Combine(dir, "profile.json");
        var cached = TryReadCache(cachePath);
        if (cached is not null && cached.Files.SequenceEqual(manifest))
        {
            return new SpeakerProfile(name, cached.Embedding);
        }

        var embeddings = files
            .Select(f => TryEmbed(f.FullName))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        if (embeddings.Count == 0)
        {
            logger.LogWarning("No usable enrollment WAVs in {Dir}", dir);
            return null;
        }

        var dim = embeddings[0].Length;
        var mean = new float[dim];
        foreach (var e in embeddings)
        {
            for (var i = 0; i < dim; i++)
            { mean[i] += e[i]; }
        }
        for (var i = 0; i < dim; i++)
        { mean[i] /= embeddings.Count; }
        var profile = OnnxSpeakerEmbedder.L2Normalize(mean);

        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(manifest, profile)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write profile cache {Path}", cachePath);
        }
        logger.LogInformation("Built speaker profile {Name} from {Count} recording(s)", name, embeddings.Count);
        return new SpeakerProfile(name, profile);
    }

    private CacheEntry? TryReadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable profile cache {Path}", path);
            return null;
        }
    }

    private float[]? TryEmbed(string wavPath)
    {
        try
        {
            var pcm = ReadWav16kMonoS16(wavPath);
            return embedder.Embed(pcm);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping enrollment WAV {Path}", wavPath);
            return null;
        }
    }

    // Minimal RIFF parser: accepts only PCM, mono, 16 kHz, 16-bit — anything else throws
    // (and the caller skips the file with a warning naming it).
    private static byte[] ReadWav16kMonoS16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file");
        }
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file");
        }

        byte[]? data = null;
        var formatOk = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                var channels = reader.ReadInt16();
                var rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                reader.BaseStream.Seek(chunkSize - 16, SeekOrigin.Current);
                formatOk = audioFormat == 1 && channels == 1 && rate == 16_000 && bits == 16;
                if (!formatOk)
                {
                    throw new InvalidDataException(
                        $"Need PCM mono 16 kHz 16-bit, got format={audioFormat} ch={channels} rate={rate} bits={bits}");
                }
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        return formatOk && data is not null
            ? data
            : throw new InvalidDataException("Missing fmt/data chunk");
    }
}