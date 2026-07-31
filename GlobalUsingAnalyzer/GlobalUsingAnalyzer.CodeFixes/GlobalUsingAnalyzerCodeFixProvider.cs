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
/// Lightbulb fixes for GUA001 and GUA003:
/// <list type="bullet">
/// <item>GUA001 (C# <c>using</c>): move to .csproj <c>&lt;Using /&gt;</c> and/or ZGlobalUsings.cs.</item>
/// <item>GUA003 (Razor <c>@using</c>): <c>.cshtml</c> → nearest <c>_ViewImports.cshtml</c>;
/// <c>.razor</c> → nearest <c>_Imports.razor</c>.</item>
/// </list>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GlobalUsingAnalyzerCodeFixProvider)), Shared]
public partial class GlobalUsingAnalyzerCodeFixProvider : CodeFixProvider
{
    /// <summary>Equivalence key for the ZGlobalUsings.cs destination (Fix All groups on this).</summary>
    public const string EquivalenceKeyZGlobalUsings = nameof(CodeFixResources.CodeFixTitle);

    /// <summary>Equivalence key for the .csproj <c>&lt;Using /&gt;</c> destination.</summary>
    public const string EquivalenceKeyCsproj = nameof(CodeFixResources.CodeFixTitleCsproj);

    /// <summary>Equivalence key for moving a Razor <c>@using</c> into <c>_Imports.razor</c>.</summary>
    public const string EquivalenceKeyImportsRazor = nameof(CodeFixResources.CodeFixTitleImportsRazor);

    // Keep old name as alias so existing tests/docs that referenced EquivalenceKey still compile if any remain.
    public const string EquivalenceKey = EquivalenceKeyZGlobalUsings;

    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(
            GlobalUsingAnalyzerAnalyzer.DiagnosticId,
            GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId);

    /// <summary>
    /// One Fix All provider that dispatches on <see cref="FixAllContext.CodeActionEquivalenceKey"/>
    /// so each lightbulb option has its own Fix all in document/project/solution.
    /// </summary>
    public sealed override FixAllProvider GetFixAllProvider() =>
        MoveUsingsFixAllProvider.Instance;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.FirstOrDefault();
        if (diagnostic == null)
        {
            return;
        }

        if (diagnostic.Id == GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId)
        {
            // GUA003 may be located on a .razor path (Error List / editor). The host still
            // supplies a C# Document from the same project for CodeFixContext when possible.
            var solution = context.Document?.Project.Solution;
            if (solution == null)
            {
                return;
            }

            // Prefer the project that owns the Razor source file (Blazor Client vs Server).
            if (diagnostic.Properties.TryGetValue(
                    GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty,
                    out var razorPath)
                && !string.IsNullOrEmpty(razorPath))
            {
                var owning = ProjectPathHelper.FindOwningProject(
                    solution,
                    razorPath,
                    context.Document.Project);
                if (owning != null)
                {
                    solution = owning.Solution;
                }
            }

            var title = GetRazorImportsFixTitle(diagnostic);
            context.RegisterCodeFix(
                new MoveToImportsRazorCodeAction(
                    title: title,
                    equivalenceKey: EquivalenceKeyImportsRazor,
                    originalSolution: solution,
                    diagnostics: ImmutableArray.Create(diagnostic)),
                diagnostic);
            return;
        }

