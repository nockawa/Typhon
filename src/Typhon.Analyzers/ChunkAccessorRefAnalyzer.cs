using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that enforces EpochChunkAccessor parameters must always be passed by ref.
/// This is a large struct designed for zero-allocation, and passing by value causes expensive
/// stack copies that defeat its performance design.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ChunkAccessorRefAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON001";
    private const string Category = "Performance";

    private static readonly LocalizableString Title =
        "EpochChunkAccessor must be passed by ref";

    private static readonly LocalizableString MessageFormat =
        "Parameter '{0}' of type '{1}' must be passed by ref (not 'in' or by value)";

    private static readonly LocalizableString Description =
        "EpochChunkAccessor is a large struct with mutating methods. " +
        "It must be passed by 'ref' only. The 'in' modifier causes defensive copies when calling " +
        "non-readonly methods, defeating the performance design. Passing by value creates expensive stack copies.";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,  // Make it an error, not just a warning
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Register analysis for method declarations
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.DelegateDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.LocalFunctionStatement);
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
    {
        // Get the parameter list from various method-like declarations
        var parameterList = context.Node switch
        {
            MethodDeclarationSyntax method => method.ParameterList,
            ConstructorDeclarationSyntax constructor => constructor.ParameterList,
            DelegateDeclarationSyntax delegateDecl => delegateDecl.ParameterList,
            LocalFunctionStatementSyntax localFunc => localFunc.ParameterList,
            _ => null
        };

        if (parameterList == null)
            return;

        foreach (var parameter in parameterList.Parameters)
        {
            // Get the type symbol for this parameter
            var typeSymbol = context.SemanticModel.GetTypeInfo(parameter.Type!).Type;

            if (typeSymbol == null)
                continue;

            // Check if the type is EpochChunkAccessor (match by name and namespace)
            if (IsChunkAccessorType(typeSymbol))
            {
                // Check if the parameter has 'ref' modifier (ONLY ref is acceptable)
                var hasRefModifier = false;

                foreach (var modifier in parameter.Modifiers)
                {
                    if (modifier.IsKind(SyntaxKind.RefKeyword))
                    {
                        hasRefModifier = true;
                        break;
                    }
                }

                // Report error if not using ref (this includes 'in', 'out', or no modifier)
                if (!hasRefModifier)
                {
                    var diagnostic = Diagnostic.Create(
                        Rule,
                        parameter.GetLocation(),
                        parameter.Identifier.Text,
                        typeSymbol.Name);

                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }

    private static bool IsChunkAccessorType(ITypeSymbol typeSymbol)
    {
        // Match by name and ensure it's in the Typhon.Engine namespace
        if (typeSymbol.Name != "EpochChunkAccessor")
            return false;

        // Check namespace - handle both with and without global prefix
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        return ns == "Typhon.Engine" || ns == "global::Typhon.Engine";
    }
}
