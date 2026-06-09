using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed record WyomingEvent(
    string Type,
    JsonObject Data,
    ReadOnlyMemory<byte> Payload)
{
    public static WyomingEvent Header(string type, JsonObject data) =>
        new(type, data, ReadOnlyMemory<byte>.Empty);

    public static WyomingEvent WithPayload(string type, JsonObject data, ReadOnlyMemory<byte> payload) =>
        new(type, data, payload);
}