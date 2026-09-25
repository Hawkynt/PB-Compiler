using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 wide integer types <c>INT128/256/512</c> and the unsigned <c>UINT*</c> forms: fixed-size
/// emulated multi-word integers. The foundation covers declaration/sizing and the conversions to and
/// from the native scalars (sign-/zero-extend on widening, truncate on narrowing); arithmetic and
/// decimal printing are follow-ups. Verified by execution (extend → truncate round trips match) plus
/// these binder/codegen tests; pb36-only (genuine PBC has no wide integers).
/// </summary>
[TestFixture]
public sealed class WideIntegerTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  private static byte[] Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  [TestCase("INT128", 16, true)]
  [TestCase("INT256", 32, true)]
  [TestCase("INT512", 64, true)]
  [TestCase("UINT128", 16, false)]
  [TestCase("UINT256", 32, false)]
  [TestCase("UINT512", 64, false)]
  public void Bind_GivenWideTypeDeclaration_ThenResolvesToWideIntTypeWithSizeAndSign(string keyword, int bytes, bool signed) {
    var model = Bind($"DIM x AS {keyword}\n");
    var type = model.ModuleVariables.Values.Single(v => v.Name.Equals("x", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.That(type, Is.InstanceOf<WideIntType>());
    var wide = (WideIntType)type;
    Assert.Multiple(() => {
      Assert.That(wide.ByteSize, Is.EqualTo(bytes));
      Assert.That(wide.Signed, Is.EqualTo(signed));
      Assert.That(wide.Words, Is.EqualTo(bytes / 2));
    });
  }

  [Test]
  public void Bind_GivenWideTypeBelowPb36_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("DIM x AS INT128\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Compile_GivenWideRoundTrip_ThenNoCodegenError() {
    // extend a constant + a runtime value into a wide, copy wide=wide, truncate back to LONG - all generate
    var image = Compile(
      "DIM a AS INT128, b AS INT128\nDIM x&, lo&\n" +
      "a = -5\n" +
      "x& = LEN(\"hi\") * 100\n" +  // runtime
      "a = x&\n" +
      "b = a\n" +
      "lo& = b\n" +
      "PRINT lo&\n");
    Assert.That(image, Is.Not.Empty, "produces an image (a COM: the program is optimized and self-contained)");
  }

  [Test]
  public void Compile_GivenWideAddAndSubtract_ThenGeneratesAdcSbbChain() {
    // c = a + b / a - b on same-width wide values is supported (multi-word ADC/SBB chain)
    var image = Compile("DIM a AS INT128, b AS INT128, c AS INT128\na = 5\nb = 8\nc = a + b\nc = b - a\nDIM lo&\nlo& = c\nPRINT lo&\n");
    Assert.That(image, Is.Not.Empty);
  }

  /// <summary>
  /// What the two tests above do not check: the arithmetic. Both assert that an image comes out, which
  /// a lowering storing zeros everywhere would also satisfy - and the whole of a wide integer that a
  /// program can observe is the low words a truncation hands back, so the carry has to be caught where
  /// it crosses INTO them.
  ///
  /// <para>
  /// <c>65535 + 65535</c> is that place. It is 131070, and a chain that dropped the carry out of word
  /// zero would answer 65534 - the same sum with bit 16 missing. The borrow is the mirror: <c>0 - 1</c>
  /// is -1 across every word, and a chain that did not borrow would leave 65535 in word zero and
  /// nothing above it. Both numbers fit a LONG, so the truncation can report them.
  /// </para>
  /// <para>
  /// Run through BOTH back ends, because the two compute it differently on purpose: the direct emitter
  /// walks the words with <c>ADC</c>/<c>SBB</c> and the routed one adds each word in thirty-two bits,
  /// the IR having no way to name a flag between two instructions.
  /// </para>
  /// </summary>
  [Test]
  public void Execute_GivenWideAddAndSubtract_ThenTheCarryCrossesTheWordBoundary() {
    const string source = """
      DIM a AS INT128, b AS INT128, c AS INT128
      DIM x&, lo&
      x& = 65535
      a = x&
      b = x&
      c = a + b
      lo& = c
      PRINT lo&
      x& = 0
      a = x&
      x& = 1
      b = x&
      c = a - b
      lo& = c
      PRINT lo&
      a = -5
      c = a
      lo& = c
      PRINT lo&
      """;

    var routed = Run(source, routed: true);
    Assert.Multiple(() => {
      Assert.That(routed, Is.EqualTo(Run(source, routed: false)), "the two back ends agree");
      Assert.That(routed, Is.EqualTo("131070 |-1 |-5"),
        "the carry left word zero, the borrow entered it, and a negative constant sign-extended");
    });
  }

  /// <summary>
  /// The FILL above the words a value actually occupies. It is read off the SOURCE and never the
  /// destination, which is the whole of what signedness decides here: a negative <c>INT128</c> widening
  /// into an <c>INT256</c> fills the eight words above it with ones, and a <c>LONG</c> of -1 stored into
  /// a <c>UINT128</c> fills with ones too - the destination being unsigned changes what the value MEANS
  /// and not which bits arrive.
  /// </summary>
  [Test]
  public void Execute_GivenAWideningAssignment_ThenTheFillComesFromTheSource() {
    const string source = """
      DIM a AS INT128, d AS INT256
      DIM u AS UINT128
      DIM x&, lo&
      a = -5
      d = a
      lo& = d
      PRINT lo&
      x& = -1
      u = x&
      lo& = u
      PRINT lo&
      """;

    var routed = Run(source, routed: true);
    Assert.Multiple(() => {
      Assert.That(routed, Is.EqualTo(Run(source, routed: false)), "the two back ends agree");
      Assert.That(routed, Is.EqualTo("-5 |-1"), "both widened with their own sign");
    });
  }

  private static string Run(string source, bool routed) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    if (routed)
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the body must route");
    return Exec.Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [Test]
  public void Bind_GivenWideMultiply_ThenReportsNotYetSupported() {
    // only + and - are wired; multiply/compare/etc. still diagnose at bind time rather than miscompile
    var unit = Parser.Parse(Lexer.Tokenize("DIM a AS INT128, b AS INT128, c AS INT128\nc = a * b\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("wide-integer operation not yet supported")), Is.True);
  }
}