        if (diagnostic.Id != GlobalUsingAnalyzerAnalyzer.DiagnosticId)
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return;
        }

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

    /// <summary>
    /// Moves GUA003 Razor <c>@using</c> items into the correct hierarchical imports file:
    /// <c>.cshtml</c> → nearest <c>_ViewImports.cshtml</c>; <c>.razor</c> → nearest <c>_Imports.razor</c>.
    /// </summary>
    internal static async Task<CsprojApplyResult> ComputeMoveToImportsRazorResultAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Group: (projectId, sourcePath) → specs to move
        var bySource = new Dictionary<(ProjectId ProjectId, string SourcePath), List<UsingSpec>>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Id != GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId)
            {
                continue;
            }

            if (!diagnostic.Properties.TryGetValue(
                    GlobalUsingAnalyzerAnalyzer.UsingIdentityProperty,
                    out var identity)
                || !UsingSpec.TryParseIdentity(identity, out var spec))
            {
                continue;
            }

            if (!diagnostic.Properties.TryGetValue(
                    GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty,
                    out var sourcePath)
                || string.IsNullOrEmpty(sourcePath))
            {
                sourcePath = diagnostic.AdditionalLocations.FirstOrDefault()?.GetLineSpan().Path;
            }

            // Never promote from hierarchical imports files themselves.
            if (string.IsNullOrEmpty(sourcePath)
                || RazorUsingEditor.IsImportsFile(sourcePath)
                || !(RazorUsingEditor.IsCshtmlFile(sourcePath)
                    || RazorUsingEditor.IsRazorComponentFile(sourcePath)))
            {
                continue;
            }

            var hintProject = diagnostic.Location.IsInSource
                ? solution.GetDocument(diagnostic.Location.SourceTree)?.Project
                : null;

            var project = ProjectPathHelper.FindOwningProject(solution, sourcePath, hintProject);
            if (project == null)
            {
                continue;
            }

            var projectDir = ProjectPathHelper.TryGetProjectDirectory(project);
            if (string.IsNullOrEmpty(projectDir))
            {
                // Without a .csproj path we cannot safely choose a create location (CWD is often the solution folder).
                continue;
            }

            // Normalize source path against THIS project's directory (not process CWD).
            sourcePath = ProjectPathHelper.ResolveAgainstProject(projectDir, sourcePath);

            if (!RazorUsingEditor.IsPathInsideProject(sourcePath, projectDir)
                || RazorUsingEditor.IsImportsFile(sourcePath))
            {
                continue;
            }

            var key = (project.Id, sourcePath);
            if (!bySource.TryGetValue(key, out var specs))
            {
                specs = new List<UsingSpec>();
                bySource[key] = specs;
            }

            if (!specs.Any(s => s.Equals(spec)))
            {
                specs.Add(spec);
            }
        }

        var diskWrites = ImmutableArray.CreateBuilder<CsprojDiskWrite>();

        // Group specs by destination imports file (MVC and Blazor targets stay separate).
        var importsUpdates = new Dictionary<(ProjectId ProjectId, string ImportsPath), List<UsingSpec>>();
        var sourceRemovals = new List<(ProjectId ProjectId, string SourcePath, List<UsingSpec> Specs)>();

        foreach (var pair in bySource)
        {
            var project = solution.GetProject(pair.Key.ProjectId);
            if (project == null)
            {
                continue;
            }

            var projectDir = ProjectPathHelper.TryGetProjectDirectory(project);
            if (string.IsNullOrEmpty(projectDir))
            {
                continue;
            }

            var sourcePath = pair.Key.SourcePath;
            if (!RazorUsingEditor.IsPathInsideProject(sourcePath, projectDir)
                || RazorUsingEditor.IsImportsFile(sourcePath))
            {
                continue;
            }

            var sourceDir = Path.GetDirectoryName(sourcePath) ?? projectDir;
            string importsFileName;
            try
            {
                importsFileName = RazorUsingEditor.GetImportsFileNameForSource(sourcePath);
            }
            catch (ArgumentException)
            {
                continue;
            }

            // Prefer same-folder and project-root imports that actually exist on disk / workspace.
            var existingImports = new List<string>();
            void AddExisting(string candidate)
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    return;
                }

                try
                {
                    candidate = Path.GetFullPath(candidate);
                }
                catch (Exception)
                {
                    return;
                }

                if (!RazorUsingEditor.IsPathInsideProject(candidate, projectDir))
                {
                    return;
                }

                if (!existingImports.Any(p => string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    existingImports.Add(candidate);
                }
            }

            // Same directory as the source (e.g. Client\_Imports.razor next to Routes.razor).
            var sameFolder = Path.Combine(sourceDir, importsFileName);
            if (File.Exists(sameFolder)
                || TextDocumentPathHelper.FindTextDocument(solution, sameFolder) != null)
            {
                AddExisting(sameFolder);
            }

            // Project root imports.
            var projectRootImports = Path.Combine(projectDir, importsFileName);
            if (File.Exists(projectRootImports)
                || TextDocumentPathHelper.FindTextDocument(solution, projectRootImports) != null)
            {
                AddExisting(projectRootImports);
            }

            foreach (var p in TextDocumentPathHelper.GetExistingImportsPaths(project, importsFileName)
                .Concat(DiscoverImportsOnDisk(sourceDir, projectDir, importsFileName)))
            {
                AddExisting(p);
            }

            var importsPath = RazorUsingEditor.FindNearestImportsPath(
                sourceDir,
                projectDir,
                importsFileName,
                existingImports);

            // Hard clamp: create-at-root is always exactly {projectDir}/{importsFileName}.
            if (!RazorUsingEditor.IsPathInsideProject(importsPath, projectDir))
            {
                importsPath = Path.GetFullPath(projectRootImports);
            }

            // Only remove local @using lines; skip specs already inherited from an ancestor imports file
            // (e.g. already in root _Imports.razor) so we don't recreate or rewrite unnecessarily.
            var inherited = await CollectInheritedUsingsForSourceAsync(
                solution,
                project,
                sourcePath,
                importsFileName,
                projectDir,
                cancellationToken).ConfigureAwait(false);

            var specsToMove = pair.Value.Where(s => !inherited.Contains(s)).ToList();
            var specsToRemoveOnly = pair.Value.Where(s => inherited.Contains(s)).ToList();

            // Always strip redundant local @usings that are already inherited.
            if (specsToRemoveOnly.Count > 0)
            {
                sourceRemovals.Add((pair.Key.ProjectId, sourcePath, specsToRemoveOnly));
            }

            if (specsToMove.Count == 0)
            {
                continue;
            }

            sourceRemovals.Add((pair.Key.ProjectId, sourcePath, specsToMove));

            var importsKey = (pair.Key.ProjectId, importsPath);
            if (!importsUpdates.TryGetValue(importsKey, out var list))
            {
                list = new List<UsingSpec>();
                importsUpdates[importsKey] = list;
            }

            foreach (var spec in specsToMove)
            {
                if (!list.Any(s => s.Equals(spec)))
                {
                    list.Add(spec);
                }
            }
        }

        // Merge removals for the same source path (inherited-only + promote).
        var mergedRemovals = new Dictionary<(ProjectId, string), List<UsingSpec>>();
        foreach (var removal in sourceRemovals)
        {
            var key = (removal.ProjectId, removal.SourcePath);
            if (!mergedRemovals.TryGetValue(key, out var list))
            {
                list = new List<UsingSpec>();
                mergedRemovals[key] = list;
            }

            foreach (var spec in removal.Specs)
            {
                if (!list.Any(s => s.Equals(spec)))
                {
                    list.Add(spec);
                }
            }
        }

        // 1) Remove @using from each source file
        foreach (var pair in mergedRemovals)
        {
            solution = await ApplyTextFileEditAsync(
                solution,
                pair.Key.Item1,
                pair.Key.Item2,
                original => RazorUsingEditor.RemoveUsings(original, pair.Value),
                createIfMissing: false,
                diskWrites,
                cancellationToken).ConfigureAwait(false);
        }

        // 2) Add to each target imports file (only when there is something to add)
        foreach (var pair in importsUpdates)
        {
            var project = solution.GetProject(pair.Key.ProjectId);
            var projectDir = ProjectPathHelper.TryGetProjectDirectory(project);
            if (string.IsNullOrEmpty(projectDir)
                || !RazorUsingEditor.IsPathInsideProject(pair.Key.ImportsPath, projectDir))
            {
                continue;
            }

            solution = await ApplyTextFileEditAsync(
                solution,
                pair.Key.ProjectId,
                pair.Key.ImportsPath,
                original => RazorUsingEditor.AddUsings(original ?? string.Empty, pair.Value),
                createIfMissing: true,
                diskWrites,
                cancellationToken).ConfigureAwait(false);
        }

        return new CsprojApplyResult(solution, diskWrites.ToImmutable());
    }

    private static string GetRazorImportsFixTitle(Diagnostic diagnostic)
    {
        if (diagnostic.Properties.TryGetValue(
                GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty,
                out var path)
            && !string.IsNullOrEmpty(path))
        {
            if (RazorUsingEditor.IsCshtmlFile(path))
            {
                return CodeFixResources.CodeFixTitleViewImportsCshtml;
            }

            if (RazorUsingEditor.IsRazorComponentFile(path))
            {
                return CodeFixResources.CodeFixTitleImportsRazorFile;
            }
        }

        return CodeFixResources.CodeFixTitleImportsRazor;
    }

    /// <summary>
    /// Applies a GUA003 promote from the Razor editor lightbulb (VSIX suggested action).
    /// Not routed through the C# <see cref="CodeFixProvider"/> pipeline because that never
    /// surfaces in the Razor language service lightbulb.
    /// </summary>
    public static async Task ApplyPromoteRazorUsingFromEditorAsync(
        Workspace workspace,
        string razorSourcePath,
        string usingIdentity,
        CancellationToken cancellationToken)
    {
        if (workspace == null
            || string.IsNullOrEmpty(razorSourcePath)
            || string.IsNullOrEmpty(usingIdentity))
        {
            return;
        }

        var diagnostic = CreateSyntheticRazorDiagnostic(razorSourcePath, usingIdentity);
        var result = await ComputeMoveToImportsRazorResultAsync(
                workspace.CurrentSolution,
                ImmutableArray.Create(diagnostic),
                cancellationToken)
            .ConfigureAwait(false);

        if (!ReferenceEquals(result.Solution, workspace.CurrentSolution))
        {
            workspace.TryApplyChanges(result.Solution);
        }

        foreach (var diskWrite in result.DiskWrites)
        {
            new WriteTextFileOperation(diskWrite.FilePath, diskWrite.NewText)
                .Apply(workspace, cancellationToken);
        }
    }

    /// <summary>Builds a GUA003 diagnostic carrying the properties the code fix expects.</summary>
    internal static Diagnostic CreateSyntheticRazorDiagnostic(string sourcePath, string identity)
    {
        var emptySpan = new TextSpan(0, 0);
        var emptyLine = new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0));
        var properties = ImmutableDictionary<string, string>.Empty
            .Add(GlobalUsingAnalyzerAnalyzer.UsingIdentityProperty, identity)
            .Add(GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty, sourcePath);

        var descriptor = new DiagnosticDescriptor(
            GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId,
            "Razor @using",
            "Razor @using '{0}' can be moved to imports file (GlobalUsingAnalyzer)",
            "Style",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        return Diagnostic.Create(
            descriptor,
            Location.Create(sourcePath ?? string.Empty, emptySpan, emptyLine),
            properties,
            identity);
    }

    /// <summary>Test-friendly GUA003 apply (folds disk writes into additional documents).</summary>
    internal static async Task<Solution> ApplyMoveToImportsRazorAsync(
        Solution solution,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await ComputeMoveToImportsRazorResultAsync(solution, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        solution = result.Solution;

        foreach (var diskWrite in result.DiskWrites)
        {
            var project = solution.Projects.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.FilePath)
                && diskWrite.FilePath.StartsWith(
                    Path.GetDirectoryName(p.FilePath) ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                ?? solution.Projects.FirstOrDefault();

            if (project == null)
            {
                continue;
            }

            if (TextDocumentPathHelper.FindTextDocument(solution, diskWrite.FilePath) != null)
            {
                solution = TextDocumentPathHelper.TryWithText(
                    solution, project.Id, diskWrite.FilePath, diskWrite.NewText,
                    createIfMissing: false, out _);
            }
            else
            {
                solution = TextDocumentPathHelper.TryWithText(
                    solution, project.Id, diskWrite.FilePath, diskWrite.NewText,
                    createIfMissing: true, out _);
            }
        }

        return solution;
    }

    private static async Task<Solution> ApplyTextFileEditAsync(
        Solution solution,
        ProjectId projectId,
        string filePath,
        Func<string, string> transform,
        bool createIfMissing,
        ImmutableArray<CsprojDiskWrite>.Builder diskWrites,
        CancellationToken cancellationToken)
    {
        var loaded = await TextDocumentPathHelper.TryGetTextAsync(solution, filePath, cancellationToken)
            .ConfigureAwait(false);

        string originalText;
        bool tracked;

        if (loaded == null)
        {
            if (!createIfMissing)
            {
                return solution;
            }

            originalText = string.Empty;
            tracked = false;
        }
        else
        {
            originalText = loaded.Value.text;
            tracked = loaded.Value.isTrackedInWorkspace;
        }

        var updatedText = transform(originalText);
        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
        {
            return solution;
        }

        if (tracked || createIfMissing)
        {
            solution = TextDocumentPathHelper.TryWithText(
                solution,
                projectId,
                filePath,
                updatedText,
                createIfMissing: createIfMissing && !tracked,
                out var applied);

            if (applied)
            {
                return solution;
            }
        }

        diskWrites.Add(new CsprojDiskWrite(filePath, updatedText));
        return solution;
    }

    /// <summary>
    /// Walks from <paramref name="sourceDir"/> up to <paramref name="projectDir"/> and
    /// returns every imports file named <paramref name="importsFileName"/> that exists on disk.
    /// Never walks above <paramref name="projectDir"/>.
    /// </summary>
    private static IEnumerable<string> DiscoverImportsOnDisk(
        string sourceDir,
        string projectDir,
        string importsFileName)
    {
        if (string.IsNullOrEmpty(projectDir) || string.IsNullOrEmpty(importsFileName))
        {
            yield break;
        }

        var root = Path.GetFullPath(projectDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string dir;
        try
        {
            dir = string.IsNullOrEmpty(sourceDir)
                ? root
                : Path.GetFullPath(sourceDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            dir = root;
        }

        // If source is outside the project, only scan the project root.
        if (!RazorUsingEditor.IsPathInsideProject(dir, root)
            && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
        {
            dir = root;
        }

        while (true)
        {
            var candidate = Path.Combine(dir, importsFileName);
            if (File.Exists(candidate))
            {
                yield return Path.GetFullPath(candidate);
            }

            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent))
            {
                yield break;
            }

            // Never walk above project root.
            var parentNorm = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!RazorUsingEditor.IsPathInsideProject(parentNorm, root)
                && !string.Equals(parentNorm, root, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            dir = parentNorm;
        }
    }

    private static async Task<HashSet<UsingSpec>> CollectInheritedUsingsForSourceAsync(
        Solution solution,
        Project project,
        string sourcePath,
        string importsFileName,
        string projectDir,
        CancellationToken cancellationToken)
    {
        var contents = new List<KeyValuePair<string, string>>();
        var paths = new List<string>();

        void AddPath(string p)
        {
            if (string.IsNullOrEmpty(p))
            {
                return;
            }

            try
            {
                p = Path.GetFullPath(p);
            }
            catch (Exception)
            {
                return;
            }

            if (!RazorUsingEditor.IsPathInsideProject(p, projectDir))
            {
                return;
            }

            if (!paths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(p);
            }
        }

        var sourceDir = Path.GetDirectoryName(sourcePath);
        AddPath(Path.Combine(sourceDir ?? projectDir, importsFileName));
        AddPath(Path.Combine(projectDir, importsFileName));

        foreach (var p in TextDocumentPathHelper.GetExistingImportsPaths(project, importsFileName)
            .Concat(DiscoverImportsOnDisk(sourceDir, projectDir, importsFileName)))
        {
            AddPath(p);
        }

        foreach (var importsPath in paths)
        {
            var loaded = await TextDocumentPathHelper.TryGetTextAsync(solution, importsPath, cancellationToken)
                .ConfigureAwait(false);
            if (loaded == null)
            {
                continue;
            }

            contents.Add(new KeyValuePair<string, string>(importsPath, loaded.Value.text));
        }

        return RazorUsingEditor.CollectInheritedUsings(sourcePath, contents);
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
