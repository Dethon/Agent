using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpChannelVoice.Services.Tse;

// Opt-in audio audit ring for the TSE live trial: one directory per extraction
// (mixture + extracted + metadata), oldest pruned beyond the cap. Best-effort by
// design — an audit failure must never affect the turn, so everything is caught.
public sealed partial class TseAuditTrail(string? dir, int maxPairs, TimeProvider clock, ILogger<TseAuditTrail> logger)
{
    private bool Enabled => !string.IsNullOrWhiteSpace(dir);

    public void Record(string speaker, double? floorRms, long latencyMs, byte[] mixtureWav, byte[] extractedWav)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
            var pairDir = Path.Combine(dir!, $"{stamp}-{SanitizeSpeaker(speaker)}");
            Directory.CreateDirectory(pairDir);
            File.WriteAllBytes(Path.Combine(pairDir, "mixture.wav"), mixtureWav);
            File.WriteAllBytes(Path.Combine(pairDir, "extracted.wav"), extractedWav);
            File.WriteAllText(Path.Combine(pairDir, "meta.json"), JsonSerializer.Serialize(new
            {
                speaker,
                floorRms,
                latencyMs,
                recordedAt = clock.GetUtcNow()
            }));
            Prune();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TSE audit write failed (turn unaffected)");
        }
    }

    private static string SanitizeSpeaker(string speaker)
    {
        var safe = UnsafeSpeakerCharsRegex().Replace(speaker, "_");
        return safe.Length == 0 ? "unknown" : safe;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]", RegexOptions.Compiled)]
    private static partial Regex UnsafeSpeakerCharsRegex();

    private void Prune()
    {
        var dirs = Directory.GetDirectories(dir!).Order().ToList();
        foreach (var stale in dirs.Take(Math.Max(0, dirs.Count - maxPairs)))
        {
            try
            {
                Directory.Delete(stale, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TSE audit prune failed for {Stale} (continuing)", stale);
            }
        }
    }
}