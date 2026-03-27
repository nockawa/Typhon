using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Static helpers for maintaining spatial R-Tree entries in response to ECS operations (spawn, update, destroy).
/// All logic is storage-mode-agnostic — only the trigger differs (tick fence for SV, commit for Versioned).
/// </summary>
internal static unsafe partial class SpatialMaintainer
{
    // ── LoggerMessage partials ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Degenerate spatial AABB for entity {EntityPK} in {ComponentName}, skipping spatial {Operation}")]
    private static partial void LogDegenerateAABB(ILogger logger, long entityPK, string componentName, string operation);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a newly spawned entity into the spatial R-Tree. Called at FinalizeSpawns (SV) or commit (Versioned).
    /// </summary>
    internal static void InsertSpatial(long entityPK, int componentChunkId, ComponentTable table, ref ChunkAccessor<PersistentStore> compAccessor, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        var tree = state.Tree;
        var desc = state.Descriptor;

        // Read tight bounds from component data
        byte* compPtr = compAccessor.GetChunkAddress(componentChunkId);
        Span<double> coords = stackalloc double[desc.CoordCount];

        if (!ReadAndValidateBounds(compPtr, fi, desc, coords, entityPK, table, "insert"))
        {
            return;
        }

        // Enlarge to fat AABB
        EnlargeCoords(coords, fi.Margin, desc);

        // Insert into tree
        var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
        try
        {
            var (leafChunkId, slotIndex) = tree.Insert(entityPK, coords, ref treeAccessor, changeSet);

            // Write back-pointer
            var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
            try
            {
                SpatialBackPointerHelper.Write(ref bpAccessor, componentChunkId, leafChunkId, (short)slotIndex);
            }
            finally
            {
                bpAccessor.Dispose();
            }
        }
        finally
        {
            treeAccessor.Dispose();
        }
    }

    /// <summary>
    /// Update an existing entity's spatial position. Fast path if tight AABB is still within fat AABB (~25ns).
    /// Slow path removes and reinserts (~500–700ns). Called at tick fence (SV) or commit (Versioned).
    /// </summary>
    internal static void UpdateSpatial(long entityPK, int componentChunkId, ComponentTable table, ref ChunkAccessor<PersistentStore> compAccessor, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        var tree = state.Tree;
        var desc = state.Descriptor;

        // Read current tight bounds
        byte* compPtr = compAccessor.GetChunkAddress(componentChunkId);
        Span<double> tightCoords = stackalloc double[desc.CoordCount];

        if (!ReadAndValidateBounds(compPtr, fi, desc, tightCoords, entityPK, table, "update"))
        {
            return;
        }

        // Read back-pointer
        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        try
        {
            var bp = SpatialBackPointerHelper.Read(ref bpAccessor, componentChunkId);
            if (bp.LeafChunkId == 0)
            {
                // No back-pointer — entity was never inserted (degenerate at spawn). Try inserting now.
                bpAccessor.Dispose();
                InsertSpatial(entityPK, componentChunkId, table, ref compAccessor, changeSet);
                return;
            }

            // Read fat AABB from tree leaf
            var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
            try
            {
                Span<double> fatCoords = stackalloc double[desc.CoordCount];
                tree.ReadLeafCoords(bp.LeafChunkId, bp.SlotIndex, fatCoords, ref treeAccessor);

                // Fast path: containment check
                if (CoordsContained(fatCoords, tightCoords, desc.CoordCount))
                {
                    return;
                }

                // Slow path: remove + reinsert
                long swappedEntityId = tree.Remove(bp.LeafChunkId, bp.SlotIndex, ref treeAccessor);

                // Update swapped entity's back-pointer if applicable
                if (swappedEntityId != 0 && swappedEntityId != entityPK)
                {
                    UpdateSwappedBackPointer(swappedEntityId, bp.LeafChunkId, bp.SlotIndex, table, ref bpAccessor);
                }

                // Compute new fat AABB and reinsert
                EnlargeCoords(tightCoords, fi.Margin, desc);
                var (newLeaf, newSlot) = tree.Insert(entityPK, tightCoords, ref treeAccessor, changeSet);
                SpatialBackPointerHelper.Write(ref bpAccessor, componentChunkId, newLeaf, (short)newSlot);
            }
            finally
            {
                treeAccessor.Dispose();
            }
        }
        finally
        {
            bpAccessor.Dispose();
        }
    }

