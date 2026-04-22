using System.Reflection;
using System.Runtime.Loader;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Per-session collectible ALC for user-supplied schema DLLs. Loads each DLL via
/// <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> (unlike <see cref="Assembly.LoadFrom"/>
/// which pins into the default ALC forever), and resolves sibling dependencies from the DLL's
/// own directory so inter-schema type refs work.
///
/// On <see cref="Dispose"/>: call <see cref="AssemblyLoadContext.Unload"/>. Any remaining managed
/// references (e.g. a <see cref="Type"/> held by the engine) will block actual unload until GC —
/// that's why <see cref="EngineLifecycle"/> disposes the engine BEFORE unloading the ALC.
/// </summary>
public sealed class WorkbenchAssemblyLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly HashSet<string> _probeDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _probeLock = new();
    private bool _unloaded;

    public WorkbenchAssemblyLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    /// <summary>
    /// Loads a schema DLL from <paramref name="path"/> into this ALC and records its directory
    /// as a probe path for subsequent reference resolution.
    /// </summary>
    public Assembly LoadSchema(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            lock (_probeLock)
            {
                _probeDirectories.Add(dir);
            }
        }
        return LoadFromAssemblyPath(full);
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        // Defer system / shared deps to the default ALC so we don't duplicate runtime types.
        // Only try to resolve from probe directories for assemblies we authored.
        // Snapshot under the lock — the CLR can invoke Load re-entrantly while LoadSchema is still
        // adding probe directories on another thread.
        string[] probes;
        lock (_probeLock)
        {
            probes = _probeDirectories.ToArray();
        }
        foreach (var dir in probes)
        {
            var candidate = Path.Combine(dir, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_unloaded) return;
        _unloaded = true;
        try { Unload(); } catch { /* already unloaded */ }
    }
}
