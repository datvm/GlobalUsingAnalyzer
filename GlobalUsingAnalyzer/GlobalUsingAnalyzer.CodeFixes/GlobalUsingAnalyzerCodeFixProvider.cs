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
/// Lightbulb fixes for GUA001:
/// <list type="number">
/// <item>Move to the .csproj as <c>&lt;Using /&gt;</c> (preferred when a .csproj is available).</item>
/// <item>Move to ZGlobalUsings.cs as <c>global using</c>.</item>
/// </list>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GlobalUsingAnalyzerCodeFixProvider)), Shared]
public partial class GlobalUsingAnalyzerCodeFixProvider : CodeFixProvider
{
    /// <summary>Equivalence key for the ZGlobalUsings.cs destination (Fix All groups on this).</summary>
    public const string EquivalenceKeyZGlobalUsings = nameof(CodeFixResources.CodeFixTitle);

    /// <summary>Equivalence key for the .csproj <c>&lt;Using /&gt;</c> destination.</summary>
    public const string EquivalenceKeyCsproj = nameof(CodeFixResources.CodeFixTitleCsproj);

    // Keep old name as alias so existing tests/docs that referenced EquivalenceKey still compile if any remain.
    public const string EquivalenceKey = EquivalenceKeyZGlobalUsings;

    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(GlobalUsingAnalyzerAnalyzer.DiagnosticId);

