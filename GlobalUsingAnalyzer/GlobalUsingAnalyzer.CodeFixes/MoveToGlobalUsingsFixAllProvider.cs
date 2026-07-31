using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

public partial class GlobalUsingAnalyzerCodeFixProvider
{
    /// <summary>
    /// Custom Fix All: gather every GUA001 in the chosen scope, then call
    /// <see cref="ApplyAsync"/> once so ZGlobalUsings.cs is written a single time per project.
    /// </summary>
    private sealed class MoveToGlobalUsingsFixAllProvider : FixAllProvider
    {
        public static MoveToGlobalUsingsFixAllProvider Instance { get; } =
            new MoveToGlobalUsingsFixAllProvider();

        private MoveToGlobalUsingsFixAllProvider()
        {
        }

        public override async Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            // Title shown in the nested "Fix all" menu, e.g. "Fix all in document".
            var title = fixAllContext.Scope switch
            {
                FixAllScope.Document => "Move all usings in document to ZGlobalUsings.cs",
                FixAllScope.Project => "Move all usings in project to ZGlobalUsings.cs",
                FixAllScope.Solution => "Move all usings in solution to ZGlobalUsings.cs",
                _ => CodeFixResources.CodeFixTitle,
            };

            // Snapshot diagnostics for this scope *before* building the CodeAction.
            // GetFixAsync must return quickly; heavy work goes in createChangedSolution.
            var diagnostics = await GetDiagnosticsInScopeAsync(fixAllContext).ConfigureAwait(false);

            return CodeAction.Create(
                title: title,
                createChangedSolution: ct => ApplyAsync(fixAllContext.Solution, diagnostics, ct),
                equivalenceKey: EquivalenceKey);
        }

        private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsInScopeAsync(
            FixAllContext context)
        {
            switch (context.Scope)
            {
                case FixAllScope.Document:
                    // Only the current file (where the lightbulb was opened).
                    if (context.Document == null)
                    {
                        return ImmutableArray<Diagnostic>.Empty;
                    }

                    return await context.GetDocumentDiagnosticsAsync(context.Document)
                        .ConfigureAwait(false);

                case FixAllScope.Project:
                    // Every document in the current project.
                    return await context.GetAllDiagnosticsAsync(context.Project)
                        .ConfigureAwait(false);

                case FixAllScope.Solution:
                    // Union of all projects in the solution.
                    var builder = ImmutableArray.CreateBuilder<Diagnostic>();
                    foreach (var project in context.Solution.Projects)
                    {
                        // Skip projects that don't understand our analyzer language, if any.
                        var projectDiagnostics = await context.GetAllDiagnosticsAsync(project)
                            .ConfigureAwait(false);
                        builder.AddRange(projectDiagnostics);
                    }

                    return builder.ToImmutable();

                default:
                    // ContainingMember / ContainingType exist on some hosts; treat like document.
                    if (context.Document != null)
                    {
                        return await context.GetDocumentDiagnosticsAsync(context.Document)
                            .ConfigureAwait(false);
                    }

                    return ImmutableArray<Diagnostic>.Empty;
            }
        }
    }
}
