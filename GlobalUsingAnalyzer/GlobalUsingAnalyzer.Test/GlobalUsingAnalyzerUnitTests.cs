using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VerifyCS = GlobalUsingAnalyzer.Test.CSharpCodeFixVerifier<
    GlobalUsingAnalyzer.GlobalUsingAnalyzerAnalyzer,
    GlobalUsingAnalyzer.GlobalUsingAnalyzerCodeFixProvider>;

namespace GlobalUsingAnalyzer.Test;

[TestClass]
public class GlobalUsingAnalyzerUnitTest
{
    // Empty file → no usings → no diagnostics.
    [TestMethod]
    public async Task EmptyFile_NoDiagnostic()
    {
        await VerifyCS.VerifyAnalyzerAsync("");
    }

    // A plain using should be reported.
    [TestMethod]
    public async Task OrdinaryUsing_ReportsDiagnostic()
    {
        var test = @"
{|#0:using System;|}

class C
{
}
";

        var expected = VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("System");

        await VerifyCS.VerifyAnalyzerAsync(test, expected);
    }

    // using static and aliases are also reported.
    [TestMethod]
    public async Task StaticAndAliasUsings_ReportDiagnostics()
    {
        var test = @"
{|#0:using static System.Math;|}
{|#1:using IO = System.IO;|}

class C
{
}
";

        await VerifyCS.VerifyAnalyzerAsync(
            test,
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments("static System.Math"),
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(1)
                .WithArguments("IO = System.IO"));
    }

    // global using outside ZGlobalUsings.cs should still be offered a move.
    [TestMethod]
    public async Task GlobalUsingOutsideZGlobalUsings_ReportsDiagnostic()
    {
        var test = @"
{|#0:global using System;|}

class C
{
}
";

        await VerifyCS.VerifyAnalyzerAsync(
            test,
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments("System"));
    }

    // Usings in ZGlobalUsings.cs are also reported (so the .csproj fix can move them).
    [TestMethod]
    public async Task UsingsInZGlobalUsings_ReportDiagnostics()
    {
        var test = new VerifyCS.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#0:global using System;|}" + Environment.NewLine
                        + "{|#1:global using static System.Math;|}" + Environment.NewLine
                        + "{|#2:global using IO = System.IO;|}" + Environment.NewLine),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("static System.Math"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(2)
                    .WithArguments("IO = System.IO"),
            },
        };

        await test.RunAsync();
    }

    // Move a misplaced global using into ZGlobalUsings.cs.
    [TestMethod]
    public async Task CodeFix_MovesGlobalUsingIntoZGlobalUsings()
    {
        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:global using System;|}

class C
{
}
",
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine),
                },
                ExpectedDiagnostics =
                {
                    // Residual: still flagged so .csproj move remains available.
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(1)
                        .WithArguments("System"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
            },
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // If ZGlobalUsings already has it, only remove the duplicate global using elsewhere.
    [TestMethod]
    public async Task CodeFix_GlobalUsingAlreadyInZGlobalUsings_OnlyRemovesDuplicate()
    {
        var test = new VerifyCS.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
{|#0:global using System;|}

class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine),
                },
            },
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine),
                },
                ExpectedDiagnostics =
                {
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(1)
                        .WithArguments("System"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("System"),
            },
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // Static and alias usings promote with the correct global using form.
    [TestMethod]
    public async Task CodeFix_StaticAndAliasUsings()
    {
        var fixedClass = @"
class C
{
}
";
        var fixedGlobalUsings =
            "{|#2:global using static System.Math;|}" + Environment.NewLine
            + "{|#3:global using IO = System.IO;|}" + Environment.NewLine;

        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:using static System.Math;|}
{|#1:using IO = System.IO;|}

class C
{
}
",
            FixedState =
            {
                Sources =
                {
                    fixedClass,
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, fixedGlobalUsings),
                },
                ExpectedDiagnostics =
                {
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(2)
                        .WithArguments("static System.Math"),
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(3)
                        .WithArguments("IO = System.IO"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("static System.Math"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("IO = System.IO"),
            },
            NumberOfIncrementalIterations = 2,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // Applying the fix removes the local using and creates ZGlobalUsings.cs.
    [TestMethod]
    public async Task CodeFix_CreatesZGlobalUsingsAndRemovesLocalUsing()
    {
        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:using System;|}

class C
{
}
",
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine),
                },
                ExpectedDiagnostics =
                {
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(1)
                        .WithArguments("System"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
            },
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // If ZGlobalUsings.cs already has the global using, only remove the local one.
    [TestMethod]
    public async Task CodeFix_AppendsWhenFileExists_SkipsDuplicate()
    {
        var test = new VerifyCS.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
{|#0:using System.Collections.Generic;|}

class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine),
                },
            },
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#1:global using System;|}" + Environment.NewLine
                        + "{|#2:global using System.Collections.Generic;|}" + Environment.NewLine),
                },
                ExpectedDiagnostics =
                {
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(1)
                        .WithArguments("System"),
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(2)
                        .WithArguments("System.Collections.Generic"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System.Collections.Generic"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("System"),
            },
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // Two single fixes in a row (simulates clicking the lightbulb twice).
    [TestMethod]
    public async Task CodeFix_TwoUsings_AppliedIncrementally()
    {
        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:using System;|}
{|#1:using System.Linq;|}

class C
{
}
",
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#2:global using System;|}" + Environment.NewLine
                        + "{|#3:global using System.Linq;|}" + Environment.NewLine),
                },
                ExpectedDiagnostics =
                {
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(2)
                        .WithArguments("System"),
                    VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                        .WithLocation(3)
                        .WithArguments("System.Linq"),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("System.Linq"),
            },
            NumberOfIncrementalIterations = 2,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        await test.RunAsync();
    }

    // Fix All in Document: one invocation moves every using in the file.
    [TestMethod]
    public async Task FixAll_InDocument_MovesAllUsings()
    {
        var fixedClass = @"
class C
{
}
";
        var fixedGlobalUsings =
            "{|#2:global using System;|}" + Environment.NewLine
            + "{|#3:global using System.Linq;|}" + Environment.NewLine;

        var residual = new[]
        {
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(2)
                .WithArguments("System"),
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(3)
                .WithArguments("System.Linq"),
        };

        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:using System;|}
{|#1:using System.Linq;|}

class C
{
}
",
            FixedState =
            {
                Sources =
                {
                    fixedClass,
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, fixedGlobalUsings),
                },
                ExpectedDiagnostics = { residual[0], residual[1] },
            },
            BatchFixedState =
            {
                Sources =
                {
                    fixedClass,
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, fixedGlobalUsings),
                },
                ExpectedDiagnostics = { residual[0], residual[1] },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("System.Linq"),
            },
            NumberOfIncrementalIterations = 2,
            NumberOfFixAllInDocumentIterations = 1,
            NumberOfFixAllInProjectIterations = 1,
            NumberOfFixAllIterations = 1,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
        };

        await test.RunAsync();
    }

    // Fix All in Project: usings from two files land in one ZGlobalUsings.cs.
    [TestMethod]
    public async Task FixAll_InProject_MovesUsingsFromMultipleDocuments()
    {
        var globalUsings =
            "{|#2:global using System;|}" + Environment.NewLine
            + "{|#3:global using System.Collections.Generic;|}" + Environment.NewLine;

        var residual = new[]
        {
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(2)
                .WithArguments("System"),
            VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                .WithLocation(3)
                .WithArguments("System.Collections.Generic"),
        };

        var test = new VerifyCS.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
{|#0:using System;|}

class A
{
}
",
                    @"
{|#1:using System.Collections.Generic;|}

class B
{
}
",
                },
            },
            FixedState =
            {
                Sources =
                {
                    @"
class A
{
}
",
                    @"
class B
{
}
",
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, globalUsings),
                },
                ExpectedDiagnostics = { residual[0], residual[1] },
            },
            BatchFixedState =
            {
                Sources =
                {
                    @"
class A
{
}
",
                    @"
class B
{
}
",
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, globalUsings),
                },
                ExpectedDiagnostics = { residual[0], residual[1] },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("System.Collections.Generic"),
            },
            NumberOfIncrementalIterations = 2,
            NumberOfFixAllInDocumentIterations = 2,
            NumberOfFixAllInProjectIterations = 1,
            NumberOfFixAllIterations = 1,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyZGlobalUsings,
        };

        await test.RunAsync();
    }

    // --- .csproj <Using /> destination ---

    [TestMethod]
    public async Task CodeFix_MoveToCsproj_CreatesUsingItemGroup()
    {
        const string projectPath = "TestProject.csproj";

        var originalCsproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
";

        var test = new VerifyCS.Test
        {
            TestCode = @"
{|#0:using System;|}

class C
{
}
",
            FixedCode = @"
class C
{
}
",
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
            },
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyCsproj,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        test.TestState.AdditionalFiles.Add((projectPath, originalCsproj));
        test.FixedState.AdditionalFiles.Add((projectPath, string.Empty)); // filled after transform expectation

        // Wire project.FilePath and assert fixed csproj via custom verifier after run is hard;
        // instead put expected content in FixedState after we know formatting.
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            solution = solution.WithProjectFilePath(projectId, projectPath);
            return solution;
        });

        // Expected fixed csproj (XmlWriter indented).
        var expectedCsproj = ProjectFileUsingEditor.AddUsings(
            originalCsproj,
            new[] { new UsingSpec("System") });

        test.FixedState.AdditionalFiles.Clear();
        test.FixedState.AdditionalFiles.Add((projectPath, expectedCsproj));
        test.TestState.AdditionalFiles.Clear();
        test.TestState.AdditionalFiles.Add((projectPath, originalCsproj));

        await test.RunAsync();
    }

    [TestMethod]
    public async Task CodeFix_MoveToCsproj_FromZGlobalUsings_StaticAndAlias_Sorted()
    {
        const string projectPath = "TestProject.csproj";

        var originalCsproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Linq"" />
  </ItemGroup>
</Project>
";

        var expectedCsproj = ProjectFileUsingEditor.AddUsings(
            originalCsproj,
            new[]
            {
                new UsingSpec("System.Math", isStatic: true),
                new UsingSpec("System.IO", alias: "IO"),
            });

        var test = new VerifyCS.Test
        {
            TestState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    (
                        GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName,
                        "{|#0:global using static System.Math;|}" + Environment.NewLine
                        + "{|#1:global using IO = System.IO;|}" + Environment.NewLine),
                },
                AdditionalFiles =
                {
                    (projectPath, originalCsproj),
                },
            },
            FixedState =
            {
                Sources =
                {
                    @"
class C
{
}
",
                    // File may remain empty (or whitespace-only) after removals.
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, string.Empty),
                },
                AdditionalFiles =
                {
                    (projectPath, expectedCsproj),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("static System.Math"),
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(1)
                    .WithArguments("IO = System.IO"),
            },
            NumberOfIncrementalIterations = 2,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKeyCsproj,
            CodeFixTestBehaviors =
                CodeFixTestBehaviors.SkipFixAllInDocumentCheck
                | CodeFixTestBehaviors.SkipFixAllInProjectCheck
                | CodeFixTestBehaviors.SkipFixAllInSolutionCheck,
        };

        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectFilePath(projectId, projectPath));

        await test.RunAsync();
    }

    // --- GUA003: MVC .cshtml → _ViewImports.cshtml; Blazor .razor → _Imports.razor ---

    [TestMethod]
    public async Task ApplyMove_Cshtml_CreatesViewImportsAtRoot()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "gua-proj-" + Path.GetRandomFileName(), "App.csproj");
        var projectDir = Path.GetDirectoryName(projectPath);
        Directory.CreateDirectory(projectDir);

        var pagesDir = Path.Combine(projectDir, "Pages");
        Directory.CreateDirectory(pagesDir);
        var cshtmlPath = Path.Combine(pagesDir, "Index.cshtml");
        var cshtml = "@using System.Linq\n@page\n";
        File.WriteAllText(cshtmlPath, cshtml);

        try
        {
            var (solution, projectId) = CreateRazorTestSolution(projectPath, cshtmlPath, cshtml, "Index.cshtml");
            var diagnostics = CreateRazorDiagnostics(cshtmlPath, "System.Linq");

            var updated = await GlobalUsingAnalyzerCodeFixProvider
                .ApplyMoveToImportsRazorAsync(solution, diagnostics, CancellationToken.None)
                .ConfigureAwait(false);

            var importsPath = Path.Combine(projectDir, "_ViewImports.cshtml");
            var importsDoc = TextDocumentPathHelper.FindTextDocument(updated, importsPath);
            Assert.IsNotNull(importsDoc, "Expected _ViewImports.cshtml at project root.");
            var importsText = (await importsDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            StringAssert.Contains(importsText, "@using System.Linq");

            var cshtmlDoc = TextDocumentPathHelper.FindTextDocument(updated, cshtmlPath);
            Assert.IsNotNull(cshtmlDoc);
            var cshtmlText = (await cshtmlDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            Assert.IsFalse(cshtmlText.Contains("@using System.Linq"));
            StringAssert.Contains(cshtmlText, "@page");

            // Must not create Blazor imports for MVC source.
            Assert.IsNull(TextDocumentPathHelper.FindTextDocument(
                updated, Path.Combine(projectDir, "_Imports.razor")));
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [TestMethod]
    public async Task ApplyMove_Razor_CreatesImportsRazorAtRoot()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "gua-proj-" + Path.GetRandomFileName(), "App.csproj");
        var projectDir = Path.GetDirectoryName(projectPath);
        var componentsDir = Path.Combine(projectDir, "Components");
        Directory.CreateDirectory(componentsDir);
        var razorPath = Path.Combine(componentsDir, "Counter.razor");
        File.WriteAllText(razorPath, "@using System.Linq\n<h1>Hi</h1>\n");

        try
        {
            var (solution, _) = CreateRazorTestSolution(projectPath, razorPath, "@using System.Linq\n<h1>Hi</h1>\n", "Counter.razor");
            var diagnostics = CreateRazorDiagnostics(razorPath, "System.Linq");

            var updated = await GlobalUsingAnalyzerCodeFixProvider
                .ApplyMoveToImportsRazorAsync(solution, diagnostics, CancellationToken.None)
                .ConfigureAwait(false);

            var importsPath = Path.Combine(projectDir, "_Imports.razor");
            var importsDoc = TextDocumentPathHelper.FindTextDocument(updated, importsPath);
            Assert.IsNotNull(importsDoc, "Expected _Imports.razor at project root.");
            StringAssert.Contains(
                (await importsDoc.GetTextAsync().ConfigureAwait(false)).ToString(),
                "@using System.Linq");

            Assert.IsNull(TextDocumentPathHelper.FindTextDocument(
                updated, Path.Combine(projectDir, "_ViewImports.cshtml")));
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [TestMethod]
    public async Task ApplyMove_Cshtml_UsesNearestExistingViewImports()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "gua-proj-" + Path.GetRandomFileName(), "App.csproj");
        var projectDir = Path.GetDirectoryName(projectPath);
        var pagesDir = Path.Combine(projectDir, "Pages");
        Directory.CreateDirectory(pagesDir);

        var existingImports = Path.Combine(pagesDir, "_ViewImports.cshtml");
        File.WriteAllText(existingImports, "@using System\n");

        var cshtmlPath = Path.Combine(pagesDir, "Index.cshtml");
        File.WriteAllText(cshtmlPath, "@using System.Linq\n");

        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .WithProjectFilePath(projectId, projectPath)
                .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.CSharp10));

            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Program.cs", "class C { }");
            var project = solution.GetProject(projectId);
            project = project.AddAdditionalDocument("_ViewImports.cshtml", "@using System\n", filePath: existingImports).Project;
            project = project.AddAdditionalDocument("Index.cshtml", "@using System.Linq\n", filePath: cshtmlPath).Project;
            solution = project.Solution;

            var updated = await GlobalUsingAnalyzerCodeFixProvider
                .ApplyMoveToImportsRazorAsync(solution, CreateRazorDiagnostics(cshtmlPath, "System.Linq"), CancellationToken.None)
                .ConfigureAwait(false);

            var importsDoc = TextDocumentPathHelper.FindTextDocument(updated, existingImports);
            Assert.IsNotNull(importsDoc);
            var importsText = (await importsDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            StringAssert.Contains(importsText, "@using System");
            StringAssert.Contains(importsText, "@using System.Linq");

            var rootImports = Path.Combine(projectDir, "_ViewImports.cshtml");
            Assert.IsFalse(string.Equals(existingImports, rootImports, StringComparison.OrdinalIgnoreCase));
            var rootDoc = TextDocumentPathHelper.FindTextDocument(updated, rootImports);
            if (rootDoc != null)
            {
                var rootText = (await rootDoc.GetTextAsync().ConfigureAwait(false)).ToString();
                Assert.IsFalse(
                    rootText.Contains("System.Linq"),
                    "System.Linq should land in Pages/_ViewImports.cshtml, not project root.");
            }
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    private static (Solution Solution, ProjectId ProjectId) CreateRazorTestSolution(
        string projectPath,
        string sourcePath,
        string sourceText,
        string sourceName)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "App", "App", LanguageNames.CSharp)
            .WithProjectFilePath(projectId, projectPath)
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.CSharp10));

        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Program.cs", "class C { }");
        var project = solution.GetProject(projectId);
        project = project.AddAdditionalDocument(sourceName, sourceText, filePath: sourcePath).Project;
        return (project.Solution, projectId);
    }

    private static ImmutableArray<Diagnostic> CreateRazorDiagnostics(string sourcePath, string identity)
    {
        var descriptor = new DiagnosticDescriptor(
            GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId,
            "t",
            "Razor @using '{0}' can be moved to imports file",
            "Style",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        var emptySpan = new TextSpan(0, 0);
        var emptyLine = new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0));
        var properties = ImmutableDictionary<string, string>.Empty
            .Add(GlobalUsingAnalyzerAnalyzer.UsingIdentityProperty, identity)
            .Add(GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty, sourcePath);

        return ImmutableArray.Create(
            Diagnostic.Create(
                descriptor,
                Location.Create("Program.cs", emptySpan, emptyLine),
                properties,
                identity));
    }

    [TestMethod]
    public async Task ApplyMove_WhenAlreadyInRootImports_OnlyRemovesLocalDuplicate()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "gua-proj-" + Path.GetRandomFileName(), "App.csproj");
        var projectDir = Path.GetDirectoryName(projectPath);
        var pagesDir = Path.Combine(projectDir, "Pages");
        Directory.CreateDirectory(pagesDir);

        var rootImports = Path.Combine(projectDir, "_Imports.razor");
        File.WriteAllText(rootImports, "@using System.Linq\n");

        var razorPath = Path.Combine(pagesDir, "Index.razor");
        File.WriteAllText(razorPath, "@using System.Linq\n<h1>Hi</h1>\n");

        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .WithProjectFilePath(projectId, projectPath)
                .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.CSharp10));

            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Program.cs", "class C { }");
            var project = solution.GetProject(projectId);
            project = project.AddAdditionalDocument("_Imports.razor", "@using System.Linq\n", filePath: rootImports).Project;
            project = project.AddAdditionalDocument("Index.razor", "@using System.Linq\n<h1>Hi</h1>\n", filePath: razorPath).Project;
            solution = project.Solution;

            var updated = await GlobalUsingAnalyzerCodeFixProvider
                .ApplyMoveToImportsRazorAsync(solution, CreateRazorDiagnostics(razorPath, "System.Linq"), CancellationToken.None)
                .ConfigureAwait(false);

            // Local duplicate removed.
            var pageDoc = TextDocumentPathHelper.FindTextDocument(updated, razorPath);
            Assert.IsNotNull(pageDoc);
            var pageText = (await pageDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            Assert.IsFalse(pageText.Contains("@using System.Linq"));
            StringAssert.Contains(pageText, "<h1>Hi</h1>");

            // Root imports unchanged (still has System.Linq once).
            var importsDoc = TextDocumentPathHelper.FindTextDocument(updated, rootImports);
            Assert.IsNotNull(importsDoc);
            var importsText = (await importsDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            Assert.AreEqual(1, RazorUsingEditor.EnumerateUsings(importsText).Count);

            // Must not create an imports file in the parent of the project.
            var parentImports = Path.Combine(Path.GetDirectoryName(projectDir), "_Imports.razor");
            Assert.IsFalse(File.Exists(parentImports), "Must not create _Imports.razor outside the project.");
            Assert.IsNull(TextDocumentPathHelper.FindTextDocument(updated, parentImports));
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    /// <summary>
    /// Blazor Web App layout: solution folder + Server + Client. Promote from Client\Routes.razor
    /// must use Client\_Imports.razor, never create solutionFolder\_Imports.razor.
    /// </summary>
    [TestMethod]
    public async Task ApplyMove_BlazorWebApp_UsesClientImports_NotSolutionFolder()
    {
        var solutionDir = Path.Combine(Path.GetTempPath(), "gua-bwa-" + Path.GetRandomFileName());
        var serverDir = Path.Combine(solutionDir, "BlazorApp1");
        var clientDir = Path.Combine(solutionDir, "BlazorApp1.Client");
        Directory.CreateDirectory(serverDir);
        Directory.CreateDirectory(clientDir);

        var serverCsproj = Path.Combine(serverDir, "BlazorApp1.csproj");
        var clientCsproj = Path.Combine(clientDir, "BlazorApp1.Client.csproj");
        File.WriteAllText(serverCsproj, "<Project />");
        File.WriteAllText(clientCsproj, "<Project />");

        var clientImports = Path.Combine(clientDir, "_Imports.razor");
        File.WriteAllText(clientImports, "@using System\n");

        var routesPath = Path.Combine(clientDir, "Routes.razor");
        File.WriteAllText(routesPath, "@using System.Linq\n");

        try
        {
            var workspace = new AdhocWorkspace();
            var serverId = ProjectId.CreateNewId();
            var clientId = ProjectId.CreateNewId();

            var solution = workspace.CurrentSolution
                .AddProject(serverId, "BlazorApp1", "BlazorApp1", LanguageNames.CSharp)
                .WithProjectFilePath(serverId, serverCsproj)
                .AddProject(clientId, "BlazorApp1.Client", "BlazorApp1.Client", LanguageNames.CSharp)
                .WithProjectFilePath(clientId, clientCsproj)
                .WithProjectParseOptions(clientId, new CSharpParseOptions(LanguageVersion.CSharp10));

            solution = solution.AddDocument(DocumentId.CreateNewId(serverId), "Program.cs", "class S { }");
            solution = solution.AddDocument(DocumentId.CreateNewId(clientId), "Program.cs", "class C { }");

            var client = solution.GetProject(clientId);
            client = client.AddAdditionalDocument("_Imports.razor", "@using System\n", filePath: clientImports).Project;
            client = client.AddAdditionalDocument("Routes.razor", "@using System.Linq\n", filePath: routesPath).Project;
            solution = client.Solution;

            // Diagnostic primary location intentionally on the *server* C# doc (wrong host) —
            // owning project must still resolve to Client via source path.
            var diagnostics = CreateRazorDiagnostics(routesPath, "System.Linq");

            var updated = await GlobalUsingAnalyzerCodeFixProvider
                .ApplyMoveToImportsRazorAsync(solution, diagnostics, CancellationToken.None)
                .ConfigureAwait(false);

            var importsDoc = TextDocumentPathHelper.FindTextDocument(updated, clientImports);
            Assert.IsNotNull(importsDoc);
            var importsText = (await importsDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            StringAssert.Contains(importsText, "@using System.Linq");
            StringAssert.Contains(importsText, "@using System");

            // Solution folder must not get a rogue _Imports.razor.
            var solutionImports = Path.Combine(solutionDir, "_Imports.razor");
            Assert.IsFalse(File.Exists(solutionImports));
            Assert.IsNull(TextDocumentPathHelper.FindTextDocument(updated, solutionImports));

            // Server project folder must not get one either.
            var serverImports = Path.Combine(serverDir, "_Imports.razor");
            Assert.IsFalse(File.Exists(serverImports));
        }
        finally
        {
            TryDeleteDirectory(solutionDir);
        }
    }

    // --- GUA003 analyzer reporting (must compile + produce diagnostics) ---

    [TestMethod]
    public async Task Analyzer_ReportsGua003_FromAdditionalFiles()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "gua-anal-af-" + Path.GetRandomFileName());
        Directory.CreateDirectory(projectDir);
        var programPath = Path.Combine(projectDir, "Program.cs");
        var razorPath = Path.Combine(projectDir, "Counter.razor");
        File.WriteAllText(programPath, "class C { }");
        File.WriteAllText(razorPath, "@using System.Linq\n<h1>Hi</h1>\n");
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), "<Project />");

        try
        {
            var diagnostics = await RunAnalyzerAsync(
                programPath,
                additionalFiles: new[] { (razorPath, File.ReadAllText(razorPath)) },
                diskOnly: false).ConfigureAwait(false);

            var gua003 = diagnostics.Where(d => d.Id == GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId).ToArray();
            Assert.AreEqual(1, gua003.Length, "Expected GUA003 from AdditionalFiles. All: " + Describe(diagnostics));
            StringAssert.Contains(gua003[0].GetMessage(), "System.Linq");
            StringAssert.Contains(gua003[0].GetMessage(), "Counter.razor");
            Assert.AreEqual(
                razorPath,
                gua003[0].Properties[GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty],
                ignoreCase: true);
            // Primary is the .razor path (external location) so Error List associates with that file.
            Assert.IsFalse(
                string.IsNullOrEmpty(gua003[0].Location.GetLineSpan().Path),
                "Primary location must point at the Razor source path.");
            var reportedPath = gua003[0].Location.GetLineSpan().Path
                ?? gua003[0].Properties[GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty];
            // MSTest: EndsWith(value, substring) — value must end with substring.
            StringAssert.EndsWith(reportedPath, "Counter.razor");
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [TestMethod]
    public async Task Analyzer_ReportsGua003_FromDiskScan_WithoutAdditionalFiles()
    {
        // VSIX path: no NuGet props → no AdditionalFiles → walk project dir from .csproj next to sources.
        var projectDir = Path.Combine(Path.GetTempPath(), "gua-anal-disk-" + Path.GetRandomFileName());
        Directory.CreateDirectory(projectDir);
        var programPath = Path.Combine(projectDir, "Program.cs");
        var componentsDir = Path.Combine(projectDir, "Components");
        Directory.CreateDirectory(componentsDir);
        var razorPath = Path.Combine(componentsDir, "Counter.razor");
        File.WriteAllText(programPath, "class C { }");
        File.WriteAllText(razorPath, "@using System.Collections.Generic\n<button />\n");
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), "<Project />");

        try
        {
            var diagnostics = await RunAnalyzerAsync(
                programPath,
                additionalFiles: Array.Empty<(string, string)>(),
                diskOnly: true).ConfigureAwait(false);

            var gua003 = diagnostics.Where(d => d.Id == GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId).ToArray();
            Assert.AreEqual(1, gua003.Length, "Expected GUA003 from disk scan. All: " + Describe(diagnostics));
            StringAssert.Contains(gua003[0].GetMessage(), "System.Collections.Generic");
            Assert.AreEqual(
                razorPath,
                gua003[0].Properties[GlobalUsingAnalyzerAnalyzer.RazorSourcePathProperty],
                ignoreCase: true);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [TestMethod]
    public async Task Analyzer_SkipsGua003_WhenUsingAlreadyInImports()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "gua-anal-skip-" + Path.GetRandomFileName());
        Directory.CreateDirectory(projectDir);
        var programPath = Path.Combine(projectDir, "Program.cs");
        var razorPath = Path.Combine(projectDir, "Counter.razor");
        var importsPath = Path.Combine(projectDir, "_Imports.razor");
        File.WriteAllText(programPath, "class C { }");
        File.WriteAllText(importsPath, "@using System.Linq\n");
        File.WriteAllText(razorPath, "@using System.Linq\n<h1>Hi</h1>\n");
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), "<Project />");

        try
        {
            var diagnostics = await RunAnalyzerAsync(
                programPath,
                additionalFiles: new[]
                {
                    (importsPath, File.ReadAllText(importsPath)),
                    (razorPath, File.ReadAllText(razorPath)),
                },
                diskOnly: false).ConfigureAwait(false);

            var gua003 = diagnostics.Where(d => d.Id == GlobalUsingAnalyzerAnalyzer.RazorUsingDiagnosticId).ToArray();
            Assert.AreEqual(0, gua003.Length, "Inherited @using must not produce GUA003. All: " + Describe(diagnostics));
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(
        string programPath,
        IReadOnlyList<(string Path, string Content)> additionalFiles,
        bool diskOnly)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(programPath), path: programPath);
        var compilation = CSharpCompilation.Create(
            "App",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additionalTexts = diskOnly
            ? ImmutableArray<AdditionalText>.Empty
            : additionalFiles
                .Select(f => (AdditionalText)new TestAdditionalText(f.Path, f.Content))
                .ToImmutableArray();

        var options = new AnalyzerOptions(additionalTexts);
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new GlobalUsingAnalyzerAnalyzer()),
            options);

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
    }

    private static string Describe(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.IsDefaultOrEmpty
            ? "(none)"
            : string.Join("; ", diagnostics.Select(d => d.Id + ": " + d.GetMessage()));

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public TestAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content ?? string.Empty);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static void TryDeleteDirectory(string projectDir)
    {
        try
        {
            Directory.Delete(projectDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}


