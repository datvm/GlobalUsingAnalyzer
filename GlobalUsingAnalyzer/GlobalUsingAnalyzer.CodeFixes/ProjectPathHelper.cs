using Microsoft.CodeAnalysis;
using System;
using System.IO;
using System.Linq;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Resolves paths relative to a project's directory. Never uses process CWD for relative paths
/// (CWD is often the solution folder in a multi-project Blazor Web App).
/// </summary>
internal static class ProjectPathHelper
{
    /// <summary>
    /// Absolute directory containing the .csproj, or null if it cannot be determined safely.
    /// Does not fall back to <see cref="Directory.GetCurrentDirectory"/>.
    /// </summary>
    public static string TryGetProjectDirectory(Project project)
    {
        if (project == null || string.IsNullOrEmpty(project.FilePath))
        {
            return null;
        }

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to an absolute path under <paramref name="projectDirectory"/>.
    /// Rooted paths are normalized; relative paths are combined with the project directory.
    /// </summary>
    public static string ResolveAgainstProject(string projectDirectory, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        try
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            if (string.IsNullOrEmpty(projectDirectory))
            {
                // No project dir — do not use CWD; return as-is for callers to reject.
                return path;
            }

            return Path.GetFullPath(Path.Combine(projectDirectory, path));
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// Picks the project that owns <paramref name="sourcePath"/>: prefer a project whose
    /// directory contains the source, then additional-document match, then C# location's project.
    /// </summary>
    public static Project FindOwningProject(
        Solution solution,
        string sourcePath,
        Project hintFromDiagnosticLocation)
    {
        if (solution == null)
        {
            return null;
        }

        // 1) Project directory contains the source file.
        if (!string.IsNullOrEmpty(sourcePath))
        {
            Project best = null;
            var bestLength = -1;

            foreach (var project in solution.Projects)
            {
                var dir = TryGetProjectDirectory(project);
                if (dir == null)
                {
                    continue;
                }

                string fullSource;
                try
                {
                    fullSource = Path.IsPathRooted(sourcePath)
                        ? Path.GetFullPath(sourcePath)
                        : Path.GetFullPath(Path.Combine(dir, sourcePath));
                }
                catch (Exception)
                {
                    continue;
                }

                if (!RazorUsingEditor.IsPathInsideProject(fullSource, dir))
                {
                    continue;
                }

                // Prefer the deepest (most specific) project directory — Client over solution-ish roots.
                if (dir.Length > bestLength)
                {
                    best = project;
                    bestLength = dir.Length;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        // 2) Explicit additional document match.
        if (!string.IsNullOrEmpty(sourcePath))
        {
            var fileName = Path.GetFileName(sourcePath);
            foreach (var project in solution.Projects)
            {
                if (project.AdditionalDocuments.Any(d =>
                    string.Equals(d.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(d.FilePath)
                        && string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(project.FilePath)
                        && RazorUsingEditor.IsPathInsideProject(
                            ResolveAgainstProject(TryGetProjectDirectory(project), d.FilePath),
                            TryGetProjectDirectory(project)))))
                {
                    return project;
                }
            }
        }

        // 3) Hint from the C# document the lightbulb was opened on (same project).
        return hintFromDiagnosticLocation;
    }
}
