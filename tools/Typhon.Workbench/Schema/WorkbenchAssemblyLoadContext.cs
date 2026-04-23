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
        // Defer shared deps to the default ALC so we don't duplicate runtime types. Probing from the
        // fixture/schema-DLL directory naively would load a SECOND copy of (e.g.) Typhon.Schema.Definition
        // when the user has `dotnet publish` output sitting next to their schema.dll — and then
        // [ComponentAttribute] on the schema types would be a *different* type than the one SchemaLoader
        // uses, and attribute lookup silently returns null → 0 components loaded.
        //
        // Defer if:
        //   (a) the default ALC already has this assembly loaded, OR
        //   (b) the name matches a well-known shared prefix (Typhon.*, System.*, Microsoft.*, runtime dlls)
        //       — handles the case where the default ALC hasn't loaded it yet but we still want runtime-
        //       resolution rather than shadowing from the probe dir.
        if (IsSharedAssembly(assemblyName) || IsAlreadyLoadedInDefaultContext(assemblyName))
        {
            return null;
        }

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

    /// <summary>Well-known prefixes for assemblies that must be resolved by the default ALC to keep type identity
    /// consistent. Loading duplicates of these silently breaks attribute lookups across ALC boundaries.</summary>
    private static bool IsSharedAssembly(AssemblyName name)
    {
        var n = name.Name;
        if (string.IsNullOrEmpty(n)) return false;
        return n.StartsWith("Typhon.", StringComparison.Ordinal)
            || n.StartsWith("System.", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.", StringComparison.Ordinal)
            || n.Equals("System", StringComparison.Ordinal)
            || n.Equals("mscorlib", StringComparison.Ordinal)
            || n.Equals("netstandard", StringComparison.Ordinal);
    }

    private static bool IsAlreadyLoadedInDefaultContext(AssemblyName name)
    {
        foreach (var asm in Default.Assemblies)
        {
            if (string.Equals(asm.GetName().Name, name.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_unloaded) return;
        _unloaded = true;
        try { Unload(); } catch { /* already unloaded */ }
    }
}
