using Cronos;
using Domain.Contracts;

namespace Infrastructure.Validation;

public class CronValidator : ICronValidator
{
    public bool IsValid(string cronExpression) =>
        CronExpression.TryParse(cronExpression, out _);

    // Cronos evaluates the expression against the zone's wall clock with correct DST handling,
    // then we project to a UTC DateTime so the store/score logic stays UTC-keyed.
    public DateTime? GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone) =>
        CronExpression.TryParse(cronExpression, out var expr)
            ? expr.GetNextOccurrence(from, zone)?.UtcDateTime
            : null;
}