using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>REDIM</c> of an array PARAMETER reallocates the CALLER's array. The descriptor a callee
/// receives is the caller's own, so this is not a private rearrangement: the caller sees the new
/// bounds afterwards, and without <c>PRESERVE</c> it sees a cleared array as well.
///
/// <para>
/// The expectations below are the GENUINE compiler's, read off PBC 3.50 through the oracle rather
/// than decided here. <c>SlotOf</c> mints a private data cell for an array parameter, and this
/// emitter recorded the new block in that cell - which nothing reads - so <c>UBOUND</c> kept
/// answering the old bound while element writes landed in the new block. It printed plausible
/// numbers rather than faulting, which is why it survived.
/// </para>
/// <para>
/// The plain <c>REDIM</c> half also has an oracle test of its own in <c>tests/diff/DIFF124.BAS</c>,
/// which compares against the real compiler on every battery run. <c>PRESERVE</c> is here instead of
/// there because the routed back end cannot yet take a <c>REDIM PRESERVE</c> of a parameter - it runs
/// out of 8086 registers, not out of semantics - and a corpus program that declines would be counted
/// as a routing regression by <c>BackendCoverageTests</c>. The construct is recorded in
/// <c>docs/DIRECT-EMITTER-RETIREMENT.md</c> instead, which is where the remaining classes live.
/// </para>
/// </summary>
[TestFixture]
public sealed class ArrayParameterRedimTests {

  private static string Run(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var codegen = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = false };
    var image = codegen.EmitExecutable();
    Assert.That(codegen.Errors, Is.Empty, string.Join("; ", codegen.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>
  /// Genuine PBC 3.50 answers <c>1 6</c> and <c>11 33 66</c>: the caller's upper bound becomes 6, and
  /// PRESERVE keeps the elements that were already there - including the one at the OLD upper bound,
  /// which is what distinguishes a preserved reallocation from a fresh one.
  /// </summary>
  [Test]
  public void Run_GivenRedimPreserveOfAnArrayParameter_ThenTheCallersArrayGrowsAndKeepsItsElements() {
    const string source = """
      SUB GrowPreserving(a%())
        REDIM PRESERVE a%(1 TO 6)
        a%(6) = 66
      END SUB
      REDIM w%(1 TO 3)
      w%(1) = 11
      w%(3) = 33
      GrowPreserving w%()
      PRINT LBOUND(w%); UBOUND(w%)
      PRINT w%(1); w%(3); w%(6)
      END
      """;

    Assert.That(Run(source), Is.EqualTo("1  6 | 11  33  66"),
      "the caller must see the reallocated array, with the elements PRESERVE kept");
  }

  /// <summary>
  /// The plain form beside it, so the two are read together: no PRESERVE means the caller sees a
  /// cleared array, and 7 must NOT survive.
  /// </summary>
  [Test]
  public void Run_GivenRedimOfAnArrayParameter_ThenTheCallersArrayIsReallocatedAndCleared() {
    const string source = """
      SUB Grow(a%())
        REDIM a%(1 TO 9)
        a%(9) = 42
      END SUB
      REDIM v%(1 TO 2)
      v%(1) = 7
      Grow v%()
      PRINT LBOUND(v%); UBOUND(v%)
      PRINT v%(1); v%(9)
      END
      """;

    Assert.That(Run(source), Is.EqualTo("1  9 | 0  42"),
      "without PRESERVE the reallocated array starts empty, so the old 7 is gone");
  }
}
