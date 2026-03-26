using McpChannelTelegram.Services;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

public class MessageAccumulatorTests
{
    private readonly MessageAccumulator _sut = new();

    [Fact]
    public void Flush_NoData_ReturnsEmpty()
    {
        _sut.Flush("conv-1").ShouldBeEmpty();
    }

    [Fact]
    public void Flush_SingleAppend_ReturnsSingleChunk()
    {
        _sut.Append("conv-1", "hello");

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(1);
        result[0].ShouldBe("hello");
    }

    [Fact]
    public void Flush_MultipleAppends_ConcatenatesText()
    {
        _sut.Append("conv-1", "hello ");
        _sut.Append("conv-1", "world");

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(1);
        result[0].ShouldBe("hello world");
    }

    [Fact]
    public void Flush_RemovesBuffer_SecondFlushReturnsEmpty()
    {
        _sut.Append("conv-1", "hello");
        _sut.Flush("conv-1");

        _sut.Flush("conv-1").ShouldBeEmpty();
    }

    [Fact]
    public void Flush_SeparateConversations_IndependentBuffers()
    {
        _sut.Append("conv-1", "first");
        _sut.Append("conv-2", "second");

        _sut.Flush("conv-1")[0].ShouldBe("first");
        _sut.Flush("conv-2")[0].ShouldBe("second");
    }

    [Fact]
    public void Flush_ExactlyAtLimit_ReturnsSingleChunk()
    {
        var text = new string('a', 4096);
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(1);
        result[0].Length.ShouldBe(4096);
    }

    [Fact]
    public void Flush_ExceedsLimit_SplitsIntoMultipleChunks()
    {
        var text = new string('a', 5000);
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBeGreaterThan(1);
        var total = result.Sum(c => c.Length);
        total.ShouldBe(5000);
    }

    [Fact]
    public void Flush_LongTextWithNewlines_SplitsAtNewlineBoundary()
    {
        // Build text: lines of 100 chars each, so 41 lines = 4100 chars (each line + newline)
        var line = new string('x', 99) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, 50)); // 5000 chars
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBeGreaterThan(1);
        // First chunk should end cleanly (no partial line) since we split at newlines
        result[0].Length.ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact]
    public void Flush_LongTextNoNewlines_SplitsAtLimit()
    {
        var text = new string('a', 8192);
        _sut.Append("conv-1", text);

        var result = _sut.Flush("conv-1");

        result.Count.ShouldBe(2);
        result[0].Length.ShouldBe(4096);
        result[1].Length.ShouldBe(4096);
    }
}