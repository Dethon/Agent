using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaTreeTests
{
    private static HaCatalog Cat() => new(
        [Entity("light.kitchen", "off"), Entity("sensor.salon_temp", "21")],
        [Service("light", "turn_on", AnyEntityTarget())],
        [new HaAreaEntities("salon", "Salón", ["sensor.salon_temp"])]);

    [Fact]
    public void Directories_IncludeRootsClassesEntitiesAndAreas()
    {
        var dirs = HaTree.Directories(Cat());
        dirs.ShouldContain("entities");
        dirs.ShouldContain("entities/light");
        dirs.ShouldContain("entities/light/kitchen");
        dirs.ShouldContain("areas");
        dirs.ShouldContain("areas/salon");
        dirs.ShouldContain("areas/salon/sensor.salon_temp");
        dirs.ShouldContain("areas/unassigned/light.kitchen");
    }

    [Fact]
    public void Files_IncludeStateAndApplicableActions()
    {
        var files = HaTree.Files(Cat());
        files.ShouldContain("entities/light/kitchen/state.json");
        files.ShouldContain("entities/light/kitchen/turn_on.sh");
        files.ShouldContain("entities/sensor/salon_temp/state.json");
        files.ShouldNotContain("entities/sensor/salon_temp/turn_on.sh"); // no actions for sensor
    }

    [Fact]
    public void Glob_TrailingSlash_ReturnsDirectoriesMarkedWithSlash()
    {
        var hits = HaTree.Glob(Cat(), "entities/light", "*/");

        hits.ShouldBe(["entities/light/kitchen/"]);
    }

    [Fact]
    public void Glob_NoTrailingSlash_MatchesFiles()
    {
        var hits = HaTree.Glob(Cat(), "entities", "**/*.sh");

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(h => h.EndsWith(".sh"));
    }

    [Fact]
    public void Glob_NoTrailingSlash_ReturnsBothDirectoriesAndFiles()
    {
        var hits = HaTree.Glob(Cat(), "entities/light", "**");

        hits.ShouldContain("entities/light/kitchen/");
        hits.ShouldContain("entities/light/kitchen/state.json");
        hits.ShouldContain("entities/light/kitchen/turn_on.sh");
    }

    [Fact]
    public void Glob_DoubleStar_MatchesZeroLeadingSegments()
    {
        // `**/X` must match X at the base level (zero leading segments), matching the Local file
        // matcher. The old `** -> .*` translation required a leading '/', so `**/entities` missed
        // the base-level `entities` directory.
        var hits = HaTree.Glob(Cat(), "", "**/entities");

        hits.ShouldContain("entities/");
    }

    [Fact]
    public void Glob_DoubleStar_StillRecursesIntoSubdirectories()
    {
        var hits = HaTree.Glob(Cat(), "", "**/state.json");

        hits.ShouldContain("entities/light/kitchen/state.json");
        hits.ShouldContain("entities/sensor/salon_temp/state.json");
    }

    [Fact]
    public void Directories_UseCompositeNameWhenFriendlyNamePresent()
    {
        var cat = new HaCatalog(
            [Entity("climate.0x00158d00abcd", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón")))],
            [],
            [new HaAreaEntities("salon", "Salón", ["climate.0x00158d00abcd"])]);

        var dirs = HaTree.Directories(cat);

        dirs.ShouldContain("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        dirs.ShouldContain("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Fact]
    public void Files_UseCompositeDir()
    {
        var cat = new HaCatalog(
            [Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen Light")))],
            [Service("light", "turn_on", AnyEntityTarget())],
            []);

        var files = HaTree.Files(cat);

        files.ShouldContain("entities/light/kitchen_(kitchen-light)/state.json");
        files.ShouldContain("entities/light/kitchen_(kitchen-light)/turn_on.sh");
    }
}