using Domain.Contracts;
using Domain.Tools.Timers.Vfs;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Timers.Vfs;

// What the model reads about the timers mount as a whole, beside the per-operation descriptions.
public class TimerFileSystemMountTests
{
    [Fact]
    public void DescribeMount_NamesTheMountsRealFiles()
    {
        var description = new TimerFileSystem(
            Mock.Of<ITimerStore>(), new FakeTimeProvider(), Mock.Of<IAlertDismisser>(),
            Mock.Of<ISatelliteCatalog>())
            .DescribeMount;

        description.ShouldContain("timer.json");
        description.ShouldContain("status.json");
        description.ShouldContain("dismiss.sh");
        description.ShouldContain("durationSeconds");
        description.ShouldContain("target");
    }
}