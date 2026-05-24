using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Domain.Tools;

namespace Domain.DTOs.FileSystem;

// Closed union for filesystem backend results: a typed success DTO (Ok) or a typed error (Err).
public abstract record FsResult<T> where T : class
{
    private FsResult() { }

    public sealed record Ok(T Value) : FsResult<T>;

    public sealed record Err(ToolErrorResult Error) : FsResult<T>;

    public JsonNode ToNode() => this switch
    {
        Ok ok => FsResultContract.ToNode(ok.Value),
        Err err => err.Error.ToNode(),
        _ => throw new InvalidOperationException("Unreachable FsResult variant.")
    };

    public bool TryGetValue([NotNullWhen(true)] out T? value, [NotNullWhen(false)] out ToolErrorResult? error)
    {
        switch (this)
        {
            case Ok ok:
                value = ok.Value;
                error = null;
                return true;
            case Err err:
                value = null;
                error = err.Error;
                return false;
            default:
                throw new InvalidOperationException("Unreachable FsResult variant.");
        }
    }
}