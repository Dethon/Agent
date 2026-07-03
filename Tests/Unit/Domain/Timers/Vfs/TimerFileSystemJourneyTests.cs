using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Timers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Timers.Vfs;

public class TimerFileSystemJourneyTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private sealed class FakeDismisser : IAlertDismisser
    {
        public List<DismissedAlert> Ringing { get; } = [];
        public IReadOnlyList<DismissedAlert> DismissAll()
        {
            var result = Ringing.ToList();
            Ringing.Clear();
            return result;
        }
    }

    private static (TimerFileSystem Fs, InMemoryTimerStore Store, FakeTimeProvider Time, FakeDismisser Dismisser) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var store = new InMemoryTimerStore();
        var dismisser = new FakeDismisser();
        return (new TimerFileSystem(store, time, dismisser), store, time, dismisser);
    }

    private const string PastaSpec = """
        {"durationSeconds": 300, "text": "pasta is ready", "target": {"room": "Kitchen"}}
        """;

    [Fact]
    public async Task CreateReadStatusCancel_FullJourney()
    {
        var (fs, _, time, _) = Build();

        var created = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var glob = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldBe(["/dismiss.sh", "/pasta/", "/pasta/status.json", "/pasta/timer.json"]);

        var spec = (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        spec.Content.ShouldContain("\"durationSeconds\": 300");
        spec.Content.ShouldContain("pasta is ready");

        time.Advance(TimeSpan.FromSeconds(100));
        var status = (await fs.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("\"remainingSeconds\": 200");
        status.Content.ShouldContain("+02:00"); // firesAt rendered in the operating zone (CEST)

        var deleted = await fs.DeleteAsync("/pasta", CancellationToken.None);
        deleted.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();
    }

    [Fact]
    public async Task Create_InvalidDuration_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 0, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("durationSeconds");
    }

    [Fact]
    public async Task Create_DurationAboveCeiling_IsRejectedTowardAlarmsCalendar()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/roast/timer.json",
            """{"durationSeconds": 14401, "target": {"room": "Kitchen"}}""",
            false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("4 hours");
        err.Error.Message.ShouldContain("alarms calendar");
    }

    [Fact]
    public async Task Create_DurationAtCeiling_IsAccepted()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/roast/timer.json",
            """{"durationSeconds": 14400, "target": {"room": "Kitchen"}}""",
            false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
    }

    [Fact]
    public async Task Create_MissingTarget_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 60}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("target");
    }

    [Fact]
    public async Task Create_DuplicateId_IsRejected()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, true, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_WrongPathShape_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync("/pasta.json", PastaSpec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Edit_IsUnsupported_TimersAreImmutable()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.EditAsync("/pasta/timer.json",
            [new TextEdit("300", "600")], CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsEditResult>.Err>();
        err.Error.Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task Delete_TimerJsonFile_IsRejected_DirIsTheUnit()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        (await fs.DeleteAsync("/pasta/timer.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    [Fact]
    public async Task Exec_DismissAtRoot_SilencesRingingAlertsAndReportsThem()
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/", "dismiss.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("alarm \"Take out the trash\"");
        result.Stdout.ShouldContain("timer \"pasta\"");
    }

    [Fact]
    public async Task Exec_OnDismissScriptPath_AlsoWorks()
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/dismiss.sh", "dismiss.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("timer \"pasta\"");
    }

    [Fact]
    public async Task Exec_DismissWithNothingRinging_SaysSo()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "dismiss.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("nothing is ringing");
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "reboot.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("dismiss.sh");
    }

    [Fact]
    public async Task Read_DismissScript_ExplainsItself()
    {
        var (fs, _, _, _) = Build();

        var read = (await fs.ReadAsync("/dismiss.sh", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("exec dismiss.sh");
    }

    [Fact]
    public async Task Search_FindsTimerSpecContent()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = (await fs.SearchAsync(
                "pasta is ready", false, null, null, null, 10, 0,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;

        result.TotalMatches.ShouldBe(1);
        result.Results[0].File.ShouldBe("/pasta/timer.json");
    }
}