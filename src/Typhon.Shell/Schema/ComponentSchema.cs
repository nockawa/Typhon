using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Shell.Schema;

/// <summary>
/// Runtime field map for a component type. Maps field names to their offset, size, and type
/// for text-to-binary conversion.
/// </summary>
internal sealed class ComponentSchema
{
    public string Name { get; }
    public int Revision { get; }
    public bool AllowMultiple { get; }
    public int StructSize { get; }
    public string AssemblyPath { get; }
    public IReadOnlyList<FieldInfo> Fields { get; }
    public IReadOnlyDictionary<string, FieldInfo> FieldsByName { get; }

    public ComponentSchema(string name, int revision, bool allowMultiple, int structSize, string assemblyPath, List<FieldInfo> fields)
    {
        Name = name;
        Revision = revision;
        AllowMultiple = allowMultiple;
        StructSize = structSize;
        AssemblyPath = assemblyPath;
        Fields = fields;

        var byName = new Dictionary<string, FieldInfo>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            byName[f.Name] = f;
        }
        FieldsByName = byName;
    }

    internal sealed class FieldInfo
    {
        public string Name { get; init; }
        public FieldType Type { get; init; }
        public int Offset { get; init; }
        public int Size { get; init; }
        public bool HasIndex { get; init; }
        public bool IndexAllowMultiple { get; init; }
    }
}
