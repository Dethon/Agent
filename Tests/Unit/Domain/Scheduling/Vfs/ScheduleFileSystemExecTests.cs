using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemExecTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store) =>
        new(store, new FakeAgentCatalog([new ScheduleAgentInfo("jonas", "J", null)]), new CronValidator());

    [Fact]
    public async Task Exec_RunNow_MarksScheduleDue()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", NextRunAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        var result = node.Deserialize<FsExecResult>(FsResultContract.ValidationOptions)!;
        result.ExitCode.ShouldBe(0);
        (store.Items["n"].NextRunAt <= DateTime.UtcNow).ShouldBeTrue();
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ExecAsync("/jonas/n", "ls -la", null, CancellationToken.None);

        var result = node.Deserialize<FsExecResult>(FsResultContract.ValidationOptions)!;
        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("run_now.sh");
    }

    [Fact]
    public async Task Exec_UnknownSchedule_ReturnsNotFound()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.ExecAsync("/jonas/ghost", "run_now.sh", null, CancellationToken.None);
        ToolErrorResult.IsErrorEnvelope(node).ShouldBeTrue();
    }
}