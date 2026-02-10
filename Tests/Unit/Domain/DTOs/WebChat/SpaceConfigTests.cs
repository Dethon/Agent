using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public class SpaceConfigTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("secret-room")]
    [InlineData("my-space")]
    [InlineData("x")]
    [InlineData("room-42")]
    public void IsValidSlug_ValidSlugs_ReturnsTrue(string slug)
    {
        SpaceConfig.IsValidSlug(slug).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("UPPERCASE")]
    [InlineData("has spaces")]
    [InlineData("has_underscore")]
    [InlineData("-leading-dash")]
    [InlineData("trailing-dash-")]
    [InlineData("double--dash")]
    [InlineData("special!chars")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("../etc/passwd")]
    public void IsValidSlug_InvalidSlugs_ReturnsFalse(string? slug)
    {
        SpaceConfig.IsValidSlug(slug).ShouldBeFalse();
    }

    [Theory]
    [InlineData("#e94560")]
    [InlineData("#fff")]
    [InlineData("#FF00AA")]
    [InlineData("#aabbccdd")]
    public void IsValidHexColor_ValidColors_ReturnsTrue(string color)
    {
        SpaceConfig.IsValidHexColor(color).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#gg0000")]
    [InlineData("e94560")]
    [InlineData("#12345")]
    [InlineData("<script>")]
    public void IsValidHexColor_InvalidColors_ReturnsFalse(string? color)
    {
        SpaceConfig.IsValidHexColor(color).ShouldBeFalse();
    }
}
