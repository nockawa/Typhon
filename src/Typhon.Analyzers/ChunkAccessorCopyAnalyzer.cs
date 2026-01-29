using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects copying of ChunkAccessor instances.
/// ChunkAccessor is a large ~1KB struct designed for zero-allocation. Copying it defeats
/// its performance design and can create unexpected behavior since it maintains internal state.
/// The only valid creation is via ChunkBasedSegment.CreateChunkAccessor().
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ChunkAccessorCopyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON003";
    private const string Category = "Performance";

    private static readonly LocalizableString Title =
        "ChunkAccessor must not be copied";

    private static readonly LocalizableString MessageFormat =
        "Copying ChunkAccessor '{0}' creates an expensive ~1KB stack copy. Use 'ref' to pass references instead.";

    private static readonly LocalizableString Description =
        "ChunkAccessor is a large struct (~1KB) designed for zero-allocation performance. " +
        "Copying it creates expensive stack copies and duplicates internal state (cache, pins, etc.). " +
        "The only valid creation is via ChunkBasedSegment.CreateChunkAccessor(). " +
        "All other usage should pass ChunkAccessor by 'ref' to avoid copies.";

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

        // Analyze assignments and initializations
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(AnalyzeVariableInitializer, OperationKind.VariableInitializer);

        // Analyze return statements
        context.RegisterOperationAction(AnalyzeReturn, OperationKind.Return);
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        // Check if we're assigning a ChunkAccessor
        if (!IsChunkAccessorType(assignment.Target.Type))
            return;

        // Allow ref reassignment (e.g., "_accessor = ref accessor")
        // These don't copy, they just redirect the reference
        if (assignment.IsRef)
            return;

        // Allow assignment from CreateChunkAccessor() calls
        if (IsCreateChunkAccessorCall(assignment.Value))
            return;

        // Allow assignment from default(ChunkAccessor) or new ChunkAccessor()
        if (IsDefaultOrNewExpression(assignment.Value))
            return;

        // This is a copy - report it
        var diagnostic = Diagnostic.Create(
            Rule,
            assignment.Syntax.GetLocation(),
            GetExpressionName(assignment.Value));

        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeVariableInitializer(OperationAnalysisContext context)
    {
        var initializer = (IVariableInitializerOperation)context.Operation;

        // Get the variable being initialized
        var variableDeclarator = initializer.Syntax.Parent as VariableDeclaratorSyntax;
        if (variableDeclarator == null)
            return;

        var variableSymbol = context.ContainingSymbol as ILocalSymbol;
        if (variableSymbol == null)
            return;

        // Check if it's a ChunkAccessor
        if (!IsChunkAccessorType(variableSymbol.Type))
            return;

        // Allow initialization from CreateChunkAccessor() calls
        if (IsCreateChunkAccessorCall(initializer.Value))
            return;

        // Allow initialization from default(ChunkAccessor) or new ChunkAccessor()
        if (IsDefaultOrNewExpression(initializer.Value))
            return;

        // This is a copy - report it
        var diagnostic = Diagnostic.Create(
            Rule,
            initializer.Syntax.GetLocation(),
            GetExpressionName(initializer.Value));

        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeReturn(OperationAnalysisContext context)
    {
        var returnOp = (IReturnOperation)context.Operation;

        // Check if we're returning a ChunkAccessor
        if (returnOp.ReturnedValue == null || !IsChunkAccessorType(returnOp.ReturnedValue.Type))
            return;

        // Allow returning from CreateChunkAccessor method
        var containingMethod = context.ContainingSymbol as IMethodSymbol;
        if (containingMethod != null && containingMethod.Name == "CreateChunkAccessor")
            return;

        // Allow returning from constructors (e.g., new ChunkAccessor(...))
        if (IsDefaultOrNewExpression(returnOp.ReturnedValue))
            return;

        // This is a copy - report it
        var diagnostic = Diagnostic.Create(
            Rule,
            returnOp.Syntax.GetLocation(),
            GetExpressionName(returnOp.ReturnedValue));

        context.ReportDiagnostic(diagnostic);
    }

    private static bool IsCreateChunkAccessorCall(IOperation operation)
    {
        // Unwrap conversion operations
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion)
        {
            unwrapped = conversion.Operand;
        }

        // Check if it's an invocation of CreateChunkAccessor
        if (unwrapped is IInvocationOperation invocation)
        {
            return invocation.TargetMethod.Name == "CreateChunkAccessor" &&
                   invocation.TargetMethod.ContainingType?.Name == "ChunkBasedSegment";
        }

        return false;
    }

    private static bool IsDefaultOrNewExpression(IOperation operation)
    {
        // Unwrap conversion operations
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion)
        {
            unwrapped = conversion.Operand;
        }

        // Check for default(ChunkAccessor), default, new ChunkAccessor()
        return unwrapped is IDefaultValueOperation ||
               unwrapped is IObjectCreationOperation;
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

    private static string GetExpressionName(IOperation operation)
    {
        // Try to get a meaningful name from the operation
        if (operation is ILocalReferenceOperation localRef)
            return localRef.Local.Name;

        if (operation is IFieldReferenceOperation fieldRef)
            return fieldRef.Field.Name;

        if (operation is IParameterReferenceOperation paramRef)
            return paramRef.Parameter.Name;

        if (operation is IInvocationOperation invocation)
            return invocation.TargetMethod.Name + "()";

        return operation.Syntax.ToString();
    }
}
