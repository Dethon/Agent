using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaStateRendererTests
{
    [Fact]
    public void ToYaml_RendersScalarsAndAttributes()
    {
        var entity = Entity("light.kitchen", "off",
            ("friendly_name", JsonValue.Create("Kitchen")),
            ("brightness", JsonValue.Create((int?)null)),
            ("modes", JsonNode.Parse("""["color_temp","xy"]""")));

        var yaml = HaStateRenderer.ToYaml(entity);

        yaml.ShouldContain("entity_id: light.kitchen");
        yaml.ShouldContain("state: \"off\"");
        yaml.ShouldContain("last_changed: 2026-05-23T09:14:02");
        yaml.ShouldContain("attributes:");
        yaml.ShouldContain("  brightness: null");
        yaml.ShouldContain("  friendly_name: \"Kitchen\"");
        yaml.ShouldContain("""  modes: ["color_temp","xy"]""");
    }

    [Fact]
    public void ToYaml_NoAttributes_EmitsEmptyMap()
    {
        HaStateRenderer.ToYaml(Entity("sun.sun", "above_horizon"))
            .ShouldContain("attributes: {}");
    }
}