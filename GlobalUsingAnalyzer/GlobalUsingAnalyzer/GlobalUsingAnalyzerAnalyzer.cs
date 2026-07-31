using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Reports a diagnostic on using directives (plain, static, alias, or global) so code fixes
/// can move them to ZGlobalUsings.cs and/or the project file's <c>&lt;Using /&gt;</c> items.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GlobalUsingAnalyzerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "GUA001";

    /// <summary>File name where promoted global usings are collected (source form).</summary>
    public const string GlobalUsingsFileName = "ZGlobalUsings.cs";

    private static readonly LocalizableString Title = new LocalizableResourceString(
        nameof(Resources.AnalyzerTitle), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
        nameof(Resources.AnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));

    private static readonly LocalizableString Description = new LocalizableResourceString(
        nameof(Resources.AnalyzerDescription), Resources.ResourceManager, typeof(Resources));

    private const string Category = "Style";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        // Info = mild suggestion (not a warning/error). Still enables the lightbulb.
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        // Don't analyze generated code (e.g. *.g.cs from source generators).
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        // Analyzer callbacks may run in parallel for different trees.
        context.EnableConcurrentExecution();

        // Fire once per using directive (using X; / using static X; / using A = B; / global using …).
        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;

        if (!IsPromotableUsing(usingDirective))
        {
            return;
        }

        // Note: usings inside ZGlobalUsings.cs are still reported so the ".csproj <Using />"
        // fix can move them. The ZGlobalUsings.cs fix simply won't register for those nodes.

        // Location is the whole using line so the lightbulb appears when the caret is on it.
        var identity = UsingSpec.FromSyntax(usingDirective).Identity;
        var diagnostic = Diagnostic.Create(Rule, usingDirective.GetLocation(), identity);
        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>
    /// True for any well-formed using we can relocate: plain, static, alias, or global.
    /// </summary>
    public static bool IsPromotableUsing(UsingDirectiveSyntax usingDirective)
    {
        // Must have a name (defensive — malformed trees).
        return usingDirective.Name != null;
    }

    /// <summary>
    /// Canonical identity for diagnostics (delegates to <see cref="UsingSpec"/>).
    /// </summary>
    public static string GetUsingIdentity(UsingDirectiveSyntax usingDirective) =>
        UsingSpec.FromSyntax(usingDirective).Identity;

    /// <summary>
    /// Parses source with the C# compiler and returns the identity of every top-level using.
    /// </summary>
    public static IEnumerable<string> GetUsingIdentitiesFromText(string sourceText)
    {
        return GetUsingSpecsFromText(sourceText).Select(s => s.Identity);
    }

    /// <summary>
    /// Parses source with the C# compiler and returns structured specs for every top-level using.
    /// </summary>
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
