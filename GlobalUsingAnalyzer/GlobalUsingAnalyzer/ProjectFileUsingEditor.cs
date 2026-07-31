using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Edits SDK-style .csproj XML to add / merge / sort <c>&lt;Using /&gt;</c> items.
/// </summary>
public static class ProjectFileUsingEditor
{
    /// <summary>
    /// Adds the given usings to the first ItemGroup that already contains <c>Using</c> items,
    /// or creates a new ItemGroup. Always re-sorts that group's <c>Using</c> elements by
    /// <see cref="UsingSpec.SortKey"/> (alias name, else include).
    /// Returns the original text when nothing new was added (after merge+sort, text may still change if order changed).
    /// </summary>
    public static string AddUsings(string csprojText, IReadOnlyList<UsingSpec> toAdd)
    {
        if (toAdd == null || toAdd.Count == 0)
        {
            return csprojText;
        }

        var document = XDocument.Parse(csprojText, LoadOptions.None);
        var root = document.Root
            ?? throw new InvalidOperationException("Project file has no root element.");

        var ns = root.Name.Namespace;

        var itemGroup = root.Elements(ns + "ItemGroup")
            .FirstOrDefault(g => g.Elements(ns + "Using").Any());

        if (itemGroup == null)
        {
            itemGroup = new XElement(ns + "ItemGroup");
            root.Add(itemGroup);
        }

        // Merge existing + new, de-dupe by UsingSpec equality, then sort.
        var merged = new List<UsingSpec>();
        var seen = new HashSet<UsingSpec>();

        foreach (var existing in itemGroup.Elements(ns + "Using").Select(UsingSpec.FromMsBuildElement))
        {
            if (seen.Add(existing))
            {
                merged.Add(existing);
            }
        }

        foreach (var spec in toAdd)
        {
            if (seen.Add(spec))
            {
                merged.Add(spec);
            }
        }

        merged.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        // Replace Using children only; leave any non-Using siblings in place after the sorted block.
        var nonUsing = itemGroup.Elements().Where(e => e.Name != ns + "Using").ToList();
        itemGroup.RemoveAll();

        foreach (var spec in merged)
        {
            itemGroup.Add(spec.ToMsBuildElement(ns));
        }

        foreach (var other in nonUsing)
        {
            itemGroup.Add(other);
        }

        return Write(document, csprojText);
    }

    /// <summary>
    /// True if the project file already declares an equivalent <c>&lt;Using /&gt;</c>.
    /// </summary>
    public static bool ContainsUsing(string csprojText, UsingSpec spec)
    {
        if (string.IsNullOrWhiteSpace(csprojText))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(csprojText, LoadOptions.None);
            var root = document.Root;
            if (root == null)
            {
                return false;
            }

            var ns = root.Name.Namespace;
            return root.Descendants(ns + "Using")
                .Select(UsingSpec.FromMsBuildElement)
                .Any(existing => existing.Equals(spec));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string Write(XDocument document, string originalText)
    {
        // Prefer UTF-8 without BOM; keep an XML declaration only if the original had one.
        var hadDeclaration = originalText.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);

        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = !hadDeclaration,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = DetectNewLine(originalText),
            NewLineHandling = System.Xml.NewLineHandling.Replace,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        var builder = new StringBuilder();
        using (var writer = System.Xml.XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        var result = builder.ToString();

        // XmlWriter may not end with a trailing newline; many csproj files do.
        if (originalText.EndsWith("\n", StringComparison.Ordinal) && !result.EndsWith("\n", StringComparison.Ordinal))
        {
            result += DetectNewLine(originalText);
        }

        return result;
    }

    private static string DetectNewLine(string text)
    {
        if (text.Contains("\r\n"))
        {
            return "\r\n";
        }

        return "\n";
    }
}
