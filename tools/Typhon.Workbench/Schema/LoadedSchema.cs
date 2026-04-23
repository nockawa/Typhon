using System.Reflection;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Result of loading one or more schema DLLs — holds the assemblies plus the flat lists of component
/// types (discovered via <c>[Component]</c> attribute) and archetype types (concrete classes inheriting
/// from <c>Typhon.Engine.Archetype&lt;&gt;</c>). Archetypes are needed separately from components because
/// the engine's <c>ArchetypeRegistry</c> is static state — concrete archetype classes must be
/// <c>RuntimeHelpers.RunClassConstructor</c>-triggered for their static field initializers (the
/// <c>Register&lt;T&gt;()</c> calls) to populate the registry. Without that, per-archetype entity storage
/// is never wired and entity counts stay at 0.
/// </summary>
public sealed record LoadedSchema(
    Assembly[] Assemblies,
    Type[] ComponentTypes,
    string[] ComponentNames,
    Type[] ArchetypeTypes);
