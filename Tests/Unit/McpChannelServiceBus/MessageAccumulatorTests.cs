using McpChannelServiceBus.Services;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class MessageAccumulatorTests
{
    private readonly MessageAccumulator _sut = new();

    [Fact]
    public void Flush_NoData_ReturnsNull()
    {
        _sut.Flush("conv-1").ShouldBeNull();
    }

    [Fact]
    public void Flush_SingleAppend_ReturnsText()
    {
        _sut.Append("conv-1", "hello");

        _sut.Flush("conv-1").ShouldBe("hello");
    }

    [Fact]
    public void Flush_MultipleAppends_ConcatenatesText()
    {
        _sut.Append("conv-1", "hello ");
        _sut.Append("conv-1", "world");

        _sut.Flush("conv-1").ShouldBe("hello world");
    }

    [Fact]
    public void Flush_RemovesBuffer_SecondFlushReturnsNull()
    {
        _sut.Append("conv-1", "hello");
        _sut.Flush("conv-1");

        _sut.Flush("conv-1").ShouldBeNull();
    }

    [Fact]
    public void Flush_SeparateConversations_IndependentBuffers()
    {
        _sut.Append("conv-1", "first");
        _sut.Append("conv-2", "second");

        _sut.Flush("conv-1").ShouldBe("first");
        _sut.Flush("conv-2").ShouldBe("second");
    }

    [Fact]
    public void Append_EmptyString_FlushReturnsEmpty()
    {
        _sut.Append("conv-1", "");

        // StringBuilder with empty string has Length 0, so Flush returns null
        _sut.Flush("conv-1").ShouldBeNull();
    }

    [Fact]
    public void Flush_LargeText_ReturnsFullContent()
    {
        var largeText = new string('a', 100_000);
        _sut.Append("conv-1", largeText);

        var result = _sut.Flush("conv-1");
        result.ShouldNotBeNull();
        result.Length.ShouldBe(100_000);
    }
}
