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
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
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
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("light.salon");
    }

    [Fact]
    public void Parse_StateFile()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/state.json");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
    }

    [Fact]
    public void Parse_ActionFile_StripsShExtension()
    {
        var n = HaVfsPath.Parse("entities/light/kitchen/turn_on.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.ClassDomain.ShouldBe("light");
        n.EntitySegment.ShouldBe("kitchen");
        n.Service.ShouldBe("turn_on");
    }

    [Fact]
    public void Parse_ActionFile_UnderArea()
    {
        var n = HaVfsPath.Parse("areas/salon/light.salon/toggle.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("light.salon");
        n.Service.ShouldBe("toggle");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("entities/light/kitchen/extra/deep")]
    [InlineData("areas/salon/light.salon/x/y")]
    public void Parse_Unknown(string path) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(HaVfsKind.Unknown);

    [Fact]
    public void Parse_CompositeEntityDir_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        n.Kind.ShouldBe(HaVfsKind.EntityDir);
        n.ClassDomain.ShouldBe("climate");
        n.EntitySegment.ShouldBe("0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Fact]
    public void Parse_CompositeStateFile_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)/state.json");
        n.Kind.ShouldBe(HaVfsKind.StateFile);
        n.ClassDomain.ShouldBe("climate");
        n.EntitySegment.ShouldBe("0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Fact]
    public void Parse_CompositeActionFile_UnderArea_KeepsRawSegment()
    {
        var n = HaVfsPath.Parse("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)/turn_off.sh");
        n.Kind.ShouldBe(HaVfsKind.ActionFile);
        n.Area.ShouldBe("salon");
        n.EntitySegment.ShouldBe("climate.0x00158d00abcd_(aire-acondicionado-salon)");
        n.Service.ShouldBe("turn_off");
    }
}