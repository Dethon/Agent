using System.Text.RegularExpressions;
using Shouldly;

namespace Tests.Integration.McpServers;

// Two things about the servers that no registration test can reach.
//
// The first is whether the table covers them all: every other test here is a theory over the rows,
// so a fourteenth server with no row is tested by nothing at all and nothing says so.
//
// The second is HOW a server reads its configuration. Every slice test in the repo hands a module a
// settings object built in C#, so a Program.cs that swapped BindSettings<T> for
// builder.Configuration.Get<T>() would stay green everywhere while losing the precedence
// ADR-0005 exists for — user secrets last, so they outrank environment variables. That failure is
// silent: DockerCompose/.env ships every secret as an empty placeholder, and several settings read
// an empty string as "feature not configured", so the deployment comes up healthy with CapSolver,
// web push and the Music Assistant action quietly switched off.
//
// Read off the source text, because the question is which configuration sources were added and in
// what order, and by the time a server is running that is a decision already taken.
public class McpServerTableTests
{
    public static TheoryData<string> Servers => McpServerRegistrations.Ids(McpServerRegistrations.All);

    private static readonly Regex SolutionProject =
        new(@"^Project\(""\{[^}]+\}""\) = ""(Mcp(?:Server|Channel)\w+)""", RegexOptions.Multiline);

    private static readonly Regex BindSettingsCall = new(@"\bBindSettings\s*<");

    // builder.Configuration.GetVoiceSettings() is deliberately not one of these: the call's own
    // type argument or parenthesis has to follow the name straight away, so a helper that wraps
    // BindSettings still reads as going through it.
    private static readonly Regex DirectConfigurationRead =
        new(@"[Cc]onfig(?:uration|Builder)?\s*\.\s*(?:Get|GetSection|GetValue|Bind)\s*[<(]");

    // A server that adds its own configuration source is stating the precedence itself, which is
    // the one decision BindSettings owns.
    private static readonly Regex OwnConfigurationSource =
        new(@"\.\s*Add(?:UserSecrets|EnvironmentVariables|JsonFile)\s*[<(]");

    [Fact]
    public void EveryServerProjectInTheSolution_HasARow() =>
        SolutionServerProjects().ShouldBe(
            McpServerRegistrations.All.Select(row => row.ProjectDirectory).Order().ToList(),
            "every McpServer*/McpChannel* project in Ziggurat.sln needs a row in McpServerRegistrations");

    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_ReadsItsConfigurationThroughBindSettings(string id) =>
        Sources(id).Values.Any(BindSettingsCall.IsMatch).ShouldBeTrue(
            $"{id} must read its configuration through BindSettings<T>, which is where the "
            + "user-secrets-last order lives");

    [Theory]
    [MemberData(nameof(Servers))]
    public void NoServer_ReadsConfigurationItself(string id) =>
        Sources(id)
            .Where(file => DirectConfigurationRead.IsMatch(file.Value)
                           || OwnConfigurationSource.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty(
                $"{id} must leave binding and configuration sources to BindSettings<T>; these files "
                + "read configuration or add a source of their own");

    private static IReadOnlyList<string> SolutionServerProjects() =>
        SolutionProject
            .Matches(File.ReadAllText(Path.Combine(McpServerRegistrations.RepoRoot, "Ziggurat.sln")))
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToList();

    // Every .cs file the server ships, not Program.cs alone: voice reads its settings from
    // Modules/ConfigModule.cs, and a direct read is just as wrong wherever it is written.
    private static IReadOnlyDictionary<string, string> Sources(string id)
    {
        var project = McpServerRegistrations.ProjectPath(McpServerRegistrations.Get(id));

        return Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(project, file).StartsWith("bin", StringComparison.Ordinal)
                           && !Path.GetRelativePath(project, file).StartsWith("obj", StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(project, file), File.ReadAllText);
    }
}