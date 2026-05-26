using McpServerScheduling.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class SchedulingSettingsTests
{
    [Fact]
    public void SchedulingSettings_BindsFromConfiguration_PopulatesAllFields()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnectionString"] = "redis:6379",
            ["DispatchIntervalSeconds"] = "15",
            ["DefaultDeliverTo:0"] = "signalr"
        }).Build();

        var settings = config.Get<SchedulingSettings>()!;

        settings.RedisConnectionString.ShouldBe("redis:6379");
        settings.DispatchIntervalSeconds.ShouldBe(15);
        settings.DefaultDeliverTo[0].ShouldBe("signalr");
    }
}