using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

public class E2EImagesTests
{
    // COPY ["Domain/Domain.csproj", "Domain/"] and COPY ["Domain/", "Domain/"] both name a
    // source directory the built image depends on. Stage-to-stage copies (--from=) carry no
    // host dependency, so they are skipped.
    private static readonly Regex _copySource = new(
        @"^\s*COPY\s+(?!--from=)\[\s*""(?<src>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static TheoryData<string> ImageNames() =>
        [.. E2EImages.All.Select(i => i.ImageName)];

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void WatchedDirs_ForEveryDirectoryTheDockerfileCopies_ContainsThatDirectory(string imageName)
    {
        var spec = E2EImages.All.Single(i => i.ImageName == imageName);
        var solutionRoot = TestHelpers.FindSolutionRoot();

        var copiedDirs = CopiedSourceDirectories(Path.Combine(solutionRoot, spec.Dockerfile));

        copiedDirs.ShouldNotBeEmpty($"{spec.Dockerfile} has no host COPY instructions — the regex likely stopped matching.");
        copiedDirs.Except(spec.WatchedDirs).ShouldBeEmpty(
            $"{spec.Dockerfile} copies these directories, but {imageName} does not watch them, "
            + "so edits to them will not trigger a rebuild and E2E tests will run against a stale image.");
    }

    private static IReadOnlyList<string> CopiedSourceDirectories(string dockerfilePath) =>
        [.. _copySource.Matches(File.ReadAllText(dockerfilePath))
            .Select(m => m.Groups["src"].Value.Split('/')[0])
            .Where(d => d.Length > 0)
            .Distinct()];
}