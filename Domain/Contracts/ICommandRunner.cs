using System.Text.Json.Nodes;

namespace Domain.Contracts;

public interface ICommandRunner
{
    Task<JsonNode> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken cancellationToken);
}