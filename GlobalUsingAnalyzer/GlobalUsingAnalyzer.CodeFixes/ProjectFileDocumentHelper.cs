using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Locates and updates the .csproj text inside a <see cref="Solution"/>.
/// Prefer workspace documents (so previews/undo work). When the host has not loaded the
/// project file, callers should write to disk via <see cref="WriteTextFileOperation"/> —
/// do NOT use <c>AddAdditionalDocument</c> for .csproj (VS project system returns ADDRESULT_Cancel).
/// </summary>
internal static class ProjectFileDocumentHelper
{
    /// <summary>
    /// Reads the project file text if it is already a workspace document, or from disk.
    /// Does not mutate the solution (no AddAdditionalDocument).
    /// </summary>
    public static async Task<(string text, bool isTrackedInWorkspace)?> TryGetProjectFileTextAsync(
        Solution solution,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null || string.IsNullOrEmpty(project.FilePath))
        {
            return null;
        }

        var textDocument = FindProjectFileDocument(solution, project.FilePath);
        if (textDocument != null)
        {
            var text = await textDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return (text.ToString(), isTrackedInWorkspace: true);
        }

        if (!File.Exists(project.FilePath))
        {
            return null;
        }

        try
        {
            return (File.ReadAllText(project.FilePath), isTrackedInWorkspace: false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Updates project file text when it is already a document or additional document in the solution.
    /// </summary>
    public static Solution WithProjectFileText(
        Solution solution,
        ProjectId projectId,
        string newText)
    {
        var project = solution.GetProject(projectId);
        if (project == null || string.IsNullOrEmpty(project.FilePath))
        {
            return solution;
        }

        var textDocument = FindProjectFileDocument(solution, project.FilePath);
        if (textDocument == null)
        {
            // Not tracked — caller should use WriteTextFileOperation instead.
            return solution;
        }

        var sourceText = SourceText.From(newText);

        if (solution.GetDocument(textDocument.Id) != null)
        {
            return solution.WithDocumentText(textDocument.Id, sourceText);
        }

        return solution.WithAdditionalDocumentText(textDocument.Id, sourceText);
    }

    public static TextDocument FindProjectFileDocument(Solution solution, string projectFilePath)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            return null;
        }

        var ids = solution.GetDocumentIdsWithFilePath(projectFilePath);
        foreach (var id in ids)
        {
            var additional = solution.GetAdditionalDocument(id);
            if (additional != null)
            {
                return additional;
            }

            var document = solution.GetDocument(id);
            if (document != null)
            {
                return document;
            }
        }

        var fileName = Path.GetFileName(projectFilePath);
        foreach (var project in solution.Projects)
        {
            var match = project.AdditionalDocuments.FirstOrDefault(d =>
                string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.FilePath, projectFilePath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
