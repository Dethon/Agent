using Infrastructure.Utils;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ToolPatternMatcherTests
{
    [Theory]
    [InlineData("mcp:server:Tool", "mcp:server:Tool", true)]
    [InlineData("mcp:server:Tool", "mcp:server:tool", true)] // case insensitive
    [InlineData("mcp:server:Tool", "mcp:server:OtherTool", false)]
    public void IsMatch_ExactPattern_MatchesCorrectly(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("mcp:mcp-library:FileSearch", "mcp:mcp-library:*", true)]
    [InlineData("mcp:mcp-library:ListFiles", "mcp:mcp-library:*", true)]
    [InlineData("mcp:other-server:FileSearch", "mcp:mcp-library:*", false)]
    public void IsMatch_ServerWildcard_MatchesAllToolsFromServer(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("mcp:any-server:AnyTool", "mcp:*", true)]
    [InlineData("mcp:localhost:TestTool", "mcp:*", true)]
    [InlineData("local:SomeTool", "mcp:*", false)]
    public void IsMatch_AllMcpWildcard_MatchesAllMcpTools(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("mcp:server:Tool", "*", true)]
    [InlineData("local:Tool", "*", true)]
    [InlineData("anything", "*", true)]
    public void IsMatch_GlobalWildcard_MatchesEverything(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Fact]
    public void IsMatch_MultiplePatterns_MatchesAny()
    {
        var matcher = new ToolPatternMatcher(["mcp:server1:*", "mcp:server2:SpecificTool"]);

        matcher.IsMatch("mcp:server1:AnyTool").ShouldBeTrue();
        matcher.IsMatch("mcp:server2:SpecificTool").ShouldBeTrue();
        matcher.IsMatch("mcp:server2:OtherTool").ShouldBeFalse();
        matcher.IsMatch("mcp:server3:Tool").ShouldBeFalse();
    }

    [Fact]
    public void IsMatch_EmptyPatterns_MatchesNothing()
    {
        var matcher = new ToolPatternMatcher([]);
        matcher.IsMatch("mcp:server:Tool").ShouldBeFalse();
    }

    [Fact]
    public void IsMatch_NullPatterns_MatchesNothing()
    {
        var matcher = new ToolPatternMatcher(null);
        matcher.IsMatch("mcp:server:Tool").ShouldBeFalse();
    }

    [Theory]
    [InlineData("mcp:mcp-library:FileSearch", "*:FileSearch", true)]
    [InlineData("mcp:other-server:FileSearch", "*:FileSearch", true)]
    [InlineData("local:FileSearch", "*:FileSearch", true)]
    [InlineData("mcp:server:OtherTool", "*:FileSearch", false)]
    public void IsMatch_ToolNameWildcard_MatchesToolFromAnySource(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }
}