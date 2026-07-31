using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Parses and edits Razor <c>@using</c> directives, and resolves the nearest imports file:
/// <list type="bullet">
/// <item>MVC / Razor Pages (<c>.cshtml</c>) → <c>_ViewImports.cshtml</c></item>
/// <item>Blazor (<c>.razor</c>) → <c>_Imports.razor</c></item>
/// </list>
/// </summary>
public static class RazorUsingEditor
{
    /// <summary>Blazor hierarchical imports file.</summary>
    public const string BlazorImportsFileName = "_Imports.razor";

    /// <summary>MVC / Razor Pages hierarchical imports file.</summary>
    public const string MvcViewImportsFileName = "_ViewImports.cshtml";

    /// <summary>A single <c>@using</c> occurrence with its span in the source text.</summary>
    public readonly struct UsingOccurrence
    {
        public UsingOccurrence(UsingSpec spec, TextSpan span)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Span = span;
        }

        public UsingSpec Spec { get; }

        public TextSpan Span { get; }
    }

    /// <summary>
    /// Enumerates top-level <c>@using</c> lines (line-oriented).
    /// </summary>
    public static IReadOnlyList<UsingOccurrence> EnumerateUsings(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<UsingOccurrence>();
        }

        var sourceText = SourceText.From(text);
        var results = new List<UsingOccurrence>();

        foreach (var line in sourceText.Lines)
        {
            var lineText = sourceText.ToString(line.Span);
            if (!UsingSpec.TryParseRazorUsingLine(lineText, out var spec))
            {
                continue;
            }

            results.Add(new UsingOccurrence(spec, line.Span));
        }

        return results;
    }

    /// <summary>
    /// Removes lines whose <c>@using</c> matches any of <paramref name="toRemove"/>.
    /// </summary>
    public static string RemoveUsings(string text, IReadOnlyList<UsingSpec> toRemove)
    {
        if (string.IsNullOrEmpty(text) || toRemove == null || toRemove.Count == 0)
        {
            return text;
        }

        var removeSet = new HashSet<UsingSpec>(toRemove);
        var sourceText = SourceText.From(text);
        var builder = new StringBuilder(text.Length);
        var removedAny = false;

        for (var i = 0; i < sourceText.Lines.Count; i++)
        {
            var line = sourceText.Lines[i];
            var lineText = sourceText.ToString(line.SpanIncludingLineBreak);

            if (UsingSpec.TryParseRazorUsingLine(sourceText.ToString(line.Span), out var spec)
                && removeSet.Contains(spec))
            {
                removedAny = true;
                continue;
            }

            builder.Append(lineText);
        }

        return removedAny ? builder.ToString() : text;
    }

    /// <summary>
    /// Merges <paramref name="toAdd"/> with any existing <c>@using</c> lines, de-dupes, and
    /// rewrites them as a single block sorted by <see cref="UsingSpec.SortKey"/>
    /// (same ordering as <see cref="ProjectFileUsingEditor.AddUsings"/>).
    /// Non-<c>@using</c> lines are preserved; the sorted block is inserted where the first
    /// <c>@using</c> was (or at the end if the file had none).
    /// </summary>
    public static string AddUsings(string text, IReadOnlyList<UsingSpec> toAdd)
    {
        text ??= string.Empty;
        toAdd ??= Array.Empty<UsingSpec>();

        var original = new List<UsingSpec>();
        var seen = new HashSet<UsingSpec>();

        foreach (var occurrence in EnumerateUsings(text))
        {
            if (seen.Add(occurrence.Spec))
            {
                original.Add(occurrence.Spec);
            }
        }

        var merged = new List<UsingSpec>(original);
        foreach (var spec in toAdd)
        {
            if (seen.Add(spec))
            {
                merged.Add(spec);
            }
        }

        if (merged.Count == 0)
        {
            return text;
        }

        merged.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        // No new items and order unchanged → keep original text (preserves trivia/spacing).
        if (merged.Count == original.Count
            && original.Select(s => s.Identity).SequenceEqual(merged.Select(s => s.Identity)))
        {
            return text;
        }

        return RewriteWithSortedUsings(text, merged);
    }

    /// <summary>
    /// Reorders all <c>@using</c> lines in <paramref name="text"/> by <see cref="UsingSpec.SortKey"/>
    /// without adding any. No-op when already sorted or there are no usings.
    /// </summary>
    public static string SortUsings(string text)
        => AddUsings(text ?? string.Empty, Array.Empty<UsingSpec>());

    /// <summary>
    /// Rebuilds <paramref name="text"/> with <paramref name="sortedUsings"/> as one contiguous
    /// <c>@using</c> block (first original @using position, else end of file).
    /// </summary>
    private static string RewriteWithSortedUsings(string text, IReadOnlyList<UsingSpec> sortedUsings)
    {
        var nl = DetectNewLine(text);
        var sourceText = SourceText.From(text);
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var seenFirstUsing = false;
        var insertedBlock = false;

        for (var i = 0; i < sourceText.Lines.Count; i++)
        {
            var line = sourceText.Lines[i];
            var content = sourceText.ToString(line.Span);
            var withBreak = sourceText.ToString(line.SpanIncludingLineBreak);
            var isUsing = UsingSpec.TryParseRazorUsingLine(content, out _);

            if (isUsing)
            {
                if (!seenFirstUsing)
                {
                    seenFirstUsing = true;
                    foreach (var spec in sortedUsings)
                    {
                        prefix.Append(spec.ToRazorUsingLine());
                        prefix.Append(nl);
                    }

                    insertedBlock = true;
                }

                continue;
            }

            if (!seenFirstUsing)
            {
                prefix.Append(withBreak);
            }
            else
            {
                suffix.Append(withBreak);
            }
        }

        if (!insertedBlock)
        {
            var body = prefix.ToString();
            var usingsBlock = new StringBuilder();
            foreach (var spec in sortedUsings)
            {
                usingsBlock.Append(spec.ToRazorUsingLine());
                usingsBlock.Append(nl);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return usingsBlock.ToString();
            }

            return body.TrimEnd('\r', '\n') + nl + usingsBlock;
        }

        return prefix.ToString() + suffix.ToString();
    }

    /// <summary>
    /// True for <c>.cshtml</c> or <c>.razor</c> sources that may contain promotable <c>@using</c>
    /// (excludes the hierarchical imports files themselves).
    /// </summary>
    public static bool IsAnalyzableRazorSourceFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (IsImportsFile(path))
        {
            return false;
        }

        return IsCshtmlFile(path) || IsRazorComponentFile(path);
    }

    public static bool IsCshtmlFile(string path)
    {
        return !string.IsNullOrEmpty(path)
            && path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRazorComponentFile(string path)
    {
        return !string.IsNullOrEmpty(path)
            && path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for <c>_Imports.razor</c> or <c>_ViewImports.cshtml</c>.
    /// </summary>
    public static bool IsImportsFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return string.Equals(name, BlazorImportsFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, MvcViewImportsFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Imports file name for a source path: <c>_ViewImports.cshtml</c> for <c>.cshtml</c>,
    /// <c>_Imports.razor</c> for <c>.razor</c>.
    /// </summary>
    public static string GetImportsFileNameForSource(string sourcePath)
    {
        if (IsCshtmlFile(sourcePath))
        {
            return MvcViewImportsFileName;
        }

        if (IsRazorComponentFile(sourcePath))
        {
            return BlazorImportsFileName;
        }

        throw new ArgumentException(
            "Source must be a .cshtml or .razor file.",
            nameof(sourcePath));
    }

    /// <summary>
    /// Hierarchical imports in directory D apply to source files in D and its descendants.
    /// </summary>
    public static bool ImportsFileAppliesToSource(string importsFilePath, string sourceFilePath)
    {
        if (string.IsNullOrEmpty(importsFilePath) || string.IsNullOrEmpty(sourceFilePath))
        {
            return false;
        }

        var importsFileName = Path.GetFileName(importsFilePath);
        string expectedName;
        try
        {
            expectedName = GetImportsFileNameForSource(sourceFilePath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!string.Equals(importsFileName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var importsDir = Path.GetDirectoryName(importsFilePath);
        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(importsDir) || string.IsNullOrEmpty(sourceDir))
        {
            return false;
        }

        return IsUnderOrEqual(sourceDir, importsDir);
    }

    /// <summary>
    /// Collects <see cref="UsingSpec"/> values already provided by hierarchical imports files
    /// that apply to <paramref name="sourceFilePath"/>.
    /// </summary>
    /// <param name="importsFileContents">
    /// Map of imports-file path → file text (e.g. from AdditionalFiles or the workspace).
    /// </param>
    public static HashSet<UsingSpec> CollectInheritedUsings(
        string sourceFilePath,
        IEnumerable<KeyValuePair<string, string>> importsFileContents)
    {
        var result = new HashSet<UsingSpec>();
        if (string.IsNullOrEmpty(sourceFilePath) || importsFileContents == null)
        {
            return result;
        }

        foreach (var pair in importsFileContents)
        {
            if (!ImportsFileAppliesToSource(pair.Key, sourceFilePath))
            {
                continue;
            }

            foreach (var occurrence in EnumerateUsings(pair.Value ?? string.Empty))
            {
                result.Add(occurrence.Spec);
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="path"/> is under <paramref name="projectDirectory"/>
    /// (or equal to it / a file inside it). Analyzer-safe (no directory IO).
    /// </summary>
    public static bool IsPathInsideProject(string path, string projectDirectory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(projectDirectory))
        {
            return false;
        }

        try
        {
            var fullPath = NormalizePath(path);
            var root = NormalizeDirectory(projectDirectory);

            if (string.Equals(NormalizeDirectory(fullPath), root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var pathDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(pathDir))
            {
                return false;
            }

            return IsUnderOrEqual(pathDir, root);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks from <paramref name="sourceFileDirectory"/> up to <paramref name="projectDirectory"/>
    /// looking for an existing imports file named <paramref name="importsFileName"/>.
    /// Returns the first found path among <paramref name="existingImportsPaths"/>, or
    /// <c>{projectDirectory}/{importsFileName}</c> if none exists (create-at-root).
    /// Does not touch the file system (analyzer-safe).
    /// </summary>
    public static string FindNearestImportsPath(
        string sourceFileDirectory,
        string projectDirectory,
        string importsFileName,
        IEnumerable<string> existingImportsPaths = null)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        }

        if (string.IsNullOrEmpty(importsFileName))
        {
            throw new ArgumentException("Imports file name is required.", nameof(importsFileName));
        }

        var root = NormalizeDirectory(projectDirectory);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingImportsPaths != null)
        {
            foreach (var p in existingImportsPaths)
            {
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }

                // Only consider paths that match the expected imports file name.
                if (!string.Equals(Path.GetFileName(p), importsFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                existing.Add(NormalizePath(p));
            }
        }

        var startDir = string.IsNullOrEmpty(sourceFileDirectory)
            ? root
            : NormalizeDirectory(sourceFileDirectory);

        if (!IsUnderOrEqual(startDir, root))
        {
            startDir = root;
        }

        var dir = startDir;
        while (true)
        {
            var candidate = NormalizePath(Path.Combine(dir, importsFileName));

            if (existing.Contains(candidate))
            {
                return candidate;
            }

            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || !IsUnderOrEqual(parent, root))
            {
                break;
            }

            dir = NormalizeDirectory(parent);
        }

        return NormalizePath(Path.Combine(root, importsFileName));
    }

    /// <summary>Backward-compatible alias for Blazor imports discovery.</summary>
    public static string FindNearestImportsRazorPath(
        string sourceFileDirectory,
        string projectDirectory,
        IEnumerable<string> existingImportsPaths = null)
        => FindNearestImportsPath(
            sourceFileDirectory,
            projectDirectory,
            BlazorImportsFileName,
            existingImportsPaths);

    /// <summary>Backward-compatible alias.</summary>
    public static bool IsImportsRazorFile(string path)
        => !string.IsNullOrEmpty(path)
            && string.Equals(Path.GetFileName(path), BlazorImportsFileName, StringComparison.OrdinalIgnoreCase);

    private static string DetectNewLine(string text)
    {
        if (!string.IsNullOrEmpty(text) && text.Contains("\r\n"))
        {
            return "\r\n";
        }

        return "\n";
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string NormalizeDirectory(string directory)
    {
        var full = NormalizePath(directory);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        var p = NormalizeDirectory(path) + Path.DirectorySeparatorChar;
        var r = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                NormalizeDirectory(path),
                NormalizeDirectory(root),
                StringComparison.OrdinalIgnoreCase);
    }
}
