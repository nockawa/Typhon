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

namespace Typhon.Analyzers;

/// <summary>
/// Code fix provider that automatically adds 'ref' modifier to ChunkAccessor parameters.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ChunkAccessorRefCodeFixProvider)), Shared]
public class ChunkAccessorRefCodeFixProvider : CodeFixProvider
{
    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(ChunkAccessorRefAnalyzer.DiagnosticId);

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

        // Find the parameter syntax node
        var parameter = root.FindToken(diagnosticSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<ParameterSyntax>()
            .First();

        if (parameter == null)
            return;

        // Register a code action that will change to 'ref' modifier
        var title = HasInModifier(parameter) ? "Replace 'in' with 'ref'" : "Add 'ref' modifier";

        context.RegisterCodeFix(
            CodeAction.Create(
                title: title,
                createChangedDocument: c => ChangeToRefModifierAsync(context.Document, parameter, c),
                equivalenceKey: nameof(ChunkAccessorRefCodeFixProvider)),
            diagnostic);
    }

    private static bool HasInModifier(ParameterSyntax parameter)
    {
        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.InKeyword))
                return true;
        }
        return false;
    }

    private static async Task<Document> ChangeToRefModifierAsync(
        Document document,
        ParameterSyntax parameter,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        ParameterSyntax newParameter;

        // Check if 'in' modifier exists and needs to be replaced
        var hasInModifier = false;
        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.InKeyword))
            {
                hasInModifier = true;
                break;
            }
        }

        if (hasInModifier)
        {
            // Replace 'in' with 'ref'
            var newModifiers = SyntaxFactory.TokenList();
            foreach (var modifier in parameter.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.InKeyword))
                {
                    // Replace 'in' with 'ref', preserving trivia
                    var refKeyword = SyntaxFactory.Token(
                        modifier.LeadingTrivia,
                        SyntaxKind.RefKeyword,
                        modifier.TrailingTrivia);
                    newModifiers = newModifiers.Add(refKeyword);
                }
                else
                {
                    newModifiers = newModifiers.Add(modifier);
                }
            }
            newParameter = parameter.WithModifiers(newModifiers);
        }
        else
        {
            // Add 'ref' modifier
            var refKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);
            newParameter = parameter.WithModifiers(parameter.Modifiers.Add(refKeyword));
        }

        // Replace the old parameter with the new one
        var newRoot = root.ReplaceNode(parameter, newParameter);
        return document.WithSyntaxRoot(newRoot);
    }
}
