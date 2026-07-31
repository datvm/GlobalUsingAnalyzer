using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

public partial class GlobalUsingAnalyzerCodeFixProvider
{
    /// <summary>
    /// Custom Fix All for all destinations. Dispatches on
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
            var key = fixAllContext.CodeActionEquivalenceKey;
            var toCsproj = key == EquivalenceKeyCsproj;
            var toImports = key == EquivalenceKeyImportsRazor;

            var title = (toCsproj, toImports, fixAllContext.Scope) switch
            {
                (true, _, FixAllScope.Document) => "Move all usings in document to .csproj",
                (true, _, FixAllScope.Project) => "Move all usings in project to .csproj",
                (true, _, FixAllScope.Solution) => "Move all usings in solution to .csproj",
                (_, true, FixAllScope.Document) => "Move all Razor usings in document to imports files",
                (_, true, FixAllScope.Project) => "Move all Razor usings in project to imports files",
                (_, true, FixAllScope.Solution) => "Move all Razor usings in solution to imports files",
                (_, _, FixAllScope.Document) => "Move all usings in document to ZGlobalUsings.cs",
                (_, _, FixAllScope.Project) => "Move all usings in project to ZGlobalUsings.cs",
                (_, _, FixAllScope.Solution) => "Move all usings in solution to ZGlobalUsings.cs",
                (true, _, _) => CodeFixResources.CodeFixTitleCsproj,
                (_, true, _) => CodeFixResources.CodeFixTitleImportsRazor,
                _ => CodeFixResources.CodeFixTitle,
            };

            var diagnostics = await GetDiagnosticsInScopeAsync(fixAllContext).ConfigureAwait(false);

            if (toCsproj)
            {
                return new MoveToCsprojCodeAction(
                    title,
                    EquivalenceKeyCsproj,
                    fixAllContext.Solution,
                    diagnostics);
            }

            if (toImports)
            {
                return new MoveToImportsRazorCodeAction(
                    title,
                    EquivalenceKeyImportsRazor,
                    fixAllContext.Solution,
                    diagnostics);
            }

            return CodeAction.Create(
                title: title,
                createChangedSolution: ct => ApplyToZGlobalUsingsAsync(fixAllContext.Solution, diagnostics, ct),
                equivalenceKey: EquivalenceKeyZGlobalUsings);
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
