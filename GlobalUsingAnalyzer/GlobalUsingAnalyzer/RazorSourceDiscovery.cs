using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Finds analyzable Razor sources (<c>.razor</c> / <c>.cshtml</c>) for GUA003.
/// Prefers MSBuild <c>AdditionalFiles</c> (NuGet props). Falls back to scanning the project
/// directory on disk so VSIX-installed analyzers work without a package reference.
/// </summary>
internal static class RazorSourceDiscovery
{
    internal readonly struct RazorFile
    {
        public RazorFile(string path, SourceText text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }

        public SourceText Text { get; }
    }

    internal readonly struct ImportsFile
    {
        public ImportsFile(string path, string content)
        {
            Path = path;
            Content = content;
        }

        public string Path { get; }

        public string Content { get; }
    }

    public static string TryGetProjectDirectory(AnalyzerOptions options, Compilation compilation)
    {
        var provider = options?.AnalyzerConfigOptionsProvider;
        if (provider != null)
        {
            var global = provider.GlobalOptions;
            if (TryGetBuildProperty(global, "projectdir", out var projectDir)
                || TryGetBuildProperty(global, "MSBuildProjectDirectory", out projectDir)
                || TryGetBuildProperty(global, "ProjectDir", out projectDir))
            {
                return NormalizeDir(projectDir);
            }

            if (TryGetBuildProperty(global, "MSBuildProjectFullPath", out var projectFile)
                || TryGetBuildProperty(global, "MSBuildProjectFileFullPath", out projectFile))
            {
                try
                {
                    return NormalizeDir(Path.GetDirectoryName(projectFile));
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }

        // Infer from syntax tree paths: walk up until a .csproj is found.
        // Prefer non-generated / non-obj trees first so multi-project Blazor apps resolve the
        // Client project rather than a shared obj intermediate path.
        foreach (var filePath in EnumeratePreferredSyntaxTreePaths(compilation))
        {
            try
            {
                var full = Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(filePath);
                var dir = Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(dir))
                {
#pragma warning disable RS1035 // Required for VSIX without NuGet AdditionalFiles
                    if (Directory.Exists(dir)
                        && Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
#pragma warning restore RS1035
                    {
                        return NormalizeDir(dir);
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception)
            {
                // try next tree
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePreferredSyntaxTreePaths(Compilation compilation)
    {
        var generated = new List<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var filePath = tree.FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            if (IsLikelyGeneratedPath(filePath))
            {
                generated.Add(filePath);
            }
            else
            {
                yield return filePath;
            }
        }

        foreach (var path in generated)
        {
            yield return path;
        }
    }

    private static bool IsLikelyGeneratedPath(string path)
    {
        return path.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("RazorSourceGenerator", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf(".g.cs", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static ImmutableArray<RazorFile> GetAnalyzableSources(
        AnalyzerOptions options,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, RazorFile>(StringComparer.OrdinalIgnoreCase);

        // 1) MSBuild AdditionalFiles (NuGet props)
        foreach (var file in options.AdditionalFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RazorUsingEditor.IsAnalyzableRazorSourceFile(file.Path))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            if (text == null || text.Length == 0)
            {
                continue;
            }

            byPath[NormalizePathKey(file.Path)] = new RazorFile(file.Path, text);
        }

        // 2) Always also try disk under project dir (VSIX). Merge without replacing richer AdditionalFiles text.
        var projectDir = TryGetProjectDirectory(options, compilation);
        if (!string.IsNullOrEmpty(projectDir))
        {
            foreach (var file in LoadFromDisk(projectDir, analyzableSourcesOnly: true, cancellationToken))
            {
                var key = NormalizePathKey(file.Path);
                if (!byPath.ContainsKey(key))
                {
                    byPath[key] = file;
                }
            }
        }

        return byPath.Values.ToImmutableArray();
    }

    public static ImmutableArray<ImportsFile> GetImportsFiles(
        AnalyzerOptions options,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, ImportsFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in options.AdditionalFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RazorUsingEditor.IsImportsFile(file.Path))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            if (text == null)
            {
                continue;
            }

            byPath[NormalizePathKey(file.Path)] = new ImportsFile(file.Path, text.ToString());
        }

        var projectDir = TryGetProjectDirectory(options, compilation);
        if (!string.IsNullOrEmpty(projectDir))
        {
#pragma warning disable RS1035
            foreach (var path in EnumerateFilesSafe(projectDir, "*.razor")
                .Concat(EnumerateFilesSafe(projectDir, "*.cshtml")))
#pragma warning restore RS1035
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RazorUsingEditor.IsImportsFile(path))
                {
                    continue;
                }

                var key = NormalizePathKey(path);
                if (byPath.ContainsKey(key))
                {
                    continue;
                }

                try
                {
#pragma warning disable RS1035
                    var content = File.ReadAllText(path);
#pragma warning restore RS1035
                    byPath[key] = new ImportsFile(path, content);
                }
                catch (IOException)
                {
                    // skip
                }
                catch (UnauthorizedAccessException)
                {
                    // skip
                }
            }
        }

        return byPath.Values.ToImmutableArray();
    }

    private static ImmutableArray<RazorFile> LoadFromDisk(
        string projectDir,
        bool analyzableSourcesOnly,
        CancellationToken cancellationToken)
    {
        var result = new List<RazorFile>();

#pragma warning disable RS1035
        foreach (var path in EnumerateFilesSafe(projectDir, "*.razor")
            .Concat(EnumerateFilesSafe(projectDir, "*.cshtml")))
#pragma warning restore RS1035
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (analyzableSourcesOnly && !RazorUsingEditor.IsAnalyzableRazorSourceFile(path))
            {
                continue;
            }

            if (!analyzableSourcesOnly && !RazorUsingEditor.IsImportsFile(path))
            {
                continue;
            }

            try
            {
#pragma warning disable RS1035
                var content = File.ReadAllText(path);
#pragma warning restore RS1035
                if (string.IsNullOrEmpty(content))
                {
                    continue;
                }

                result.Add(new RazorFile(path, SourceText.From(content)));
            }
            catch (IOException)
            {
                // skip
            }
            catch (UnauthorizedAccessException)
            {
                // skip
            }
        }

        return result.ToImmutableArray();
    }

#pragma warning disable RS1035 // Intentional: VSIX analyzers do not get NuGet AdditionalFiles props
    private static IEnumerable<string> EnumerateFilesSafe(string projectDir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(projectDir, pattern, SearchOption.AllDirectories)
                .Where(p => !IsUnderBinOrObj(p, projectDir));
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
#pragma warning restore RS1035

    private static bool IsUnderBinOrObj(string path, string projectDir)
    {
        try
        {
            var relative = path;
            if (path.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            {
                relative = path.Substring(projectDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            return segments.Any(s =>
                string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetBuildProperty(
        AnalyzerConfigOptions options,
        string name,
        out string value)
    {
        if (options.TryGetValue($"build_property.{name}", out value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static string NormalizeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(dir.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return dir.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path ?? string.Empty;
        }
    }
}
