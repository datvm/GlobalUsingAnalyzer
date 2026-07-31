using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace GlobalUsingAnalyzer.Test;

[TestClass]
public class ProjectFileUsingEditorTests
{
    [TestMethod]
    public void AddUsings_CreatesItemGroup_WhenNoneExists()
    {
        var input = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
";

        var result = ProjectFileUsingEditor.AddUsings(
            input,
            new List<UsingSpec> { new UsingSpec("System.Linq") });

        StringAssert.Contains(result, "<Using Include=\"System.Linq\" />");
        StringAssert.Contains(result, "<ItemGroup>");
    }

    [TestMethod]
    public void AddUsings_MergesIntoExistingUsingItemGroup_AndSorts()
    {
        var input = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Using Include=""System.Linq"" />
    <Using Include=""System"" />
  </ItemGroup>
</Project>
";

        var result = ProjectFileUsingEditor.AddUsings(
            input,
            new List<UsingSpec>
            {
                new UsingSpec("System.Collections.Generic"),
                new UsingSpec("System.IO", alias: "IO"),
                new UsingSpec("System.Math", isStatic: true),
            });

        // Sorted by SortKey (alias name if present, else Include), ordinal-ignore-case:
        // IO, System, System.Collections.Generic, System.Linq, System.Math
        var usings = result.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("<Using "))
            .ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "<Using Include=\"System.IO\" Alias=\"IO\" />",
                "<Using Include=\"System\" />",
                "<Using Include=\"System.Collections.Generic\" />",
                "<Using Include=\"System.Linq\" />",
                "<Using Include=\"System.Math\" Static=\"True\" />",
            },
            usings);
    }

    [TestMethod]
    public void AddUsings_DoesNotDuplicateExisting()
    {
        var input = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Using Include=""System"" />
  </ItemGroup>
</Project>
";

        var result = ProjectFileUsingEditor.AddUsings(
            input,
            new List<UsingSpec> { new UsingSpec("System") });

        Assert.AreEqual(1, result.Split(new[] { "<Using " }, System.StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void ContainsUsing_FindsAliasAndStatic()
    {
        var input = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Using Include=""System.IO"" Alias=""IO"" />
    <Using Include=""System.Math"" Static=""True"" />
  </ItemGroup>
</Project>
";

        Assert.IsTrue(ProjectFileUsingEditor.ContainsUsing(input, new UsingSpec("System.IO", alias: "IO")));
        Assert.IsTrue(ProjectFileUsingEditor.ContainsUsing(input, new UsingSpec("System.Math", isStatic: true)));
        Assert.IsFalse(ProjectFileUsingEditor.ContainsUsing(input, new UsingSpec("System")));
    }
}
