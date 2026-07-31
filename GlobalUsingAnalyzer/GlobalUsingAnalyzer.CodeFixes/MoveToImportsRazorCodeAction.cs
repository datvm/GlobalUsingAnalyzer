using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Moves Razor <c>@using</c> directives into the hierarchical imports file for that stack:
/// <c>.cshtml</c> → nearest <c>_ViewImports.cshtml</c>; <c>.razor</c> → nearest <c>_Imports.razor</c>.
/// Untracked file writes are deferred until <see cref="CodeActionOperation.Apply"/>.
/// </summary>
internal sealed class MoveToImportsRazorCodeAction : CodeAction
{
    private readonly string _title;
    private readonly string _equivalenceKey;
    private readonly Solution _originalSolution;
    private readonly ImmutableArray<Diagnostic> _diagnostics;

    public MoveToImportsRazorCodeAction(
        string title,
        string equivalenceKey,
        Solution originalSolution,
        ImmutableArray<Diagnostic> diagnostics)
    {
        _title = title;
        _equivalenceKey = equivalenceKey;
        _originalSolution = originalSolution;
        _diagnostics = diagnostics;
    }

    public override string Title => _title;

    public override string EquivalenceKey => _equivalenceKey;

    protected override async Task<Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeMoveToImportsRazorResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        return result.Solution;
    }

    protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(
        CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeMoveToImportsRazorResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        var operations = new List<CodeActionOperation>();

        if (!ReferenceEquals(result.Solution, _originalSolution))
        {
            operations.Add(new ApplyChangesOperation(result.Solution));
        }

        foreach (var diskWrite in result.DiskWrites)
        {
            operations.Add(new WriteTextFileOperation(diskWrite.FilePath, diskWrite.NewText));
        }

        return operations;
    }

    protected override async Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(
        CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeMoveToImportsRazorResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        if (ReferenceEquals(result.Solution, _originalSolution))
        {
            return System.Array.Empty<CodeActionOperation>();
        }

        return new CodeActionOperation[] { new ApplyChangesOperation(result.Solution) };
    }
}
