using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer
{
    /// <summary>
    /// Lightbulb fix for GUA001: remove a local <c>using N;</c> and add
    /// <c>global using N;</c> to ZGlobalUsings.cs at the project root (creating the file if needed).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GlobalUsingAnalyzerCodeFixProvider)), Shared]
    public class GlobalUsingAnalyzerCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(GlobalUsingAnalyzerAnalyzer.DiagnosticId);

        // BatchFixer is awkward when many fixes all edit the same destination file.
        // Returning null disables "Fix all" until a custom FixAllProvider is written.
        public sealed override FixAllProvider GetFixAllProvider() => null;

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
                    // createChangedSolution: we touch two documents, so return a whole Solution.
                    createChangedSolution: ct => MoveUsingToGlobalUsingsAsync(context.Document, usingDirective, ct),
                    // equivalenceKey groups identical fixes for Fix All (if you add one later).
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitle)),
                diagnostic);
        }

        private static async Task<Solution> MoveUsingToGlobalUsingsAsync(
            Document document,
            UsingDirectiveSyntax usingDirective,
            CancellationToken cancellationToken)
        {
            var namespaceName = usingDirective.Name.ToString();
            var globalUsingLine = $"global using {namespaceName};";

            // --- Step A: remove the local using from the current file ---
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            // KeepNoTrivia avoids leaving a blank ghost line where the using was.
            var newRoot = root.RemoveNode(usingDirective, SyntaxRemoveOptions.KeepNoTrivia);
            var solution = document.Project.Solution.WithDocumentSyntaxRoot(document.Id, newRoot);

            // --- Step B: add "global using N;" to ZGlobalUsings.cs ---
            solution = await AddOrUpdateGlobalUsingsFileAsync(
                solution,
                document.Project.Id,
                globalUsingLine,
                namespaceName,
                cancellationToken).ConfigureAwait(false);

            return solution;
        }

        private static async Task<Solution> AddOrUpdateGlobalUsingsFileAsync(
            Solution solution,
            ProjectId projectId,
            string globalUsingLine,
            string namespaceName,
            CancellationToken cancellationToken)
        {
            var project = solution.GetProject(projectId);
            if (project == null)
            {
                return solution;
            }

            var globalUsingsDocument = FindGlobalUsingsDocument(project);

            if (globalUsingsDocument == null)
            {
                // File does not exist yet → create at project root.
                var filePath = GetGlobalUsingsFilePath(project);
                var content = globalUsingLine + Environment.NewLine;

                // AddDocument returns the new Document; its Project.Solution is the updated solution.
                var added = project.AddDocument(
                    name: GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                    text: content,
                    folders: null,
                    filePath: filePath);

                return added.Project.Solution;
            }

            // File exists → append if this namespace is not already listed.
            var text = await globalUsingsDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var existing = text.ToString();

            if (ContainsGlobalUsing(existing, namespaceName))
            {
                // Already present; removal of the local using is enough.
                return solution;
            }

            var newContent = string.IsNullOrWhiteSpace(existing)
                ? globalUsingLine + Environment.NewLine
                : existing.TrimEnd() + Environment.NewLine + globalUsingLine + Environment.NewLine;

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
                    var trimmed = line.Trim();

                    // Strip optional trailing comment.
                    var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);
                    if (commentIndex >= 0)
                    {
                        trimmed = trimmed.Substring(0, commentIndex).TrimEnd();
                    }

                    if (!trimmed.EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Expect tokens: global using Some.Namespace
                    var withoutSemi = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                    var parts = withoutSemi.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3
                        && parts[0] == "global"
                        && parts[1] == "using")
                    {
                        // Namespace is everything after "using" (normally a single token like System.IO).
                        var name = string.Concat(parts.Skip(2));
                        if (string.Equals(name, namespaceName, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}

