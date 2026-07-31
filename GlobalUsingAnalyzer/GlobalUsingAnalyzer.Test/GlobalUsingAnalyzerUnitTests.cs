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

    // using static and aliases are left alone.
    [TestMethod]
    public async Task StaticAndAliasUsings_NoDiagnostic()
    {
        var test = @"
using static System.Math;
using IO = System.IO;

class C
{
}
";

        await VerifyCS.VerifyAnalyzerAsync(test);
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
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, "global using System;" + Environment.NewLine),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System"),
            },
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
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, "global using System;" + Environment.NewLine),
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
                        "global using System;" + Environment.NewLine
                        + "global using System.Collections.Generic;" + Environment.NewLine),
                },
            },
            ExpectedDiagnostics =
            {
                VerifyCS.Diagnostic(GlobalUsingAnalyzerAnalyzer.DiagnosticId)
                    .WithLocation(0)
                    .WithArguments("System.Collections.Generic"),
            },
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
                        "global using System;" + Environment.NewLine
                        + "global using System.Linq;" + Environment.NewLine),
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
            // Exact: apply the single fix twice (one diagnostic each time).
            NumberOfIncrementalIterations = 2,
            // Skip Fix All checks here — covered by dedicated tests below.
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
            "global using System;" + Environment.NewLine
            + "global using System.Linq;" + Environment.NewLine;

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
            },
            BatchFixedState =
            {
                Sources =
                {
                    fixedClass,
                    (GlobalUsingAnalyzerAnalyzer.GlobalUsingsFileName, fixedGlobalUsings),
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
            NumberOfFixAllInDocumentIterations = 1,
            NumberOfFixAllInProjectIterations = 1,
            NumberOfFixAllIterations = 1,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKey,
        };

        await test.RunAsync();
    }

    // Fix All in Project: usings from two files land in one ZGlobalUsings.cs.
    [TestMethod]
    public async Task FixAll_InProject_MovesUsingsFromMultipleDocuments()
    {
        var globalUsings =
            "global using System;" + Environment.NewLine
            + "global using System.Collections.Generic;" + Environment.NewLine;

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
            NumberOfFixAllInDocumentIterations = 2, // one Fix-all-doc pass per file
            NumberOfFixAllInProjectIterations = 1,  // one Fix-all-project pass for both
            NumberOfFixAllIterations = 1,
            CodeActionEquivalenceKey = GlobalUsingAnalyzerCodeFixProvider.EquivalenceKey,
        };

        await test.RunAsync();
    }
}
