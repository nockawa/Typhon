using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Engine;

[AttributeUsage(AttributeTargets.Struct)]
[PublicAPI]
public sealed class ComponentAttribute : Attribute
{
    public string Name { get; }
    public int Revision { get; }
    public bool AllowMultiple { get; }

    public ComponentAttribute(string name, int revision, bool allowMultiple = false)
    {
        Name = name;
        Revision = revision;
        AllowMultiple = allowMultiple;
    }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class FieldAttribute : Attribute
{
    public int? FieldId { get; set; }
    public string Name { get; set; }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class IndexAttribute : Attribute
{
    public bool AllowMultiple { get; set; }
}

[Component(SchemaName, 1, true)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    public const string SchemaName = "Typhon.Schema.Field";

    public String64 Name;

    public int FieldId;
    public FieldType Type;
    public uint IndexSPI;
    public bool IsStatic;
    public bool HasIndex;
    public bool IndexAllowMultiple;
    public int ArrayLength;
    public bool IsArray => ArrayLength > 0;
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ComponentR1
{
    public const string SchemaName = "Typhon.Schema.Component";

    public String64 Name;
    public String64 POCOType;
    public int CompSize;
    public int CompOverhead;

    public int ComponentSPI;
    public int VersionSPI;
    public int DefaultIndexSPI;
    public int String64IndexSPI;

    public ComponentCollection<FieldR1> Fields;
}

public class DatabaseEngineOptions
{
}

[PublicAPI]
public class DatabaseEngine : IDisposable
{
    private readonly DatabaseEngineOptions      _options;
    private readonly ILogger<DatabaseEngine>    _log;

    private ComponentTable _fieldsTable;
    private ComponentTable _componentsTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;
    private long _curPrimaryKey;
    private ConcurrentDictionary<int, ChunkBasedSegment> _componentCollectionSegmentByStride;
    private ConcurrentDictionary<Type, VariableSizedBufferSegmentBase> _componentCollectionVSBSByType;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF MMF { get; }

    internal TransactionChain TransactionChain { get; }

    /// <summary>
    /// Create a transaction in order to make Queries and CRUD operation on the database
    /// </summary>
    /// <returns>The transaction object</returns>
    /// <remarks>
    /// Typhon deals with accesses and changes through transaction only, even for query purpose. When the user creates a transaction, "now" (the
    /// time when the transaction was created) is used as the reference point, every access will be based on the data that existed up to this point.
    /// Every change will be isolated from other transactions until the content is committed.
    /// </remarks>
    public Transaction CreateTransaction() => TransactionChain.CreateTransaction(this);

    public DatabaseEngine(DatabaseEngineOptions options, ManagedPagedMMF mmf, ILogger<DatabaseEngine> log)
    {
        MMF = mmf;
        _log = log;
        _options = options;
        _componentCollectionSegmentByStride = new ConcurrentDictionary<int, ChunkBasedSegment>();
        _componentCollectionVSBSByType = new ConcurrentDictionary<Type, VariableSizedBufferSegmentBase>();
        TransactionChain = new TransactionChain();

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();

        if (MMF.IsDatabaseFileCreating)
        {
            CreateSystemSchemaR1();
        }
        else
        {
            
        }
        
    }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        TransactionChain.Dispose();
        MMF.Dispose();

        IsDisposed = true;
        GC.SuppressFinalize(this);
    }
    private void ConstructComponentStore()
    {
        _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
        _curPrimaryKey = 0;
    }

    private static int RoundToStandardStride(int size) =>
        size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 64 => 64,
            _ => (int)BitOperations.RoundUpToPowerOf2((uint)size)
        };

    private const int ComponentCollectionItemCountPerChunk      = 8;
    private const int ComponentCollectionSegmentStartingSize    = 8;

    internal VariableSizedBufferSegment<T> GetComponentCollectionVSBS<T>() where T : unmanaged =>
        (VariableSizedBufferSegment<T>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            stride => new VariableSizedBufferSegment<T>(GetComponentCollectionSegment<T>()));

    internal VariableSizedBufferSegmentBase GetComponentCollectionVSBS(Type itemType) =>
        _componentCollectionVSBSByType.GetOrAdd(itemType,
            type =>
            {
                // Create the type for ComponentCollection<T>
                var ctType = typeof(VariableSizedBufferSegment<>).MakeGenericType(type);
                var fieldSize = DatabaseSchemaExtensions.FromType(type).field.SizeInComp();
                var segment = GetComponentCollectionSegment(fieldSize);
                return (VariableSizedBufferSegmentBase)Activator.CreateInstance(ctType, segment);
            });

    unsafe internal ChunkBasedSegment GetComponentCollectionSegment<T>() where T : unmanaged =>
        _componentCollectionSegmentByStride.GetOrAdd(RoundToStandardStride(sizeof(T) * 8),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    internal ChunkBasedSegment GetComponentCollectionSegment(int itemSize) =>
        _componentCollectionSegmentByStride.GetOrAdd(RoundToStandardStride(itemSize * 8),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

    // Create the first revision of the system schema
    private void CreateSystemSchemaR1()
    {
        // Register the system components
        RegisterComponentFromAccessor<FieldR1>();
        RegisterComponentFromAccessor<ComponentR1>();

        // Get their table
        _fieldsTable = GetComponentTable<FieldR1>();
        _componentsTable = GetComponentTable<ComponentR1>();

        MMF.RequestPage(0, true, out var pa);
        using (pa)
        {
            // Save the entry points in the file header
            var cs = MMF.CreateChangeSet();
            cs.Add(pa);
            ref var rootFileHeader = ref pa.PageHeader.Cast<byte, RootFileHeader>()[0];

            rootFileHeader.SystemSchemaRevision = 1;
            rootFileHeader.FieldTableSPI = _fieldsTable.ComponentSegment.RootPageIndex;
            rootFileHeader.ComponentTableSPI = _componentsTable.ComponentSegment.RootPageIndex;
            
            cs.SaveChanges();
        }
        
        // Now save the system components schema in the database (to load them next time we open the database)
        SaveInSystemSchema(_fieldsTable);
        SaveInSystemSchema(_componentsTable);
    }

    private void SaveInSystemSchema(ComponentTable table)
    {
        var definition = table.Definition;
        using var t = CreateTransaction();

        var comp = new ComponentR1
        {
            Name                = (String64)definition.Name, 
            POCOType            = (String64)definition.POCOType.FullName,
            CompSize             = definition.ComponentStorageSize,
            CompOverhead         = definition.ComponentStorageOverhead,
            ComponentSPI        = table.ComponentSegment.RootPageIndex,
            VersionSPI          = table.CompRevTableSegment.RootPageIndex,
            DefaultIndexSPI     = table.DefaultIndexSegment.RootPageIndex,
            String64IndexSPI    = table.String64IndexSegment.RootPageIndex,
        };

        {
            using var a = t.CreateComponentCollectionAccessor(ref comp.Fields);

            foreach (var kvp in table.Definition.FieldsByName)
            {
                var field = kvp.Value;
                var f = new FieldR1
                {
                    Name = (String64)field.Name,
                    FieldId = field.FieldId,
                    Type = field.Type,
                    ArrayLength = field.ArrayLength
                };
            
                a.Add(f);
            }
        }
        
        t.Commit();
    }

    public bool RegisterComponentFromAccessor<T>() where T : unmanaged
    {
        var definition = DBD.CreateFromAccessor<T>();
        if (definition == null)
        {
            return false;
        }

        var componentTable = new ComponentTable();
        componentTable.Create(this, definition);
        _componentTableByType.TryAdd(typeof(T), componentTable);

        return true;
    }

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);
}