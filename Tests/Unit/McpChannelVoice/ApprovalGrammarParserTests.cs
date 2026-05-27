using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ApprovalGrammarParserTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes please")]
    [InlineData("sí")]
    [InlineData("si por favor")]
    [InlineData("confirm")]
    [InlineData("ok")]
    [InlineData("okay")]
    [InlineData("vale")]
    public void Parse_Affirmative(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Approved);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("cancel")]
    [InlineData("cancelar")]
    [InlineData("nope")]
    public void Parse_Negative(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Declined);
    }

    [Theory]
    [InlineData("yes please cancel that")]
    [InlineData("maybe")]
    [InlineData("")]
    public void Parse_Ambiguous(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Ambiguous);
    }
}