using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

public record LibraryDescriptionNode
{
    public required LibraryEntryType Type { [UsedImplicitly] get; init; }
    public required string Name { [UsedImplicitly] get; init; }
    public LibraryDescriptionNode[]? Children { [UsedImplicitly] get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LibraryEntryType
{
    File,
    Directory
}