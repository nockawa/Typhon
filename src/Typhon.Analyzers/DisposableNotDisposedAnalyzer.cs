using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects IDisposable instances returned from method calls that are not properly disposed.
/// This addresses limitations in CA2000 which lacks inter-procedural analysis and misses many patterns.
/// 
/// Critical Typhon types (ChunkAccessor, Transaction, PageAccessor) are reported as errors because
/// failing to dispose them causes system-level bugs like page cache deadlock or data corruption.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DisposableNotDisposedAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON004";
    private const string Category = "Reliability";

    // Diagnostic for discarded disposable results (Method(); or _ = Method();)
    private static readonly LocalizableString DiscardedTitle =
        "IDisposable result is discarded";

    private static readonly LocalizableString DiscardedMessageFormat =
        "'{0}' returns '{1}' which is discarded without being disposed{2}";

    private static readonly LocalizableString DiscardedDescription =
        "Method calls that return IDisposable instances must have their results captured and disposed. " +
        "Discarding the result causes a resource leak.";

    private static readonly DiagnosticDescriptor DiscardedRule = new DiagnosticDescriptor(
        DiagnosticId,
        DiscardedTitle,
        DiscardedMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: DiscardedDescription);

    // Diagnostic for variables that are never disposed
    private static readonly LocalizableString UndisposedTitle =
        "IDisposable variable is never disposed";

    private static readonly LocalizableString UndisposedMessageFormat =
        "Variable '{0}' of type '{1}' is never disposed{2}";

    private static readonly LocalizableString UndisposedDescription =
        "Variables holding IDisposable instances must be disposed before going out of scope. " +
        "Use a 'using' statement/declaration or call Dispose() explicitly.";

    private static readonly DiagnosticDescriptor UndisposedRule = new DiagnosticDescriptor(
        DiagnosticId,
        UndisposedTitle,
        UndisposedMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: UndisposedDescription);

    // Diagnostic for reassignment without disposing first
    private static readonly LocalizableString ReassignmentTitle =
        "IDisposable reassigned without disposing previous value";

    private static readonly LocalizableString ReassignmentMessageFormat =
        "Variable '{0}' is reassigned without disposing the previous '{1}' value{2}";

    private static readonly LocalizableString ReassignmentDescription =
        "When reassigning a variable that holds an IDisposable, the previous value must be disposed first " +
        "to prevent resource leaks.";

    private static readonly DiagnosticDescriptor ReassignmentRule = new DiagnosticDescriptor(
        DiagnosticId,
        ReassignmentTitle,
        ReassignmentMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: ReassignmentDescription);

    /// <summary>
    /// Critical Typhon types that MUST be disposed. Maps type name to consequence description.
    /// Only these types are tracked - general IDisposable is not tracked to avoid false positives.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> CriticalTypes =
        ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, string>("Typhon.Engine.ChunkAccessor", " - causes page cache deadlock"),
            new KeyValuePair<string, string>("Typhon.Engine.Transaction", " - causes uncommitted changes and resource leak"),
            new KeyValuePair<string, string>("Typhon.Engine.PageAccessor", " - causes page lock leak"),
        });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiscardedRule, UndisposedRule, ReassignmentRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Use operation block analysis to track disposables across the entire method body
        context.RegisterOperationBlockAction(AnalyzeOperationBlock);
    }

    private static void AnalyzeOperationBlock(OperationBlockAnalysisContext context)
    {
        // Only analyze method bodies
        if (context.OwningSymbol is not IMethodSymbol)
            return;

        // Track state for each local variable holding a disposable
        var tracker = new DisposableTracker(context.Compilation);

        foreach (var block in context.OperationBlocks)
        {
            AnalyzeOperationsRecursively(block, tracker, context);
        }

        // Report any variables that were never disposed
        foreach (var undisposed in tracker.GetUndisposedVariables())
        {
            var consequence = GetConsequenceMessage(undisposed.Type);
            var diagnostic = Diagnostic.Create(
                UndisposedRule,
                undisposed.Location,
                undisposed.Name,
                undisposed.TypeName,
                consequence);
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void AnalyzeOperationsRecursively(
        IOperation operation,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        switch (operation)
        {
            case IUsingOperation usingOp:
                // using statement - resources are automatically disposed
                // Process body but don't track the declared variable
                if (usingOp.Body != null)
                {
                    AnalyzeOperationsRecursively(usingOp.Body, tracker, context);
                }
                return; // Don't recurse into children normally - we handled the body

            case IUsingDeclarationOperation:
                // using declaration - automatically disposed at end of scope
                return;

            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                {
                    AnalyzeVariableDeclaration(decl, tracker, context);
                }
                break;

            case IExpressionStatementOperation exprStmt:
                // Check for discarded disposable results (e.g., Method();)
                AnalyzeExpressionStatement(exprStmt, tracker, context);
                break;

            case ISimpleAssignmentOperation assignment:
                AnalyzeAssignment(assignment, tracker, context);
                break;

            case IInvocationOperation invocation:
                // Check if this is a Dispose() call
                AnalyzeInvocation(invocation, tracker);
                break;

            case IReturnOperation returnOp:
                // Returning a disposable transfers ownership to caller
                AnalyzeReturn(returnOp, tracker);
                break;

            case IFieldReferenceOperation fieldRef when IsAssignmentTarget(fieldRef):
                // Assigning to a field transfers ownership to containing object
                AnalyzeFieldAssignment(fieldRef, tracker);
                break;

            case IPropertyReferenceOperation propRef when IsAssignmentTarget(propRef):
                // Assigning to a property transfers ownership
                AnalyzePropertyAssignment(propRef, tracker);
                break;

            case IArgumentOperation argument:
                // Passing to out/ref parameter or to a method that takes ownership
                AnalyzeArgument(argument, tracker);
                break;
        }

        // Recurse into children
        foreach (var child in operation.ChildOperations)
        {
            AnalyzeOperationsRecursively(child, tracker, context);
        }
    }

    private static void AnalyzeVariableDeclaration(
        IVariableDeclarationOperation declaration,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        foreach (var declarator in declaration.Declarators)
        {
            if (declarator.Initializer?.Value == null)
                continue;

            var initialValue = declarator.Initializer.Value;
            var local = declarator.Symbol;

            // Check if the type implements IDisposable
            if (!IsDisposableType(local.Type, context.Compilation))
                continue;

            // Check if this is inside a using declaration
            if (IsInsideUsingDeclaration(declarator))
                continue;

            // Skip traversal patterns - these are not new allocations
            // e.g., var cur = Head; or var cur = node.Next;
            if (IsTraversalPattern(initialValue))
                continue;

            // Track this variable
            tracker.TrackVariable(local, initialValue, declarator.Syntax.GetLocation());
        }
    }

    private static void AnalyzeExpressionStatement(
        IExpressionStatementOperation exprStmt,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        var expr = exprStmt.Operation;

        // Unwrap conversion
        while (expr is IConversionOperation conversion)
        {
            expr = conversion.Operand;
        }

        // Check for invocation with discarded result
        if (expr is IInvocationOperation invocation)
        {
            var returnType = invocation.TargetMethod.ReturnType;
            if (IsDisposableType(returnType, context.Compilation))
            {
                var consequence = GetConsequenceMessage(returnType);
                var diagnostic = Diagnostic.Create(
                    DiscardedRule,
                    exprStmt.Syntax.GetLocation(),
                    invocation.TargetMethod.Name,
                    returnType.Name,
                    consequence);
                context.ReportDiagnostic(diagnostic);
            }
        }

        // Check for explicit discard: _ = Method();
        if (expr is ISimpleAssignmentOperation assignment &&
            assignment.Target is IDiscardOperation)
        {
            var value = assignment.Value;
            while (value is IConversionOperation conv)
            {
                value = conv.Operand;
            }

            if (value is IInvocationOperation discardedInvocation)
            {
                var returnType = discardedInvocation.TargetMethod.ReturnType;
                if (IsDisposableType(returnType, context.Compilation))
                {
                    var consequence = GetConsequenceMessage(returnType);
                    var diagnostic = Diagnostic.Create(
                        DiscardedRule,
                        exprStmt.Syntax.GetLocation(),
                        discardedInvocation.TargetMethod.Name,
                        returnType.Name,
                        consequence);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }

    private static void AnalyzeAssignment(
        ISimpleAssignmentOperation assignment,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        // Only handle assignments to local variables
        if (assignment.Target is not ILocalReferenceOperation localRef)
            return;

        var local = localRef.Local;

        // Check if we're assigning a disposable
        if (!IsDisposableType(local.Type, context.Compilation))
            return;

        // Skip traversal patterns - these are not new allocations
        // e.g., cur = cur.Next; or cur = node.Previous;
        if (IsTraversalPattern(assignment.Value))
        {
            // For traversal, just stop tracking - we don't own the value
            tracker.MarkOwnershipTransferred(local);
            return;
        }

        // Check if this variable already holds an undisposed value
        if (tracker.IsTrackedAndUndisposed(local))
        {
            var consequence = GetConsequenceMessage(local.Type);
            var diagnostic = Diagnostic.Create(
                ReassignmentRule,
                assignment.Syntax.GetLocation(),
                local.Name,
                local.Type.Name,
                consequence);
            context.ReportDiagnostic(diagnostic);
        }

        // Track the new value
        tracker.TrackVariable(local, assignment.Value, assignment.Syntax.GetLocation());
    }

    private static void AnalyzeInvocation(IInvocationOperation invocation, DisposableTracker tracker)
    {
        // Check if this is a Dispose() call on a tracked variable
        if (invocation.TargetMethod.Name != "Dispose")
            return;

        if (invocation.Instance is ILocalReferenceOperation localRef)
        {
            tracker.MarkDisposed(localRef.Local);
        }

        // Also check for null-conditional: x?.Dispose()
        if (invocation.Instance is IConditionalAccessInstanceOperation)
        {
            // The parent should be a conditional access operation
            var parent = invocation.Parent;
            while (parent != null)
            {
                if (parent is IConditionalAccessOperation conditionalAccess &&
                    conditionalAccess.Operation is ILocalReferenceOperation condLocalRef)
                {
                    tracker.MarkDisposed(condLocalRef.Local);
                    break;
                }
                parent = parent.Parent;
            }
        }
    }

    private static void AnalyzeReturn(IReturnOperation returnOp, DisposableTracker tracker)
    {
        if (returnOp.ReturnedValue == null)
            return;

        var value = returnOp.ReturnedValue;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        // Returning a local transfers ownership to caller
        if (value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzeFieldAssignment(IFieldReferenceOperation fieldRef, DisposableTracker tracker)
    {
        // Find the assignment operation
        var parent = fieldRef.Parent;
        if (parent is ISimpleAssignmentOperation assignment && assignment.Value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzePropertyAssignment(IPropertyReferenceOperation propRef, DisposableTracker tracker)
    {
        // Find the assignment operation
        var parent = propRef.Parent;
        if (parent is ISimpleAssignmentOperation assignment && assignment.Value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzeArgument(IArgumentOperation argument, DisposableTracker tracker)
    {
        // Passing to out/ref transfers ownership
        if (argument.Parameter?.RefKind is RefKind.Out or RefKind.Ref)
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
        }

        // Check for [TransfersOwnership] attribute on the parameter
        if (argument.Parameter != null && HasTransfersOwnershipAttribute(argument.Parameter))
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
            return;
        }

        // Passing to a constructor parameter - assume ownership transfer (conservative)
        if (argument.Parent is IObjectCreationOperation)
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
        }
    }

    /// <summary>
    /// Checks if a parameter has the [TransfersOwnership] attribute.
    /// </summary>
    private static bool HasTransfersOwnershipAttribute(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            var attrName = attribute.AttributeClass?.Name;
            if (attrName == "TransfersOwnershipAttribute" || attrName == "TransfersOwnership")
            {
                // Verify it's from Typhon.Engine namespace
                var ns = attribute.AttributeClass?.ContainingNamespace?.ToDisplayString();
                if (ns == "Typhon.Engine" || ns == "global::Typhon.Engine")
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsAssignmentTarget(IOperation operation)
    {
        return operation.Parent is ISimpleAssignmentOperation assignment &&
               assignment.Target == operation;
    }

    private static bool IsInsideUsingDeclaration(IVariableDeclaratorOperation declarator)
    {
        // Check if this declarator is part of a using declaration
        var syntax = declarator.Syntax;
        while (syntax != null)
        {
            if (syntax is UsingStatementSyntax)
                return true;
            if (syntax is LocalDeclarationStatementSyntax localDecl && localDecl.UsingKeyword != default)
                return true;
            syntax = syntax.Parent;
        }
        return false;
    }

    /// <summary>
    /// Detects traversal patterns where a value is obtained from a property/field access
    /// rather than being newly created. These patterns don't represent new allocations
    /// and the caller doesn't own the value.
    /// Examples: var cur = Head; cur = cur.Next; var t = _transaction;
    /// </summary>
    private static bool IsTraversalPattern(IOperation operation)
    {
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion)
        {
            unwrapped = conversion.Operand;
        }

        // Property access (e.g., node.Next, Head, Tail)
        if (unwrapped is IPropertyReferenceOperation)
            return true;

        // Field access (e.g., _head, _transaction)
        if (unwrapped is IFieldReferenceOperation)
            return true;

        // Local variable reference (e.g., var x = existingVar;)
        if (unwrapped is ILocalReferenceOperation)
            return true;

        // Parameter reference (e.g., var x = paramTransaction;)
        if (unwrapped is IParameterReferenceOperation)
            return true;

        // Array/indexer access (e.g., var x = transactions[i];)
        if (unwrapped is IArrayElementReferenceOperation)
            return true;

        // Unsafe.AsRef calls - these are reference casts, not new allocations
        // e.g., ref var owner = ref Unsafe.AsRef<ChunkAccessor>(_ownerPtr);
        if (unwrapped is IInvocationOperation invocation)
        {
            var methodName = invocation.TargetMethod.Name;
            var typeName = invocation.TargetMethod.ContainingType?.Name;
            if (typeName == "Unsafe" && (methodName == "AsRef" || methodName == "As" || methodName == "NullRef"))
                return true;
        }

        return false;
    }

    private static bool IsDisposableType(ITypeSymbol type, Compilation compilation)
    {
        if (type == null)
            return false;

        // ONLY track critical Typhon types to avoid false positives
        // General IDisposable tracking causes too many false positives in complex codebases
        var fullName = GetFullTypeName(type);
        return CriticalTypes.ContainsKey(fullName);
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

    private static string GetConsequenceMessage(ITypeSymbol type)
    {
        var fullName = GetFullTypeName(type);
        return CriticalTypes.TryGetValue(fullName, out var consequence) ? consequence : "";
    }

    /// <summary>
    /// Tracks the disposal state of local variables within a method.
    /// </summary>
    private sealed class DisposableTracker
    {
        private readonly Compilation _compilation;
        private readonly Dictionary<ILocalSymbol, TrackedVariable> _variables;

        public DisposableTracker(Compilation compilation)
        {
            _compilation = compilation;
            _variables = new Dictionary<ILocalSymbol, TrackedVariable>(SymbolEqualityComparer.Default);
        }

        public void TrackVariable(ILocalSymbol local, IOperation initialValue, Location location)
        {
            _variables[local] = new TrackedVariable(local, initialValue, location);
        }

        public bool IsTrackedAndUndisposed(ILocalSymbol local)
        {
            return _variables.TryGetValue(local, out var tracked) &&
                   tracked.State == DisposalState.Tracked;
        }

        public void MarkDisposed(ILocalSymbol local)
        {
            if (_variables.TryGetValue(local, out var tracked))
            {
                tracked.State = DisposalState.Disposed;
            }
        }

        public void MarkOwnershipTransferred(ILocalSymbol local)
        {
            if (_variables.TryGetValue(local, out var tracked))
            {
                tracked.State = DisposalState.OwnershipTransferred;
            }
        }

        public IEnumerable<UndisposedInfo> GetUndisposedVariables()
        {
            foreach (var kvp in _variables)
            {
                if (kvp.Value.State == DisposalState.Tracked)
                {
                    yield return new UndisposedInfo(
                        kvp.Key.Name,
                        kvp.Key.Type.Name,
                        kvp.Key.Type,
                        kvp.Value.Location);
                }
            }
        }

        private enum DisposalState
        {
            Tracked,
            Disposed,
            OwnershipTransferred
        }

        private sealed class TrackedVariable
        {
            public ILocalSymbol Local { get; }
            public IOperation InitialValue { get; }
            public Location Location { get; }
            public DisposalState State { get; set; }

            public TrackedVariable(ILocalSymbol local, IOperation initialValue, Location location)
            {
                Local = local;
                InitialValue = initialValue;
                Location = location;
                State = DisposalState.Tracked;
            }
        }
    }

    public readonly struct UndisposedInfo
    {
        public string Name { get; }
        public string TypeName { get; }
        public ITypeSymbol Type { get; }
        public Location Location { get; }

        public UndisposedInfo(string name, string typeName, ITypeSymbol type, Location location)
        {
            Name = name;
            TypeName = typeName;
            Type = type;
            Location = location;
        }
    }
}
