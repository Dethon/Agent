using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterHttpHelpersCostTests
{
    [Fact]
    public void ExtractCostFromSseData_WithUsageCost_ReturnsCost()
    {
        var data = """{"usage":{"prompt_tokens":10,"completion_tokens":20,"cost":0.00123}}""";

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBe(0.00123m);
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"hello"}}]}""")]
    [InlineData("""{"usage":{"prompt_tokens":10,"completion_tokens":20}}""")]
    [InlineData("not valid json")]
    [InlineData("""{"usage":{"prompt_tokens":10,"cost":null}}""")]
    public void ExtractCostFromSseData_WithNoCost_ReturnsNull(string data)
    {
        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBeNull();
    }
}
