using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typhon.Generators;

/// <summary>
/// Source generator that attributes every <c>TyphonEvent.BeginXxx(...)</c> call site to a deterministic
/// <c>ushort</c> id and emits per-call-site <c>[InterceptsLocation]</c> wrappers that forward to the matching
/// <c>BeginXxxWithSiteId</c> overload, baking the literal id into the IL.
///
/// Design: see `claude/design/observability/09-profiler-source-attribution.md`.
///
/// Output:
///   - One generated source unit holding all interceptor methods (file-local class).
///   - One generated source unit holding the static <c>SourceLocations</c> table (id → file/line/method/kind).
///
/// Determinism: discovered call sites are sorted by <c>(filePath, line, column)</c> before id assignment, so
/// every build of the same source produces a byte-identical generated table.
///
/// Scope: factories that have a corresponding <c>BeginXxxWithSiteId</c> overload on <c>TyphonEvent</c> are
/// intercepted. Factories without that overload are left alone (they fall through to the default zero siteId
/// pass-through path); a build info diagnostic is emitted with attributed/skipped counts so a regression in
/// coverage is loud.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class SourceLocationGenerator : IIncrementalGenerator
{
    private const string TyphonEventFqn = "Typhon.Engine.Profiler.TyphonEvent";
    private const string GeneratedNamespace = "Typhon.Generators.Generated";
    /// <summary>
    /// Per design Q3 (claude/design/observability/09-profiler-source-attribution.md §4.1):
    /// the generator's interceptor scope is Typhon.Engine *only*. Other consumers (tests,
    /// tools) pay through siteId=0 ("unknown source"). The SourceLocations table is also
    /// engine-only; tests can read it via the engine's assembly.
    /// </summary>
    private const string TargetAssemblyName = "Typhon.Engine";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Repo root from MSBuild — used to rewrite absolute paths to "/_/..." form (matches the design's
        // PathMap convention; the C# compiler's PathMap doesn't affect SyntaxTree.FilePath, so we do it here).
        var repoRoot = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
        {
            return provider.GlobalOptions.TryGetValue("build_property.TyphonRepoRoot", out var v) ? v : "";
        });

        // Discover candidate invocations: any TyphonEvent.BeginXxx(...) call.
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidateInvocation(node),
            transform: TryExtractCallSite
        ).Where(static c => c != null);

        // Cross-reference TyphonEvent's known WithSiteId overloads (so we only intercept where the runtime
        // actually has a matching factory). The compilation provider gives us the type symbol once.
        // Also gates on assembly name — interceptors are emitted into Typhon.Engine only (Q3).
        var typhonEventInfo = context.CompilationProvider.Select(static (compilation, _) =>
        {
            // Engine-only scope: skip emission entirely if this isn't the Typhon.Engine compilation.
            if (compilation.AssemblyName != TargetAssemblyName)
            {
                return ImmutableHashSet<string>.Empty;
            }
            var type = compilation.GetTypeByMetadataName(TyphonEventFqn);
            if (type == null)
            {
                return ImmutableHashSet<string>.Empty;
            }
            // Walk every public-static method on TyphonEvent, collect those ending with "WithSiteId".
            // The "BeginXxx" we care about intercepting is the method whose name is the WithSiteId one
            // minus the "WithSiteId" suffix.
            var begins = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol m
                    && m.IsStatic
                    && m.DeclaredAccessibility == Accessibility.Public
                    && m.Name.StartsWith("Begin", StringComparison.Ordinal)
                    && m.Name.EndsWith("WithSiteId", StringComparison.Ordinal))
                {
                    var baseName = m.Name.Substring(0, m.Name.Length - "WithSiteId".Length);
                    begins.Add(baseName);
                }
            }
            return begins.ToImmutable();
        });

        // Combine: emit interceptors for the call sites whose BaseName matches a WithSiteId factory.
        // Also bring in repo root so we can strip absolute paths down to /_/-prefixed repo-relative form.
        var combined = candidates.Combine(typhonEventInfo).Collect().Combine(repoRoot);

        context.RegisterSourceOutput(combined, static (spc, input) =>
        {
            var (sites, repoRootValue) = input;
            EmitOutputs(spc, sites, repoRootValue);
        });
    }

    // ───────────────────────────────────────────────────────────────────────
    // Discovery
    // ───────────────────────────────────────────────────────────────────────

    private static bool IsCandidateInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }
        // Cheap textual prefilter — `TyphonEvent.Begin*(...)` appears as MemberAccess `<expr>.BeginXxx`.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }
        return memberAccess.Name.Identifier.Text.StartsWith("Begin", StringComparison.Ordinal);
    }

    private sealed class CallSite
    {
        public InterceptableLocation Location { get; }
        public string BaseName { get; }
        public string ReturnTypeFqn { get; }
        public ImmutableArray<ParamInfo> Parameters { get; }
        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }
        public string MethodName { get; }

        public CallSite(InterceptableLocation location, string baseName, string returnTypeFqn, ImmutableArray<ParamInfo> parameters, string filePath, int line, int column, string methodName)
        {
            Location = location;
            BaseName = baseName;
            ReturnTypeFqn = returnTypeFqn;
            Parameters = parameters;
            FilePath = filePath;
            Line = line;
            Column = column;
            MethodName = methodName;
        }
    }

    private readonly struct ParamInfo
    {
        public string TypeFqn { get; }
        public string Name { get; }
        public ParamInfo(string typeFqn, string name)
        {
            TypeFqn = typeFqn;
            Name = name;
        }
    }

    private static CallSite TryExtractCallSite(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(invocation, ct);
        if (symbolInfo.Symbol is not IMethodSymbol method)
        {
            return null;
        }

        var containingType = method.ContainingType;
        if (containingType == null || containingType.ToDisplayString() != TyphonEventFqn)
        {
            return null;
        }
        if (!method.Name.StartsWith("Begin", StringComparison.Ordinal))
        {
            return null;
        }
        // Skip the WithSiteId overloads themselves — only the original Begin* variants are interception targets.
        if (method.Name.EndsWith("WithSiteId", StringComparison.Ordinal))
        {
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location == null)
        {
            return null;
        }

        // Resolve the containing-method name for the generated table (free, since we have the syntax).
        var enclosingMethod = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        var methodName = enclosingMethod?.Identifier.Text ?? "<unknown>";

        var span = invocation.GetLocation().GetLineSpan();

        // Capture the method's actual signature: return type + parameter list. The interceptor must match
        // the original signature exactly (same return type, same params), so we never guess from the name.
        var returnTypeFqn = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var paramsBuilder = ImmutableArray.CreateBuilder<ParamInfo>(method.Parameters.Length);
        foreach (var p in method.Parameters)
        {
            paramsBuilder.Add(new ParamInfo(
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                p.Name));
        }

        return new CallSite(
            location: location,
            baseName: method.Name,
            returnTypeFqn: returnTypeFqn,
            parameters: paramsBuilder.ToImmutable(),
            filePath: span.Path ?? "<unknown>",
            line: span.StartLinePosition.Line + 1,         // GetLineSpan is 0-based; we surface 1-based to humans.
            column: span.StartLinePosition.Character + 1,
            methodName: methodName);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Emit
    // ───────────────────────────────────────────────────────────────────────

    private static void EmitOutputs(
        SourceProductionContext spc,
        ImmutableArray<(CallSite site, ImmutableHashSet<string> withSiteIdBaseNames)> input,
        string repoRoot)
    {
        if (input.IsDefaultOrEmpty)
        {
            return;
        }

        // Collect attributable sites (have a matching WithSiteId factory).
        var attributable = new List<CallSite>();
        var skippedCount = 0;
        var withSiteIdSet = input[0].withSiteIdBaseNames; // Same across all entries.

        // Engine-only scope (Q3): if this isn't the Typhon.Engine compilation, emit no source files at all.
        // The compilation provider returns an empty hashset for foreign assemblies. Without this gate, the
        // generator would emit an *empty* SourceLocations class into the test compilation, shadowing the real
        // one from Typhon.Engine.dll and breaking any test that reads the table.
        if (withSiteIdSet.IsEmpty)
        {
            return;
        }

        foreach (var (site, _) in input)
        {
            if (site == null)
            {
                continue;
            }
            if (withSiteIdSet.Contains(site.BaseName))
            {
                attributable.Add(NormalizeFilePath(site, repoRoot));
            }
            else
            {
                skippedCount++;
            }
        }

        // Deterministic order: sort by (filePath, line, column).
        attributable.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (c != 0) return c;
            c = a.Line.CompareTo(b.Line);
            if (c != 0) return c;
            return a.Column.CompareTo(b.Column);
        });

        // Cap enforcement: ushort max is 65535, id 0 is reserved for "unknown source".
        if (attributable.Count > 65535)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "TPH9002",
                    "Too many TyphonEvent emission sites",
                    $"SourceLocationGenerator: {attributable.Count} sites exceed the 65535 ushort cap; id width must be widened.",
                    "Typhon.Profiler",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                Location.None));
            return;
        }

        // Emit interceptor file.
        var interceptorSource = BuildInterceptorSource(attributable);
        spc.AddSource("SourceLocations.Interceptors.g.cs", interceptorSource);

        // Emit static SourceLocations table.
        var tableSource = BuildSourceLocationsTable(attributable);
        spc.AddSource("SourceLocations.g.cs", tableSource);

        // Build-time summary diagnostic — loud if a regression cuts attribution coverage.
        spc.ReportDiagnostic(Diagnostic.Create(
            new DiagnosticDescriptor(
                "TPH9000",
                "SourceLocationGenerator summary",
                $"SourceLocationGenerator: {attributable.Count} sites attributed, {skippedCount} skipped (no WithSiteId factory).",
                "Typhon.Profiler",
                DiagnosticSeverity.Info,
                isEnabledByDefault: true),
            Location.None));
    }

    private static string BuildInterceptorSource(List<CallSite> sites)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> SourceLocationGenerator — interceptor wrappers.");
        sb.AppendLine("// Each method redirects a `TyphonEvent.Begin*` call site to the matching `BeginXxxWithSiteId`,");
        sb.AppendLine("// passing a literal ushort siteId baked into the IL by the C# 14 interceptors feature.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"namespace {GeneratedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine("    file static class TyphonEventInterceptors");
        sb.AppendLine("    {");

        for (int i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var siteId = (ushort)(i + 1); // ids start at 1; 0 = unknown.

            // Build the parameter list for the interceptor (must exactly match the original factory's signature).
            var paramSb = new StringBuilder();
            for (int p = 0; p < site.Parameters.Length; p++)
            {
                if (p > 0) paramSb.Append(", ");
                paramSb.Append(site.Parameters[p].TypeFqn).Append(' ').Append(site.Parameters[p].Name);
            }
            // Build the forwarded argument list for the WithSiteId call: literal siteId, then the param names.
            var forwardSb = new StringBuilder();
            forwardSb.Append("0x").Append(siteId.ToString("X4"));
            foreach (var p in site.Parameters)
            {
                forwardSb.Append(", ").Append(p.Name);
            }

            sb.Append("        ");
            sb.AppendLine(site.Location.GetInterceptsLocationAttributeSyntax());
            sb.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.Append("        public static ")
              .Append(site.ReturnTypeFqn)
              .Append(" Intercepted_")
              .Append(site.BaseName)
              .Append("_0x")
              .Append(siteId.ToString("X4"))
              .Append('(')
              .Append(paramSb)
              .AppendLine(")");
            sb.AppendLine("        {");
            sb.Append("            return global::Typhon.Engine.Profiler.TyphonEvent.")
              .Append(site.BaseName)
              .Append("WithSiteId(")
              .Append(forwardSb)
              .AppendLine(");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSourceLocationsTable(List<CallSite> sites)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> SourceLocationGenerator — id → (file, line, method, kind) table.");
        sb.AppendLine("// Wire-format manifest emitters dump these arrays straight to the trace stream / file / cache.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine("namespace Typhon.Engine.Profiler.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Compile-time source-location table built by SourceLocationGenerator.");
        sb.AppendLine("    /// Indexed by the ushort siteId carried in span records (when SpanFlags bit 1 is set).");
        sb.AppendLine("    /// Public so wire-format manifest emitters in Typhon.Profiler and tests in Typhon.Engine.Tests can read it.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class SourceLocations");
        sb.AppendLine("    {");

        // File table (string[] indexed by FileId starting at 0).
        var files = sites.Select(s => s.FilePath).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var fileIdMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < files.Count; i++)
        {
            fileIdMap[files[i]] = i;
        }

        sb.AppendLine("        public static readonly string[] Files = new string[]");
        sb.AppendLine("        {");
        foreach (var f in files)
        {
            sb.Append("            ");
            sb.Append(EncodeStringLiteral(f));
            sb.AppendLine(",");
        }
        sb.AppendLine("        };");
        sb.AppendLine();

        // Entries: id → (fileId, line, method, kind-byte derived from base name).
        sb.AppendLine("        public readonly record struct Entry(ushort Id, ushort FileId, int Line, string Method, byte KindByte);");
        sb.AppendLine();
        sb.AppendLine("        public static readonly Entry[] All = new Entry[]");
        sb.AppendLine("        {");
        for (int i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var siteId = (ushort)(i + 1);
            var fileId = (ushort)fileIdMap[site.FilePath];
            // KindByte is derived from BaseName: BeginBTreeInsert → "BTreeInsert" → matches enum member name.
            // We don't reference the enum at gen time (would couple Typhon.Generators to Typhon.Profiler);
            // we emit the bare name and let a runtime helper resolve to the byte if needed. For v1 we leave
            // this 0 and rely on the wire's existing kind byte for decoding; the table just carries the name.
            sb.Append("            new Entry(0x")
              .Append(siteId.ToString("X4"))
              .Append(", ")
              .Append(fileId)
              .Append(", ")
              .Append(site.Line)
              .Append(", ")
              .Append(EncodeStringLiteral(site.MethodName))
              .Append(", 0)")
              .AppendLine(",");
        }
        sb.AppendLine("        };");

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EncodeStringLiteral(string value)
    {
        if (value == null)
        {
            return "null";
        }
        // SymbolDisplay covers escaping for both regular and verbatim strings.
        return SymbolDisplay.FormatLiteral(value, quote: true);
    }

    /// <summary>
    /// Rewrite absolute file paths from <c>SyntaxTree.FilePath</c> into the design's repo-relative
    /// "/_/..." form, using the <c>TyphonRepoRoot</c> MSBuild property as the prefix to strip.
    /// Backslashes are normalized to forward slashes so the manifest is platform-agnostic.
    /// If the repo root isn't configured or the path doesn't sit under it, the path is left unchanged.
    /// </summary>
    private static CallSite NormalizeFilePath(CallSite site, string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return site;
        }
        var path = site.FilePath;
        // Tolerate trailing-slash variations and case differences on Windows filesystems.
        if (path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path.Substring(repoRoot.Length).Replace('\\', '/').TrimStart('/');
            path = "/_/" + rel;
        }
        else
        {
            // Already mapped (e.g., compiler PathMap kicked in) or external — only normalize separators.
            path = path.Replace('\\', '/');
        }
        return new CallSite(site.Location, site.BaseName, site.ReturnTypeFqn, site.Parameters, path, site.Line, site.Column, site.MethodName);
    }
}
