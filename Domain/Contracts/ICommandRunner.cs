using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public interface ICommandRunner
{
    Task<FsResult<FsExecResult>> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken cancellationToken);
}