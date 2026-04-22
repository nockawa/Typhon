using System.Reflection;
using Typhon.Schema.Definition;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Loads user-supplied schema DLLs into a <see cref="WorkbenchAssemblyLoadContext"/> and extracts
/// component types (structs tagged with <c>[Component]</c>). Does not touch the engine — that
/// happens in <see cref="EngineLifecycle"/> after compatibility classification.
/// </summary>
public static class SchemaLoader
{
    /// <exception cref="WorkbenchException">
    /// 404 schema_missing — a path doesn't exist on disk.
    /// 400 schema_load_failed — DLL is not a valid managed assembly or fails to load.
    /// 400 schema_missing_dependency — a referenced assembly isn't resolvable.
    /// </exception>
    public static LoadedSchema LoadSchemaDlls(WorkbenchAssemblyLoadContext alc, string[] paths)
    {
        ArgumentNullException.ThrowIfNull(alc);
        ArgumentNullException.ThrowIfNull(paths);

        var assemblies = new List<Assembly>(paths.Length);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new WorkbenchException(404, "schema_missing", $"Schema DLL not found: {path}");
            }
            try
            {
                assemblies.Add(alc.LoadSchema(path));
            }
            catch (FileLoadException ex)
            {
                throw new WorkbenchException(400, "schema_missing_dependency",
                    $"Failed to resolve a dependency for {path}: {ex.Message}", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new WorkbenchException(400, "schema_load_failed",
                    $"Not a valid .NET assembly: {path}", ex);
            }
            catch (Exception ex)
            {
                throw new WorkbenchException(400, "schema_load_failed",
                    $"Failed to load {path}: {ex.Message}", ex);
            }
        }

        var types = new List<Type>();
        var names = new List<string>();
        foreach (var asm in assemblies)
        {
            Type[] exported;
            try
            {
                exported = asm.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaderMsg = ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message;
                throw new WorkbenchException(400, "schema_missing_dependency",
                    $"Type load error in {asm.GetName().Name}: {loaderMsg}", ex);
            }

            foreach (var type in exported)
            {
                if (!type.IsValueType) continue;
                var attr = type.GetCustomAttribute<ComponentAttribute>();
                if (attr == null) continue;
                types.Add(type);
                names.Add(attr.Name ?? type.Name);
            }
        }

        return new LoadedSchema(assemblies.ToArray(), types.ToArray(), names.ToArray());
    }
}
