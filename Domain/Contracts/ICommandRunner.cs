namespace Domain.Contracts;

public interface ICommandRunner
{
    Task<string> Run(string command, CancellationToken ct);
}