    /// <summary>
    /// One Fix All provider that dispatches on <see cref="FixAllContext.CodeActionEquivalenceKey"/>
    /// so each lightbulb option has its own Fix all in document/project/solution.
    /// </summary>
    public sealed override FixAllProvider GetFixAllProvider() =>
        MoveUsingsFixAllProvider.Instance;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var usingDirective = root.FindToken(diagnosticSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<UsingDirectiveSyntax>()
            .FirstOrDefault();

        if (usingDirective == null || !GlobalUsingAnalyzerAnalyzer.IsPromotableUsing(usingDirective))
        {
            return;
        }

        var document = context.Document;
        var alreadyInZGlobalUsings = GlobalUsingAnalyzerAnalyzer.IsGlobalUsingsFile(
            document.FilePath ?? document.Name);

        // --- Option 1 (preferred / listed first): move into .csproj <Using /> ---
        // Custom CodeAction: can write .csproj on disk when VS does not track it as a document.
        if (IsCsprojProject(document.Project))
        {
            context.RegisterCodeFix(
                new MoveToCsprojCodeAction(
                    title: CodeFixResources.CodeFixTitleCsproj,
                    equivalenceKey: EquivalenceKeyCsproj,
                    originalSolution: document.Project.Solution,
                    diagnostics: ImmutableArray.Create(diagnostic)),
                diagnostic);
        }

        // --- Option 2: move into ZGlobalUsings.cs (not offered when already there) ---
        if (!alreadyInZGlobalUsings)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitle,
                    createChangedSolution: ct => ApplyToZGlobalUsingsAsync(
                        document.Project.Solution,
                        ImmutableArray.Create(diagnostic),
                        ct),
                    equivalenceKey: EquivalenceKeyZGlobalUsings),
                diagnostic);
        }
    }

    /// <summary>
    /// Remove usings from source files and ensure each identity appears once in ZGlobalUsings.cs.
    /// </summary>
    internal static async Task<Solution> ApplyToZGlobalUsingsAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var (removalsByDocument, specsByProject) = await CollectAsync(solution, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        solution = await ApplyRemovalsAsync(solution, removalsByDocument, cancellationToken)
            .ConfigureAwait(false);

        foreach (var pair in specsByProject)
        {
            solution = await AddSpecsToGlobalUsingsFileAsync(
                solution,
                pair.Key,
                pair.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return solution;
    }

    /// <summary>
    /// Result of preparing a move-to-csproj fix: C# solution edits plus optional on-disk .csproj writes.
    /// </summary>
    internal readonly struct CsprojApplyResult
    {
        public CsprojApplyResult(
            Solution solution,
            ImmutableArray<CsprojDiskWrite> diskWrites)
        {
            Solution = solution;
            DiskWrites = diskWrites;
        }

        public Solution Solution { get; }

        public ImmutableArray<CsprojDiskWrite> DiskWrites { get; }
    }

    internal readonly struct CsprojDiskWrite
    {
        public CsprojDiskWrite(string filePath, string newText)
        {
            FilePath = filePath;
            NewText = newText;
        }

        public string FilePath { get; }

        public string NewText { get; }
    }

    /// <summary>
    /// Shared by <see cref="MoveToCsprojCodeAction"/> and unit tests.
    /// Removes usings from C# files; updates .csproj via workspace when tracked, otherwise
    /// returns disk writes (never AddAdditionalDocument — VS rejects that for project files).
    /// </summary>
    internal static async Task<CsprojApplyResult> ComputeCsprojApplyResultAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var (removalsByDocument, specsByProject) = await CollectAsync(solution, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        solution = await ApplyRemovalsAsync(solution, removalsByDocument, cancellationToken)
            .ConfigureAwait(false);

        var diskWrites = ImmutableArray.CreateBuilder<CsprojDiskWrite>();

        foreach (var pair in specsByProject)
        {
            var project = solution.GetProject(pair.Key);
            if (project == null
                || pair.Value.Count == 0
                || !IsCsprojProject(project))
            {
                continue;
            }

            var loaded = await ProjectFileDocumentHelper.TryGetProjectFileTextAsync(
                solution, pair.Key, cancellationToken).ConfigureAwait(false);

            if (loaded == null)
            {
                continue;
            }

            var originalText = loaded.Value.text;
            var updatedText = ProjectFileUsingEditor.AddUsings(originalText, pair.Value);

            if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            {
                continue;
            }

            if (loaded.Value.isTrackedInWorkspace)
            {
                solution = ProjectFileDocumentHelper.WithProjectFileText(solution, pair.Key, updatedText);
            }
            else
            {
                diskWrites.Add(new CsprojDiskWrite(project.FilePath, updatedText));
            }
        }

        return new CsprojApplyResult(solution, diskWrites.ToImmutable());
    }

    /// <summary>
    /// Test-friendly path: applies C# + workspace-tracked csproj edits only
    /// (disk writes are applied by folding them into additional documents when present).
    /// </summary>
    internal static async Task<Solution> ApplyToCsprojAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await ComputeCsprojApplyResultAsync(solution, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        solution = result.Solution;

        // Unit-test hosts often expose the .csproj as an additional document after SolutionTransforms;
        // if Compute left disk writes but the document is findable now, apply them to the solution.
        foreach (var diskWrite in result.DiskWrites)
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.FilePath, diskWrite.FilePath, StringComparison.OrdinalIgnoreCase));

            if (project != null
                && ProjectFileDocumentHelper.FindProjectFileDocument(solution, diskWrite.FilePath) != null)
            {
                solution = ProjectFileDocumentHelper.WithProjectFileText(
                    solution, project.Id, diskWrite.NewText);
            }
            else if (project != null)
            {
                // Last resort for tests: attach as additional document (never do this in VS apply path).
                var fileName = Path.GetFileName(diskWrite.FilePath);
                var added = project.AddAdditionalDocument(
                    fileName, diskWrite.NewText, filePath: diskWrite.FilePath);
                solution = added.Project.Solution;
            }
        }

        return solution;
    }

    private static async Task<(
        Dictionary<DocumentId, List<UsingDirectiveSyntax>> Removals,
        Dictionary<ProjectId, List<UsingSpec>> SpecsByProject)> CollectAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var removalsByDocument = new Dictionary<DocumentId, List<UsingDirectiveSyntax>>();
        var specsByProject = new Dictionary<ProjectId, List<UsingSpec>>();

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

            if (usingDirective == null || !GlobalUsingAnalyzerAnalyzer.IsPromotableUsing(usingDirective))
            {
                continue;
            }

            var spec = UsingSpec.FromSyntax(usingDirective);

            if (!removalsByDocument.TryGetValue(document.Id, out var list))
            {
                list = new List<UsingDirectiveSyntax>();
                removalsByDocument[document.Id] = list;
            }

            list.Add(usingDirective);

            if (!specsByProject.TryGetValue(document.Project.Id, out var specs))
            {
                specs = new List<UsingSpec>();
                specsByProject[document.Project.Id] = specs;
            }

            if (!specs.Any(s => s.Equals(spec)))
            {
                specs.Add(spec);
            }
        }

        return (removalsByDocument, specsByProject);
    }

    private static async Task<Solution> ApplyRemovalsAsync(
        Solution solution,
        Dictionary<DocumentId, List<UsingDirectiveSyntax>> removalsByDocument,
        CancellationToken cancellationToken)
    {
        foreach (var pair in removalsByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            if (document == null)
            {
                continue;
            }

            // Removals were collected against an earlier snapshot; re-bind by span on the current tree.
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                continue;
            }

            var nodesToRemove = new List<UsingDirectiveSyntax>();
            foreach (var original in pair.Value)
            {
                var current = root.FindNode(original.Span, findInsideTrivia: false, getInnermostNodeForTie: true)
                    as UsingDirectiveSyntax
                    ?? root.FindToken(original.Span.Start).Parent?
                        .AncestorsAndSelf()
                        .OfType<UsingDirectiveSyntax>()
                        .FirstOrDefault();

                if (current != null)
                {
                    nodesToRemove.Add(current);
                }
            }

            if (nodesToRemove.Count == 0)
            {
                continue;
            }

            var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);
            solution = solution.WithDocumentSyntaxRoot(pair.Key, newRoot);
        }

        return solution;
    }

    private static async Task<Solution> AddSpecsToGlobalUsingsFileAsync(
        Solution solution,
        ProjectId projectId,
        IReadOnlyList<UsingSpec> specs,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null || specs.Count == 0)
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

        var existingSpecs = new HashSet<UsingSpec>(
            GlobalUsingAnalyzerAnalyzer.GetUsingSpecsFromText(existing));

        var linesToAdd = new List<string>();
        foreach (var spec in specs)
        {
            if (!existingSpecs.Add(spec))
            {
                continue;
            }

            linesToAdd.Add(spec.ToGlobalUsingLine());
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

    /// <summary>True when this Roslyn project is backed by a <c>.csproj</c> on disk.</summary>
    internal static bool IsCsprojProject(Project project)
    {
        if (project == null || string.IsNullOrEmpty(project.FilePath))
        {
            return false;
        }

        return project.FilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }
}
