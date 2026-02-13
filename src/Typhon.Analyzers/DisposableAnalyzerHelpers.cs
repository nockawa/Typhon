using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Typhon.Analyzers;

/// <summary>
/// Shared helpers for Typhon dispose analyzers (TYPHON004 through TYPHON007).
/// Centralizes the critical-type list, type-name resolution, and field discovery
/// so each analyzer doesn't duplicate this logic.
/// </summary>
static class DisposableAnalyzerHelpers
{
    /// <summary>
    /// Critical Typhon types that MUST be disposed. Maps fully-qualified type name to consequence description.
    /// Only these types are tracked — general IDisposable is not tracked to avoid false positives.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> CriticalTypes =
        ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, string>("Typhon.Engine.ChunkAccessor", " - causes page cache deadlock"),
            new KeyValuePair<string, string>("Typhon.Engine.Transaction", " - causes uncommitted changes and resource leak"),
        });

    /// <summary>
    /// Returns the fully-qualified type name without the <c>global::</c> prefix.
    /// </summary>
    public static string GetFullTypeName(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrEmpty(ns))
            return type.Name;

        if (ns.StartsWith("global::"))
            ns = ns.Substring(8);

        return $"{ns}.{type.Name}";
    }

    /// <summary>
    /// Finds all non-static, non-compiler-generated, non-ref instance fields of critical disposable types.
    /// Ref fields are excluded because they represent borrowed references — the caller owns the resource.
    /// </summary>
    public static List<(IFieldSymbol Field, string TypeFullName, string Consequence)> FindCriticalFields(
        INamedTypeSymbol type)
    {
        var result = new List<(IFieldSymbol, string, string)>();

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field)
                continue;

            if (field.IsStatic)
                continue;

            if (field.IsImplicitlyDeclared)
                continue;

            // Skip ref fields — borrowed references, not owned resources
            if (field.RefKind != RefKind.None)
                continue;

            var fieldTypeName = GetFullTypeName(field.Type);
            if (CriticalTypes.TryGetValue(fieldTypeName, out var consequence))
            {
                result.Add((field, fieldTypeName, consequence));
            }
        }

        return result;
    }
}
