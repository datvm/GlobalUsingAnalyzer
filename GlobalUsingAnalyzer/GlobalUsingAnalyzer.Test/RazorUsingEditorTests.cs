using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlobalUsingAnalyzer.Test;

[TestClass]
public class RazorUsingEditorTests
{
    [TestMethod]
    public void EnumerateUsings_ParsesPlainStaticAndAlias()
    {
        var text = @"
@page
@using System.Linq
@using static System.Math
@using IO = System.IO
<div></div>
";

        var found = RazorUsingEditor.EnumerateUsings(text);
        CollectionAssert.AreEqual(
            new[] { "System.Linq", "static System.Math", "IO = System.IO" },
            found.Select(o => o.Spec.Identity).ToArray());
    }

    [TestMethod]
    public void RemoveUsings_RemovesMatchingLines()
    {
        var text = "@using System\r\n@using System.Linq\r\n@using System.Text\r\n";
        var result = RazorUsingEditor.RemoveUsings(
            text,
            new List<UsingSpec> { new UsingSpec("System.Linq") });

        StringAssert.Contains(result, "@using System");
        StringAssert.Contains(result, "@using System.Text");
        Assert.IsFalse(result.Contains("System.Linq"));
    }

    [TestMethod]
    public void AddUsings_MergesWithoutDuplicates_AndSorts()
    {
        var text = "@using System.Linq\n@using System\n";
        var result = RazorUsingEditor.AddUsings(
            text,
            new List<UsingSpec>
            {
                new UsingSpec("System"),
                new UsingSpec("System.Collections.Generic"),
                new UsingSpec("System.IO", alias: "IO"),
                new UsingSpec("System.Math", isStatic: true),
            });

        // SortKey order (ordinal-ignore-case): IO, System, System.Collections.Generic, System.Linq, System.Math
        var specs = RazorUsingEditor.EnumerateUsings(result).Select(o => o.Spec.Identity).ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                "IO = System.IO",
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "static System.Math",
            },
            specs);
    }

    [TestMethod]
    public void AddUsings_PreservesNonUsingLines_AroundSortedBlock()
    {
        var text = "@page \"/\"\n@using System.Linq\n@using System\n@inject IFoo Foo\n";
        var result = RazorUsingEditor.AddUsings(
            text,
            new List<UsingSpec> { new UsingSpec("System.Text") });

        StringAssert.StartsWith(result.TrimStart(), "@page");
        StringAssert.Contains(result, "@inject IFoo Foo");

        var specs = RazorUsingEditor.EnumerateUsings(result).Select(o => o.Spec.Identity).ToList();
        CollectionAssert.AreEqual(
            new[] { "System", "System.Linq", "System.Text" },
            specs);

        // Sorted block sits where first @using was (after @page).
        var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();
        Assert.AreEqual("@page \"/\"", lines[0]);
        Assert.AreEqual("@using System", lines[1]);
        Assert.AreEqual("@using System.Linq", lines[2]);
        Assert.AreEqual("@using System.Text", lines[3]);
        Assert.AreEqual("@inject IFoo Foo", lines[4]);
    }

    [TestMethod]
    public void SortUsings_ReordersExistingOnly()
    {
        var text = "@using System.Linq\n@using System\n@using static System.Math\n";
        var result = RazorUsingEditor.SortUsings(text);

        CollectionAssert.AreEqual(
            new[] { "System", "System.Linq", "static System.Math" },
            RazorUsingEditor.EnumerateUsings(result).Select(o => o.Spec.Identity).ToArray());
    }

    [TestMethod]
    public void GetImportsFileNameForSource_MvcVsBlazor()
    {
        Assert.AreEqual(
            RazorUsingEditor.MvcViewImportsFileName,
            RazorUsingEditor.GetImportsFileNameForSource("Pages/Index.cshtml"));
        Assert.AreEqual(
            RazorUsingEditor.BlazorImportsFileName,
            RazorUsingEditor.GetImportsFileNameForSource("Components/Counter.razor"));
    }

    [TestMethod]
    public void IsAnalyzableRazorSourceFile_ExcludesImportsFiles()
    {
        Assert.IsTrue(RazorUsingEditor.IsAnalyzableRazorSourceFile("Pages/Index.cshtml"));
        Assert.IsTrue(RazorUsingEditor.IsAnalyzableRazorSourceFile("Counter.razor"));
        Assert.IsFalse(RazorUsingEditor.IsAnalyzableRazorSourceFile("_ViewImports.cshtml"));
        Assert.IsFalse(RazorUsingEditor.IsAnalyzableRazorSourceFile("Pages/_ViewImports.cshtml"));
        Assert.IsFalse(RazorUsingEditor.IsAnalyzableRazorSourceFile("_Imports.razor"));
        Assert.IsFalse(RazorUsingEditor.IsAnalyzableRazorSourceFile("Components/_Imports.razor"));
    }

    [TestMethod]
    public void FindNearestImportsPath_MvcAndBlazorTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-razor-test-" + Path.GetRandomFileName());
        var nested = Path.Combine(root, "Pages", "Admin");
        Directory.CreateDirectory(nested);

        try
        {
            // MVC: no known imports → project root _ViewImports.cshtml
            var mvcTarget = RazorUsingEditor.FindNearestImportsPath(
                nested, root, RazorUsingEditor.MvcViewImportsFileName, existingImportsPaths: null);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(root, "_ViewImports.cshtml")),
                mvcTarget);

            // Blazor: mid-level _Imports.razor listed
            var midBlazor = Path.GetFullPath(Path.Combine(root, "Pages", "_Imports.razor"));
            var blazorTarget = RazorUsingEditor.FindNearestImportsPath(
                nested, root, RazorUsingEditor.BlazorImportsFileName, new[] { midBlazor });
            Assert.AreEqual(midBlazor, blazorTarget);

            // MVC mid-level should not be selected when looking for Blazor imports name
            var midMvc = Path.GetFullPath(Path.Combine(root, "Pages", "_ViewImports.cshtml"));
            blazorTarget = RazorUsingEditor.FindNearestImportsPath(
                nested, root, RazorUsingEditor.BlazorImportsFileName, new[] { midMvc });
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(root, "_Imports.razor")),
                blazorTarget);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    [TestMethod]
    public void TryParseRazorUsingLine_AcceptsOptionalSemicolon()
    {
        Assert.IsTrue(UsingSpec.TryParseRazorUsingLine("  @using System.Linq;  ", out var spec));
        Assert.AreEqual("System.Linq", spec.Identity);
    }

    [TestMethod]
    public void CollectInheritedUsings_RootImportsApplyToNestedSource()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gua-inh-" + Path.GetRandomFileName()));
        var nested = Path.Combine(root, "Pages");
        var rootImports = Path.Combine(root, "_Imports.razor");
        var source = Path.Combine(nested, "Index.razor");

        var inherited = RazorUsingEditor.CollectInheritedUsings(
            source,
            new[]
            {
                new KeyValuePair<string, string>(rootImports, "@using System.Linq\n@using System\n"),
            });

        Assert.IsTrue(inherited.Contains(new UsingSpec("System.Linq")));
        Assert.IsTrue(inherited.Contains(new UsingSpec("System")));
    }

    [TestMethod]
    public void CollectInheritedUsings_SiblingFolderDoesNotApply()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gua-inh-" + Path.GetRandomFileName()));
        var imports = Path.Combine(root, "Admin", "_Imports.razor");
        var source = Path.Combine(root, "Pages", "Index.razor");

        var inherited = RazorUsingEditor.CollectInheritedUsings(
            source,
            new[]
            {
                new KeyValuePair<string, string>(imports, "@using System.Linq\n"),
            });

        Assert.AreEqual(0, inherited.Count);
    }

    [TestMethod]
    public void IsPathInsideProject_RejectsParentFolder()
    {
        var projectDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gua-proj-" + Path.GetRandomFileName()));
        var parentFile = Path.Combine(Path.GetDirectoryName(projectDir), "_Imports.razor");
        var insideFile = Path.Combine(projectDir, "_Imports.razor");

        Assert.IsTrue(RazorUsingEditor.IsPathInsideProject(insideFile, projectDir));
        Assert.IsFalse(RazorUsingEditor.IsPathInsideProject(parentFile, projectDir));
    }
}
