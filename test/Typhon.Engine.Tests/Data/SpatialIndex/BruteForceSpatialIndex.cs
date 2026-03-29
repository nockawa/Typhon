using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Naive linear-scan spatial index for query correctness verification.
/// Oracle: results from R-Tree must match results from brute force.
/// </summary>
internal class BruteForceSpatialIndex
{
    private readonly List<(long entityId, double[] coords, uint categoryMask)> _entries = new();
    private readonly int _coordCount;

    internal BruteForceSpatialIndex(int coordCount) => _coordCount = coordCount;

    internal void Insert(long entityId, ReadOnlySpan<double> coords, uint categoryMask = uint.MaxValue) =>
        _entries.Add((entityId, coords.ToArray(), categoryMask));

    private void Remove(long entityId) => _entries.RemoveAll(e => e.entityId == entityId);

    internal void Update(long entityId, ReadOnlySpan<double> newCoords)
    {
        int idx = _entries.FindIndex(e => e.entityId == entityId);
        uint mask = idx >= 0 ? _entries[idx].categoryMask : uint.MaxValue;
        Remove(entityId);
        Insert(entityId, newCoords, mask);
    }

    internal void SetCategoryMask(long entityId, uint mask)
    {
        int idx = _entries.FindIndex(e => e.entityId == entityId);
        if (idx >= 0)
        {
            _entries[idx] = (_entries[idx].entityId, _entries[idx].coords, mask);
        }
    }

    internal List<long> QueryAABB(ReadOnlySpan<double> queryCoords, uint categoryMask = 0)
    {
        var result = new List<long>();
        int halfCoord = _coordCount / 2;
        foreach (var (entityId, coords, entryMask) in _entries)
        {
            if (categoryMask != 0 && (entryMask & categoryMask) != categoryMask)
            {
                continue;
            }
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

    internal List<long> QueryRadius(ReadOnlySpan<double> center, double radius, uint categoryMask = 0)
    {
        // Convert to AABB query (same as tree implementation — coarse filter)
        int halfCoord = _coordCount / 2;
        Span<double> aabb = stackalloc double[_coordCount];
        for (int d = 0; d < halfCoord; d++)
        {
            aabb[d] = center[d] - radius;
            aabb[d + halfCoord] = center[d] + radius;
        }
        return QueryAABB(aabb, categoryMask);
    }

    internal List<(long entityId, double t)> QueryRay(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist)
    {
        var result = new List<(long entityId, double t)>();
        int halfCoord = _coordCount / 2;

        // Precompute inverse direction
        Span<double> invDir = stackalloc double[halfCoord];
        for (int d = 0; d < halfCoord; d++)
        {
            invDir[d] = direction[d] != 0 ? 1.0 / direction[d] : double.MaxValue;
        }

        foreach (var (entityId, coords, _) in _entries)
        {
            // Ray-AABB slab test
            double tNear = double.MinValue;
            double tFar = double.MaxValue;
            bool hit = true;

            for (int d = 0; d < halfCoord; d++)
            {
                double t1 = (coords[d] - origin[d]) * invDir[d];
                double t2 = (coords[d + halfCoord] - origin[d]) * invDir[d];
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }
                tNear = Math.Max(tNear, t1);
                tFar = Math.Min(tFar, t2);
                if (tNear > tFar)
                {
                    hit = false;
                    break;
                }
            }

            if (hit && tFar >= 0)
            {
                double tEntry = Math.Max(tNear, 0);
                if (tEntry <= maxDist)
                {
                    result.Add((entityId, tEntry));
                }
            }
        }

        result.Sort((a, b) => a.t.CompareTo(b.t));
        return result;
    }

    internal List<long> QueryFrustum(ReadOnlySpan<double> planes, int planeCount)
    {
        var result = new List<long>();
        int halfCoord = _coordCount / 2;
        int dimCount = halfCoord;
        int planeStride = dimCount + 1;

        foreach (var (entityId, coords, _) in _entries)
        {
            bool outside = false;
            for (int p = 0; p < planeCount && !outside; p++)
            {
                int po = p * planeStride;
                // Positive vertex test
                double dot = 0;
                for (int d = 0; d < dimCount; d++)
                {
                    double normal = planes[po + d];
                    dot += normal * (normal >= 0 ? coords[d + halfCoord] : coords[d]);
                }
                if (dot + planes[po + dimCount] < 0)
                {
                    outside = true;
                }
            }
            if (!outside)
            {
                result.Add(entityId);
            }
        }
        return result;
    }

    internal List<(long entityId, double distSq)> QueryKNN(ReadOnlySpan<double> center, int k)
    {
        var all = new List<(long entityId, double distSq)>();
        int halfCoord = _coordCount / 2;
        foreach (var (entityId, coords, _) in _entries)
        {
            double distSq = 0;
            for (int d = 0; d < halfCoord; d++)
            {
                double mid = (coords[d] + coords[d + halfCoord]) * 0.5;
                double diff = center[d] - mid;
                distSq += diff * diff;
            }
            all.Add((entityId, distSq));
        }
        all.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        return all.GetRange(0, Math.Min(k, all.Count));
    }

    internal int CountInAABB(ReadOnlySpan<double> queryCoords, uint categoryMask = 0) => QueryAABB(queryCoords, categoryMask).Count;

    internal int CountInRadius(ReadOnlySpan<double> center, double radius, uint categoryMask = 0) => QueryRadius(center, radius, categoryMask).Count;

    internal int Count => _entries.Count;
}
