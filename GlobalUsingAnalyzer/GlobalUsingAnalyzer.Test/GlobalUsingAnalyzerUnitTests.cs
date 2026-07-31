using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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
}
