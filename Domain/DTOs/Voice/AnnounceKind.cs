using System.Text.Json.Serialization;

namespace Domain.DTOs.Voice;

[JsonConverter(typeof(JsonStringEnumConverter<AnnounceKind>))]
public enum AnnounceKind
{
    Alarm,
    Timer
}