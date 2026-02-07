using Domain.Contracts;
using NCrontab;

namespace Infrastructure.Validation;

public class CronValidator : ICronValidator
{
    public bool IsValid(string cronExpression)
    {
        var result = CrontabSchedule.TryParse(cronExpression);
        return result is not null;
    }

    public DateTime? GetNextOccurrence(string cronExpression, DateTime from)
    {
        var schedule = CrontabSchedule.TryParse(cronExpression);
        return schedule?.GetNextOccurrence(from);
    }
}
