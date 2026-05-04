using System.Text.Json.Serialization;
using Domain.Json;

namespace Domain.DTOs;

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<VfsGlobMode>))]
public enum VfsGlobMode
{
    Files,
    Directories
}

[JsonConverter(typeof(CamelCaseEnumConverter<VfsTextSearchOutputMode>))]
public enum VfsTextSearchOutputMode
{
    Content,
    FilesOnly
}
