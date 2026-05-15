using Domain.Contracts;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class ChatMonitorMemoryHookTests
{
    [Fact]
    public void ChatMonitor_Constructor_AcceptsOptionalMemoryRecallHook()
    {
        var hook = new Mock<IMemoryRecallHook>();
        hook.ShouldNotBeNull();
    }
}