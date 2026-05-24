using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaArgParserTests
{
    private static HaServiceField Field(JsonNode? selector) =>
        new() { Selector = selector };

    private static HaServiceDefinition Svc() => Service("light", "turn_on", AnyEntityTarget(),
        ("brightness_pct", Field(JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
        ("on", Field(JsonNode.Parse("""{"boolean":{}}"""))),
        ("modes", Field(JsonNode.Parse("""{"select":{"multiple":true,"options":["a","b"]}}"""))),
        ("flash", Field(JsonNode.Parse("""{"select":{"options":[{"value":"short"},{"value":"long"}]}}"""))),
        ("advanced", Field(JsonNode.Parse("""{"object":{}}"""))),
        ("name", Field(JsonNode.Parse("""{"text":{}}"""))));

    [Fact]
    public void Parse_CoercesBySelectorType()
    {
        var data = HaArgParser.Parse(
            ["--brightness_pct", "60", "--on", "true", "--modes", "a,b", "--advanced", """{"eco":true}""", "--name", "Lamp"],
            Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["on"]!.GetValue<bool>().ShouldBeTrue();
        ((JsonArray)data["modes"]!).Count.ShouldBe(2);
        data["advanced"]!["eco"]!.GetValue<bool>().ShouldBeTrue();
        data["name"]!.GetValue<string>().ShouldBe("Lamp");
    }

    [Fact]
    public void Parse_UnknownFlag_Throws()
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--nope", "1"], Svc()))
            .Message.ShouldContain("nope");
    }

    [Fact]
    public void Parse_BadBoolean_Throws()
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--on", "yes"], Svc()))
            .Message.ShouldContain("on");
    }

    [Fact]
    public void Parse_BadNumber_Throws()
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--brightness_pct", "NaN"], Svc()))
            .Message.ShouldContain("brightness_pct");
    }

    [Fact]
    public void Parse_Empty_ReturnsEmptyObject()
    {
        HaArgParser.Parse([], Svc()).Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_SingleSelectValidOption_Passes()
    {
        HaArgParser.Parse(["--flash", "short"], Svc())["flash"]!.GetValue<string>().ShouldBe("short");
    }

    [Fact]
    public void Parse_SingleSelectInvalidOption_Throws()
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--flash", "bogus"], Svc()))
            .Message.ShouldContain("flash");
    }

    [Fact]
    public void Parse_EqualsSyntax_CoercesBySelectorType()
    {
        var data = HaArgParser.Parse(
            ["--brightness_pct=60", "--name=Lamp", """--advanced={"eco":true}"""],
            Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["name"]!.GetValue<string>().ShouldBe("Lamp");
        data["advanced"]!["eco"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Parse_EqualsSyntax_SplitsOnFirstEqualsOnly()
    {
        HaArgParser.Parse(["--name=a=b"], Svc())["name"]!.GetValue<string>().ShouldBe("a=b");
    }

    [Fact]
    public void Parse_MixedEqualsAndSpaceForms_BothWork()
    {
        var data = HaArgParser.Parse(["--brightness_pct=60", "--on", "true"], Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["on"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Parse_SpaceFormValueLooksLikeFlag_Throws()
    {
        // A bare `--flag` whose space-form value is itself a `--flag` must not be silently swallowed;
        // the value is missing. Use --name=<value> when the value legitimately begins with '--'.
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(["--name", "--on"], Svc()))
            .Message.ShouldContain("name");
    }

    [Fact]
    public void Parse_SpaceFormSingleDashValue_IsAccepted()
    {
        // Only '--' starts a flag; a single '-' (e.g. a negative number) is a valid value.
        HaArgParser.Parse(["--brightness_pct", "-5"], Svc())["brightness_pct"]!.GetValue<int>().ShouldBe(-5);
    }
}