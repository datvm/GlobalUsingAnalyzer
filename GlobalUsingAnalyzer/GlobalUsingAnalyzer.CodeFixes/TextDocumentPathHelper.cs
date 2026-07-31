using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Locates and updates arbitrary text files (e.g. <c>.cshtml</c>, <c>.razor</c>,
/// <c>_ViewImports.cshtml</c>, <c>_Imports.razor</c>) inside a <see cref="Solution"/> or on disk.
/// </summary>
internal static class TextDocumentPathHelper
{
    public static async Task<(string text, bool isTrackedInWorkspace)?> TryGetTextAsync(
        Solution solution,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var textDocument = FindTextDocument(solution, filePath);
        if (textDocument != null)
        {
            var text = await textDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return (text.ToString(), isTrackedInWorkspace: true);
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return (File.ReadAllText(filePath), isTrackedInWorkspace: false);
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
    /// Updates text when tracked; otherwise returns null so the caller can emit a disk write.
    /// When <paramref name="createIfMissing"/> is true and the file is not tracked, adds an
    /// additional document (suitable for new <c>_Imports.razor</c> files, not for .csproj).
    /// </summary>
    public static Solution TryWithText(
        Solution solution,
        ProjectId projectId,
        string filePath,
        string newText,
        bool createIfMissing,
        out bool appliedInWorkspace)
    {
        appliedInWorkspace = false;
        var project = solution.GetProject(projectId);
        if (project == null || string.IsNullOrEmpty(filePath))
        {
            return solution;
        }

        var textDocument = FindTextDocument(solution, filePath);
        if (textDocument != null)
        {
            var sourceText = SourceText.From(newText);
            appliedInWorkspace = true;
            if (solution.GetDocument(textDocument.Id) != null)
            {
                return solution.WithDocumentText(textDocument.Id, sourceText);
            }

            return solution.WithAdditionalDocumentText(textDocument.Id, sourceText);
        }

        if (!createIfMissing)
        {
            return solution;
        }

        var fileName = Path.GetFileName(filePath);
        var added = project.AddAdditionalDocument(fileName, newText, filePath: filePath);
        appliedInWorkspace = true;
        return added.Project.Solution;
    }

    public static TextDocument FindTextDocument(Solution solution, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var ids = solution.GetDocumentIdsWithFilePath(filePath);
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

        // Relative / name-only match (unit tests). Prefer exact FilePath; only use Name when
        // the request is not rooted (otherwise "root/_Imports.razor" would hit "Pages/_Imports.razor").
        var fileName = Path.GetFileName(filePath);
        var requestRooted = Path.IsPathRooted(filePath);
        string fullRequest = null;
        try
        {
            fullRequest = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            // ignore invalid paths
        }

        foreach (var project in solution.Projects)
        {
            foreach (var d in project.AdditionalDocuments)
            {
                if (!string.IsNullOrEmpty(d.FilePath))
                {
                    if (string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }

                    if (fullRequest != null
                        && string.Equals(Path.GetFullPath(d.FilePath), fullRequest, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }
                }

                if (!requestRooted
                    && string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            foreach (var d in project.Documents)
            {
                if (!string.IsNullOrEmpty(d.FilePath))
                {
                    if (string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }

                    if (fullRequest != null
                        && string.Equals(Path.GetFullPath(d.FilePath), fullRequest, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }
                }

                if (!requestRooted
                    && string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Collects known imports-file paths from the project matching <paramref name="importsFileName"/>
    /// (e.g. <c>_Imports.razor</c> or <c>_ViewImports.cshtml</c>).
    /// </summary>
    public static string[] GetExistingImportsPaths(Project project, string importsFileName)
    {
        if (project == null || string.IsNullOrEmpty(importsFileName))
        {
            return Array.Empty<string>();
        }

        var projectDir = ProjectPathHelper.TryGetProjectDirectory(project);
        var results = new List<string>();

        foreach (var doc in project.Documents.Cast<TextDocument>().Concat(project.AdditionalDocuments))
        {
            var nameMatches =
                string.Equals(Path.GetFileName(doc.FilePath ?? string.Empty), importsFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.Name, importsFileName, StringComparison.OrdinalIgnoreCase);

            if (!nameMatches)
            {
                continue;
            }

            string resolved = null;
            if (!string.IsNullOrEmpty(doc.FilePath) && projectDir != null)
            {
                resolved = ProjectPathHelper.ResolveAgainstProject(projectDir, doc.FilePath);
            }
            else if (!string.IsNullOrEmpty(doc.FilePath) && Path.IsPathRooted(doc.FilePath))
            {
                try
                {
                    resolved = Path.GetFullPath(doc.FilePath);
                }
                catch (Exception)
                {
                    resolved = null;
                }
            }
            else if (!string.IsNullOrEmpty(projectDir))
            {
                var parts = new List<string> { projectDir };
                if (doc.Folders != null && doc.Folders.Count > 0)
                {
                    parts.AddRange(doc.Folders);
                }

                parts.Add(doc.Name);
                resolved = Path.GetFullPath(Path.Combine(parts.ToArray()));
            }

            if (string.IsNullOrEmpty(resolved))
            {
                continue;
            }

            // Drop paths that don't live under the project (e.g. CWD-resolved solution-folder junk).
            if (projectDir != null && !RazorUsingEditor.IsPathInsideProject(resolved, projectDir))
            {
                continue;
            }

            results.Add(resolved);
        }

        return results.ToArray();
    }

    /// <summary>Backward-compatible helper for Blazor imports only.</summary>
    public static string[] GetExistingImportsRazorPaths(Project project)
        => GetExistingImportsPaths(project, RazorUsingEditor.BlazorImportsFileName);
}
