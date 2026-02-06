using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects classes and structs containing fields of critical IDisposable types
/// (ChunkAccessor, Transaction, PageAccessor) that don't implement IDisposable themselves.
/// 
/// This catches bugs like StringTableSegment holding a ChunkAccessor field but not being
/// IDisposable, which causes page cache deadlocks when the container goes out of scope
/// without disposing its owned resources.
/// 
/// The analyzer is smart enough to skip false positives:
/// - Types nested inside an IDisposable container (disposal managed by parent)
/// - Inline arrays (compiler-generated, disposal managed by containing struct)
/// - Types with explicit disposal methods that call Dispose() on the fields
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DisposableFieldContainerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON005";
    private const string Category = "Reliability";

    private static readonly LocalizableString Title =
        "Type with disposable field does not implement IDisposable";

    private static readonly LocalizableString MessageFormat =
        "Type '{0}' contains field '{1}' of disposable type '{2}' but does not implement IDisposable{3}";

    private static readonly LocalizableString Description =
        "Types that contain fields of critical IDisposable types (ChunkAccessor, Transaction, PageAccessor) " +
        "must implement IDisposable to ensure proper cleanup. Failing to do so causes resource leaks " +
        "such as page cache deadlocks or uncommitted transaction data.";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    /// <summary>
    /// Critical Typhon types that MUST be disposed. Maps type name to consequence description.
    /// A container holding any of these types must implement IDisposable.
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

        // Analyze class and struct declarations
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        // Only analyze classes and structs
        if (namedType.TypeKind != TypeKind.Class && namedType.TypeKind != TypeKind.Struct)
            return;

        // Skip if the type already implements IDisposable
        if (ImplementsIDisposable(namedType, context.Compilation))
            return;

        // Skip ref structs - they can't implement interfaces but are typically
        // short-lived stack-allocated types with explicit disposal patterns
        if (namedType.IsRefLikeType)
            return;

        // Skip inline arrays - they are compiler-generated storage and their
        // disposal is managed by the containing struct (e.g., ChunkAccessor)
        if (IsInlineArray(namedType))
            return;

        // Skip types nested inside an IDisposable container - disposal is
        // managed by the parent type (e.g., ComponentInfoBase inside Transaction)
        if (IsNestedInsideDisposableType(namedType, context.Compilation))
            return;

        // Find all fields that are of critical disposable types
        var criticalFields = new List<(IFieldSymbol Field, string TypeFullName, string Consequence)>();

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IFieldSymbol field)
                continue;

            // Skip static fields - they have different lifetime semantics
            if (field.IsStatic)
                continue;

            // Skip compiler-generated backing fields for auto-properties
            if (field.IsImplicitlyDeclared)
                continue;

            var fieldTypeName = GetFullTypeName(field.Type);
            if (CriticalTypes.TryGetValue(fieldTypeName, out var consequence))
            {
                criticalFields.Add((field, fieldTypeName, consequence));
            }
        }

        // No critical fields found
        if (criticalFields.Count == 0)
            return;

        // Check if the type has a method that disposes the critical fields
        // This handles patterns like ComponentInfoBase.DisposeAccessors()
        if (HasMethodDisposingFields(namedType, criticalFields))
            return;

        // Report diagnostics for each critical field
        foreach (var (field, typeName, consequence) in criticalFields)
        {
            var location = field.Locations.FirstOrDefault() ?? namedType.Locations.FirstOrDefault();
            if (location == null)
                continue;

            var diagnostic = Diagnostic.Create(
                Rule,
                location,
                namedType.Name,
                field.Name,
                field.Type.Name,
                consequence);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool ImplementsIDisposable(INamedTypeSymbol type, Compilation compilation)
    {
        var disposableType = compilation.GetTypeByMetadataName("System.IDisposable");
        if (disposableType == null)
            return false;

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, disposableType));
    }

    /// <summary>
    /// Checks if the type is an inline array (C# 12 feature).
    /// Inline arrays have the [InlineArray] attribute.
    /// </summary>
    private static bool IsInlineArray(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (attrName == "InlineArrayAttribute" || attrName == "InlineArray")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if the type is nested inside a type that implements IDisposable.
    /// This handles patterns where an inner class's disposal is managed by its container.
    /// </summary>
    private static bool IsNestedInsideDisposableType(INamedTypeSymbol type, Compilation compilation)
    {
        var containingType = type.ContainingType;
        while (containingType != null)
        {
            if (ImplementsIDisposable(containingType, compilation))
            {
                return true;
            }
            containingType = containingType.ContainingType;
        }
        return false;
    }

    /// <summary>
    /// Checks if the type has a method that disposes all the critical fields.
    /// This handles patterns like DisposeAccessors() that explicitly dispose fields
    /// without the containing type implementing IDisposable.
    /// </summary>
    private static bool HasMethodDisposingFields(
        INamedTypeSymbol type,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        // Look for methods that might dispose the fields
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            // Skip constructors, property accessors, etc.
            if (method.MethodKind != MethodKind.Ordinary)
                continue;

            // Check if method name suggests disposal
            var methodName = method.Name;
            if (!methodName.Contains("Dispose") && 
                !methodName.Contains("Cleanup") && 
                !methodName.Contains("Release") &&
                !methodName.Contains("Close"))
                continue;

            // Check if the method body calls Dispose() on the critical fields
            // We need to analyze the syntax tree for this
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                if (syntax is not MethodDeclarationSyntax methodSyntax)
                    continue;

                // Count how many critical fields are disposed in this method
                var disposedFieldCount = CountDisposedFields(methodSyntax, criticalFields);
                if (disposedFieldCount == criticalFields.Count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Counts how many of the critical fields have .Dispose() called on them in the method body.
    /// </summary>
    private static int CountDisposedFields(
        MethodDeclarationSyntax method,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? "";
        
        var count = 0;
        foreach (var (field, _, _) in criticalFields)
        {
            // Simple text-based check: look for "fieldName.Dispose()"
            if (body.Contains($"{field.Name}.Dispose()"))
            {
                count++;
            }
        }
        
        return count;
    }

    private static string GetFullTypeName(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrEmpty(ns))
            return type.Name;

        // Handle global:: prefix
        if (ns.StartsWith("global::"))
            ns = ns.Substring(8);

        return $"{ns}.{type.Name}";
    }
}
