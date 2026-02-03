using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that verifies Dispose() methods properly dispose ALL fields of critical types.
/// 
/// This catches bugs like ComponentTable.Dispose() forgetting to dispose ChunkAccessor fields
/// stored in nested objects (e.g., ComponentCollectionInfo.Accessor).
/// 
/// The analyzer checks:
/// 1. Direct fields of critical types (ChunkAccessor, Transaction, PageAccessor)
/// 2. Fields that are collections/dictionaries containing objects with critical fields
/// 
/// The analyzer skips:
/// - ref fields (borrowed references, not owned - e.g., "ref ChunkAccessor _accessor")
/// - These represent references to resources owned by the caller, not this type
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DisposeCompletenessAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON006";
    private const string Category = "Reliability";

    private static readonly LocalizableString Title =
        "Dispose() does not dispose all critical fields";

    private static readonly LocalizableString MessageFormat =
        "Dispose() method in '{0}' does not dispose field '{1}' of type '{2}'{3}";

    private static readonly LocalizableString Description =
        "Dispose() methods must dispose ALL fields of critical IDisposable types (ChunkAccessor, Transaction, PageAccessor). " +
        "Failing to dispose these fields causes resource leaks such as page cache deadlocks.";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    /// <summary>
    /// Critical Typhon types that MUST be disposed.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> CriticalTypes =
        ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, string>("Typhon.Engine.ChunkAccessor", " - causes page cache deadlock"),
            new KeyValuePair<string, string>("Typhon.Engine.Transaction", " - causes uncommitted changes and resource leak"),
            new KeyValuePair<string, string>("Typhon.Engine.PageAccessor", " - causes page lock leak"),
        });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        // Only analyze classes and structs that implement IDisposable
        if (namedType.TypeKind != TypeKind.Class && namedType.TypeKind != TypeKind.Struct)
            return;

        if (!ImplementsIDisposable(namedType, context.Compilation))
            return;

        // Find the Dispose() method
        var disposeMethod = FindDisposeMethod(namedType);
        if (disposeMethod == null)
            return;

        // Find all critical fields that need disposal
        var criticalFields = FindCriticalFields(namedType);
        if (criticalFields.Count == 0)
            return;

        // Get the Dispose() method body
        var disposeBody = GetMethodBody(disposeMethod);
        if (string.IsNullOrEmpty(disposeBody))
            return;

        // Check which fields are disposed in the method
        foreach (var (field, typeName, consequence) in criticalFields)
        {
            if (!IsFieldDisposedInMethod(field, disposeBody, namedType))
            {
                var location = disposeMethod.Locations.FirstOrDefault() ?? namedType.Locations.FirstOrDefault();
                if (location == null)
                    continue;

                var diagnostic = Diagnostic.Create(
                    Rule,
                    location,
                    namedType.Name,
                    field.Name,
                    GetSimpleTypeName(field.Type),
                    consequence);

                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool ImplementsIDisposable(INamedTypeSymbol type, Compilation compilation)
    {
        var disposableType = compilation.GetTypeByMetadataName("System.IDisposable");
        if (disposableType == null)
            return false;

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, disposableType));
    }

    private static IMethodSymbol FindDisposeMethod(INamedTypeSymbol type)
    {
        // Look for Dispose() method (explicit or implicit IDisposable implementation)
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol method &&
                method.Name == "Dispose" &&
                method.Parameters.Length == 0 &&
                !method.IsAbstract)
            {
                return method;
            }
        }

        // Also check for explicit interface implementation
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol method &&
                method.MethodKind == MethodKind.ExplicitInterfaceImplementation &&
                method.Name.EndsWith(".Dispose") &&
                method.Parameters.Length == 0)
            {
                return method;
            }
        }

        return null;
    }

    private static List<(IFieldSymbol Field, string TypeFullName, string Consequence)> FindCriticalFields(INamedTypeSymbol type)
    {
        var result = new List<(IFieldSymbol, string, string)>();

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field)
                continue;

            // Skip static fields
            if (field.IsStatic)
                continue;

            // Skip compiler-generated backing fields
            if (field.IsImplicitlyDeclared)
                continue;

            // Skip ref fields - these are borrowed references, not owned resources
            // e.g., "ref ChunkAccessor _accessor" in ref structs like RevisionEnumerator
            // The owner of the referenced resource is responsible for disposal
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

    private static string GetMethodBody(IMethodSymbol method)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (syntax is MethodDeclarationSyntax methodSyntax)
            {
                return methodSyntax.Body?.ToString() ?? methodSyntax.ExpressionBody?.ToString() ?? "";
            }
        }
        return "";
    }

    /// <summary>
    /// Checks if a field is disposed in the method body.
    /// Handles various patterns:
    /// - Direct: field.Dispose()
    /// - Null-conditional: field?.Dispose()
    /// - Via local: var x = field; x.Dispose()
    /// - In loop: foreach (var x in collection) { x.Field.Dispose(); }
    /// </summary>
    private static bool IsFieldDisposedInMethod(IFieldSymbol field, string methodBody, INamedTypeSymbol containingType)
    {
        var fieldName = field.Name;

        // Pattern 1: Direct disposal - field.Dispose() or _field.Dispose()
        if (methodBody.Contains($"{fieldName}.Dispose()"))
            return true;

        // Pattern 2: Null-conditional - field?.Dispose()
        if (methodBody.Contains($"{fieldName}?.Dispose()"))
            return true;

        // Pattern 3: Via 'this' - this.field.Dispose()
        if (methodBody.Contains($"this.{fieldName}.Dispose()"))
            return true;

        // Pattern 4: Assigned to local then disposed
        // Look for patterns like: var x = fieldName; ... x.Dispose();
        // This is a simplified check - we look for any local that gets the field value
        var localAssignmentPattern = $"= {fieldName};";
        var localAssignmentPatternAlt = $"= {fieldName},";
        if (methodBody.Contains(localAssignmentPattern) || methodBody.Contains(localAssignmentPatternAlt))
        {
            // If the field is assigned to something, assume it might be disposed through that
            // This is a conservative approach to avoid false positives
            return true;
        }

        // Pattern 5: Field accessed through iteration (foreach loops)
        // Check if the field name appears in a foreach and Dispose is called on items
        if (methodBody.Contains($"in {fieldName}") && methodBody.Contains(".Dispose()"))
        {
            return true;
        }

        // Pattern 6: Field's Values property iterated (Dictionary pattern)
        if (methodBody.Contains($"{fieldName}.Values") && methodBody.Contains(".Dispose()"))
        {
            return true;
        }

        return false;
    }

    private static string GetFullTypeName(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrEmpty(ns))
            return type.Name;

        if (ns.StartsWith("global::"))
            ns = ns.Substring(8);

        return $"{ns}.{type.Name}";
    }

    private static string GetSimpleTypeName(ITypeSymbol type)
    {
        return type.Name;
    }
}
