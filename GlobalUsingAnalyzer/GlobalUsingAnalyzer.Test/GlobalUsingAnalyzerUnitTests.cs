using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using VerifyCS = GlobalUsingAnalyzer.Test.CSharpCodeFixVerifier<
    GlobalUsingAnalyzer.GlobalUsingAnalyzerAnalyzer,
    GlobalUsingAnalyzer.GlobalUsingAnalyzerCodeFixProvider>;

namespace GlobalUsingAnalyzer.Test
{
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
                        // After fix: local using is gone.
                        @"
class C
{
}
",
                        // New file at project root (by name in the test workspace).
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
    }
}
