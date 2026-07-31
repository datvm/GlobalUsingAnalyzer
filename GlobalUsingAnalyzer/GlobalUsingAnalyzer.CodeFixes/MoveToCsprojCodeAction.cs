using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Code action that removes usings from C# documents and updates the .csproj.
///
/// Host paths (do not write the .csproj to disk until <see cref="CodeActionOperation.Apply"/>):
/// <list type="bullet">
/// <item><b>GetChangedSolutionAsync</b> — used by Fix All / preview to build a solution snapshot.
/// Must be pure: no disk I/O. Cancel must leave the project file untouched.</item>
/// <item><b>ComputeOperationsAsync</b> — used when applying. Includes
/// <see cref="WriteTextFileOperation"/> so untracked .csproj files are written only on Apply.</item>
/// <item><b>ComputePreviewOperationsAsync</b> — preview only; never includes disk writes.</item>
/// </list>
/// </summary>
internal sealed class MoveToCsprojCodeAction : CodeAction
{
    private readonly string _title;
    private readonly string _equivalenceKey;
    private readonly Solution _originalSolution;
    private readonly ImmutableArray<Diagnostic> _diagnostics;

    public MoveToCsprojCodeAction(
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

    /// <summary>
    /// Pure solution computation for Fix All / hosts that only ask for a new <see cref="Solution"/>.
    /// <para>
    /// Must NOT write files. This method runs when building the preview; the user may still Cancel.
    /// </para>
    /// </summary>
    protected override async Task<Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeCsprojApplyResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        // Tracked .csproj edits are already inside result.Solution.
        // Untracked .csproj paths are in result.DiskWrites and must wait for Apply
        // (see ComputeOperationsAsync → WriteTextFileOperation).
        return result.Solution;
    }

    /// <summary>
    /// Full apply path: solution changes + deferred disk writes for untracked .csproj files.
    /// Disk I/O happens only inside <see cref="WriteTextFileOperation.Apply"/> after the user confirms.
    /// </summary>
    protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(
        CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeCsprojApplyResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        var operations = new List<CodeActionOperation>();

        // Roslyn returns a new Solution instance when anything changed.
        if (!ReferenceEquals(result.Solution, _originalSolution))
        {
            operations.Add(new ApplyChangesOperation(result.Solution));
        }

        foreach (var diskWrite in result.DiskWrites)
        {
            // Applied only when the host commits operations (not on Cancel).
            operations.Add(new WriteTextFileOperation(diskWrite.FilePath, diskWrite.NewText));
        }

        return operations;
    }

    /// <summary>
    /// Preview path: show C# / tracked-document edits only. Never touch disk.
    /// </summary>
    protected override async Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(
        CancellationToken cancellationToken)
    {
        var result = await GlobalUsingAnalyzerCodeFixProvider
            .ComputeCsprojApplyResultAsync(_originalSolution, _diagnostics, cancellationToken)
            .ConfigureAwait(false);

        if (ReferenceEquals(result.Solution, _originalSolution))
        {
            return new CodeActionOperation[0];
        }

        return new CodeActionOperation[] { new ApplyChangesOperation(result.Solution) };
    }
}
