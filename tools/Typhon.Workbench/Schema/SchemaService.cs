using System.Collections.Generic;
using System.Linq;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Reads component type metadata from a session's <see cref="DatabaseEngine"/> and projects it into DTOs for the
/// Workbench Schema Inspector. Stateless — looks sessions up on demand via <see cref="SessionManager"/>.
/// </summary>
public sealed partial class SchemaService
{
    private readonly SessionManager _sessions;

    public SchemaService(SessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <summary>
    /// Triage-friendly list of every registered component in the session's engine. Used by the Schema Browser panel
    /// and by the palette's <c>#schema</c> fuzzy-match feed.
    /// </summary>
    /// <exception cref="SessionNotFoundException">Session id doesn't resolve.</exception>
    /// <exception cref="SessionKindException">Session is not an Open (file-backed) session.</exception>
    public ComponentSummaryDto[] ListComponents(Guid sessionId)
    {
        var engine = RequireOpenEngine(sessionId);

        var tables = engine.GetAllComponentTables().ToArray();
        var summaries = new ComponentSummaryDto[tables.Length];
        for (int i = 0; i < tables.Length; i++)
        {
            summaries[i] = BuildSummary(tables[i]);
        }
        return summaries;
    }

    /// <summary>
    /// Full byte-layout schema for a single component type. Fields are ordered ascending by offset so the Layout
    /// view can iterate them linearly and derive padding from offset gaps.
    /// </summary>
    /// <exception cref="SessionNotFoundException">Session id doesn't resolve.</exception>
    /// <exception cref="SessionKindException">Session is not an Open (file-backed) session.</exception>
    /// <exception cref="KeyNotFoundException">No registered component matches <paramref name="typeName"/>.</exception>
    public ComponentSchemaDto GetComponentSchema(Guid sessionId, string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var engine = RequireOpenEngine(sessionId);

        ComponentTable table = null;
        foreach (var t in engine.GetAllComponentTables())
        {
            if (t.Definition.Name == typeName)
            {
                table = t;
                break;
            }
        }
        if (table == null)
        {
            throw new KeyNotFoundException($"Component '{typeName}' is not registered in session {sessionId}.");
        }

        return BuildSchema(table);
    }

    private static ComponentSummaryDto BuildSummary(ComponentTable table)
    {
        var def = table.Definition;
        return new ComponentSummaryDto(
            TypeName: def.Name,
            FullName: def.POCOType?.FullName ?? def.Name,
            StorageSize: def.ComponentStorageSize,
            FieldCount: def.FieldsByName.Count,
            ArchetypeCount: null, // Phase 2 — requires ArchetypeRegistry accessor
            EntityCount: table.EstimatedEntityCount,
            IndexCount: table.IndexedFieldInfos?.Length ?? 0);
    }

    private static ComponentSchemaDto BuildSchema(ComponentTable table)
    {
        var def = table.Definition;
        var fields = def.FieldsByName.Values
            .OrderBy(f => f.OffsetInComponentStorage)
            .Select(f => new FieldDto(
                Name: f.Name,
                TypeName: f.Type.ToString(),
                TypeFullName: f.DotNetType?.FullName ?? f.Type.ToString(),
                Offset: f.OffsetInComponentStorage,
                Size: f.SizeInComponentStorage,
                FieldId: f.FieldId,
                IsIndexed: f.HasIndex,
                IndexAllowsMultiple: f.IndexAllowMultiple))
            .ToArray();

        return new ComponentSchemaDto(
            TypeName: def.Name,
            FullName: def.POCOType?.FullName ?? def.Name,
            StorageSize: def.ComponentStorageSize,
            TotalSize: def.ComponentStorageTotalSize,
            AllowMultiple: def.AllowMultiple,
            Revision: def.Revision,
            Fields: fields);
    }

    /// <summary>
    /// Every archetype registered in the session's engine, without a component filter. Powers the Archetype Browser
    /// panel — the archetype-first counterpart to <see cref="ListComponents"/>. Same per-archetype metadata as
    /// <see cref="GetArchetypesForComponent"/>, minus the per-component size (no focused component). O(N) over the
    /// registry; N ≤ 4096 by construction.
    /// </summary>
    public ArchetypeInfoDto[] ListArchetypes(Guid sessionId)
    {
        var engine = RequireOpenEngine(sessionId);

        var result = new List<ArchetypeInfoDto>();
        foreach (var archetype in ArchetypeRegistry.GetAllArchetypes())
        {
            if (archetype._slotToComponentType == null || archetype.ComponentCount == 0)
            {
                continue;
            }

            var entityCount = engine.GetArchetypeEntityCount(archetype.ArchetypeId);
            var componentTypes = archetype.GetComponentTypes().Select(t => t.FullName ?? t.Name).ToArray();

            int chunkCount = 0;
            int chunkCapacity = 0;
            double occupancyPct = 0;
            string storageMode;

            if (archetype.IsClusterEligible && archetype.ClusterLayout != null)
            {
                storageMode = "cluster";
                chunkCapacity = archetype.ClusterLayout.ClusterSize;
                chunkCount = engine.GetArchetypeClusterChunkCount(archetype.ArchetypeId);
                if (chunkCount > 0 && chunkCapacity > 0)
                {
                    double total = (double)chunkCount * chunkCapacity;
                    occupancyPct = total > 0 ? (entityCount * 100.0 / total) : 0;
                    if (occupancyPct > 100) occupancyPct = 100;
                }
            }
            else
            {
                storageMode = "legacy";
            }

            result.Add(new ArchetypeInfoDto(
                ArchetypeId: archetype.ArchetypeId.ToString(),
                ComponentTypes: componentTypes,
                EntityCount: entityCount,
                ComponentSize: 0,
                StorageMode: storageMode,
                ChunkCount: chunkCount,
                ChunkCapacity: chunkCapacity,
                OccupancyPct: occupancyPct));
        }
        return result.ToArray();
    }

    /// <summary>
    /// All archetypes that contain the given component type. Scans <see cref="ArchetypeRegistry.GetAllArchetypes"/> (O(N)
    /// — N = 20-200 typical) and filters via <c>HasComponent</c>. Cluster vs. legacy storage is disambiguated in the DTO
    /// so the UI can flag suboptimal layouts.
    /// </summary>
    public ArchetypeInfoDto[] GetArchetypesForComponent(Guid sessionId, string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var engine = RequireOpenEngine(sessionId);

        var table = ResolveComponentTable(engine, typeName);
        var componentType = table.Definition.POCOType;
        if (componentType == null)
        {
            return [];
        }

        var result = new List<ArchetypeInfoDto>();
        foreach (var archetype in ArchetypeRegistry.GetAllArchetypes())
        {
            // Skip half-initialized archetypes — same guard as ListArchetypes. EngineLifecycle's
            // per-archetype try/catch can let a partially-finalized metadata slip through with a
            // null _slotToComponentType.
            if (archetype._slotToComponentType == null || archetype.ComponentCount == 0)
            {
                continue;
            }

            // Find the slot of the focused component in this archetype by matching CLR Type — avoids needing access to
            // ArchetypeRegistry's private Type→componentTypeId map.
            int matchingSlot = -1;
            var slotTypes = archetype._slotToComponentType;
            for (int s = 0; s < slotTypes.Length; s++)
            {
                if (slotTypes[s] == componentType)
                {
                    matchingSlot = s;
                    break;
                }
            }
            if (matchingSlot < 0) continue;

            var entityCount = engine.GetArchetypeEntityCount(archetype.ArchetypeId);
            var componentTypes = archetype.GetComponentTypes().Select(t => t.FullName ?? t.Name).ToArray();

            int componentSize = 0;
            int chunkCount = 0;
            int chunkCapacity = 0;
            double occupancyPct = 0;
            string storageMode;

            if (archetype.IsClusterEligible && archetype.ClusterLayout != null)
            {
                storageMode = "cluster";
                componentSize = archetype.ClusterLayout.ComponentSize(matchingSlot);
                chunkCapacity = archetype.ClusterLayout.ClusterSize;
                chunkCount = engine.GetArchetypeClusterChunkCount(archetype.ArchetypeId);
                if (chunkCount > 0 && chunkCapacity > 0)
                {
                    double total = (double)chunkCount * chunkCapacity;
                    occupancyPct = total > 0 ? (entityCount * 100.0 / total) : 0;
                    if (occupancyPct > 100) occupancyPct = 100; // clamp — chunks can be sparse
                }
            }
            else
            {
                storageMode = "legacy";
                componentSize = table.Definition.ComponentStorageSize;
            }

            result.Add(new ArchetypeInfoDto(
                ArchetypeId: archetype.ArchetypeId.ToString(),
                ComponentTypes: componentTypes,
                EntityCount: entityCount,
                ComponentSize: componentSize,
                StorageMode: storageMode,
                ChunkCount: chunkCount,
                ChunkCapacity: chunkCapacity,
                OccupancyPct: occupancyPct));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Indexes covering fields of the given component type. Iterates <c>ComponentTable.IndexedFieldInfos</c> (accessible
    /// via <c>InternalsVisibleTo</c>) and resolves the field name by matching offsets against the schema definition.
    /// Typhon ships only B+Tree indexes today, so <c>IndexType</c> is always "BTree".
    /// </summary>
    public IndexInfoDto[] GetIndexesForComponent(Guid sessionId, string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var engine = RequireOpenEngine(sessionId);

        var table = ResolveComponentTable(engine, typeName);
        var infos = table.IndexedFieldInfos;
        if (infos == null || infos.Length == 0)
        {
            return [];
        }

        var fieldsByOffset = table.Definition.FieldsByName.Values
            .ToDictionary(f => f.OffsetInComponentStorage);

        var result = new IndexInfoDto[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            var name = fieldsByOffset.TryGetValue(info.OffsetToField, out var f) ? f.Name : $"@{info.OffsetToField}";
            result[i] = new IndexInfoDto(
                FieldName: name,
                FieldOffset: info.OffsetToField,
                FieldSize: info.Size,
                AllowsMultiple: info.AllowMultiple,
                IndexType: "BTree");
        }
        return result;
    }

    /// <summary>
    /// Systems that read (via input view) or reactively trigger on the given component type. Runtime-gated: the Workbench
    /// does not host a <c>TyphonRuntime</c> today (see <c>HeartbeatStream.cs</c>), so the envelope's <c>RuntimeHosted</c>
    /// is always <c>false</c> in v1. Once runtime hosting lands, wiring <c>EngineLifecycle.Runtime</c> into this method
    /// flips the flag and populates <c>Systems</c> — with no client changes.
    /// </summary>
    public SystemRelationshipsResponseDto GetSystemRelationships(Guid sessionId, string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var engine = RequireOpenEngine(sessionId);

        // Validate the component exists before reporting "no systems" — matches the other endpoints' error semantics.
        _ = ResolveComponentTable(engine, typeName);

        // Runtime not hosted in Workbench today. Architecture: when OpenSession holds a reference to a TyphonRuntime,
        // this branch flips and populates Systems from runtime.Scheduler.Systems filtered by Input view schema and
        // ChangeFilterTypes. Tracked separately — not in Phase 2 scope.
        return new SystemRelationshipsResponseDto(RuntimeHosted: false, Systems: []);
    }

    /// <summary>Looks up a ComponentTable by <c>DBComponentDefinition.Name</c>. Throws <see cref="KeyNotFoundException"/>
    /// if no match — controller maps to 404.</summary>
    private static ComponentTable ResolveComponentTable(DatabaseEngine engine, string typeName)
    {
        foreach (var t in engine.GetAllComponentTables())
        {
            if (t.Definition.Name == typeName) return t;
        }
        throw new KeyNotFoundException($"Component '{typeName}' is not registered.");
    }

    private DatabaseEngine RequireOpenEngine(Guid sessionId)
    {
        if (!_sessions.TryGet(sessionId, out var session))
        {
            throw new SessionNotFoundException(sessionId);
        }
        if (session is not OpenSession open)
        {
            throw new SessionKindException(sessionId, session.GetType().Name);
        }
        return open.Engine.Engine;
    }
}

/// <summary>The requested session id is not registered with the <see cref="SessionManager"/>.</summary>
public sealed class SessionNotFoundException(Guid sessionId)
    : Exception($"Session {sessionId} not found.")
{
    public Guid SessionId { get; } = sessionId;
}

/// <summary>The session exists but is not an <see cref="OpenSession"/> (e.g., Attach or Trace) — schema data unavailable.</summary>
public sealed class SessionKindException(Guid sessionId, string actualKind)
    : Exception($"Session {sessionId} is of kind '{actualKind}', not an Open (file) session.")
{
    public Guid SessionId { get; } = sessionId;
    public string ActualKind { get; } = actualKind;
}
