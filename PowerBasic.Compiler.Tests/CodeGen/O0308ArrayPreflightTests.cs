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
    var generator = new CodeGenerator(model) { UseExperimentalBackend = false };
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

  private static string Loop(string feature, int elements, string operation, params string[] extraDirectives) {
    string[] lines = [
      $"$CPU 80586 {feature}",
      "$OPTIMIZE SPEED",
      "$ERROR OVERFLOW ON",
      .. extraDirectives,
      $"DIM a%(1 TO {elements}), b%(1 TO {elements}), c%(1 TO {elements})",
      "DIM i%",
      $"FOR i% = 1 TO {elements}",
      $" c%(i%) = a%(i%) {operation} b%(i%)",
      "NEXT",
      "",
    ];
    return string.Join('\n', lines);
  }

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
    var image = Compile("""
      $CPU 80586 MMX
      $OPTIMIZE SPEED
      $ERROR OVERFLOW ON
      DIM a%(32700 TO 32767), b%(32700 TO 32767), c%(32700 TO 32767)
      DIM i%
      FOR i% = 32700 TO 32767
        c%(i%) = a%(i%) + b%(i%)
      NEXT
      """);

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

  [Test]
  public void Compile_GivenNumericChecking_ThenKeepsCheckedScalarLoop() {
    var image = Compile(Loop("MMX", 100, "+", "$ERROR NUMERIC ON"));

    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0),
      "counter-wrap checking remains outside the array preflight proof");
  }

  [Test]
  public void Compile_GivenBoundsChecking_ThenKeepsCheckedScalarLoop() {
    var image = Compile(Loop("MMX", 100, "+", "$ERROR BOUNDS ON"));

    Assert.That(Count(image, 0x0F, 0xFD), Is.EqualTo(0),
      "the preflight does not subsume per-element bounds checks");
  }
}
