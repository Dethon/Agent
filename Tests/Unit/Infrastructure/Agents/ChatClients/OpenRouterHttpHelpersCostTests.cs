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

    [Fact]
    public void ExtractCachedTokensFromSseData_ReadsThePromptCacheCounter()
    {
        // The only direct measure of whether the ~17k static prefix is served from cache; inferring
        // it from cost fails because the :nitro routing variants may not price like the base model.
        const string data = """
            {"usage":{"prompt_tokens":21639,"completion_tokens":50,"cost":0.004,
             "prompt_tokens_details":{"cached_tokens":13800,"cache_write_tokens":0}}}
            """;

        OpenRouterHttpHelpers.ExtractCachedTokensFromSseData(data).ShouldBe(13800);
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":10}}""")]
    [InlineData("""{"usage":{"prompt_tokens_details":{"audio_tokens":0}}}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("not json")]
    public void ExtractCachedTokensFromSseData_WithoutTheCounter_ReturnsNull(string data)
    {
        // Null, never 0: "the provider said nothing" must stay distinguishable from "nothing cached".
        OpenRouterHttpHelpers.ExtractCachedTokensFromSseData(data).ShouldBeNull();
    }

}