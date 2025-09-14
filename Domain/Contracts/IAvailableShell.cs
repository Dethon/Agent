namespace Domain.Contracts;

public interface IAvailableShell
{
    Task<string> Get(CancellationToken ct);
}