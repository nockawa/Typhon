using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public ComponentAttribute(string name, int revision)
    {
        Name = name;
        Revision = revision;
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

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    public const string SchemaName = "Typhon.Schema.Field";

    public String64 Name;

    [Index(AllowMultiple = true)]
    public int ComponentFK;

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

[PublicAPI]
public struct ComponentCollection<T> where T : unmanaged
{
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
        TransactionChain = new TransactionChain();

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();

        MMF.CreatingEvent += OnCreating;
        MMF.LoadingEvent += OnLoading;
    }

    private void OnLoading(object sender, EventArgs args)
    {
    }

    private void OnCreating(object sender, EventArgs args) => CreateSystemSchemaR1();

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

    internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

    // Create the first revision of the system schema
    private void CreateSystemSchemaR1()
    {
        const int revision = 1;
        
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