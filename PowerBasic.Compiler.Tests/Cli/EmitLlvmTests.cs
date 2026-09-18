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
      "INPUT k%\nFOR i% = 0 TO k%\n  a%(i%) = sq%(i%)\nNEXT i%\n" +
      "\n" +
      "FUNCTION sq%(BYVAL n%)\n  sq% = n% OR n%\nEND FUNCTION");

    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(output, Does.Contain("define void @main()"));
    Assert.That(output, Does.Contain("target triple"));
    Assert.That(output, Does.Contain("phi i16"));        // mem2reg formed the loop counter phi
    Assert.That(output, Does.Not.Contain("@sq"));        // sq() inlined away (INPUT's runtime calls remain)
  }

  /// <summary>
  /// The subject is a placeholder for "something the lowering has no answer for", and which construct
  /// that is moves as the subset grows - it was <c>BEEP</c> until BEEP became a call to rt_sound, and
  /// <c>WAIT</c> until the port spin became blocks over rt_inp.
  ///
  /// <para>
  /// <c>GOTO DWORD</c> is the one least likely to move next. The lowering models a computed jump as an
  /// indirect branch over the labels of the function it is IN, and this one jumps to a PROCEDURE
  /// address - which is not in that set and cannot be put there. <c>Compile_GivenEveryStatementForm</c>
  /// is the list that tracks the rest.
  /// </para>
  /// </summary>
  [Test]
  public void EmitLlvm_ForAnUnsupportedProgram_FailsWithADiagnostic() {
    var (code, _, err) = RunEmit(
      "DIM gp AS DWORD\ngp = CODEPTR32(S6)\nGOTO DWORD gp\nEND\nSUB S6()\nEND SUB");

    Assert.That(code, Is.EqualTo(1));
    Assert.That(err, Does.Contain("--emit-llvm"));
  }
}
