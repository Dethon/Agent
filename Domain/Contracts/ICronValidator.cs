namespace Domain.Contracts;

public interface ICronValidator
{
    bool IsValid(string cronExpression);
    DateTime? GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone);
}