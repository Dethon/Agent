using Domain.Contracts;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WebActionToolTests
{
    [Theory]
    [InlineData(null, WebActionType.Click)]
    [InlineData("", WebActionType.Click)]
    [InlineData("click", WebActionType.Click)]
    [InlineData("type", WebActionType.Type)]
    [InlineData("fill", WebActionType.Fill)]
    [InlineData("select", WebActionType.Select)]
    [InlineData("selectoption", WebActionType.Select)]
    [InlineData("press", WebActionType.Press)]
    [InlineData("clear", WebActionType.Clear)]
    [InlineData("hover", WebActionType.Hover)]
    [InlineData("focus", WebActionType.Focus)]
    [InlineData("drag", WebActionType.Drag)]
    [InlineData("back", WebActionType.Back)]
    [InlineData("handledialog", WebActionType.HandleDialog)]
    [InlineData("dialog", WebActionType.HandleDialog)]
    [InlineData("CLICK", WebActionType.Click)]
    [InlineData("Type", WebActionType.Type)]
    [InlineData("HOVER", WebActionType.Hover)]
    public void ParseActionType_ReturnsCorrectValue(string? input, WebActionType expected)
    {
        WebActionTool.ParseActionType(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tap")]
    [InlineData("swipe")]
    [InlineData("screenshot")]
    public void ParseActionType_ThrowsForUnknownAction(string input)
    {
        Should.Throw<ArgumentException>(() => WebActionTool.ParseActionType(input));
    }
}