    /// <summary>
    /// Remove a destroyed entity from the spatial R-Tree. Called at destroy (SV tick fence or Versioned commit).
    /// </summary>
    internal static void RemoveFromSpatial(long entityPK, int componentChunkId, ComponentTable table, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var tree = state.Tree;

        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        try
        {
            var bp = SpatialBackPointerHelper.Read(ref bpAccessor, componentChunkId);
            if (bp.LeafChunkId == 0)
            {
                return; // Never inserted (degenerate bounds)
            }

            var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
            try
            {
                long swappedEntityId = tree.Remove(bp.LeafChunkId, bp.SlotIndex, ref treeAccessor);

                if (swappedEntityId != 0 && swappedEntityId != entityPK)
                {
                    UpdateSwappedBackPointer(swappedEntityId, bp.LeafChunkId, bp.SlotIndex, table, ref bpAccessor);
                }
            }
            finally
            {
                treeAccessor.Dispose();
            }

            SpatialBackPointerHelper.Clear(ref bpAccessor, componentChunkId);
        }
        finally
        {
            bpAccessor.Dispose();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Read spatial bounds from component data, convert BSphere to AABB if needed, write to coords array.
    /// Returns false if bounds are degenerate (NaN/Inf/Min>Max).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ReadAndValidateBounds(byte* compPtr, SpatialFieldInfo fi, SpatialNodeDescriptor desc, Span<double> coords, long entityPK, 
        ComponentTable table, string operation)
    {
        byte* fieldPtr = compPtr + fi.FieldOffset;

        switch (fi.FieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                var aabb = *(AABB2F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3F:
            {
                var aabb = *(AABB3F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2F:
            {
                var s = *(BSphere2F*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3F:
            {
                var s = *(BSphere3F*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.AABB2D:
            {
                var aabb = *(AABB2D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3D:
            {
                var aabb = *(AABB3D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2D:
            {
                var s = *(BSphere2D*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3D:
            {
                var s = *(BSphere3D*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Enlarge coords in-place by margin. Coords are [min0, min1, ..., max0, max1, ...].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnlargeCoords(Span<double> coords, float margin, SpatialNodeDescriptor desc)
    {
        int half = desc.CoordCount / 2;
        for (int i = 0; i < half; i++)
        {
            coords[i] -= margin;
        }
        for (int i = half; i < desc.CoordCount; i++)
        {
            coords[i] += margin;
        }
    }

    /// <summary>
    /// Check if tight AABB is fully contained within fat AABB. Coords ordered [min0..., max0...].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CoordsContained(ReadOnlySpan<double> fat, ReadOnlySpan<double> tight, int coordCount)
    {
        int half = coordCount / 2;
        for (int i = 0; i < half; i++)
        {
            if (fat[i] > tight[i])
            {
                return false; // fat min > tight min → not contained
            }
        }
        for (int i = half; i < coordCount; i++)
        {
            if (fat[i] < tight[i])
            {
                return false; // fat max < tight max → not contained
            }
        }
        return true;
    }

    /// <summary>
    /// After a swap-with-last in Remove, update the swapped entity's back-pointer to the vacated slot.
    /// Resolves entityId → componentChunkId using EntityMap (via DatabaseEngine).
    /// </summary>
    private static void UpdateSwappedBackPointer(long swappedEntityId, int leafChunkId, int slotIndex,
        ComponentTable table, ref ChunkAccessor<PersistentStore> bpAccessor)
    {
        // The swapped entity now occupies (leafChunkId, slotIndex). We need its componentChunkId to update the back-pointer.
        // The entity's componentChunkId can be resolved from the EntityMap via EntityId → EntityRecord → Location[slot].
        // For now, we search the back-pointer segment: the entity's old back-pointer has the old leaf position.
        // Since we know the swappedEntityId, we can find its componentChunkId by looking it up in the ArchetypeState's EntityMap.

        var dbe = table.DBE;
        var entityId = EntityId.FromRaw(swappedEntityId);
        if (entityId.ArchetypeId >= dbe._archetypeStates.Length)
        {
            return;
        }
        var archState = dbe._archetypeStates[entityId.ArchetypeId];
        if (archState?.EntityMap == null)
        {
            return;
        }

        // Find the component slot for this table in the archetype
        int compSlot = -1;
        for (int s = 0; s < archState.SlotToComponentTable.Length; s++)
        {
            if (archState.SlotToComponentTable[s] == table)
            {
                compSlot = s;
                break;
            }
        }
        if (compSlot < 0)
        {
            return;
        }

        // Read the entity record to get the component chunkId
        byte* recordBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        var emAccessor = archState.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            if (archState.EntityMap.TryGet(entityId.EntityKey, recordBuf, ref emAccessor))
            {
                int swappedCompChunkId = EntityRecordAccessor.GetLocation(recordBuf, compSlot);
                SpatialBackPointerHelper.Write(ref bpAccessor, swappedCompChunkId, leafChunkId, (short)slotIndex);
            }
        }
        finally
        {
            emAccessor.Dispose();
        }
    }
}
