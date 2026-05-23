using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaServiceHelpRendererTests
{
    private static HaServiceField Field(string? desc, bool required, JsonNode? selector) =>
        new() { Description = desc, Required = required, Selector = selector };

    [Fact]
    public void Render_HeaderFieldsAndTypes()
    {
        var svc = Service("light", "turn_on", AnyEntityTarget(),
            ("brightness_pct", Field("Brightness", false, JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
            ("flash", Field(null, false, JsonNode.Parse("""{"select":{"options":["short","long"]}}"""))));

        var help = HaServiceHelpRenderer.Render("light.kitchen", svc);

        help.ShouldContain("turn_on.sh — call light.turn_on on light.kitchen");
        help.ShouldContain("--brightness_pct");
        help.ShouldContain("1-100");
        help.ShouldContain("--flash");
        help.ShouldContain("short");
    }

    [Fact]
    public void Render_NoFields_SaysNoArguments()
    {
        HaServiceHelpRenderer.Render("light.kitchen", Service("light", "toggle", AnyEntityTarget()))
            .ShouldContain("(no arguments)");
    }
}