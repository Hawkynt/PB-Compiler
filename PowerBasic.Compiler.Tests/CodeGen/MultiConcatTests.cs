using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O24 multi-concat behavioral tests: a chain/tree of three or more string concatenations builds
/// with a single heap allocation (rt_strcatn) yet produces the exact same string and side effects
/// as the pairwise StrCat chain. Execution under DOSBox is skipped when DOSBox is unavailable; the
/// firing/structure assertions live in <see cref="OptimizerTests"/>.
/// </summary>
[TestFixture, Category("Slow")]
[NonParallelizable]
public sealed class MultiConcatTests {

  private sealed class MemorySourceProvider(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static string Run(string source, Dialect dialect = Dialect.Pb36) {
    var tokens = Preprocessor.Expand("T.BAS", new MemorySourceProvider(source));
    var unit = Parser.Parse(tokens, "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Concat_GivenFourStringVariables_WhenRun_ThenConcatenatedInOrder() {
    var output = Run("""
      a$ = "Alpha"
      b$ = "Beta"
      c$ = "Gamma"
      d$ = "Delta"
      r$ = a$ & b$ & c$ & d$
      PRINT r$
      """);
    Assert.That(output, Is.EqualTo("AlphaBetaGammaDelta\n"));
  }

  [Test]
  public void Concat_GivenLiteralsMixedWithVariables_WhenRun_ThenConcatenatedInOrder() {
    var output = Run("""
      n$ = "World"
      r$ = "Hello, " & n$ & "! " & "Bye"
      PRINT r$
      """);
    Assert.That(output, Is.EqualTo("Hello, World! Bye\n"));
  }

  [Test]
  public void Concat_GivenPlusConcatChain_WhenRun_ThenConcatenatedInOrder() {
    var output = Run("""
      a$ = "1"
      b$ = "2"
      c$ = "3"
      d$ = "4"
      e$ = "5"
      r$ = a$ + b$ + c$ + d$ + e$
      PRINT r$
      """);
    Assert.That(output, Is.EqualTo("12345\n"));
  }

  [Test]
  public void Concat_GivenEmptyOperands_WhenRun_ThenEmptiesContributeNothing() {
    // equivalence class: empty leaves (a literal "" and an unset variable) must concatenate to a
    // zero-length contribution, the boundary case for the length-sum and copy passes.
    var output = Run("""
      a$ = "X"
      e$ = ""
      r$ = a$ & e$ & u$ & "Y"
      PRINT r$
      PRINT LEN(r$)
      """);
    Assert.That(output, Is.EqualTo("XY\n 2\n"));
  }

  [Test]
  public void Concat_GivenAllEmptyOperands_WhenRun_ThenResultIsEmpty() {
    // boundary: every leaf empty -> total length 0 -> StrAlloc returns the empty handle, no copy.
    var output = Run("""
      a$ = ""
      b$ = ""
      r$ = a$ & b$ & c$ & d$
      PRINT "[" & r$ & "]"
      """);
    Assert.That(output, Is.EqualTo("[]\n"));
  }

  [Test]
  public void Concat_GivenBalancedTree_WhenRun_ThenConcatenatedInOrder() {
    // (a$+b$) + (c$+d$): a tree, not a left-leaning chain - flattening yields the same four leaves.
    var output = Run("""
      a$ = "aa"
      b$ = "bb"
      c$ = "cc"
      d$ = "dd"
      r$ = (a$ + b$) + (c$ + d$)
      PRINT r$
      """);
    Assert.That(output, Is.EqualTo("aabbccdd\n"));
  }

  [Test]
  public void Concat_GivenSideEffectingOperands_WhenRun_ThenEvaluatedLeftToRightOnce() {
    // each F$ call appends its argument to a log; the chain must call them left-to-right exactly
    // once, so the log reads "1-2-3-" and the concatenation is "ABC".
    var output = Run("""
      g$ = ""
      r$ = F$("A", "1") & F$("B", "2") & F$("C", "3")
      PRINT r$
      PRINT g$
      END

      FUNCTION F$(s$, tag$)
        SHARED g$
        g$ = g$ + tag$ + "-"
        F$ = s$
      END FUNCTION
      """);
    Assert.That(output, Is.EqualTo("ABC\n1-2-3-\n"));
  }

  [Test]
  public void Concat_GivenLongChain_WhenRun_ThenAllOperandsPresent() {
    // a longer chain (8 leaves) exercises the multi-operand sum/copy/free loops past the small cases.
    var output = Run("""
      r$ = "1" & "2" & "3" & "4" & "5" & "6" & "7" & "8"
      PRINT r$
      """);
    Assert.That(output, Is.EqualTo("12345678\n"));
  }

  [Test]
  public void Concat_GivenSameOutputUnderPb35AndPb36_WhenRun_ThenIdentical() {
    // output-equivalence: the optimized (pb36) and unoptimized (pb35) builds produce identical text.
    const string source = """
      a$ = "foo"
      b$ = "bar"
      c$ = "baz"
      d$ = "qux"
      r$ = a$ & b$ & c$ & d$
      PRINT r$
      """;
    Assert.That(Run(source, Dialect.Pb36), Is.EqualTo(Run(source, Dialect.Pb35)));
  }
}
