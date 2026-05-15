using System.Text;

namespace Domain.Memory;

public static class MemoryLaneRouter
{
    // Stable FNV-1a hash so a given user always maps to the same lane within and
    // across processes (string.GetHashCode is randomized per process).
    public static int LaneFor(string? userId, int laneCount)
    {
        if (laneCount <= 1)
        {
            return 0;
        }

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(userId ?? string.Empty))
        {
            hash = (hash ^ b) * prime;
        }

        return (int)(hash % (uint)laneCount);
    }
}