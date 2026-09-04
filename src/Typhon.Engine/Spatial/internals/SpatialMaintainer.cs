using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Shared spatial decoding for the cluster path, plus the migration-storm warning.
/// </summary>
/// <remarks>
/// <b>Named for what it used to do.</b> This class held the insert / update / remove maintenance for the entity-level R-Tree — the fat-AABB containment
/// check, the back-pointer fixups, the Layer-1 occupancy counters. #872 step 13 removed that tree, and with it every writer here. Two things survived: the
/// migration-storm warning, and <see cref="ReadAndValidateBoundsFromPtr"/> — the single decoder for all eight <see cref="SpatialFieldType"/> shapes, which
/// the CLUSTER path had always borrowed and which has <b>18</b> live call sites across <c>src/</c> and <c>tools/</c>.
/// </remarks>
internal static unsafe partial class SpatialMaintainer
{
    // ── LoggerMessage partials ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cluster migration storm: {MigrationCount} migrations in a single tick for archetype id {ArchetypeId} ({DurationMs:F3} ms) — possible viewport warp, teleport event, or unphysical speed")]
    internal static partial void LogHighMigrationRate(ILogger logger, int migrationCount, ushort archetypeId, double durationMs);

    /// <summary>
    /// Read spatial bounds from a raw field pointer, convert BSphere to AABB if needed.
    /// Used by cluster path where fieldPtr points directly into cluster SoA data.
    /// Returns false if bounds are degenerate (NaN/Inf/Min>Max).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ReadAndValidateBoundsFromPtr(byte* fieldPtr, SpatialFieldInfo fi, Span<double> coords, SpatialNodeDescriptor desc)
    {
        switch (fi.FieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                var aabb = *(AABB2F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3F:
            {
                var aabb = *(AABB3F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.AABB2D:
            {
                var aabb = *(AABB2D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3D:
            {
                var aabb = *(AABB3D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            default:
                return false;
        }

        return true;
    }
}
