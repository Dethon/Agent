using Domain.Tools;

namespace Domain.DTOs.FileSystem;

// The envelope a chunk operation had no way to return. ReadChunksAsync and WriteChunksAsync are
// streams rather than FsResults, so a backend that knows exactly why it refused — denied path,
// read-only mount, wrong file type — used to flatten that into an IOException and lose the code
// with it, leaving VfsCopyTool to relabel a permanent refusal as retryable. Carrying the error
// through means the caller answers with the backend's own envelope.
//
// It is an IOException because that is what the chunk path already threw, so anything catching the
// old shape still catches this one.
public sealed class FileSystemOperationException(ToolErrorResult error) : IOException(error.Message)
{
    public ToolErrorResult Error { get; } = error;
}