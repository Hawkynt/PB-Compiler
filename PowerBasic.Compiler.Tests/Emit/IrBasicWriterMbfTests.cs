using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterMbfTests {

  private static void AssertConstantRoundTrips(IrConstantFloat constant, byte[] expected, bool optimize)
    => AssertConstantRoundTrips(constant, expected, optimize, Dialect.Pb35);

  private static void AssertConstantRoundTrips(
      IrConstantFloat constant, byte[] expected, bool optimize, Dialect compat) {
    var function = new IrFunction("ExactValue", constant.Type);
    var entry = function.CreateBlock("entry");
    entry.Append(new IrRet(constant));
    var rendered = IrBasicWriter.Write(function);
    var source = $"""
      $COMPAT {compat.CanonicalName()}
      DIM y AS EXT
      y = ExactValue():p%=VARPTR(y)
      OPEN "EXACT.TXT" FOR OUTPUT AS #1
      FOR i%=0 TO 9:PRINT #1,PEEK(p%+i%);:NEXT i%:PRINT #1,
      CLOSE #1
      END
      {rendered}
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35),
      Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("EXACT.TXT"),
      Is.EqualTo(string.Concat(expected.Select(value => $" {value} ")) + "\r\n"
        + (compat.HasMbfStorage() ? "\u001a" : "")), rendered);
  }

  [TestCase(0x4000, 0x9999999999999A00UL, false)]
  [TestCase(0x4000, 0x9999999999999A00UL, true)]
  [TestCase(0x3FFF, 0x8000000000000100UL, false)]
  [TestCase(0x3FFF, 0x8000000000000100UL, true)]
  [TestCase(0xBFFF, 0x8000000000000100UL, false)]
  [TestCase(0xBFFF, 0x8000000000000100UL, true)]
  [TestCase(0x0000, 0x0000000000000001UL, false)]
  [TestCase(0x0000, 0x0000000000000001UL, true)]
  [TestCase(0x0001, 0x8000000000000000UL, false)]
  [TestCase(0x0001, 0x8000000000000000UL, true)]
  [TestCase(0x7FFE, 0xFFFFFFFFFFFFFFFFUL, false)]
  [TestCase(0x7FFE, 0xFFFFFFFFFFFFFFFFUL, true)]
  [TestCase(0x3BFF, 0x8000000000000001UL, false)]
  [TestCase(0x3BFF, 0x8000000000000001UL, true)]
  [TestCase(0x43FF, 0x8000000000000001UL, false)]
  [TestCase(0x43FF, 0x8000000000000001UL, true)]
  [TestCase(0x8000, 0x0000000000000000UL, false)]
  [TestCase(0x8000, 0x0000000000000000UL, true)]
  public void Write_GivenAFiniteExtendedConstant_ThenPreservesEveryBit(
      int exponent, ulong significand, bool optimize) {
    var constant = IrConstantFloat.FromFloat80Bits((ushort)exponent, significand);
    var expected = BitConverter.GetBytes(significand).Concat(BitConverter.GetBytes((ushort)exponent)).ToArray();
    AssertConstantRoundTrips(constant, expected, optimize);
  }

  [TestCase(32, false)]
  [TestCase(32, true)]
  [TestCase(64, false)]
  [TestCase(64, true)]
  public void Write_GivenIeeeNegativeZero_ThenPreservesItsSignWhenWidened(int bits, bool optimize) {
    var constant = new IrConstantFloat(bits == 32 ? IrType.F32 : IrType.F64, -0.0);
    AssertConstantRoundTrips(constant, [0, 0, 0, 0, 0, 0, 0, 0, 0, 128], optimize);
  }

  [TestCase(Dialect.Gw, false)]
  [TestCase(Dialect.Gw, true)]
  [TestCase(Dialect.Basica, false)]
  [TestCase(Dialect.Basica, true)]
  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Write_GivenNegativeZeroWithMbfCompatibility_ThenDoesNotDecodeItAsAnMbfNumber(
      Dialect compat, bool optimize) {
    var constant = IrConstantFloat.FromFloat80Bits(0x8000, 0);
    AssertConstantRoundTrips(constant, [0, 0, 0, 0, 0, 0, 0, 0, 0, 128], optimize, compat);
  }

  [TestCase(0x7FFF, 0x8000000000000000UL)]
  [TestCase(0x7FFF, 0xC000000000000001UL)]
  [TestCase(0x0001, 0x0000000000000001UL)]
  public void Write_GivenANonFiniteOrNonCanonicalExtendedConstant_ThenDeclinesExplicitly(
      int exponent, ulong significand) {
    var function = new IrFunction("ExactValue", IrType.F80);
    function.CreateBlock("entry").Append(new IrRet(IrConstantFloat.FromFloat80Bits((ushort)exponent, significand)));
    Assert.Throws<IrBasicWriterException>(() => IrBasicWriter.Write(function));
  }

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
