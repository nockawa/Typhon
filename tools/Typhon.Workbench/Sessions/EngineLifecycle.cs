using Typhon.Engine;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session wrapper around a DatabaseEngine, its private IServiceProvider, and (optionally) a
/// collectible AssemblyLoadContext holding the user's schema DLLs. Disposal order is precise:
/// engine first, ALC next, ServiceProvider last — unwinding MMF, WAL, allocators, and releasing
/// the file handle in the right sequence.
/// </summary>
public sealed class EngineLifecycle : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly WorkbenchAssemblyLoadContext _alc;
    private bool _disposed;

    public DatabaseEngine Engine { get; }
    public IResourceRegistry Registry { get; }
    public IMemoryAllocator MemoryAllocator { get; }
    public string FilePath { get; }
    public SchemaCompatibility.State State { get; }
    public int LoadedComponentTypes { get; }
    public SchemaCompatibility.Diagnostic[] Diagnostics { get; }

    private EngineLifecycle(
        ServiceProvider services,
        WorkbenchAssemblyLoadContext alc,
        DatabaseEngine engine,
        IResourceRegistry registry,
        IMemoryAllocator allocator,
        string filePath,
        SchemaCompatibility.State state,
        int loadedComponentTypes,
        SchemaCompatibility.Diagnostic[] diagnostics)
    {
        _services = services;
        _alc = alc;
        Engine = engine;
        Registry = registry;
        MemoryAllocator = allocator;
        FilePath = filePath;
        State = state;
        LoadedComponentTypes = loadedComponentTypes;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Opens (or auto-creates) a database at the given path, optionally loading schema DLLs into a
    /// per-session collectible ALC and registering their component types. On breaking-schema or
    /// downgrade, the engine stays open but non-<see cref="SchemaCompatibility.State.Ready"/> —
    /// the caller surfaces a banner.
    /// </summary>
    /// <exception cref="WorkbenchException">
    /// 409 file_locked — file held by another process/session.
    /// 400 engine_open_failed — any other engine open failure.
    /// 404 schema_missing / 400 schema_load_failed / 400 schema_missing_dependency.
    /// </exception>
    public static async Task<EngineLifecycle> OpenAsync(string filePath, string[] schemaDllPaths = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        schemaDllPaths ??= [];

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new WorkbenchException(400, "engine_open_failed", $"Invalid path: {filePath}");
        }

        Directory.CreateDirectory(directory);
        var databaseName = Path.GetFileNameWithoutExtension(fullPath);
        var walDir = Path.Combine(directory, "wal");
        Directory.CreateDirectory(walDir);

        // Windows doesn't release MMF file handles synchronously on Dispose — a fresh POST arriving
        // milliseconds after a DELETE can hit a transient sharing violation. Retry briefly before
        // surfacing it as file_locked (mirrors the engine test harness's DeleteAndWait pattern).
        const int maxAttempts = 6;
        const int retryDelayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TryOpenOnce(fullPath, directory, databaseName, walDir, schemaDllPaths);
            }
            catch (WorkbenchException wb) when (wb.ErrorCode == "file_locked" && attempt < maxAttempts)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    private static EngineLifecycle TryOpenOnce(
        string fullPath,
        string directory,
        string databaseName,
        string walDir,
        string[] schemaDllPaths)
    {
        ServiceProvider sp = null;
        WorkbenchAssemblyLoadContext alc = null;

        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddFilter((_, level) => level >= LogLevel.Warning));
            services
                .AddResourceRegistry()
                .AddMemoryAllocator()
                .AddEpochManager()
                .AddHighResolutionSharedTimer()
                .AddDeadlineWatchdog()
                .AddManagedPagedMMF(opts =>
                {
                    opts.DatabaseName = databaseName;
                    opts.DatabaseDirectory = directory;
                    // 65536 pages × 8KB = 512 MiB page cache.
                    opts.DatabaseCacheSize = 65536UL * 8192;
                })
                .AddSingleton<IWalFileIO, WalFileIO>()
                .AddDatabaseEngine(engineOpts =>
                {
                    engineOpts.Wal = new WalWriterOptions
                    {
                        WalDirectory = walDir,
                        UseFUA = false
                    };
                });

            sp = services.BuildServiceProvider();
            var engine = sp.GetRequiredService<DatabaseEngine>();
            var registry = sp.GetRequiredService<IResourceRegistry>();
            var allocator = sp.GetRequiredService<IMemoryAllocator>();

            SchemaCompatibility.State state = SchemaCompatibility.State.Ready;
            SchemaCompatibility.Diagnostic[] diagnostics = [];
            int loaded = 0;

            if (schemaDllPaths.Length > 0)
            {
                alc = new WorkbenchAssemblyLoadContext($"Workbench-Session-{Guid.NewGuid():N}");
                var loadedSchema = SchemaLoader.LoadSchemaDlls(alc, schemaDllPaths);
                var result = SchemaCompatibility.ClassifyAndRegister(engine, loadedSchema);
                state = result.State;
                diagnostics = result.Diagnostics;
                loaded = result.RegisteredCount;
            }

            return new EngineLifecycle(sp, alc, engine, registry, allocator, fullPath, state, loaded, diagnostics);
        }
        catch (WorkbenchException)
        {
            alc?.Dispose();
            sp?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            alc?.Dispose();
            sp?.Dispose();
            if (FindIOException(ex) is { } io && IsFileLocked(io))
            {
                throw new WorkbenchException(409, "file_locked", $"Database file is in use: {fullPath}", ex);
            }
            throw new WorkbenchException(400, "engine_open_failed", $"Failed to open database: {ex.Message}", ex);
        }
    }

    private static IOException FindIOException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is IOException io) return io;
        }
        return null;
    }

    private static bool IsFileLocked(IOException ex)
    {
        // Windows ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33.
        var hr = ex.HResult & 0xFFFF;
        return hr == 32 || hr == 33;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Order: engine (flush WAL, unmap pages) → ALC (unload collectible assemblies) → ServiceProvider
        // (unmap MMF, dispose allocator). Engine must go first so it's done touching ALC-loaded Types
        // before we Unload().
        try { Engine.Dispose(); } catch { /* non-fatal; ServiceProvider disposal still runs */ }
        try { _alc?.Dispose(); } catch { /* already unloaded */ }
        try { _services.Dispose(); } catch { /* MMF already unmapped — nothing to do */ }
    }
}
