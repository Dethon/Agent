using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemExecTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store)
    {
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("jonas", "J", null)]);
        return new ScheduleFileSystem(store, catalog, new CronValidator());
    }

    [Fact]
    public async Task Exec_RunNow_MarksScheduleDue()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", NextRunAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0);
        (store.Items["n"].NextRunAt <= DateTime.UtcNow).ShouldBeTrue();
    }

    [Fact]
    public async Task Exec_RunNow_OnNeverRunSchedule_DoesNotSetLastRunAt()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", NextRunAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        store.Items["n"].LastRunAt.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_RunNow_PreservesExistingLastRunAt()
    {
        var lastRun = DateTime.UtcNow.AddHours(-3);
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", LastRunAt = lastRun, NextRunAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        store.Items["n"].LastRunAt.ShouldBe(lastRun);
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.ExecAsync("/jonas/n", "ls -la", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(127);
        exec.Stderr.ShouldContain("run_now.sh");
    }

    [Fact]
    public async Task Exec_UnknownSchedule_ReturnsNotFound()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.ExecAsync("/jonas/ghost", "run_now.sh", null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsExecResult>.Err>();
    }
}