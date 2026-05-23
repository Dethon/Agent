using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaVfsPathTests
{
    [Theory]
    [InlineData("", HaVfsKind.Root)]
    [InlineData("entities", HaVfsKind.EntitiesRoot)]
    [InlineData("areas", HaVfsKind.AreasRoot)]
    public void Parse_Roots(string path, HaVfsKind kind) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(kind);

    [Fact]
    public void Parse_ClassDir()
    {
        var n = HaVfsPath.Parse("entities/light");
        n.Kind.ShouldBe(HaVfsKind.ClassDir);
        n.ClassDomain.ShouldBe("light");
    }

    [Fact]
    public void Parse_EntityDir_FromEntitiesRoot()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.EntityId.ShouldBe("light.kitchen");
    }

    [Fact]
    public void Parse_AreaDir()
    {
        var n = HaVfsPath.Parse("areas/salon");
        n.Kind.ShouldBe(HaVfsKind.AreaDir);
        n.Area.ShouldBe("salon");
    }

    [Fact]
    public void Parse_EntityDir_FromAreasRoot_UsesFullEntityId()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.EntityId.ShouldBe("light.salon");
        n.Area.ShouldBe("salon");
    }

    [Fact]
    public void Parse_StateFile()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/state.yaml");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.EntityId.ShouldBe("light.kitchen");
    }

    [Fact]
    public void Parse_ActionFile_StripsShExtension()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/turn_on.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.EntityId.ShouldBe("light.kitchen");
        n.Service.ShouldBe("turn_on");
    }

    [Fact]
    public void Parse_ActionFile_UnderArea()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon/toggle.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.EntityId.ShouldBe("light.salon");
        n.Service.ShouldBe("toggle");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("entities/light/kitchen/extra/deep")]
    [InlineData("areas/salon/light.salon/x/y")]
    public void Parse_Unknown(string path) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(HaVfsKind.Unknown);
}