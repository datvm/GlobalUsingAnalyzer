using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using System.IO;
using System.Threading;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Writes text to a path that is not safely editable as a workspace project item
/// (notably the .csproj itself). Used after <see cref="ApplyChangesOperation"/> so
/// C# document edits still apply even when the project file is not tracked.
/// </summary>
internal sealed class WriteTextFileOperation(string filePath, string newText) : CodeActionOperation
{
    public override string Title => $"Update {Path.GetFileName(filePath)}";

    public override void Apply(Workspace workspace, CancellationToken cancellationToken)
    {
        // If the host opened/tracked the file since we computed the fix, prefer a workspace edit.
        var solution = workspace.CurrentSolution;
        var textDocument = ProjectFileDocumentHelper.FindProjectFileDocument(solution, filePath);
        if (textDocument != null)
        {
            var sourceText = SourceText.From(newText);
            Solution updated;
            if (solution.GetDocument(textDocument.Id) != null)
            {
                updated = solution.WithDocumentText(textDocument.Id, sourceText);
            }
            else
            {
                updated = solution.WithAdditionalDocumentText(textDocument.Id, sourceText);
            }

            workspace.TryApplyChanges(updated);
            return;
        }

        // Typical VS case: project system owns the .csproj; write on disk and let CPS reload.
        File.WriteAllText(filePath, newText);
    }
}
