using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterUnsignedLiteralTests {

  [TestCase(8, -6, "250")]
  [TestCase(16, -1, "65535")]
  public void Write_GivenUnsignedNarrowBitPattern_ThenUsesItsPositiveBasicLiteral(int bits, long pattern, string expected) {
    var type = IrType.Integer(bits, signed: false);
    var print = new IrFunction(bits == 8 ? "rt_print_u8" : "rt_print_u16", IrType.Void, [new IrArgument(type, 0)]);
    var fn = new IrFunction("main", IrType.Void);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Void, print, [new IrConstantInt(type, pattern)]));
    entry.Append(new IrRet());

    var text = IrBasicWriter.Write(fn);

    Assert.That(text, Does.Contain($"PRINT {expected};"));
    Assert.That(text, Does.Not.Contain($"PRINT {pattern};"));
  }
}
