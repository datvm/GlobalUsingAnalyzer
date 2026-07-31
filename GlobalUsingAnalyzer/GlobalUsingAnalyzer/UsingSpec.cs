using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Xml.Linq;

namespace GlobalUsingAnalyzer;

/// <summary>
/// Structured description of a using directive (plain, static, or alias).
/// Shared by diagnostics, ZGlobalUsings.cs lines, and MSBuild <c>&lt;Using /&gt;</c> items.
/// </summary>
public sealed class UsingSpec : IEquatable<UsingSpec>
{
    public UsingSpec(string include, string alias = null, bool isStatic = false)
    {
        Include = include ?? throw new ArgumentNullException(nameof(include));
        Alias = string.IsNullOrEmpty(alias) ? null : alias;
        IsStatic = isStatic;
    }

    /// <summary>Namespace or type name (MSBuild <c>Include</c>).</summary>
    public string Include { get; }

    /// <summary>Alias name when this is <c>using A = N;</c>; otherwise null.</summary>
    public string Alias { get; }

    /// <summary>True for <c>using static</c>.</summary>
    public bool IsStatic { get; }

    /// <summary>
    /// Sort key for .csproj ItemGroup ordering: alias name if present, otherwise the include.
    /// </summary>
    public string SortKey => Alias ?? Include;

    /// <summary>
    /// Canonical identity for diagnostics and ZGlobalUsings.cs
    /// (e.g. <c>System</c>, <c>static System.Math</c>, <c>IO = System.IO</c>).
    /// </summary>
    public string Identity
    {
        get
        {
            if (Alias != null)
            {
                return $"{Alias} = {Include}";
            }

            if (IsStatic)
            {
                return $"static {Include}";
            }

            return Include;
        }
    }

    public static UsingSpec FromSyntax(UsingDirectiveSyntax usingDirective)
    {
        if (usingDirective == null)
        {
            throw new ArgumentNullException(nameof(usingDirective));
        }

        var include = usingDirective.Name?.ToString() ?? string.Empty;
        string alias = null;
        var isStatic = false;

        if (usingDirective.Alias != null)
        {
            alias = usingDirective.Alias.Name.Identifier.ValueText;
        }
        else if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            isStatic = true;
        }

        return new UsingSpec(include, alias, isStatic);
    }

    public string ToGlobalUsingLine() => $"global using {Identity};";

    /// <summary>MSBuild item: <c>&lt;Using Include="…" [Static="True"] [Alias="…"] /&gt;</c>.</summary>
    public XElement ToMsBuildElement(XNamespace ns)
    {
        var element = new XElement(ns + "Using", new XAttribute("Include", Include));

        if (IsStatic)
        {
            element.Add(new XAttribute("Static", "True"));
        }

        if (Alias != null)
        {
            element.Add(new XAttribute("Alias", Alias));
        }

        return element;
    }

    public static UsingSpec FromMsBuildElement(XElement element)
    {
        if (element == null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        var include = (string)element.Attribute("Include") ?? string.Empty;
        var alias = (string)element.Attribute("Alias");
        var staticAttr = (string)element.Attribute("Static");
        var isStatic = string.Equals(staticAttr, "True", StringComparison.OrdinalIgnoreCase)
            || string.Equals(staticAttr, "true", StringComparison.OrdinalIgnoreCase);

        return new UsingSpec(include, alias, isStatic);
    }

    public bool Equals(UsingSpec other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Include, other.Include, StringComparison.Ordinal)
            && string.Equals(Alias, other.Alias, StringComparison.Ordinal)
            && IsStatic == other.IsStatic;
    }

    public override bool Equals(object obj) => obj is UsingSpec other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(Include);
            hash = (hash * 397) ^ (Alias != null ? StringComparer.Ordinal.GetHashCode(Alias) : 0);
            hash = (hash * 397) ^ IsStatic.GetHashCode();
            return hash;
        }
    }

    public override string ToString() => Identity;
}
