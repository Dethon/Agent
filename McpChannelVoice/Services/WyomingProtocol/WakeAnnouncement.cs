using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

// What a satellite reports about the wake it just detected: how loud, how confident, what triggered
// it, and how loud the room was just before. Travels as an argument from the run-pipeline frame that
// carried it to the wake turn that opens on the strength of it.
public readonly record struct WakeAnnouncement(double? Rms, double? Score, string Source, double? RoomRms = null)
{
    // Wake metadata is peer-supplied and optional: pre-arbitration firmware sends run-pipeline with
    // no data object at all, and Wyoming has no schema to stop a peer sending the wrong types. Every
    // read here has to survive absent, null and wrong-typed values, because an exception on the read
    // loop tears down the satellite connection mid-utterance.
    public static WakeAnnouncement Read(JsonObject data) => new(
        JsonNumber.ReadDouble(data, "wake_rms"),
        JsonNumber.ReadDouble(data, "wake_score"),
        data["source"] is JsonValue value
        && value.TryGetValue<string>(out var source)
        && !string.IsNullOrWhiteSpace(source)
            ? source
            : "wake",
        JsonNumber.ReadDouble(data, "room_rms"));
}