using System.Text;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0300: when the IR owns the complete byte vector and every byte is 7-bit ASCII, UCASE$/LCASE$
/// becomes a mapped pooled literal. Dynamic input and anything containing a high-bit byte must keep
/// the general runtime call; the optimizer has no licence to guess the active DOS code page.
/// </summary>
[TestFixture]
public sealed class AsciiStringSpecializationTests {

  private static IrModule Lower(string expression) {
    var source = $"DIM result AS STRING\nresult = {expression}\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IEnumerable<IrCall> Calls(IrModule module, string name)
    => module.Functions
      .Where(static function => !function.IsDeclaration)
      .SelectMany(static function => function.AllInstructions)
      .OfType<IrCall>()
      .Where(call => call.Callee is IrFunction { Name: var callee } && callee == name);

  [TestCase("UCASE$", "azAZ09@[`{", "AZAZ09@[`{", "rt_str_ucase")]
  [TestCase("LCASE$", "AZaz09@[`{", "azaz09@[`{", "rt_str_lcase")]
  public void Run_GivenSevenBitLiteral_WhenCaseMapped_ThenRuntimeWalkIsReplacedByMappedLiteral(
    string function, string input, string expected, string runtime) {
    var module = Lower($"{function}(\"{input}\")");
    Assert.That(Calls(module, runtime).Count(), Is.EqualTo(1),
      "precondition: lowering must contain the case call");

    var folded = StringConstantFold.Run(module);

    var literal = Calls(module, "rt_str_const").Single();
    Assert.Multiple(() => {
      Assert.That(folded, Is.GreaterThanOrEqualTo(1));
      Assert.That(Calls(module, runtime), Is.Empty, "the ASCII case walk should be gone");
      Assert.That(literal.GetOperand(1), Is.InstanceOf<IrGlobalVariable>());
      Assert.That(((IrGlobalVariable)literal.GetOperand(1)).Bytes,
        Is.EqualTo(Encoding.ASCII.GetBytes(expected)), "only the ASCII case pair must change");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase("UCASE$", "rt_str_ucase")]
  [TestCase("LCASE$", "rt_str_lcase")]
  public void Run_GivenDynamicHighBitValue_WhenAsciiCannotBeProven_ThenGeneralRuntimeCallRemains(
    string function, string runtime) {
    var module = Lower($"{function}(CHR$(255))");

    _ = StringConstantFold.Run(module);

    Assert.Multiple(() => {
      Assert.That(Calls(module, runtime).Count(), Is.EqualTo(1),
        "CHR$(255) is not 7-bit ASCII and must not be specialized");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenConcatenatedAsciiLiterals_WhenFoldedToFixpoint_ThenCaseFoldConsumesTheJoinedLiteral() {
    var module = Lower("UCASE$(\"ab\" + \"z{\")");

    _ = StringConstantFold.Run(module);

    var literal = Calls(module, "rt_str_const").Single();
    Assert.Multiple(() => {
      Assert.That(Calls(module, "rt_str_concat"), Is.Empty, "the literal concat should fold first");
      Assert.That(Calls(module, "rt_str_ucase"), Is.Empty, "the newly joined ASCII literal should then case-fold");
      Assert.That(((IrGlobalVariable)literal.GetOperand(1)).Bytes, Is.EqualTo(Encoding.ASCII.GetBytes("ABZ{")));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }
}
