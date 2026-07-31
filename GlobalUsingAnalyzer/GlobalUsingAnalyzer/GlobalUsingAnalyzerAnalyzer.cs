using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.IO;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Reports a diagnostic on ordinary <c>using Namespace;</c> directives so a code fix
/// can offer to promote them to <c>global using</c> in ZGlobalUsings.cs.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GlobalUsingAnalyzerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "GUA001";

    /// <summary>File name where promoted global usings are collected.</summary>
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

        // Fire once per using directive syntax node (using X; / using static X; / global using X;).
        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;

        // Only ordinary "using Namespace;" — skip "using static", aliases, and global usings.
        if (!IsOrdinaryUsing(usingDirective))
        {
            return;
        }

        // Don't flag usings that already live in the destination file.
        if (IsGlobalUsingsFile(usingDirective.SyntaxTree.FilePath))
        {
            return;
        }

        // Location is the whole using line so the lightbulb appears when the caret is on it.
        // The namespace name is passed as the diagnostic message argument ({0}).
        var namespaceName = usingDirective.Name?.ToString() ?? string.Empty;
        var diagnostic = Diagnostic.Create(Rule, usingDirective.GetLocation(), namespaceName);
        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>
    /// True for plain <c>using N;</c>. False for static, alias, or global usings.
    /// </summary>
    public static bool IsOrdinaryUsing(UsingDirectiveSyntax usingDirective)
    {
        // using static System.Math;
        if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            return false;
        }

        // using IO = System.IO;
        if (usingDirective.Alias != null)
        {
            return false;
        }

        // global using System;  (C# 10+)
        if (usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
        {
            return false;
        }

        // Must have a name (defensive — malformed trees).
        return usingDirective.Name != null;
    }

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
