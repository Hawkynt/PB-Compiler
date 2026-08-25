using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 C2 loop-top alignment: under <c>$CPU 80486</c>/<c>80586</c> + <c>$OPTIMIZE SPEED</c> a hot loop
/// top is NOP-padded to a 16-byte boundary (better instruction fetch / branch-target prefetch). The pad
/// runs once on fall-through entry and is skipped by the back-edge, so it is output-invariant - verified
/// here by the presence/absence of the NOP run and confirmed byte-identical by the differential harness.
/// </summary>
[TestFixture]
public sealed class LoopAlignmentTests {

  private static byte[] Compile(string source, bool useBackend) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = useBackend };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    if (useBackend)
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the alignment test must exercise routed code");
    return exe;
  }

  // a CALL in the loop body keeps it off every register-resident / closed-form fast path,
  // so it takes the general loop emitter whose top is alignment-padded. NOINLINE is what makes
  // the call a barrier: an empty body is absorbable at the call site, and an empty loop body
  // leaves nothing to align.
  private const string _LOOP =
    "DECLARE SUB Foo()\nDIM i%\nFOR i% = 1 TO 100\n CALL Foo\nNEXT\nEND\nSUB Foo() NOINLINE\nEND SUB\n";

  private static int MaxNopRun(byte[] image) {
    int best = 0, run = 0;
    foreach (var b in image) {
      run = b == 0x90 ? run + 1 : 0;
      best = System.Math.Max(best, run);
    }
    return best;
  }

  private static int NopCount(byte[] image) => image.Count(b => b == 0x90);

  [TestCase(false)]
  [TestCase(true)]
  public void Compile_GivenLoopWithCpu586AndSpeed_ThenLoopTopNopPadded(bool useBackend) {
    // $CPU 80586 has no procedure-entry alignment (that is 486-only), so this isolates the loop-top pad
    var aligned = Compile("$CPU 80586\n$OPTIMIZE SPEED\n" + _LOOP, useBackend);
    var noSpeed = Compile("$CPU 80586\n" + _LOOP, useBackend);
    Assert.Multiple(() => {
      Assert.That(MaxNopRun(aligned), Is.GreaterThan(0), "the loop top is NOP-padded under 586 + SPEED");
      Assert.That(NopCount(aligned), Is.GreaterThan(NopCount(noSpeed)), "alignment is a SPEED-gated tradeoff");
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Compile_GivenLoopWithCpu386_ThenNoLoopAlignment(bool useBackend) {
    // alignment is a 486+/586+ cache-line optimization; a 386 target keeps the loop unpadded
    var asm386 = Compile("$CPU 80386\n$OPTIMIZE SPEED\n" + _LOOP, useBackend);
    var noCpu = Compile("$OPTIMIZE SPEED\n" + _LOOP, useBackend);
    Assert.That(NopCount(asm386), Is.EqualTo(NopCount(noCpu)), "no 486/586 -> no alignment pad");
  }
}
