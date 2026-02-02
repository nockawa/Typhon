using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Typhon.Analyzers;

/// <summary>
/// Code fix provider that offers fixes for undisposed IDisposable instances:
/// 1. Convert to using declaration: "using var x = Method();"
/// 2. Add Dispose() call at end of scope
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DisposableNotDisposedCodeFixProvider)), Shared]
public class DisposableNotDisposedCodeFixProvider : CodeFixProvider
{
    private const string AddUsingDeclarationTitle = "Add 'using' declaration";
    private const string AddDisposeCallTitle = "Add Dispose() call";

    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DisposableNotDisposedAnalyzer.DiagnosticId);

    public sealed override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if (root == null)
            return;

        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        // Find the node at the diagnostic location
        var node = root.FindNode(diagnosticSpan);

        // Try to find a local declaration statement (for "variable never disposed" case)
        var localDeclaration = node.AncestorsAndSelf()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault();

        if (localDeclaration != null)
        {
            // Offer to add 'using' keyword
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: AddUsingDeclarationTitle,
                    createChangedDocument: c => AddUsingDeclarationAsync(context.Document, localDeclaration, c),
                    equivalenceKey: AddUsingDeclarationTitle),
                diagnostic);

            // Offer to add Dispose() call at end of block
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: AddDisposeCallTitle,
                    createChangedDocument: c => AddDisposeCallAsync(context.Document, localDeclaration, c),
                    equivalenceKey: AddDisposeCallTitle),
                diagnostic);

            return;
        }

        // Try to find an expression statement (for "discarded result" case)
        var expressionStatement = node.AncestorsAndSelf()
            .OfType<ExpressionStatementSyntax>()
            .FirstOrDefault();

        if (expressionStatement != null)
        {
            // Offer to wrap in using declaration
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: AddUsingDeclarationTitle,
                    createChangedDocument: c => WrapInUsingDeclarationAsync(context.Document, expressionStatement, c),
                    equivalenceKey: AddUsingDeclarationTitle),
                diagnostic);
        }
    }

    /// <summary>
    /// Converts "var x = Method();" to "using var x = Method();"
    /// </summary>
    private static async Task<Document> AddUsingDeclarationAsync(
        Document document,
        LocalDeclarationStatementSyntax localDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        // Create new declaration with 'using' keyword
        var usingKeyword = SyntaxFactory.Token(SyntaxKind.UsingKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newDeclaration = localDeclaration
            .WithUsingKeyword(usingKeyword)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(localDeclaration, newDeclaration);
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Adds a Dispose() call at the end of the containing block.
    /// </summary>
    private static async Task<Document> AddDisposeCallAsync(
        Document document,
        LocalDeclarationStatementSyntax localDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        // Get the variable name
        var variableName = localDeclaration.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
        if (variableName == null)
            return document;

        // Find the containing block
        var containingBlock = localDeclaration.Ancestors()
            .OfType<BlockSyntax>()
            .FirstOrDefault();

        if (containingBlock == null)
            return document;

        // Create the Dispose() call statement
        var disposeCall = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(variableName),
                    SyntaxFactory.IdentifierName("Dispose"))))
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Find the position to insert - at the end of the block, before any return statement
        var statements = containingBlock.Statements;
        var insertIndex = statements.Count;

        // If the last statement is a return, insert before it
        if (statements.Count > 0 && statements.Last() is ReturnStatementSyntax)
        {
            insertIndex = statements.Count - 1;
        }

        // Insert the Dispose() call
        var newStatements = statements.Insert(insertIndex, disposeCall);
        var newBlock = containingBlock.WithStatements(newStatements);

        var newRoot = root.ReplaceNode(containingBlock, newBlock);
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Converts "Method();" to "using var temp = Method();"
    /// </summary>
    private static async Task<Document> WrapInUsingDeclarationAsync(
        Document document,
        ExpressionStatementSyntax expressionStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        // Extract the invocation expression
        var expression = expressionStatement.Expression;

        // Handle discard pattern: _ = Method();
        if (expression is AssignmentExpressionSyntax assignment &&
            assignment.Left is IdentifierNameSyntax id &&
            id.Identifier.Text == "_")
        {
            expression = assignment.Right;
        }

        // Generate a variable name based on the method name or type
        var variableName = GenerateVariableName(expression);

        // Create: using var variableName = expression;
        var usingDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            default,
            default,
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(variableName).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithInitializer(
                        SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())
                            .WithEqualsToken(
                                SyntaxFactory.Token(SyntaxKind.EqualsToken)
                                    .WithLeadingTrivia(SyntaxFactory.Space)
                                    .WithTrailingTrivia(SyntaxFactory.Space))))),
            SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(expressionStatement.GetLeadingTrivia())
            .WithTrailingTrivia(expressionStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(expressionStatement, usingDeclaration);
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Generates a suitable variable name based on the expression.
    /// </summary>
    private static string GenerateVariableName(ExpressionSyntax expression)
    {
        // Try to get name from method call
        if (expression is InvocationExpressionSyntax invocation)
        {
            string methodName = null;

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                methodName = memberAccess.Name.Identifier.Text;
            }
            else if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                methodName = identifier.Identifier.Text;
            }

            if (methodName != null)
            {
                // Convert CreateTransaction -> transaction
                // GetAccessor -> accessor
                if (methodName.StartsWith("Create"))
                    return ToCamelCase(methodName.Substring(6));
                if (methodName.StartsWith("Get"))
                    return ToCamelCase(methodName.Substring(3));
                if (methodName.StartsWith("Open"))
                    return ToCamelCase(methodName.Substring(4));

                return ToCamelCase(methodName);
            }
        }

        // Default fallback
        return "disposable";
    }

    /// <summary>
    /// Converts a string to camelCase.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "disposable";

        // Handle single character
        if (name.Length == 1)
            return name.ToLowerInvariant();

        // Convert first character to lowercase
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
