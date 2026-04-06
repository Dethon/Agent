using Domain.Contracts;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WebClickToolTests
{
    [Theory]
    [InlineData("selectoption", ClickAction.SelectOption)]
    [InlineData("selectOption", ClickAction.SelectOption)]
    [InlineData("setrange", ClickAction.SetRange)]
    [InlineData("setRange", ClickAction.SetRange)]
    [InlineData("type", ClickAction.Type)]
    [InlineData("Type", ClickAction.Type)]
    public void ParseAction_NewActions_ReturnCorrectEnum(string input, ClickAction expected)
    {
        var result = TestableWebClickTool.TestParseAction(input);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("click", ClickAction.Click)]
    [InlineData("fill", ClickAction.Fill)]
    [InlineData("clear", ClickAction.Clear)]
    [InlineData("press", ClickAction.Press)]
    [InlineData("doubleclick", ClickAction.DoubleClick)]
    [InlineData("rightclick", ClickAction.RightClick)]
    [InlineData("hover", ClickAction.Hover)]
    [InlineData(null, ClickAction.Click)]
    [InlineData("", ClickAction.Click)]
    public void ParseAction_ExistingActions_StillWork(string? input, ClickAction expected)
    {
        var result = TestableWebClickTool.TestParseAction(input);
        result.ShouldBe(expected);
    }
}

public class TestableWebClickTool : global::Domain.Tools.Web.WebClickTool
{
    public TestableWebClickTool() : base(null!) { }

    public static ClickAction TestParseAction(string? action)
    {
        return ParseActionValue(action);
    }
}
