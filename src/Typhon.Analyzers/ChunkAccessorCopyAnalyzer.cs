using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects copying of EpochChunkAccessor instances.
/// This is a large struct designed for zero-allocation. Copying it defeats
/// its performance design and can create unexpected behavior since it maintains internal state.
/// The only valid creation is via ChunkBasedSegment.CreateEpochChunkAccessor().
///
/// This analyzer detects:
/// 1. Direct assignment copies: var x = existingAccessor;
/// 2. Ref field dereference copies: var x = refStruct.RefField; (where RefField is 'ref EpochChunkAccessor')
/// 3. Return statement copies
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ChunkAccessorCopyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON003";
    private const string Category = "Performance";

    private static readonly LocalizableString Title =
        "EpochChunkAccessor must not be copied";

    private static readonly LocalizableString MessageFormat =
        "Copying '{0}' creates an expensive stack copy. Use 'ref' to pass references instead.";

    private static readonly LocalizableString Description =
        "EpochChunkAccessor is a large struct designed for zero-allocation performance. " +
        "Copying it creates expensive stack copies and duplicates internal state (cache, etc.). " +
        "The only valid creation is via ChunkBasedSegment.CreateEpochChunkAccessor(). " +
        "All other usage should pass by 'ref' to avoid copies.";

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
        context.RegisterOperationAction(AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);

        // Analyze return statements
        context.RegisterOperationAction(AnalyzeReturn, OperationKind.Return);
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        // Check if we're assigning an EpochChunkAccessor
        if (!IsChunkAccessorType(assignment.Target.Type))
            return;

        // Allow ref reassignment (e.g., "_accessor = ref accessor")
        // These don't copy, they just redirect the reference
        if (assignment.IsRef)
            return;

        // Allow assignment from CreateChunkAccessor() calls
        if (IsCreateChunkAccessorCall(assignment.Value))
            return;

        // Allow assignment from default(EpochChunkAccessor) or new EpochChunkAccessor()
        if (IsDefaultOrNewExpression(assignment.Value))
            return;

        // This is a copy - report it
        var diagnostic = Diagnostic.Create(
            Rule,
            assignment.Syntax.GetLocation(),
            GetExpressionName(assignment.Value));

        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeVariableDeclarator(OperationAnalysisContext context)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;
        
        // Get the local symbol being declared
        var localSymbol = declarator.Symbol;
        if (localSymbol == null)
            return;

        // Check if it's an EpochChunkAccessor type
        if (!IsChunkAccessorType(localSymbol.Type))
            return;

        // If this is a ref local (ref var x = ref ...), it's allowed - no copy occurs
        if (localSymbol.RefKind != RefKind.None)
            return;

        // Get the initializer
        var initializer = declarator.Initializer;
        if (initializer == null)
            return;

        var initValue = initializer.Value;
        if (initValue == null)
            return;

        // Allow initialization from CreateChunkAccessor() calls
        if (IsCreateChunkAccessorCall(initValue))
            return;

        // Allow initialization from default(EpochChunkAccessor) or new EpochChunkAccessor()
        if (IsDefaultOrNewExpression(initValue))
            return;

        // This is a copy - report it
        // Include extra context if this is a ref field dereference
        var expressionName = GetExpressionName(initValue);
        if (IsRefFieldAccess(initValue))
        {
            expressionName += " (ref field dereference - use 'ref var' instead of 'var')";
        }

        var diagnostic = Diagnostic.Create(
            Rule,
            declarator.Syntax.GetLocation(),
            expressionName);

        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>
    /// Checks if the operation is accessing a ref field (which would cause a copy if not using ref on the LHS).
    /// </summary>
    private static bool IsRefFieldAccess(IOperation operation)
    {
        // Unwrap conversions
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion)
        {
            unwrapped = conversion.Operand;
        }

        // Check if it's a field reference where the field is a ref field
        if (unwrapped is IFieldReferenceOperation fieldRef)
        {
            return fieldRef.Field.RefKind != RefKind.None;
        }

        return false;
    }

    private static void AnalyzeReturn(OperationAnalysisContext context)
    {
        var returnOp = (IReturnOperation)context.Operation;

        // Check if we're returning an EpochChunkAccessor
        if (returnOp.ReturnedValue == null || !IsChunkAccessorType(returnOp.ReturnedValue.Type))
            return;

        // Allow returning from CreateEpochChunkAccessor methods
        var containingMethod = context.ContainingSymbol as IMethodSymbol;
        if (containingMethod != null && containingMethod.Name == "CreateEpochChunkAccessor")
            return;

        // Allow returning from constructors (e.g., new EpochChunkAccessor(...))
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

        // Check if it's an invocation of CreateEpochChunkAccessor
        if (unwrapped is IInvocationOperation invocation)
        {
            var methodName = invocation.TargetMethod.Name;
            return methodName == "CreateEpochChunkAccessor" &&
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

        // Check for default(EpochChunkAccessor), default, new EpochChunkAccessor()
        if (unwrapped is IDefaultValueOperation || unwrapped is IObjectCreationOperation)
            return true;

        // Handle conditional expressions: condition ? trueValue : falseValue
        // Both branches must be allowed (CreateChunkAccessor or default/new)
        if (unwrapped is IConditionalOperation conditional)
        {
            return IsAllowedInitializer(conditional.WhenTrue) && 
                   IsAllowedInitializer(conditional.WhenFalse);
        }

        return false;
    }

    /// <summary>
    /// Checks if an operation is an allowed initializer for EpochChunkAccessor
    /// (CreateEpochChunkAccessor call, default, or new expression).
    /// </summary>
    private static bool IsAllowedInitializer(IOperation operation)
    {
        return IsCreateChunkAccessorCall(operation) || IsDefaultOrNewExpression(operation);
    }

    private static bool IsChunkAccessorType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null)
            return false;

        // Match by name and ensure it's in the Typhon.Engine namespace
        if (typeSymbol.Name != "EpochChunkAccessor")
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
