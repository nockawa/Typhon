using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Naive linear-scan spatial index for query correctness verification.
/// Oracle: results from R-Tree must match results from brute force.
/// </summary>
internal class BruteForceSpatialIndex
{
    private readonly List<(long entityId, double[] coords)> _entries = new();
    private readonly int _coordCount;

    internal BruteForceSpatialIndex(int coordCount) => _coordCount = coordCount;

    internal void Insert(long entityId, ReadOnlySpan<double> coords) =>
        _entries.Add((entityId, coords.ToArray()));

    internal void Remove(long entityId) =>
        _entries.RemoveAll(e => e.entityId == entityId);

    internal void Update(long entityId, ReadOnlySpan<double> newCoords)
    {
        Remove(entityId);
        Insert(entityId, newCoords);
    }

    internal List<long> QueryAABB(ReadOnlySpan<double> queryCoords)
    {
        var result = new List<long>();
        int halfCoord = _coordCount / 2;
        foreach (var (entityId, coords) in _entries)
        {
            bool overlaps = true;
            for (int d = 0; d < halfCoord; d++)
            {
                // Separating axis: entry.max < query.min || entry.min > query.max → no overlap
                if (coords[d + halfCoord] < queryCoords[d] || coords[d] > queryCoords[d + halfCoord])
                {
                    overlaps = false;
                    break;
                }
            }
            if (overlaps)
            {
                result.Add(entityId);
            }
        }
        return result;
    }

    internal int Count => _entries.Count;
}
