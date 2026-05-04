using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Json;

public sealed class SnakeCaseLowerEnumConverter<T> : JsonStringEnumConverter<T>
    where T : struct, Enum
{
    public SnakeCaseLowerEnumConverter() : base(JsonNamingPolicy.SnakeCaseLower) { }
}

public sealed class CamelCaseEnumConverter<T> : JsonStringEnumConverter<T>
    where T : struct, Enum
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}
