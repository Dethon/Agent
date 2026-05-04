using System.Text.Json;
using Domain.Contracts;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WebActionToolTests
{
    [Theory]
    [InlineData("\"click\"", WebActionType.Click)]
    [InlineData("\"type\"", WebActionType.Type)]
    [InlineData("\"fill\"", WebActionType.Fill)]
    [InlineData("\"select\"", WebActionType.Select)]
    [InlineData("\"press\"", WebActionType.Press)]
    [InlineData("\"clear\"", WebActionType.Clear)]
    [InlineData("\"hover\"", WebActionType.Hover)]
    [InlineData("\"focus\"", WebActionType.Focus)]
    [InlineData("\"drag\"", WebActionType.Drag)]
    [InlineData("\"back\"", WebActionType.Back)]
    [InlineData("\"CLICK\"", WebActionType.Click)]
    [InlineData("\"Type\"", WebActionType.Type)]
    public void WebActionType_DeserializesFromSnakeCaseLowerString(string json, WebActionType expected)
    {
        JsonSerializer.Deserialize<WebActionType>(json).ShouldBe(expected);
    }

    [Theory]
    [InlineData(WebActionType.Click, "\"click\"")]
    [InlineData(WebActionType.Back, "\"back\"")]
    [InlineData(WebActionType.Hover, "\"hover\"")]
    public void WebActionType_SerializesToSnakeCaseLowerString(WebActionType value, string expected)
    {
        JsonSerializer.Serialize(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"tap\"")]
    [InlineData("\"swipe\"")]
    public void WebActionType_ThrowsForUnknownValue(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<WebActionType>(json));
    }
}
