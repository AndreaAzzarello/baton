using Baton.Core;

namespace Baton.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public void Plan_SplitsIntoContiguousRanges()
    {
        var plan = SegmentScheduler.Plan(10_000_001, supportsRanges: true, parts: 4, minSize: 1000);

        Assert.Equal(4, plan.Count);
        Assert.Equal(0, plan[0].Start);
        for (var i = 1; i < plan.Count; i++)
            Assert.Equal(plan[i - 1].End + 1, plan[i].Start);
        Assert.Equal(10_000_000, plan[^1].End);
    }

    [Fact]
    public void Plan_SmallFile_UsesFewerParts()
    {
        Assert.Single(SegmentScheduler.Plan(1500, true, 8, 1000));
        Assert.Single(SegmentScheduler.Plan(null, true, 8, 1000));
        Assert.Single(SegmentScheduler.Plan(1_000_000, false, 8, 1000));
        Assert.Empty(SegmentScheduler.Plan(0, true, 8, 1000));
    }

    [Fact]
    public void Acquire_SplitsLargestActiveRangeWhenNothingIsIdle()
    {
        var segments = new List<SegmentState> { new() { Start = 0, End = 999 } };
        var scheduler = new SegmentScheduler(segments, minSplit: 100);

        var first = scheduler.Acquire()!;
        scheduler.Advance(first, 200);
        var stolen = scheduler.Acquire()!;

        Assert.Equal(600, stolen.Start);   // half of the 800 bytes still missing
        Assert.Equal(999, stolen.End);
        Assert.Equal(599, first.End);
        Assert.Equal(400, scheduler.Clamp(first, 200, 1000)); // the old owner stops at its new end
    }

    [Fact]
    public void Acquire_DoesNotSplitBelowMinimum()
    {
        var segments = new List<SegmentState> { new() { Start = 0, End = 150 } };
        var scheduler = new SegmentScheduler(segments, minSplit: 100);

        Assert.NotNull(scheduler.Acquire());
        Assert.Null(scheduler.Acquire());
    }

    [Fact]
    public void ResumedState_SkipsCompletedSegments()
    {
        var segments = new List<SegmentState>
        {
            new() { Start = 0, End = 99, Written = 100 },
            new() { Start = 100, End = 199, Written = 40 },
        };
        var scheduler = new SegmentScheduler(segments, 10);

        var next = scheduler.Acquire()!;

        Assert.Equal(100, next.Start);
        Assert.Equal(140, scheduler.PositionOf(next));
    }
}
