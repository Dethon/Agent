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
        // Cron schedules are evaluated in UTC; NCrontab carries the input's Kind onto the
        // result, so normalize to UTC to keep NextRunAt unambiguous for every caller.
        return schedule is null ? null : DateTime.SpecifyKind(schedule.GetNextOccurrence(from), DateTimeKind.Utc);
    }
}