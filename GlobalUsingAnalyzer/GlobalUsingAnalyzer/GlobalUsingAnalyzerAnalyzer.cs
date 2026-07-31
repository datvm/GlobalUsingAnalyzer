using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Reports diagnostics so code fixes can relocate usings:
/// <list type="bullet">
/// <item><see cref="DiagnosticId"/> (GUA001) — C# <c>using</c> directives.</item>
/// <item><see cref="RazorUsingDiagnosticId"/> (GUA003) — Razor <c>@using</c> in <c>.cshtml</c> /
/// <c>.razor</c>. Discovery via AdditionalFiles and/or disk scan under the project directory
/// (VSIX). Reported from a compilation action onto a real C# host tree so the IDE Error List
/// and C# code-fix pipeline can surface them.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GlobalUsingAnalyzerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "GUA001";

    public const string RazorUsingDiagnosticId = "GUA003";

    public const string UsingIdentityProperty = "UsingIdentity";

    public const string RazorSourcePathProperty = "RazorSourcePath";

    public const string CshtmlPathProperty = RazorSourcePathProperty;

    public const string GlobalUsingsFileName = "ZGlobalUsings.cs";

    private static readonly LocalizableString Title = new LocalizableResourceString(
        nameof(Resources.AnalyzerTitle), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
        nameof(Resources.AnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString Description = new LocalizableResourceString(
        nameof(Resources.AnalyzerDescription), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString RazorTitle = new LocalizableResourceString(
        nameof(Resources.RazorUsingAnalyzerTitle), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString RazorMessageFormat = new LocalizableResourceString(
        nameof(Resources.RazorUsingAnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString RazorDescription = new LocalizableResourceString(
        nameof(Resources.RazorUsingAnalyzerDescription), Resources.ResourceManager, typeof(Resources));

    private const string Category = "Style";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    // Warning so Error List shows it without enabling "Messages" (Info is filtered by default).
    private static readonly DiagnosticDescriptor RazorUsingRule = new(
        RazorUsingDiagnosticId,
        RazorTitle,
        RazorMessageFormat,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: RazorDescription);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule, RazorUsingRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);

        // GUA003: once per compilation. SyntaxTreeAnalysisContext has no Compilation API, so
        // we cannot drive this from RegisterSyntaxTreeAction. CompilationAction runs in the IDE
        // and at build time, and has full access to Options + Compilation for discovery.
        context.RegisterCompilationAction(AnalyzeCompilationForRazor);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;

        if (!IsPromotableUsing(usingDirective))
        {
            return;
        }

        var identity = UsingSpec.FromSyntax(usingDirective).Identity;
        var diagnostic = Diagnostic.Create(Rule, usingDirective.GetLocation(), identity);
        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeCompilationForRazor(CompilationAnalysisContext context)
    {
        try
        {
            ReportRazorUsings(
                context.Compilation,
                context.Options,
                context.CancellationToken,
                context.ReportDiagnostic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Never let disk/IO failures take down the whole analyzer host.
        }
    }

    private static void ReportRazorUsings(
        Compilation compilation,
        AnalyzerOptions options,
        CancellationToken cancellationToken,
        Action<Diagnostic> report)
    {
        if (compilation == null)
        {
            return;
        }

        var hostTree = PickHostSyntaxTree(compilation);
        if (hostTree == null)
        {
            return;
        }

        var sources = RazorSourceDiscovery.GetAnalyzableSources(options, compilation, cancellationToken);
        if (sources.IsDefaultOrEmpty)
        {
            return;
        }

        // Host C# location is kept as an additional location so Error List can still navigate
        // into the owning project. Primary must be the real .razor/.cshtml path so
        // "Current Document" / file association work when that file is open.
        // (The .razor editor lightbulb is provided separately by the VSIX suggested-action MEF part;
        // a C# CodeFixProvider never appears in the Razor language lightbulb.)
        var hostRoot = hostTree.GetRoot(cancellationToken);
        var hostLocation = hostRoot.GetFirstToken(includeZeroWidth: true).GetLocation();
        if (hostLocation.SourceTree == null)
        {
            hostLocation = Location.Create(hostTree, new TextSpan(0, 0));
        }

        var importsFiles = RazorSourceDiscovery.GetImportsFiles(options, compilation, cancellationToken);
        var importsContents = importsFiles
            .Select(f => new KeyValuePair<string, string>(f.Path, f.Content))
            .ToList();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = source.Path;
            if (RazorUsingEditor.IsImportsFile(path)
                || !RazorUsingEditor.IsAnalyzableRazorSourceFile(path))
            {
                continue;
            }

            var text = source.Text;
            var inheritedUsings = RazorUsingEditor.CollectInheritedUsings(path, importsContents);
            var shortName = Path.GetFileName(path);

            foreach (var occurrence in RazorUsingEditor.EnumerateUsings(text.ToString()))
            {
                if (string.IsNullOrEmpty(occurrence.Spec.Include))
                {
                    continue;
                }

                if (inheritedUsings.Contains(occurrence.Spec))
                {
                    continue;
                }

                var span = occurrence.Span;
                if (span.End > text.Length)
                {
                    span = TextSpan.FromBounds(span.Start, text.Length);
                }

                var lineSpan = text.Lines.GetLinePositionSpan(span);
                var razorLocation = Location.Create(path, span, lineSpan);
                var identity = occurrence.Spec.Identity;

                var properties = ImmutableDictionary<string, string>.Empty
                    .Add(UsingIdentityProperty, identity)
                    .Add(RazorSourcePathProperty, path);

                var diagnostic = Diagnostic.Create(
                    RazorUsingRule,
                    razorLocation,
                    additionalLocations: ImmutableArray.Create(hostLocation),
                    properties: properties,
                    messageArgs: new object[] { identity, shortName });

                report(diagnostic);
            }
        }
    }

    private static SyntaxTree PickHostSyntaxTree(Compilation compilation)
    {
        SyntaxTree fallback = null;

        // Prefer real project sources over generated/obj trees so project-dir discovery
        // (walk-up to .csproj) lands on the correct project in multi-project Blazor apps.
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGeneratedOrRazorCompilerTree(tree.FilePath))
            {
                if (fallback == null)
                {
                    fallback = tree;
                }

                continue;
            }

            if (IsGlobalUsingsFile(tree.FilePath))
            {
                return tree;
            }

            if (!string.IsNullOrEmpty(tree.FilePath) && Path.IsPathRooted(tree.FilePath))
            {
                return tree;
            }

            if (fallback == null)
            {
                fallback = tree;
            }
        }

        return fallback ?? compilation.SyntaxTrees.FirstOrDefault();
    }

    private static bool IsGeneratedOrRazorCompilerTree(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        return path.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("RazorSourceGenerator", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf(".g.cs", StringComparison.OrdinalIgnoreCase) >= 0
            || path.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cshtml.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPromotableUsing(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.Name != null;
    }

    public static string GetUsingIdentity(UsingDirectiveSyntax usingDirective) =>
        UsingSpec.FromSyntax(usingDirective).Identity;

    public static IEnumerable<string> GetUsingIdentitiesFromText(string sourceText)
    {
        return GetUsingSpecsFromText(sourceText).Select(s => s.Identity);
    }

    public static IEnumerable<UsingSpec> GetUsingSpecsFromText(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            yield break;
        }

        var root = CSharpSyntaxTree.ParseText(sourceText).GetCompilationUnitRoot();
        foreach (var usingDirective in root.Usings)
        {
            if (usingDirective.Name == null)
            {
                continue;
            }

            yield return UsingSpec.FromSyntax(usingDirective);
        }
    }

    public static string ToGlobalUsingLine(UsingDirectiveSyntax usingDirective) =>
        UsingSpec.FromSyntax(usingDirective).ToGlobalUsingLine();

    public static string ToGlobalUsingLine(string identity) =>
        $"global using {identity};";

    public static bool IsGlobalUsingsFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(filePath),
            GlobalUsingsFileName,
            StringComparison.OrdinalIgnoreCase);
    }
}
