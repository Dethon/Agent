using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// The one place a server's configuration is read, tested over a configuration builder rather than a
// booted server: precedence, nested binding and validation are all unreachable from a seam that
// starts with an already-bound settings object.
public class SettingsBinderTests : IDisposable
{
    private const string SearchKeyEnvironmentVariable = "Search__ApiKey";
    private const string SolverKeyEnvironmentVariable = "Solver__ApiKey";

    // A secrets id of this test's own, so writing a secret cannot touch the one the test project
    // really uses.
    private readonly string _secretsId = $"ziggurat-bindsettings-{Guid.NewGuid()}";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SearchKeyEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(SolverKeyEnvironmentVariable, null);

        var directory = Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(_secretsId))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ADR-0005, and the assertion most likely to be broken by someone tidying the order toward the
    // framework default. DockerCompose/.env ships every secret as an empty placeholder and compose
    // exports an empty value as an empty string, so a secret that lost to an environment variable
    // would be blanked on every containerised deployment — silently, because several settings read
    // an empty string as "feature not configured".
    [Fact]
    public void AUserSecret_BeatsAnEnvironmentVariableWithTheSameKey()
    {
        Environment.SetEnvironmentVariable(SearchKeyEnvironmentVariable, "from-the-environment");
        WriteUserSecret("""{ "Search:ApiKey": "from-the-secret" }""");

        var settings = new ConfigurationBuilder().BindSettings<ProbeSettings>(_secretsId);

        settings.Search.ApiKey.ShouldBe("from-the-secret");
    }

    // The claim the deleted explicit re-bind in the web-search server was written for. A nested
    // section binds from the environment through the plain call, so there is nothing to re-bind.
    [Fact]
    public void ANestedOptionalSection_BindsFromAnEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(SolverKeyEnvironmentVariable, "solver-key");

        var settings = Bind(("Search:ApiKey", "brave-key"));

        settings.Solver!.ApiKey.ShouldBe("solver-key");
    }

    [Fact]
    public void AnAbsentOptionalSection_StaysNull() =>
        Bind(("Search:ApiKey", "brave-key")).Solver.ShouldBeNull();

    // What replaces the guard that never fired. A genuinely missing section used to come out as a
    // null sub-record and surface later as a NullReferenceException from wherever the value was
    // first read, with nothing in the message naming the missing key.
    [Fact]
    public void AMissingRequiredSection_FailsNamingIt() =>
        Should.Throw<InvalidOperationException>(() => Bind())
            .Message.ShouldContain("Search");

    [Fact]
    public void AMissingRequiredMemberOfASection_FailsNamingThePathToIt() =>
        Should.Throw<InvalidOperationException>(() => Bind(("Search:ApiUrl", "https://example")))
            .Message.ShouldContain("Search.ApiKey");

    // Null only, never empty. McpChannelServiceBus's connection string, Telegram's bot tokens and
    // WebSearch's Brave key all ship as "" in appsettings.json and are filled from secrets, and an
    // empty CapSolver key is how that feature is switched off. Empty-is-invalid would refuse to
    // start three shipped servers.
    [Fact]
    public void AnEmptyRequiredMember_Binds() =>
        Bind(("Search:ApiKey", "")).Search.ApiKey.ShouldBe("");

    // Five servers ask for user secrets from a project with no UserSecretsId. The source is simply
    // absent for them, which is exactly today's behaviour, and they must keep starting.
    [Fact]
    public void NoUserSecretsId_DoesNotThrow() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ApiKey"] = "brave-key" })
            .BindSettings<ProbeSettings>(userSecretsId: null)
            .Search.ApiKey.ShouldBe("brave-key");

    // The shipping call reads the id off the entry assembly, which under the test runner is the
    // test host — an assembly with no UserSecretsId, the same state five servers are in.
    [Fact]
    public void TheEntryAssemblysSecretsId_IsWhatTheShippingCallUses() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ApiKey"] = "brave-key" })
            .BindSettings<ProbeSettings>()
            .Search.ApiKey.ShouldBe("brave-key");

    // Stands in for appsettings.json, which is where the shipped empty placeholders live.
    private ProbeSettings Bind(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .BindSettings<ProbeSettings>(_secretsId);

    private void WriteUserSecret(string json)
    {
        var path = PathHelper.GetSecretsPathFromSecretsId(_secretsId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}

// Shaped like the web-search server: one required section, one optional one that a deployment
// without the feature simply leaves out.
public record ProbeSettings
{
    public required ProbeSearchConfig Search { get; init; }

    public ProbeSolverConfig? Solver { get; init; }
}

public record ProbeSearchConfig
{
    public required string ApiKey { get; init; }

    public string ApiUrl { get; init; } = "https://example";
}

public record ProbeSolverConfig
{
    public required string ApiKey { get; init; }
}