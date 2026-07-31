using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

public partial class GlobalUsingAnalyzerCodeFixProvider
{
    /// <summary>
    /// Custom Fix All for both destinations. Dispatches on
    /// <see cref="FixAllContext.CodeActionEquivalenceKey"/> so "Fix all" follows
    /// whichever lightbulb option the user expanded.
    /// </summary>
    private sealed class MoveUsingsFixAllProvider : FixAllProvider
    {
        public static MoveUsingsFixAllProvider Instance { get; } = new MoveUsingsFixAllProvider();

        private MoveUsingsFixAllProvider()
        {
        }

        public override async Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            var toCsproj = fixAllContext.CodeActionEquivalenceKey == EquivalenceKeyCsproj;

            var title = (toCsproj, fixAllContext.Scope) switch
            {
                (true, FixAllScope.Document) => "Move all usings in document to .csproj",
                (true, FixAllScope.Project) => "Move all usings in project to .csproj",
                (true, FixAllScope.Solution) => "Move all usings in solution to .csproj",
                (false, FixAllScope.Document) => "Move all usings in document to ZGlobalUsings.cs",
                (false, FixAllScope.Project) => "Move all usings in project to ZGlobalUsings.cs",
                (false, FixAllScope.Solution) => "Move all usings in solution to ZGlobalUsings.cs",
                (true, _) => CodeFixResources.CodeFixTitleCsproj,
                _ => CodeFixResources.CodeFixTitle,
            };

            var diagnostics = await GetDiagnosticsInScopeAsync(fixAllContext).ConfigureAwait(false);
            var equivalenceKey = toCsproj ? EquivalenceKeyCsproj : EquivalenceKeyZGlobalUsings;

            if (toCsproj)
            {
                return new MoveToCsprojCodeAction(
                    title,
                    equivalenceKey,
                    fixAllContext.Solution,
                    diagnostics);
            }

            return CodeAction.Create(
                title: title,
                createChangedSolution: ct => ApplyToZGlobalUsingsAsync(fixAllContext.Solution, diagnostics, ct),
                equivalenceKey: equivalenceKey);
        }

        private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsInScopeAsync(
            FixAllContext context)
        {
            switch (context.Scope)
            {
                case FixAllScope.Document:
                    if (context.Document == null)
                    {
                        return ImmutableArray<Diagnostic>.Empty;
                    }

                    return await context.GetDocumentDiagnosticsAsync(context.Document)
                        .ConfigureAwait(false);

                case FixAllScope.Project:
                    return await context.GetAllDiagnosticsAsync(context.Project)
                        .ConfigureAwait(false);

                case FixAllScope.Solution:
                    var builder = ImmutableArray.CreateBuilder<Diagnostic>();
                    foreach (var project in context.Solution.Projects)
                    {
                        var projectDiagnostics = await context.GetAllDiagnosticsAsync(project)
                            .ConfigureAwait(false);
                        builder.AddRange(projectDiagnostics);
                    }

                    return builder.ToImmutable();

                default:
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
