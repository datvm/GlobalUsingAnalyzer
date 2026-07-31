using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Lightbulb fix for GUA001: remove a local <c>using N;</c> and add
/// <c>global using N;</c> to ZGlobalUsings.cs at the project root (creating the file if needed).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GlobalUsingAnalyzerCodeFixProvider)), Shared]
public partial class GlobalUsingAnalyzerCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// Shared equivalence key so Fix All groups every "move to global using" action together.
    /// (If keys differ, the IDE treats them as different fixes and won't batch them.)
    /// </summary>
    public const string EquivalenceKey = nameof(CodeFixResources.CodeFixTitle);

    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(GlobalUsingAnalyzerAnalyzer.DiagnosticId);

    /// <summary>
    /// Enables the lightbulb submenu:
    /// Fix all in document / project / solution.
    ///
    /// We cannot use <see cref="WellKnownFixAllProviders.BatchFixer"/> here: that helper
    /// runs each single fix against the *original* solution and merges text edits. Every
    /// fix writes the same ZGlobalUsings.cs file, so later edits overwrite earlier ones.
    /// A custom provider collects all namespaces first, then writes the file once.
    /// </summary>
    public sealed override FixAllProvider GetFixAllProvider() =>
        MoveToGlobalUsingsFixAllProvider.Instance;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        // Walk from the token under the diagnostic up to the UsingDirectiveSyntax node.
        var usingDirective = root.FindToken(diagnosticSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<UsingDirectiveSyntax>()
            .FirstOrDefault();

        if (usingDirective == null || !GlobalUsingAnalyzerAnalyzer.IsOrdinaryUsing(usingDirective))
        {
            return;
        }

        // Title shown in the lightbulb menu (from CodeFixResources.resx).
        context.RegisterCodeFix(
            CodeAction.Create(
                title: CodeFixResources.CodeFixTitle,
                // Single occurrence: reuse the same bulk helper with one diagnostic.
                createChangedSolution: ct => ApplyAsync(
                    context.Document.Project.Solution,
                    ImmutableArray.Create(diagnostic),
                    ct),
                equivalenceKey: EquivalenceKey),
            diagnostic);
    }

    /// <summary>
    /// Shared implementation for single fix and Fix All.
    ///
    /// Algorithm:
    /// 1. Map each diagnostic → (document, using node, namespace name).
    /// 2. Per document, remove all matching using nodes in one tree rewrite.
    /// 3. Per project, merge the namespace names into that project's ZGlobalUsings.cs once.
    /// </summary>
    internal static async Task<Solution> ApplyAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // --- Collect work items (skip anything we cannot resolve to a using) ---
        var removalsByDocument = new Dictionary<DocumentId, List<UsingDirectiveSyntax>>();
        // projectId → ordered, unique namespace names to add
        var namespacesByProject = new Dictionary<ProjectId, List<string>>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Id != GlobalUsingAnalyzerAnalyzer.DiagnosticId)
            {
                continue;
            }

            var document = solution.GetDocument(diagnostic.Location.SourceTree);
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                continue;
            }

            var usingDirective = root.FindToken(diagnostic.Location.SourceSpan.Start)
                .Parent?
                .AncestorsAndSelf()
                .OfType<UsingDirectiveSyntax>()
                .FirstOrDefault();

            if (usingDirective == null || !GlobalUsingAnalyzerAnalyzer.IsOrdinaryUsing(usingDirective))
            {
                continue;
            }

            var namespaceName = usingDirective.Name.ToString();

            if (!removalsByDocument.TryGetValue(document.Id, out var list))
            {
                list = new List<UsingDirectiveSyntax>();
                removalsByDocument[document.Id] = list;
            }

            list.Add(usingDirective);

            if (!namespacesByProject.TryGetValue(document.Project.Id, out var names))
            {
                names = new List<string>();
                namespacesByProject[document.Project.Id] = names;
            }

            // Preserve first-seen order; skip duplicates (same using in many files).
            if (!names.Contains(namespaceName, StringComparer.Ordinal))
            {
                names.Add(namespaceName);
            }
        }

        // --- Step A: remove local usings (one rewrite per document) ---
        foreach (var pair in removalsByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                continue;
            }

            // RemoveNodes handles multiple nodes safely (BatchFixer would not coordinate this).
            var newRoot = root.RemoveNodes(pair.Value, SyntaxRemoveOptions.KeepNoTrivia);
            solution = solution.WithDocumentSyntaxRoot(pair.Key, newRoot);
        }

        // --- Step B: update each project's ZGlobalUsings.cs once ---
        foreach (var pair in namespacesByProject)
        {
            solution = await AddNamespacesToGlobalUsingsFileAsync(
                solution,
                pair.Key,
                pair.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return solution;
    }

    private static async Task<Solution> AddNamespacesToGlobalUsingsFileAsync(
        Solution solution,
        ProjectId projectId,
        IReadOnlyList<string> namespaceNames,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null || namespaceNames.Count == 0)
        {
            return solution;
        }

        var globalUsingsDocument = FindGlobalUsingsDocument(project);
        var existing = string.Empty;

        if (globalUsingsDocument != null)
        {
            var text = await globalUsingsDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            existing = text.ToString();
        }

        // Only append namespaces not already present as global usings.
        var linesToAdd = new List<string>();
        foreach (var name in namespaceNames)
        {
            if (!ContainsGlobalUsing(existing, name)
                && !linesToAdd.Any(l => string.Equals(ExtractNamespaceFromGlobalUsingLine(l), name, StringComparison.Ordinal)))
            {
                linesToAdd.Add($"global using {name};");
            }
        }

        if (linesToAdd.Count == 0)
        {
            return solution;
        }

        var addition = string.Join(Environment.NewLine, linesToAdd) + Environment.NewLine;
        var newContent = string.IsNullOrWhiteSpace(existing)
            ? addition
            : existing.TrimEnd() + Environment.NewLine + addition;

        if (globalUsingsDocument == null)
        {
            var filePath = GetGlobalUsingsFilePath(project);
            var added = project.AddDocument(
                name: GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                text: newContent,
                folders: null,
                filePath: filePath);

            return added.Project.Solution;
        }

        return solution.WithDocumentText(globalUsingsDocument.Id, SourceText.From(newContent));
    }

    private static Document FindGlobalUsingsDocument(Project project)
    {
        return project.Documents.FirstOrDefault(d =>
        {
            // Prefer FilePath (real path on disk); fall back to Name (used in unit tests).
            if (GlobalUsingAnalyzerAnalyzer.IsGlobalUsingsFile(d.FilePath))
            {
                return true;
            }

            return string.Equals(
                d.Name,
                GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string GetGlobalUsingsFilePath(Project project)
    {
        // project.FilePath is the .csproj path when available (IDE). Unit-test workspaces often leave it null.
        if (!string.IsNullOrEmpty(project.FilePath))
        {
            var directory = Path.GetDirectoryName(project.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                return Path.Combine(directory, GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName);
            }
        }

        return GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName;
    }

    /// <summary>
    /// Detects an existing <c>global using N;</c> (tolerant of extra spaces).
    /// </summary>
    private static bool ContainsGlobalUsing(string fileContent, string namespaceName)
    {
        using (var reader = new StringReader(fileContent))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var name = ExtractNamespaceFromGlobalUsingLine(line);
                if (string.Equals(name, namespaceName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractNamespaceFromGlobalUsingLine(string line)
    {
        var trimmed = line.Trim();

        // Strip optional trailing comment.
        var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            trimmed = trimmed.Substring(0, commentIndex).TrimEnd();
        }

        if (!trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            return null;
        }

        // Expect tokens: global using Some.Namespace
        var withoutSemi = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
        var parts = withoutSemi.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && parts[0] == "global"
            && parts[1] == "using")
        {
            return string.Concat(parts.Skip(2));
        }

        return null;
    }
}
