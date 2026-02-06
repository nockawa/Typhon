using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects early return statements in Dispose() methods that skip disposal
/// of critical fields.
/// 
/// This catches bugs like:
/// <code>
/// public void Dispose()
/// {
///     if (!IsValid)
///     {
///         return;  // BUG: Skips _accessor.Dispose() below!
///     }
///     // ... other code ...
///     _accessor.Dispose();
/// }
/// </code>
/// 
/// The fix is to ensure critical fields are disposed before any early return:
/// <code>
/// public void Dispose()
/// {
///     if (!IsValid)
///     {
///         _accessor.Dispose();  // Always dispose!
///         return;
///     }
///     // ...
/// }
/// </code>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DisposeEarlyReturnAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON007";
    private const string Category = "Reliability";

    private static readonly LocalizableString Title =
        "Early return in Dispose() skips disposal of critical field";

    private static readonly LocalizableString MessageFormat =
        "Early return in Dispose() of '{0}' may skip disposal of field '{1}'{2}";

    private static readonly LocalizableString Description =
        "Early return statements in Dispose() methods must not skip disposal of critical fields " +
        "(ChunkAccessor, Transaction, PageAccessor). Either dispose the field before returning, " +
        "or restructure the code to ensure all paths dispose all critical fields.";

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

        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
    {
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;

        // Only analyze Dispose() methods
        if (methodDeclaration.Identifier.Text != "Dispose")
            return;

        // Must have no parameters (standard Dispose pattern)
        if (methodDeclaration.ParameterList.Parameters.Count != 0)
            return;

        // Must have a body
        var body = methodDeclaration.Body;
        if (body == null)
            return;

        // Get the containing type
        var containingType = context.SemanticModel.GetDeclaredSymbol(methodDeclaration)?.ContainingType;
        if (containingType == null)
            return;

        // Find all critical fields in the type
        var criticalFields = FindCriticalFields(containingType);
        if (criticalFields.Count == 0)
            return;

        // Find all return statements in the method
        var returnStatements = body.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        if (returnStatements.Count == 0)
            return;

        // For each return statement, check if it's an "early return" that might skip disposal
        foreach (var returnStatement in returnStatements)
        {
            // Skip the final return at the end of the method (if it's the last statement)
            if (IsLastStatementInMethod(returnStatement, body))
                continue;

            // Check which critical fields are disposed BEFORE this return statement
            var disposedBeforeReturn = GetFieldsDisposedBefore(returnStatement, body, criticalFields);

            // Report any fields that are NOT disposed before this early return
            foreach (var (field, _, consequence) in criticalFields)
            {
                if (!disposedBeforeReturn.Contains(field.Name))
                {
                    var diagnostic = Diagnostic.Create(
                        Rule,
                        returnStatement.GetLocation(),
                        containingType.Name,
                        field.Name,
                        consequence);

                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }

    private static List<(IFieldSymbol Field, string TypeFullName, string Consequence)> FindCriticalFields(INamedTypeSymbol type)
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

            // Skip ref fields - these are borrowed references, not owned resources
            // e.g., "ref ChunkAccessor _accessor" in ref structs
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

    /// <summary>
    /// Checks if a return statement is the last statement in the method body.
    /// A return at the very end of a method is not an "early return".
    /// </summary>
    private static bool IsLastStatementInMethod(ReturnStatementSyntax returnStatement, BlockSyntax methodBody)
    {
        // Get the last statement in the method body
        var lastStatement = methodBody.Statements.LastOrDefault();
        if (lastStatement == null)
            return false;

        // If the return IS the last statement, it's not early
        if (lastStatement == returnStatement)
            return true;

        // Check if return is inside an if/else at the end that covers all paths
        // This is a simplified check - we just see if the return's parent block
        // ends at or near the method body's end
        var returnParent = returnStatement.Parent;
        while (returnParent != null && returnParent != methodBody)
        {
            if (returnParent == lastStatement)
                return true;
            returnParent = returnParent.Parent;
        }

        return false;
    }

    /// <summary>
    /// Gets the names of critical fields that are disposed BEFORE the given return statement
    /// in the control flow of the method.
    /// </summary>
    private static HashSet<string> GetFieldsDisposedBefore(
        ReturnStatementSyntax returnStatement,
        BlockSyntax methodBody,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        var disposedFields = new HashSet<string>();

        // Get the text position of the return statement
        var returnPosition = returnStatement.SpanStart;

        // Find the block that directly contains this return (could be inside an if)
        var containingBlock = returnStatement.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (containingBlock == null)
            return disposedFields;

        // Look for Dispose() calls on critical fields that appear BEFORE the return
        // in the same block or in parent blocks (lexically before)
        foreach (var (field, _, _) in criticalFields)
        {
            if (IsFieldDisposedBeforeReturn(field.Name, returnStatement, containingBlock, methodBody))
            {
                disposedFields.Add(field.Name);
            }
        }

        return disposedFields;
    }

    /// <summary>
    /// Checks if a field's Dispose() is called before the return statement in the control flow.
    /// </summary>
    private static bool IsFieldDisposedBeforeReturn(
        string fieldName,
        ReturnStatementSyntax returnStatement,
        BlockSyntax containingBlock,
        BlockSyntax methodBody)
    {
        // Get all statements that come before the return in the containing block
        var statementsBeforeReturn = new List<StatementSyntax>();
        
        foreach (var statement in containingBlock.Statements)
        {
            if (statement.SpanStart >= returnStatement.SpanStart)
                break;
            statementsBeforeReturn.Add(statement);
        }

        // Check if any of these statements dispose the field
        foreach (var statement in statementsBeforeReturn)
        {
            var statementText = statement.ToString();
            
            // Check for direct disposal patterns
            if (statementText.Contains($"{fieldName}.Dispose()") ||
                statementText.Contains($"{fieldName}?.Dispose()"))
            {
                return true;
            }
        }

        // Also check parent blocks if we're in a nested block (e.g., inside an if)
        if (containingBlock != methodBody)
        {
            var parentBlock = containingBlock.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            if (parentBlock != null)
            {
                // Check statements in parent block that come before the containing if/block
                var containingStatement = containingBlock.Ancestors().OfType<StatementSyntax>()
                    .FirstOrDefault(s => s.Parent == parentBlock);
                
                if (containingStatement != null)
                {
                    foreach (var statement in parentBlock.Statements)
                    {
                        if (statement.SpanStart >= containingStatement.SpanStart)
                            break;
                        
                        var statementText = statement.ToString();
                        if (statementText.Contains($"{fieldName}.Dispose()") ||
                            statementText.Contains($"{fieldName}?.Dispose()"))
                        {
                            return true;
                        }
                    }
                }
            }
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
}
