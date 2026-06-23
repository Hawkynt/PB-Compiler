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
    Assert.That(image[0], Is.EqualTo((byte)'M'), "produces a valid MZ image");
    Assert.That(image[1], Is.EqualTo((byte)'Z'));
  }

  [Test]
  public void Bind_GivenWideArithmetic_ThenReportsNotYetSupported() {
    // arithmetic on wide values is a follow-up - it must diagnose at bind time, not miscompile
    var unit = Parser.Parse(Lexer.Tokenize("DIM a AS INT128, b AS INT128, c AS INT128\nc = a + b\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("wide-integer arithmetic")), Is.True);
  }
}
