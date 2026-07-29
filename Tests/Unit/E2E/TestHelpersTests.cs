using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

public class TestHelpersTests
{
    // Testcontainers' ImageFromDockerfileBuilder posts to the Docker Engine /build endpoint,
    // which serves the legacy builder. That cache is disjoint from the one docker compose and
    // buildx use, and the legacy builder ignores --mount=type=cache, so every E2E rebuild
    // re-resolved NuGet from scratch and could never reuse a compose build. Driving the docker
    // CLI instead puts image builds on BuildKit and back on the shared cache.
    [Fact]
    public void CreateBuildCommand_ForAnyImage_RequestsBuildKit()
    {
        var command = TestHelpers.CreateBuildCommand("/src", "Agent/Dockerfile", "agent:latest");

        command.FileName.ShouldBe("docker");
        command.Environment.ShouldContainKeyAndValue("DOCKER_BUILDKIT", "1");
    }

    [Fact]
    public void CreateBuildCommand_ForAnyImage_TargetsTheDockerfileAndTagWithSolutionRootAsContext()
    {
        var solutionRoot = Path.Combine(Path.DirectorySeparatorChar.ToString(), "src");
        var expectedDockerfile = Path.Combine(solutionRoot, "Agent/Dockerfile");

        var command = TestHelpers.CreateBuildCommand(solutionRoot, "Agent/Dockerfile", "agent:latest");

        command.Arguments.ShouldStartWith("build ");
        command.Arguments.ShouldContain($"--file \"{expectedDockerfile}\"");
        command.Arguments.ShouldContain("--tag \"agent:latest\"");
        command.Arguments.ShouldEndWith($"\"{solutionRoot}\"");
    }
}