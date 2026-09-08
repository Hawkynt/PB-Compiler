using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0299: comparisons between literal handles whose backing pool entry is already canonical.</summary>
[TestFixture]
public sealed class StringConstantFoldTests {

  [Test]
  public void Compare_GivenTwoCallsBackedByTheSameInternedLiteral_ThenFoldsToEqual() {
    var module = new IrModule("test");
    var literal = module.AddFunction(new IrFunction("rt_str_const", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var compare = module.AddFunction(new IrFunction("rt_str_compare", IrType.I32,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1)]));
    var main = module.AddFunction(new IrFunction("main", IrType.I32));
    var entry = main.CreateBlock("entry");

    var firstGlobal = module.AddStringConstant("fast"u8.ToArray());
    var secondGlobal = module.AddStringConstant("fast"u8.ToArray());
    Assert.That(secondGlobal, Is.SameAs(firstGlobal), "precondition: exact literals must be interned");

    var left = entry.Append(new IrCall(IrType.Ptr, literal,
      [firstGlobal, new IrConstantInt(IrType.I32, 4)]));
    var right = entry.Append(new IrCall(IrType.Ptr, literal,
      [secondGlobal, new IrConstantInt(IrType.I32, 4)]));
    var result = entry.Append(new IrCall(IrType.I32, compare, [left, right]));
    entry.Append(new IrRet(result));
    Assert.That(IrVerifier.Verify(module), Is.Empty, "precondition: test IR must be valid");

    Assert.That(StringConstantFold.Run(module), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(entry.Instructions.OfType<IrCall>(), Is.Empty,
        "the comparison and both literal-handle producers should disappear");
      Assert.That(entry.Terminator, Is.TypeOf<IrRet>());
      Assert.That(((IrRet)entry.Terminator!).Value, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)((IrRet)entry.Terminator!).Value!).Value, Is.Zero);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }
}
