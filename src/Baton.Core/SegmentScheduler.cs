namespace Baton.Core;

/// <summary>
/// Hands out byte ranges to connections. A connection that finishes its range takes the
/// unfinished half of the largest range still being downloaded, so slow connections
/// cannot hold up the end of the file.
/// </summary>
public sealed class SegmentScheduler
{
    private readonly object _lock = new();
    private readonly List<SegmentState> _segments;
    private readonly HashSet<SegmentState> _active = [];
    private readonly long _minSplit;

    public SegmentScheduler(List<SegmentState> segments, long minSplit)
    {
        _segments = segments;
        _minSplit = Math.Max(1, minSplit);
    }

    /// <summary>Splits [0, totalSize) into up to <paramref name="parts"/> ranges no smaller than minSize.</summary>
    public static List<SegmentState> Plan(long? totalSize, bool supportsRanges, int parts, long minSize)
    {
        if (totalSize == 0)
            return [];
        if (totalSize is not { } size || !supportsRanges)
            return [new SegmentState { Start = 0, End = totalSize is { } s ? s - 1 : -1 }];

        var count = (int)Math.Clamp(size / Math.Max(1, minSize), 1, Math.Max(1, parts));
        var chunk = size / count;
        var result = new List<SegmentState>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * chunk;
            var end = i == count - 1 ? size - 1 : start + chunk - 1;
            result.Add(new SegmentState { Start = start, End = end });
        }
        return result;
    }

    public int ActiveCount
    {
        get { lock (_lock) return _active.Count; }
    }

    public bool AllComplete
    {
        get { lock (_lock) return _segments.All(s => s.IsComplete); }
    }

    public SegmentState? Acquire()
    {
        lock (_lock)
        {
            var idle = _segments.FirstOrDefault(s => !s.IsComplete && !_active.Contains(s));
            if (idle is not null)
            {
                _active.Add(idle);
                return idle;
            }

            var victim = _active
                .Where(s => s.HasKnownEnd)
                .OrderByDescending(s => s.Remaining)
                .FirstOrDefault();
            if (victim is null || victim.Remaining < 2 * _minSplit)
                return null;

            var mid = victim.Position + victim.Remaining / 2;
            var stolen = new SegmentState { Start = mid, End = victim.End };
            victim.End = mid - 1;
            _segments.Insert(_segments.IndexOf(victim) + 1, stolen);
            _active.Add(stolen);
            return stolen;
        }
    }

    public void Release(SegmentState segment)
    {
        lock (_lock) _active.Remove(segment);
    }

    public long PositionOf(SegmentState segment)
    {
        lock (_lock) return segment.Position;
    }

    public long EndOf(SegmentState segment)
    {
        lock (_lock) return segment.End;
    }

    public bool IsComplete(SegmentState segment)
    {
        lock (_lock) return segment.IsComplete;
    }

    /// <summary>How many of <paramref name="count"/> bytes at <paramref name="offset"/> still belong to the segment.</summary>
    public int Clamp(SegmentState segment, long offset, int count)
    {
        lock (_lock)
        {
            if (!segment.HasKnownEnd)
                return count;
            return (int)Math.Clamp(segment.End - offset + 1, 0, count);
        }
    }

    public void Advance(SegmentState segment, int bytes)
    {
        lock (_lock) segment.Written += bytes;
    }

    /// <summary>Restarts a segment from its first byte (used when the server cannot resume).</summary>
    public void Reset(SegmentState segment)
    {
        lock (_lock) segment.Written = 0;
    }

    /// <summary>Marks an open-ended segment finished at its current position.</summary>
    public void CloseOpenEnd(SegmentState segment)
    {
        lock (_lock)
        {
            segment.End = segment.Position - 1;
            segment.Closed = true;
        }
    }

    public List<SegmentState> Snapshot()
    {
        lock (_lock) return _segments.Select(s => s.Clone()).ToList();
    }

    /// <summary>Every range with whether a connection is working on it, for the progress view.</summary>
    public List<SegmentView> View()
    {
        lock (_lock) return _segments.Select(s => new SegmentView(s.Start, s.End, s.Written, _active.Contains(s))).ToList();
    }
}
