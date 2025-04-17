namespace Domain.Contracts;

public interface ILargeLanguageModel
{
    Task<string> Prompt(string prompt, CancellationToken cancellationToken = default);
}