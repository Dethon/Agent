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

    // Adjusting a success payload before answering is what every tool that reports a path does, and
    // each one used to pattern-match the union, rebuild the wrapper and hand the error back
    // untouched. Said once here, those sites become one expression.
    public FsResult<T> Map(Func<T, T> transform) => this switch
    {
        Ok ok => new Ok(transform(ok.Value)),
        Err => this,
        _ => throw new InvalidOperationException("Unreachable FsResult variant.")
    };

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