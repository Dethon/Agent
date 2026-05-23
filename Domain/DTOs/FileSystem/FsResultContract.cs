using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Domain.DTOs.FileSystem;

// Single source of truth for fs_* success-payload serialization and validation.
// Producers serialize through SerializerOptions (camelCase, omit nulls). The agent
// boundary and the conformance tests validate through ValidationOptions (strict:
// unknown members and missing required members both fail).
public static class FsResultContract
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions ValidationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static readonly IReadOnlyDictionary<string, Type> ResultTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["fs_read"] = typeof(FsReadResult),
            ["fs_info"] = typeof(FsInfoResult),
            ["fs_glob"] = typeof(FsGlobResult),
            ["fs_search"] = typeof(FsSearchResult),
            ["fs_exec"] = typeof(FsExecResult),
            ["fs_create"] = typeof(FsCreateResult),
            ["fs_edit"] = typeof(FsEditResult),
            ["fs_move"] = typeof(FsMoveResult),
            ["fs_delete"] = typeof(FsRemoveResult),
            ["fs_copy"] = typeof(FsCopyResult),
            ["fs_blob_read"] = typeof(FsBlobReadResult),
            ["fs_blob_write"] = typeof(FsBlobWriteResult)
        };

    public static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, SerializerOptions)
        ?? throw new InvalidOperationException($"Failed to serialize {typeof(T).Name}");

    public static bool TryValidate(string toolName, JsonNode payload, out string? error)
    {
        error = null;
        if (!ResultTypes.TryGetValue(toolName, out var type))
        {
            return true;
        }

        try
        {
            payload.Deserialize(type, ValidationOptions);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}