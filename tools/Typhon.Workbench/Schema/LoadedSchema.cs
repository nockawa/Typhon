using System.Reflection;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Result of loading one or more schema DLLs — holds the assemblies plus the flat list of
/// component types discovered via <c>[Component]</c> attribute.
/// </summary>
public sealed record LoadedSchema(
    Assembly[] Assemblies,
    Type[] ComponentTypes,
    string[] ComponentNames);
