using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 R4 auto-vectorisation: a constant-trip <c>FOR i: c(i) = a(i) OP b(i)</c> over rank-1
/// 2-byte-element arrays emits packed MMX (four lanes/iteration) when <c>$CPU 80586 MMX</c> and
/// <c>$OPTIMIZE SPEED</c> are set, and stays scalar otherwise. The lane ops are wrap-correct, so the
/// output is byte-identical to the scalar loop (verified by execution in DOSBox); these tests pin
/// that the MMX opcodes appear only with the feature gate.
/// </summary>
[TestFixture]
public sealed class AutoVectorizeTests {

  private static byte[] Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  private static int Count(byte[] image, params byte[] pattern) {
    var n = 0;
    for (var i = 0; i + pattern.Length <= image.Length; ++i) {
      var hit = true;
      for (var k = 0; k < pattern.Length; ++k)
        if (image[i + k] != pattern[k]) { hit = false; break; }
      if (hit)
        ++n;
    }
    return n;
  }

  private const string _ADD_LOOP =
    "DIM a%(1 TO 100), b%(1 TO 100), c%(1 TO 100)\nDIM i%\nFOR i% = 1 TO 100\n c%(i%) = a%(i%) + b%(i%)\nNEXT\n";

  [Test]
  public void Compile_GivenAddLoopWithMmxAndSpeed_ThenEmitsPaddw() {
    var image = Compile("$CPU 80586 MMX\n$OPTIMIZE SPEED\n" + _ADD_LOOP);
    Assert.That(Count(image, 0x0F, 0xFD), Is.GreaterThan(0), "the add loop vectorises to PADDW");
    Assert.That(Count(image, 0x0F, 0x77), Is.GreaterThan(0), "and ends the MMX block with EMMS");
  }

  [Test]
  public void Compile_GivenAddLoopWithoutMmxFeature_ThenStaysScalar() {
    // $OPTIMIZE SPEED but no MMX feature requested -> no SIMD
    var image = Compile("$CPU 80586\n$OPTIMIZE SPEED\n" + _ADD_LOOP);
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0), "without the MMX feature the loop stays scalar");
  }

  [Test]
  public void Compile_GivenMultiplyLoop_ThenEmitsPmullw() {
    var image = Compile("$CPU 80586 MMX\n$OPTIMIZE SPEED\nDIM a%(1 TO 100), b%(1 TO 100), c%(1 TO 100)\nDIM i%\nFOR i% = 1 TO 100\n c%(i%) = a%(i%) * b%(i%)\nNEXT\n");
    Assert.That(Count(image, 0x0F, 0xD5), Is.GreaterThan(0), "the multiply loop vectorises to PMULLW");
  }

  [Test]
  public void Compile_GivenSmallLoop_ThenNotVectorized() {
    // a tiny trip count is left to the scalar/unroll path (n < 8)
    var image = Compile("$CPU 80586 MMX\n$OPTIMIZE SPEED\nDIM a%(1 TO 4), b%(1 TO 4), c%(1 TO 4)\nDIM i%\nFOR i% = 1 TO 4\n c%(i%) = a%(i%) + b%(i%)\nNEXT\n");
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0), "n < 8 is not vectorised");
  }

  [Test]
  public void Compile_GivenNonVectorizableBody_ThenStaysScalar() {
    // c(i) = a(i) + 1 is not the a(i) OP b(i) shape -> no MMX
    var image = Compile("$CPU 80586 MMX\n$OPTIMIZE SPEED\nDIM a%(1 TO 100), c%(1 TO 100)\nDIM i%\nFOR i% = 1 TO 100\n c%(i%) = a%(i%) + 1\nNEXT\n");
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0));
  }
}
