using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 C2 loop-top alignment: under <c>$CPU 80486</c>/<c>80586</c> + <c>$OPTIMIZE SPEED</c> a hot loop
/// top is NOP-padded to a 16-byte boundary (better instruction fetch / branch-target prefetch). The pad
/// runs once on fall-through entry and is skipped by the back-edge, so it is output-invariant - verified
/// here through emitter-owned padding counts, not NOP-looking bytes inside operands or data.
/// </summary>
[TestFixture]
public sealed class LoopAlignmentTests {

  private static (byte[] Image, int PaddingBytes) Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the alignment test must exercise routed code");
    return (exe, generator.CodeAlignmentPaddingForTesting);
  }

  // a CALL in the loop body keeps it off every register-resident / closed-form fast path,
  // so it takes the general loop emitter whose top is alignment-padded. NOINLINE is what makes
  // the call a barrier: an empty body is absorbable at the call site, and an empty loop body
  // leaves nothing to align.
  private const string _LOOP =
    "DECLARE SUB Foo()\nDIM i%\nFOR i% = 1 TO 100\n CALL Foo\nNEXT\nEND\nSUB Foo() NOINLINE\nEND SUB\n";

  [Test]
  public void Compile_GivenNopValuedImmediate_ThenDoesNotCountItAsAlignment() {
    var image = Compile("! MOV AX, 144\nEND\n");
    Assert.That(image.Image, Does.Contain((byte)0x90), "the immediate deliberately contains the NOP byte value");
    Assert.That(image.PaddingBytes, Is.Zero, "operand bytes are not alignment instructions");
  }

  [Test]
  public void Compile_GivenLoopWithCpu586AndSpeed_ThenLoopTopNopPadded() {
    // $CPU 80586 has no procedure-entry alignment (that is 486-only), so this isolates the loop-top pad
    var aligned = Compile("$CPU 80586\n$OPTIMIZE SPEED\n" + _LOOP);
    var noSpeed = Compile("$CPU 80586\n" + _LOOP);
    Assert.Multiple(() => {
      Assert.That(aligned.PaddingBytes, Is.GreaterThan(0), "the loop top is NOP-padded under 586 + SPEED");
      Assert.That(aligned.PaddingBytes, Is.GreaterThan(noSpeed.PaddingBytes), "alignment is a SPEED-gated tradeoff");
    });
  }

  [Test]
  public void Compile_GivenLoopWithCpu386_ThenNoLoopAlignment() {
    // alignment is a 486+/586+ cache-line optimization; a 386 target keeps the loop unpadded
    var asm386 = Compile("$CPU 80386\n$OPTIMIZE SPEED\n" + _LOOP);
    var noCpu = Compile("$OPTIMIZE SPEED\n" + _LOOP);
    Assert.Multiple(() => {
      Assert.That(asm386.PaddingBytes, Is.Zero, "a 386 target must not request alignment padding");
      Assert.That(noCpu.PaddingBytes, Is.Zero, "an 8086 target must not request alignment padding");
    });
  }
}
