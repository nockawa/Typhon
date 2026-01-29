using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects unsafe usage of ChunkAccessor fields in managed types.
/// ChunkAccessor.GetChunkHandleUnsafe() creates a void* pointer to the ChunkAccessor.
/// If ChunkAccessor is a field in a heap-allocated class, the GC can move the containing
/// object during compaction, making the stored pointer dangling and causing memory corruption.
///
/// Safe alternative: Use ChunkAccessor.GetChunkHandle() which stores a 'ref ChunkAccessor'
/// instead of a pointer. The GC updates managed references automatically.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ChunkAccessorFieldAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON002";
    private const string Category = "Safety";

    private static readonly LocalizableString Title =
        "ChunkAccessor field calls GetChunkHandleUnsafe in managed type";

    private static readonly LocalizableString MessageFormat =
        "Field '{0}' of type 'ChunkAccessor' in class '{1}' calls GetChunkHandleUnsafe(), " +
        "which creates a pointer that becomes invalid when the GC moves the object. " +
        "Use GetChunkHandle() instead (returns ChunkHandle with ref instead of pointer).";

    private static readonly LocalizableString Description =
        "ChunkAccessor.GetChunkHandleUnsafe() creates a void* pointer to the ChunkAccessor instance. " +
        "When ChunkAccessor is a field in a heap-allocated class, the .NET GC can move the object " +
        "during garbage collection, making the pointer invalid and causing memory corruption or crashes. " +
        "Solution: Use GetChunkHandle() which returns ChunkHandle (uses 'ref ChunkAccessor' that the GC updates automatically).";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Analyze entire type declarations (class/struct) to check field usage
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        // Only analyze reference types (classes)
        if (!namedType.IsReferenceType)
            return;

        // Find all ChunkAccessor fields in this type
        var chunkAccessorFields = namedType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => IsChunkAccessorType(f.Type))
            .ToList();

        if (chunkAccessorFields.Count == 0)
            return;

        // Get all method bodies in this type to search for GetChunkHandleUnsafe calls
        var compilation = context.Compilation;

        foreach (var syntaxRef in namedType.DeclaringSyntaxReferences)
        {
            var typeDecl = syntaxRef.GetSyntax(context.CancellationToken) as TypeDeclarationSyntax;
            if (typeDecl == null)
                continue;

            var semanticModel = compilation.GetSemanticModel(typeDecl.SyntaxTree);

            // Search for all invocations in this type
            var invocations = typeDecl.DescendantNodes()
                .OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                // Check if this is a call to GetChunkHandleUnsafe()
                if (!IsGetChunkHandleUnsafeCall(invocation, semanticModel))
                    continue;

                // Check if it's called on one of our ChunkAccessor fields
                var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                if (memberAccess == null)
                    continue;

                var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression, context.CancellationToken);
                var fieldSymbol = symbolInfo.Symbol as IFieldSymbol;

                if (fieldSymbol == null)
                    continue;

                // Check if this field is one of the ChunkAccessor fields we found
                var matchingField = chunkAccessorFields.FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f, fieldSymbol));
                if (matchingField != null)
                {
                    // Found a violation! Report it on the field declaration
                    foreach (var fieldRef in matchingField.DeclaringSyntaxReferences)
                    {
                        var fieldDecl = fieldRef.GetSyntax(context.CancellationToken);

                        // Get the specific variable declarator for this field
                        var variableDeclarator = fieldDecl.AncestorsAndSelf()
                            .OfType<VariableDeclaratorSyntax>()
                            .FirstOrDefault();

                        if (variableDeclarator != null)
                        {
                            var diagnostic = Diagnostic.Create(
                                Rule,
                                variableDeclarator.GetLocation(),
                                matchingField.Name,
                                namedType.Name);

                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }
        }
    }

    private static bool IsGetChunkHandleUnsafeCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var method = symbolInfo.Symbol as IMethodSymbol;

        if (method == null)
            return false;

        // Check if the method is GetChunkHandleUnsafe on ChunkAccessor
        return method.Name == "GetChunkHandleUnsafe" &&
               IsChunkAccessorType(method.ContainingType);
    }

    private static bool IsChunkAccessorType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null)
            return false;

        // Match by name and ensure it's in the Typhon.Engine namespace
        if (typeSymbol.Name != "ChunkAccessor")
            return false;

        // Check namespace - handle both with and without global prefix
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        return ns == "Typhon.Engine" || ns == "global::Typhon.Engine";
    }
}
