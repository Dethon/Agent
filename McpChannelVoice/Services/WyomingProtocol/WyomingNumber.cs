using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

// Wyoming audio headers carry integer rate/width/channels, but a non-conformant peer may send a
// JSON float (e.g. 16000.0) or an out-of-range number. JsonValue.GetValue<int>() THROWS on a
// non-integral or out-of-range Number (a documented .NET 10 STJ gotcha), which — read inside an
// audio-chunk/audio-start frame on the read loop — would unwind and tear down the whole satellite
// connection mid-utterance. Parse tolerantly and fall back to the expected value instead.
internal static class WyomingNumber
{
    public static int ReadInt(JsonObject data, string key, int fallback)
    {
        if (data[key] is not JsonValue value)
        {
            return fallback;
        }
        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }
        if (value.TryGetValue<double>(out var d) && !double.IsNaN(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            return (int)Math.Round(d);
        }
        return fallback;
    }

    // Like ReadInt but for frame-length fields, where an oversized value must still reach the
    // caller's frame-size guard rather than silently falling back: a present-but-out-of-range
    // length is clamped into the long range (so the guard rejects it) instead of dropped.
    public static long ReadLong(JsonObject data, string key, long fallback)
    {
        if (data[key] is not JsonValue value)
        {
            return fallback;
        }
        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }
        if (value.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            if (d >= long.MaxValue)
            {
                return long.MaxValue;
            }
            if (d <= long.MinValue)
            {
                return long.MinValue;
            }
            return (long)Math.Round(d);
        }
        return fallback;
    }
}