using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>The <c>pbc --emit-llvm</c> front-end path: lower → optimize → emit textual LLVM.</summary>
[TestFixture]
public sealed class EmitLlvmTests {

  private static (int Code, string Out, string Err) RunEmit(string source) {
    var path = Path.Combine(Path.GetTempPath(), $"pbc_llvm_{Guid.NewGuid():N}.bas");
    File.WriteAllText(path, source);
    try {
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      var code = Driver.Run(["--emit-llvm", path], stdout, stderr);
      return (code, stdout.ToString(), stderr.ToString());
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void EmitLlvm_ForASupportedProgram_PrintsOptimizedLlvmModule() {
    var (code, output, err) = RunEmit(
      "DECLARE FUNCTION sq%(BYVAL n%)\n" +
      "DIM a%(0 TO 4)\n" +
      "FOR i% = 0 TO 4\n  a%(i%) = sq%(i%)\nNEXT i%\n" +
      "\n" +
      "FUNCTION sq%(BYVAL n%)\n  sq% = n% OR n%\nEND FUNCTION");

    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(output, Does.Contain("define void @main()"));
    Assert.That(output, Does.Contain("target triple"));
    Assert.That(output, Does.Contain("phi i16"));        // mem2reg formed the loop counter phi
    Assert.That(output, Does.Not.Contain("call "));      // sq() inlined away
  }

  [Test]
  public void EmitLlvm_ForAnUnsupportedProgram_FailsWithADiagnostic() {
    var (code, _, err) = RunEmit("OPEN \"x\" FOR INPUT AS #1\nCLOSE #1");   // file I/O is not in the subset yet

    Assert.That(code, Is.EqualTo(1));
    Assert.That(err, Does.Contain("--emit-llvm"));
  }
}
