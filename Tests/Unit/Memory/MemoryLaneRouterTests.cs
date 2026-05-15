using Domain.Memory;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryLaneRouterTests
{
    [Fact]
    public void LaneFor_SameUser_IsDeterministic()
    {
        var a = MemoryLaneRouter.LaneFor("alice", 4);
        var b = MemoryLaneRouter.LaneFor("alice", 4);

        a.ShouldBe(b);
    }

    [Fact]
    public void LaneFor_AlwaysWithinRange()
    {
        var lanes = Enumerable.Range(0, 200)
            .Select(i => MemoryLaneRouter.LaneFor($"user-{i}", 4))
            .ToList();

        lanes.ShouldAllBe(l => l >= 0 && l < 4);
    }

    [Fact]
    public void LaneFor_SingleLane_AlwaysZero()
    {
        MemoryLaneRouter.LaneFor("alice", 1).ShouldBe(0);
        MemoryLaneRouter.LaneFor("bob", 1).ShouldBe(0);
    }

    [Fact]
    public void LaneFor_DistributesAcrossLanes()
    {
        var distinct = Enumerable.Range(0, 200)
            .Select(i => MemoryLaneRouter.LaneFor($"user-{i}", 4))
            .Distinct()
            .Count();

        distinct.ShouldBe(4);
    }

    [Fact]
    public void LaneFor_NullOrEmptyUser_ReturnsStableLane()
    {
        MemoryLaneRouter.LaneFor(null, 4).ShouldBe(MemoryLaneRouter.LaneFor(null, 4));
        MemoryLaneRouter.LaneFor("", 4).ShouldBeInRange(0, 3);
    }
}