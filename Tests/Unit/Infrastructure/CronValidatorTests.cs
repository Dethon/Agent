using Infrastructure.Validation;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class CronValidatorTests
{
    private readonly CronValidator _validator = new();

    [Theory]
    [InlineData("0 9 * * *", true)]       // Every day at 9am
    [InlineData("*/15 * * * *", true)]    // Every 15 minutes
    [InlineData("0 0 1 * *", true)]       // First of month
    [InlineData("invalid", false)]
    [InlineData("", false)]
    [InlineData("0 9 * *", false)]        // Missing field
    public void IsValid_VariousExpressions_ReturnsExpected(string cron, bool expected)
    {
        _validator.IsValid(cron).ShouldBe(expected);
    }

    [Fact]
    public void GetNextOccurrence_ValidCron_ReturnsNextTime()
    {
        var from = new DateTime(2024, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        var next = _validator.GetNextOccurrence("0 9 * * *", from);

        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(9);
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void GetNextOccurrence_InvalidCron_ReturnsNull()
    {
        var next = _validator.GetNextOccurrence("invalid", DateTime.UtcNow);
        next.ShouldBeNull();
    }
}
