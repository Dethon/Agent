using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public sealed class SpaceConfigTests
{
    [Fact]
    public void DefaultAccentColor_IsAValidHexColor()
    {
        SpaceConfig.IsValidHexColor(SpaceConfig.DefaultAccentColor).ShouldBeTrue();
    }
}