using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Typhon.Engine;
using Typhon.Shell.Extensibility;

namespace Typhon.Shell.Session;

/// <summary>
/// Manages all mutable state for a shell session: database, transaction, schemas, and settings.
/// </summary>
internal sealed class ShellSession : IDisposable
{
    // Database lifecycle
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _engine;
    private Transaction _transaction;
    private bool _dirty;
    private string _databasePath;
    private string _databaseName;

    // Schema state
    private readonly List<string> _assemblyPaths = [];
    private readonly Dictionary<string, Schema.ComponentSchema> _componentSchemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Type> _componentTypes = new(StringComparer.OrdinalIgnoreCase);

    // Extension commands discovered from loaded assemblies
    private readonly Dictionary<string, ShellCommand> _customCommands = new(StringComparer.OrdinalIgnoreCase);

    // Settings
    public string Format { get; set; } = "table";
    public bool AutoCommit { get; set; }
    public bool Verbose { get; set; }
    public int PageSize { get; set; } = 20;
    public string Color { get; set; } = "auto";
    public bool Timing { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Warning;

    // Resource graph
    private IResourceRegistry _resourceRegistry;
    private ResourceGraph _resourceGraph;
    private IMemoryAllocator _memoryAllocator;

    // State queries
    public bool IsOpen => _engine != null;
    public bool HasTransaction => _transaction != null;
    public bool IsDirty => _dirty;
    public string DatabaseName => _databaseName;
    public string DatabasePath => _databasePath;
    public DatabaseEngine Engine => _engine;
    public Transaction Transaction => _transaction;
    public IReadOnlyDictionary<string, Schema.ComponentSchema> ComponentSchemas => _componentSchemas;
    public IReadOnlyDictionary<string, Type> ComponentTypes => _componentTypes;
    public IReadOnlyList<string> AssemblyPaths => _assemblyPaths;
    public IReadOnlyDictionary<string, ShellCommand> CustomCommands => _customCommands;
    public IResourceRegistry ResourceRegistry => _resourceRegistry;
    public ResourceGraph ResourceGraph => _resourceGraph;

    /// <summary>
    /// Opens (or creates) a database file. Disposes any previously open database.
    /// </summary>
    public string OpenDatabase(string path)
    {
        if (IsOpen)
        {
            CloseDatabase();
        }

        // The engine names database files as "{DatabaseName}.bin" automatically.
        // The user may provide a path like "mydb", "mydb.bin", or "mydb.typhon" —
        // we strip any extension and use the stem as the DatabaseName.
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        _databaseName = Path.GetFileNameWithoutExtension(fullPath);

        // The actual file on disk is always {DatabaseName}.bin (engine convention)
        Debug.Assert(directory != null, nameof(directory) + " != null");
        _databasePath = Path.Combine(directory, $"{_databaseName}.bin");
        var isNew = !File.Exists(_databasePath);

        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                });
                builder.SetMinimumLevel(LogLevel);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(options =>
            {
                options.DatabaseName = _databaseName;
                options.DatabaseDirectory = directory;
                // 1024 pages = 8 MiB — generous enough for interactive bulk operations
                // while still exercising the backpressure path under heavy writes.
                options.DatabaseCacheSize = 1024 * 8192UL;
            })
            .AddMemoryAllocator()
            .AddSingleton<IWalFileIO, WalFileIO>()
            .AddDatabaseEngine(engineOpts =>
            {
                var walDir = Path.Combine(directory, "wal");
                Directory.CreateDirectory(walDir);
                engineOpts.Wal = new WalWriterOptions
                {
                    WalDirectory = walDir,
                    // Shell uses Deferred durability — WAL is only needed for checkpoint-driven page flushing.
                    // FUA off since we don't need per-write durability guarantees in tsh.
                    UseFUA = false
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _engine = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _resourceRegistry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        _resourceGraph = new ResourceGraph(_resourceRegistry);
        _memoryAllocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();

        // Re-register any previously loaded component types
        foreach (var kvp in _componentTypes)
        {
            RegisterComponentWithEngine(kvp.Value);
        }

        return isNew
            ? $"Opened {_databaseName} (new database created)"
            : $"Opened {_databaseName}";
    }

    /// <summary>
    /// Closes the current database and releases all resources.
    /// </summary>
    public void CloseDatabase()
    {
        if (_transaction != null)
        {
            _transaction.Rollback();
            _transaction.Dispose();
            _transaction = null;
            _dirty = false;
        }

        if (_engine != null)
        {
            _engine.Dispose();
            _engine = null;
        }

        if (_serviceProvider != null)
        {
            _serviceProvider.Dispose();
            _serviceProvider = null;
        }

        _resourceGraph = null;
        _resourceRegistry = null;
        _databasePath = null;
        _databaseName = null;
    }

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    public Transaction BeginTransaction()
    {
        if (_transaction != null)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        _transaction = _engine.CreateQuickTransaction();
        _dirty = false;
        return _transaction;
    }

    /// <summary>
    /// Commits the active transaction.
    /// </summary>
    public bool CommitTransaction()
    {
        if (_transaction == null)
        {
            throw new InvalidOperationException("No active transaction.");
        }

        var result = _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        _dirty = false;
        return result;
    }

    /// <summary>
    /// Rolls back the active transaction.
    /// </summary>
    public void RollbackTransaction()
    {
        if (_transaction == null)
        {
            throw new InvalidOperationException("No active transaction.");
        }

        _transaction.Rollback();
        _transaction.Dispose();
        _transaction = null;
        _dirty = false;
    }

    /// <summary>
    /// Gets the active transaction, or creates an auto-commit transaction if auto-commit is on.
    /// Returns null if no transaction is available and auto-commit is off.
    /// </summary>
    public Transaction GetOrCreateTransaction(out bool isAutoCommit)
    {
        if (_transaction != null)
        {
            isAutoCommit = false;
            return _transaction;
        }

        if (AutoCommit)
        {
            isAutoCommit = true;
            return _engine.CreateQuickTransaction();
        }

        isAutoCommit = false;
        return null;
    }

    /// <summary>
    /// Marks the current transaction as dirty (has uncommitted changes).
    /// </summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>
    /// Registers a component type discovered from an assembly.
    /// </summary>
    public void RegisterComponent(string name, Type type, Schema.ComponentSchema schema)
    {
        _componentSchemas[name] = schema;
        _componentTypes[name] = type;

        if (IsOpen)
        {
            RegisterComponentWithEngine(type);
        }
    }

    /// <summary>
    /// Records an assembly path for reload purposes.
    /// </summary>
    public void AddAssemblyPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_assemblyPaths.Contains(fullPath))
        {
            _assemblyPaths.Add(fullPath);
        }
    }

    /// <summary>
    /// Registers an extension command discovered from a loaded assembly.
    /// </summary>
    public void RegisterCommand(ShellCommand command) => _customCommands[command.Name] = command;

    /// <summary>
    /// Clears all extension commands (for reload).
    /// </summary>
    public void ClearCommands() => _customCommands.Clear();

    /// <summary>
    /// Clears all loaded schemas (for reload).
    /// </summary>
    public void ClearSchemas()
    {
        _componentSchemas.Clear();
        _componentTypes.Clear();
    }

    private void RegisterComponentWithEngine(Type componentType)
    {
        var method = typeof(DatabaseEngine).GetMethod("RegisterComponentFromAccessor");
        if (method == null)
        {
            return;
        }

        var generic = method.MakeGenericMethod(componentType);
        generic.Invoke(_engine, [null, SchemaValidationMode.Enforce]);
    }

    public void Dispose() => CloseDatabase();
}
