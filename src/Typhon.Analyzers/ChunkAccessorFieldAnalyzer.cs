using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

        // Analyze invocation expressions directly - the context provides the semantic model
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Quick syntactic check before using semantic model
        // GetChunkHandleUnsafe is always called via member access: field.GetChunkHandleUnsafe()
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        // Quick name check to avoid unnecessary semantic analysis
        if (memberAccess.Name.Identifier.Text != "GetChunkHandleUnsafe")
            return;

        // Now use the semantic model provided by the context (no RS1030 violation)
        var semanticModel = context.SemanticModel;

        // Verify this is actually ChunkAccessor.GetChunkHandleUnsafe()
        var symbolInfo = semanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol method)
            return;

        if (!IsChunkAccessorType(method.ContainingType))
            return;

        // Check what GetChunkHandleUnsafe() is being called on
        var targetInfo = semanticModel.GetSymbolInfo(memberAccess.Expression, context.CancellationToken);

        // We're looking for field access on a reference type (class)
        if (targetInfo.Symbol is not IFieldSymbol field)
            return;

        // Only flag if the field is in a reference type (class) - not structs
        // Structs can be pinned or are on the stack, so the pointer is stable
        if (!field.ContainingType.IsReferenceType)
            return;

        // Verify the field type is ChunkAccessor
        if (!IsChunkAccessorType(field.Type))
            return;

        // Found a violation: GetChunkHandleUnsafe() called on a ChunkAccessor field in a class
        var diagnostic = Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            field.Name,
            field.ContainingType.Name);

        context.ReportDiagnostic(diagnostic);
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
