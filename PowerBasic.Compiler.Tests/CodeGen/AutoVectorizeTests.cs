using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 R4 auto-vectorisation: a counted <c>FOR i: c(i) = a(i) OP b(i)</c> over 2-byte elements becomes a
/// packed kernel (<c>Ir.Passes.PackedLoopVectorization</c> + the DOS runtime's <c>rt_packed16_*</c>) that
/// runs the widest SIMD the target declares - MMX, SSE2, AVX2 or AVX-512 - under <c>$OPTIMIZE SPEED</c>,
/// and stays scalar otherwise. The lane ops wrap exactly like the scalar ones, so the output is the
/// scalar loop's; the MMX path is proven by execution in DOSBox, the wider ones by their encodings.
///
/// <para>
/// Every program here is OBSERVABLE: the arrays are filled through <c>Opaque%</c>, whose inline assembly
/// the optimizer cannot see into, and a checksum loop reads the result. The first version of these
/// tests computed into arrays nobody read - a loop any optimizer is right to delete, and once one did,
/// every "stays scalar" assertion passed for the wrong reason.
/// </para>
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

  /// <summary>An observable <c>c(i) = a(i) OP b(i)</c> over <paramref name="n"/> elements, after the directives.</summary>
  internal static string Loop(string directives, string op = "+", int n = 100, string body = "c%(i%) = a%(i%) {0} b%(i%)") => $"""
    {directives}
    DECLARE FUNCTION Opaque%(BYVAL v%)
    DIM a%(1 TO {n}), b%(1 TO {n}), c%(1 TO {n})
    DIM i%, s%
    FOR i% = 1 TO {n}
      a%(i%) = Opaque%(i% * 37 - 900)
      b%(i%) = Opaque%(i% * 101 + 7)
    NEXT
    FOR i% = 1 TO {n}
      {string.Format(body, op)}
    NEXT
    FOR i% = 1 TO {n}
      s% = (s% XOR c%(i%)) + i%
    NEXT
    PRINT s%; c%(1); c%({n})
    END
    FUNCTION Opaque%(BYVAL v%) NOINLINE
      ! nop
      Opaque% = v%
    END FUNCTION
    """;

  [Test]
  public void Compile_GivenAddLoopWithMmxAndSpeed_ThenEmitsPaddw() {
    var image = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED"));
    Assert.That(Count(image, 0x0F, 0xFD), Is.GreaterThan(0), "the add loop vectorises to PADDW");
    Assert.That(Count(image, 0x0F, 0x77), Is.GreaterThan(0), "and ends the MMX block with EMMS");
  }

  [Test]
  public void Compile_GivenAddLoopWithoutMmxFeature_ThenStaysScalar() {
    // $OPTIMIZE SPEED but no MMX feature requested -> no SIMD
    var image = Compile(Loop("$CPU 80586\n$OPTIMIZE SPEED"));
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0), "without the MMX feature the loop stays scalar");
  }

  [Test]
  public void Compile_GivenAddLoopWithMmxButNoSpeed_ThenStaysScalar() {
    // the kernel is a SPEED trade - a call and a routine for a loop that was a few bytes
    var image = Compile(Loop("$CPU 80586 MMX"));
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0), "without $OPTIMIZE SPEED the loop stays scalar");
  }

  [Test]
  public void Compile_GivenAddLoopWithSse2_ThenEmitsWiderXmmPaddw() {
    // with SSE2 the vectoriser picks the 128-bit XMM width (8 lanes): 66-prefixed PADDW, no MMX/EMMS
    var image = Compile(Loop("$CPU 80586 SSE2\n$OPTIMIZE SPEED"));
    Assert.Multiple(() => {
      Assert.That(Count(image, 0x66, 0x0F, 0xFD), Is.GreaterThan(0), "SSE2 emits the 128-bit XMM PADDW");
      Assert.That(Count(image, 0x0F, 0x77), Is.EqualTo(0), "XMM does not alias x87, so no EMMS");
    });
  }

  [Test]
  public void Compile_GivenAddLoopWithAvx2_ThenEmitsVexYmmVpaddw() {
    // AVX2 picks 256-bit YMM (16 lanes): VEX-encoded VPADDW (C5 .. FD), 3-operand non-destructive
    var image = Compile(Loop("$CPU 80586 AVX2\n$OPTIMIZE SPEED"));
    Assert.That(Count(image, 0xC5, 0xFD, 0xFD), Is.GreaterThan(0), "AVX2 emits VEX VPADDW on YMM");
  }

  [Test]
  public void Compile_GivenAddLoopWithAvx512_ThenEmitsEvexZmmVpaddw() {
    // AVX-512 picks 512-bit ZMM (32 lanes): EVEX-encoded VPADDW (62 F1 7D 48 FD)
    var image = Compile(Loop("$CPU 80586 AVX512\n$OPTIMIZE SPEED"));
    Assert.That(Count(image, 0x62, 0xF1, 0x7D, 0x48, 0xFD), Is.GreaterThan(0), "AVX-512 emits EVEX VPADDW on ZMM");
  }

  [Test]
  public void Compile_GivenMultiplyLoop_ThenEmitsPmullw() {
    var image = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED", "*"));
    Assert.That(Count(image, 0x0F, 0xD5), Is.GreaterThan(0), "the multiply loop vectorises to PMULLW");
  }

  [Test]
  public void Compile_GivenSmallLoop_ThenNotVectorized() {
    // a tiny trip count is left to the scalar/unroll path (n < 8)
    var image = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED", n: 4));
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0), "n < 8 is not vectorised");
  }

  [Test]
  public void Compile_GivenNonVectorizableBody_ThenStaysScalar() {
    // c(i) = a(i) + 1 is not the a(i) OP b(i) shape -> no MMX
    var image = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED", body: "c%(i%) = a%(i%) + 1"));
    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0));
  }

  /// <summary>
  /// The packed kernels compute what the loop computes, for every operator, including the scalar tail:
  /// 103 elements are 25 MMX vectors and three words. The unoptimized build is the reference.
  /// </summary>
  [TestCase("+")]
  [TestCase("-")]
  [TestCase("AND")]
  [TestCase("OR")]
  [TestCase("XOR")]
  [TestCase("*")]
  public void Execute_GivenAVectorizedLoop_ThenItPrintsWhatTheScalarLoopPrints(string op) {
    var vectorized = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED", op, 103));
    var scalar = Compile(Loop("$CPU 80586 MMX\n$OPTIMIZE OFF", op, 103));
    Assert.That(Count(vectorized, 0x0F, 0x77), Is.GreaterThan(0), "the optimized build must actually use the MMX kernel");
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(vectorized)), Is.EqualTo(DosBoxRunner.Normalize(DosBoxRunner.Run(scalar))));
  }
}
