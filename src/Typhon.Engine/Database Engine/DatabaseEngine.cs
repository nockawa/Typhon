using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Engine;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class ComponentAttribute : Attribute
{
    public string Name { get; }

    public ComponentAttribute(string name)
    {
        Name = name;
    }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class FieldAttribute : Attribute
{
    public int? FieldId { get; set; }
    public string Name { get; set; }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class IndexAttribute : Attribute
{
    public bool AllowMultiple { get; set; }
}

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct FieldRow
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

[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct ComponentRow
{
    public const string SchemaName = "Typhon.Schema.Component";

    public String64 Name;
    public String64 POCOType;
    public int RowSize;

    public uint TableSPI;

    public ComponentCollection<FieldRow> Fields;
}

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
    private readonly ManagedPagedMMF            _pmmf;
    private readonly ILogger<DatabaseEngine>    _log;

    private ComponentTable _fieldsTable;
    private ComponentTable _componentsTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;
    private long _curPrimaryKey;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF PMMF => _pmmf;
    
    /// <summary>
    /// Create a transaction in order to make Queries and CRUD operation on the database
    /// </summary>
    /// <param name="exclusiveConcurrency">If <c>true</c> the write accesses on the Components will be exclusive: any updated, deleted Components
    /// will be locked for the rest of the transaction, preventing other transactions in other threads to modify them as well.
    /// If <c>false</c> the transaction is running in optimistic concurrency mode, allowing concurrent changes across transactions with possible
    /// conflicts being resolved during commit time.
    /// </param>
    /// <returns>The transaction object</returns>
    /// <remarks>
    /// Typhon deals with accesses and changes through transaction only, even for query purpose. When the user creates a transaction, "now" (the
    /// time when the transaction was created) is used as the reference point, every access will be based on the data that existed up to this point.
    /// Every change will be isolated from other transactions until the content is committed.
    /// </remarks>
    public Transaction NewTransaction(bool exclusiveConcurrency) => new(this, exclusiveConcurrency);

    public DatabaseEngine(DatabaseEngineOptions options, ManagedPagedMMF pmmf, ILogger<DatabaseEngine> log)
    {
        _pmmf = pmmf;
        _log = log;
        _options = options;

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();

        _pmmf.CreatingEvent += OnCreating;
        _pmmf.LoadingEvent += OnLoading;
    }

    private void OnLoading(object sender, EventArgs args)
    {
    }

    unsafe private void OnCreating(object sender, EventArgs args)
    {
        // TOFIX
        // CreateComponentStore(e.Header);
    }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        GC.SuppressFinalize(this);
        
        _pmmf.Dispose();

        IsDisposed = true;
    }
    private void ConstructComponentStore()
    {
        _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
        _curPrimaryKey = 0;
    }

    internal long GetNewPrimaryKey() => Interlocked.Increment(ref _curPrimaryKey);

    private unsafe void CreateComponentStore(RootFileHeader* rootFileHeader)
    {
        RegisterComponentFromRowAccessor<FieldRow>();
        RegisterComponentFromRowAccessor<ComponentRow>();

        _fieldsTable = GetComponentTable<FieldRow>();
        _componentsTable = GetComponentTable<ComponentRow>();

        rootFileHeader->DatabaseEngine = SerializeSettings();
    }

    public bool RegisterComponentFromRowAccessor<T>() where T : unmanaged
    {
        var dcd = DBD.CreateFromRowAccessor<T>();
        if (dcd == null) return false;

        var componentTable = new ComponentTable();
        componentTable.Create(this, DBD.GetComponent(dcd.Name));
        _componentTableByType.TryAdd(typeof(T), componentTable);

        return true;
    }

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type)
    {
        if (_componentTableByType.TryGetValue(type, out var ct) == false)
        {
            return null;
        }

        return ct;
    }
    internal struct SerializationData
    {
        public ComponentTable.SerializationData FieldsTable;
        public ComponentTable.SerializationData ComponentsTable;
    }

    internal SerializationData SerializeSettings() =>
        new()
        {
            FieldsTable = _fieldsTable.SerializeSettings(),
            ComponentsTable = _componentsTable.SerializeSettings()
        };
}