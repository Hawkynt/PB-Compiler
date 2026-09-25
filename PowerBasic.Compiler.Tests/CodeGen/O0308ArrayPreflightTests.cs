using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0308's direct-emitter consumer: checked signed INTEGER add/sub array loops may pay one
/// read-only whole-range overflow scan so O0026 can use packed arithmetic afterwards. These tests
/// pin the consumer boundary rather than O0026 itself: under $ERROR OVERFLOW the old vectorizer is
/// ineligible, so seeing a packed opcode proves the preflight version was selected.
/// </summary>
[TestFixture]
public sealed class O0308ArrayPreflightTests {

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
    var count = 0;
    for (var i = 0; i + pattern.Length <= image.Length; ++i) {
      var matches = true;
      for (var k = 0; k < pattern.Length; ++k)
        if (image[i + k] != pattern[k]) {
          matches = false;
          break;
        }
      if (matches)
        ++count;
    }
    return count;
  }

  // The observable loop AutoVectorizeTests uses, under $ERROR OVERFLOW: filled through Opaque%, read
  // back by a checksum. An unread result is a loop the optimizer deletes, which is not a claim about
  // the preflight at all.
  private static string Loop(string feature, int elements, string operation, params string[] extraDirectives)
    => AutoVectorizeTests.Loop(string.Join('\n', [$"$CPU 80586 {feature}", "$OPTIMIZE SPEED", "$ERROR OVERFLOW ON", .. extraDirectives]),
      operation, elements);

  [Test]
  public void Compile_GivenCheckedAddAndMmx_ThenPreflightUnlocksPaddw() {
    var image = Compile(Loop("MMX", 100, "+"));

    Assert.Multiple(() => {
      Assert.That(Count(image, 0x0F, 0xFD), Is.GreaterThan(0), "checked add reaches O0026 only through O0308 preflight");
      Assert.That(Count(image, 0x0F, 0x77), Is.GreaterThan(0), "MMX fast path still closes with EMMS");
    });
  }

  [Test]
  public void Compile_GivenCheckedSubtractAndSse2_ThenPreflightUnlocksPsubw() {
    var image = Compile(Loop("SSE2", 100, "-"));

    Assert.That(Count(image, 0x66, 0x0F, 0xF9), Is.GreaterThan(0),
      "checked subtract reaches the 128-bit PSUBW fast path after the scalar overflow scan");
  }

  [Test]
  public void Compile_GivenCheckedMultiply_ThenDoesNotPretendAddSubProofApplies() {
    var image = Compile(Loop("MMX", 100, "*"));

    Assert.That(Count(image, 0x0F, 0xD5), Is.EqualTo(0),
      "PMULLW stays unavailable until O0308 has a separate signed-product range proof");
  }

  [Test]
  public void Compile_GivenMmxLoopBelowProfitabilityFloor_ThenSkipsSecondArrayWalk() {
    var tooSmall = Compile(Loop("MMX", 31, "+"));
    var worthwhile = Compile(Loop("MMX", 32, "+"));

    Assert.Multiple(() => {
      Assert.That(Count(tooSmall, 0x0F, 0xFD), Is.EqualTo(0), "31 elements do not amortize a second O(n) scan");
      Assert.That(Count(worthwhile, 0x0F, 0xFD), Is.GreaterThan(0), "32 elements meet the MMX preflight floor");
    });
  }

  [Test]
  public void Compile_GivenCounterWouldWrapAfterShortMax_ThenKeepsOriginalLoopSemantics() {
    var image = Compile(AutoVectorizeTests.Loop("$CPU 80586 MMX\n$OPTIMIZE SPEED\n$ERROR OVERFLOW ON", "+", 100)
      .Replace("FOR i% = 1 TO 100\n  c%(i%) = a%(i%) + b%(i%)", "FOR i% = 32700 TO 32767\n  c%(i% - 32600) = a%(i% - 32600) + b%(i% - 32600)"));

    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0),
      "without $ERROR NUMERIC the final INTEGER increment wraps to -32768 and the FOR continues");
  }

  [Test]
  public void Compile_GivenAvx512Loop_ThenRequiresTwoFullVectors() {
    var oneVectorAndTail = Compile(Loop("AVX512", 63, "+"));
    var twoVectors = Compile(Loop("AVX512", 64, "+"));

    Assert.Multiple(() => {
      Assert.That(Count(oneVectorAndTail, 0x62, 0xF1, 0x7D, 0x48, 0xFD), Is.EqualTo(0));
      Assert.That(Count(twoVectors, 0x62, 0xF1, 0x7D, 0x48, 0xFD), Is.GreaterThan(0));
    });
  }

  /// <summary>No element overflows: the preflight passes and the packed result is the loop's.</summary>
  [Test]
  public void Execute_GivenACheckedLoopThatFits_ThenThePackedResultIsTheLoops() {
    var packed = Compile(Loop("MMX", 100, "+"));
    var scalar = Compile(Loop("MMX", 100, "+").Replace("$OPTIMIZE SPEED", "$OPTIMIZE OFF"));
    Assert.That(Count(packed, 0x0F, 0xFD), Is.GreaterThan(0), "the checked kernel must be in the optimized build");
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(packed)), Is.EqualTo(DosBoxRunner.Normalize(DosBoxRunner.Run(scalar))));
  }

  /// <summary>An element overflows: nothing is computed packed, and the checked loop raises Error 6 as before.</summary>
  [Test]
  public void Execute_GivenACheckedLoopThatOverflows_ThenErrorSixIsRaisedAsBefore() {
    string Overflowing(string optimize) => Loop("MMX", 100, "*").Replace("$OPTIMIZE SPEED", optimize)
      .Replace("c%(i%) = a%(i%) * b%(i%)", "c%(i%) = a%(i%) + b%(i%) + 0")
      .Replace("b%(i%) = Opaque%(i% * 101 + 7)", "b%(i%) = Opaque%(i% * 300)");
    var packed = Compile(Overflowing("$OPTIMIZE SPEED"));
    var scalar = Compile(Overflowing("$OPTIMIZE OFF"));
    var packedRun = DosBoxRunner.Normalize(DosBoxRunner.Run(packed));
    var scalarRun = DosBoxRunner.Normalize(DosBoxRunner.Run(scalar));
    Assert.Multiple(() => {
      Assert.That(Count(packed, 0x0F, 0xFD), Is.GreaterThan(0), "the loop must actually be behind the checked kernel");
      Assert.That(packedRun, Is.EqualTo(scalarRun));
      Assert.That(packedRun, Does.Contain("RUNTIME ERROR"), "the overflow still stops the program");
    });
  }

  /// <summary>
  /// A counter running 1 TO 100 cannot wrap, so $ERROR NUMERIC's check on it is proven away and the
  /// loop is the same loop as without the directive. The counter that CAN wrap is the short-max case
  /// above, which stays scalar.
  /// </summary>
  [Test]
  public void Compile_GivenNumericChecking_WhenTheCounterCannotWrap_ThenTheLoopStillVectorizes() {
    var image = Compile(Loop("MMX", 100, "+", "$ERROR NUMERIC ON"));

    Assert.That(Count(image, 0x0F, 0xFD), Is.GreaterThan(0), "a check proven never to fire does not keep the loop scalar");
  }

  /// <summary>
  /// Subscripts proven in range lose their bounds checks and vectorize; one the analysis cannot prove
  /// keeps its per-element check, which no packed kernel performs, so that loop stays scalar.
  /// </summary>
  [Test]
  public void Compile_GivenBoundsChecking_ThenOnlyUnprovenSubscriptsKeepTheScalarLoop() {
    var proven = Compile(Loop("MMX", 100, "+", "$ERROR BOUNDS ON"));
    var unproven = Compile(Loop("MMX", 100, "+", "$ERROR BOUNDS ON")
      .Replace("c%(i%) = a%(i%) + b%(i%)", "c%(i% + k%) = a%(i% + k%) + b%(i% + k%)")
      .Replace("DIM i%, s%", "DIM i%, s%, k%\nk% = Opaque%(0)"));

    Assert.Multiple(() => {
      Assert.That(Count(proven, 0x0F, 0xFD), Is.GreaterThan(0), "in-range subscripts need no check");
      Assert.That(Count(unproven, 0x0F, 0xFD), Is.EqualTo(0), "the preflight does not subsume per-element bounds checks");
    });
  }
}
