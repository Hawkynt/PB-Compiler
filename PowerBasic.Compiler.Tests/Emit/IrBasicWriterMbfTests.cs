using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterMbfTests {

  [TestCase("0##", "0##")]
  [TestCase("1##+2##^(-24)", "1##+2##^(-23)")]
  [TestCase("-1##-2##^(-24)", "-1##-2##^(-23)")]
  [TestCase("1##+3##*2##^(-24)", "1##+2##^(-22)")]
  [TestCase("1##+2##^(-24)-2##^(-55)", "1##")]
  [TestCase("1##+2##^(-24)+2##^(-55)", "1##+2##^(-23)")]
  [TestCase("2##-2##^(-24)", "2##")]
  [TestCase("2##^(-128)+2##^(-152)", "2##^(-128)+2##^(-151)")]
  [TestCase("2##^126+2##^102", "2##^126+2##^103")]
  public void Write_GivenMbfSingleConversion_ThenRenderedArithmeticKeepsItsPrecisionRule(
      string input, string expected) {
    var parameter = new IrArgument(IrType.F80, 0);
    var runtime = new IrFunction("rt_mbf32_narrow", IrType.F80, [new IrArgument(IrType.F80, 0)]);
    var function = new IrFunction("Narrow", IrType.F80, [parameter]);
    var entry = function.CreateBlock("entry");
    var rounded = entry.Append(new IrCall(IrType.F80, runtime, [parameter]));
    entry.Append(new IrRet(rounded));
    var rendered = IrBasicWriter.Write(function);
    var source = $"""
      DIM x AS EXT
      DIM y AS EXT
      x = {input}
      y = Narrow(x)
      IF y = ({expected}) THEN PRINT "exact" ELSE PRINT "wrong"
      END
      {rendered}
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35),
      Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).Output.Trim(), Is.EqualTo("exact"), rendered);
  }
}
