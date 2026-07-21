using System.Text.Json;

namespace McpChannelVoice.Services.Verification;

// Builds one profile per voices/<identity>/ directory: each 16 kHz mono S16LE WAV is
// embedded as its own prototype, and the re-normalized mean of the takes joins them (a
// capture between two conditions can sit closer to the mean than to any single take).
// Prototypes are cached in profile.json beside the WAVs, keyed by file name/length/mtime,
// so startup does not re-run the model when nothing changed. Wrong-format WAVs are
// skipped with a warning.
public sealed class SpeakerProfileStore(string voicesPath, ISpeakerEmbedder embedder, ILogger<SpeakerProfileStore> logger)
{
    // Pipeline version — bump whenever the embedding pipeline changes (model swap, fbank
    // change, normalization change, etc.) so every cached profile.json is invalidated.
    // v2: mean-centroid Embedding became per-take Prototypes (+ mean).
    private const int CacheVersion = 2;

    private sealed record CacheEntry(int Version, List<CachedFile> Files, float[][] Prototypes);
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
        if (cached is { Prototypes: not null } && cached.Version == CacheVersion && cached.Files.SequenceEqual(manifest))
        {
            return new SpeakerProfile(name, cached.Prototypes);
        }

        var takes = files
            .Select(f => TryEmbed(f.FullName))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        if (takes.Count == 0)
        {
            logger.LogWarning("No usable enrollment WAVs in {Dir}", dir);
            return null;
        }

        var prototypes = takes.Count > 1
            ? takes.Append(MeanPrototype(takes)).ToArray()
            : takes.ToArray();

        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(CacheVersion, manifest, prototypes)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write profile cache {Path}", cachePath);
        }
        logger.LogInformation(
            "Built speaker profile {Name} from {Count} recording(s) ({Prototypes} prototypes)",
            name, takes.Count, prototypes.Length);
        return new SpeakerProfile(name, prototypes);
    }

    private static float[] MeanPrototype(IReadOnlyList<float[]> takes)
    {
        var mean = new float[takes[0].Length];
        foreach (var e in takes)
        {
            for (var i = 0; i < mean.Length; i++)
            { mean[i] += e[i]; }
        }
        for (var i = 0; i < mean.Length; i++)
        { mean[i] /= takes.Count; }
        return OnnxSpeakerEmbedder.L2Normalize(mean);
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
                if (data.Length != chunkSize)
                {
                    throw new InvalidDataException(
                        $"Truncated data chunk: expected {chunkSize} bytes, got {data.Length}");
                }
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            // RIFF pads every odd-sized chunk with one extra byte not counted in chunkSize.
            if (chunkSize % 2 != 0)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }
        return formatOk && data is not null
            ? data
            : throw new InvalidDataException("Missing fmt/data chunk");
    }
}