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

    [Fact]
    public void ExtractCostFromSseData_WithNoUsage_ReturnsNull()
    {
        var data = """{"choices":[{"delta":{"content":"hello"}}]}""";

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractCostFromSseData_WithUsageButNoCost_ReturnsNull()
    {
        var data = """{"usage":{"prompt_tokens":10,"completion_tokens":20}}""";

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractCostFromSseData_WithInvalidJson_ReturnsNull()
    {
        var data = "not valid json";

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractCostFromSseData_WithNullCostValue_ReturnsNull()
    {
        var data = """{"usage":{"prompt_tokens":10,"cost":null}}""";

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(data);

        result.ShouldBeNull();
    }
}
