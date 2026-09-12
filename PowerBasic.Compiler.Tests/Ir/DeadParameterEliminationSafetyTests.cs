using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Ownership and malformed-IR refusal cases for O0069.</summary>
[TestFixture]
public sealed class DeadParameterEliminationSafetyTests {

  [Test]
  public void Function_GivenTheSameCallAlsoPassesItsAddress_ThenTheEscapeIsNotMistakenForOwnership() {
    var module = new IrModule("t") { OwnsProcedureAbi = true };
    var parameter = new IrArgument(IrType.Ptr, 0, "unused");
    var callee = module.AddFunction(new IrFunction("callee", IrType.Void, [parameter]));
    callee.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.Void, callee, [callee]));
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.Zero);
    Assert.That(callee.Parameters, Has.Count.EqualTo(1));
    Assert.That(call.ArgCount, Is.EqualTo(1));
  }

  [Test]
  public void Function_GivenACallWithTheWrongArgumentType_ThenTheMalformedShapeIsDeclined() {
    var module = new IrModule("t") { OwnsProcedureAbi = true };
    var parameter = new IrArgument(IrType.I16, 0, "unused");
    var callee = module.AddFunction(new IrFunction("callee", IrType.Void, [parameter]));
    callee.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.Void, callee, [new IrConstantInt(IrType.I32, 1)]));
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.Zero);
    Assert.That(callee.Parameters, Has.Count.EqualTo(1));
    Assert.That(call.ArgCount, Is.EqualTo(1));
  }

  [TestCase(true, false)]
  [TestCase(false, true)]
  public void Function_GivenOpaqueControlFlow_ThenItsAbiIsNotRewritten(bool errorHandler, bool inlineAsm) {
    var module = new IrModule("t") { OwnsProcedureAbi = true };
    var parameter = new IrArgument(IrType.I16, 0, "unused");
    var callee = module.AddFunction(new IrFunction("callee", IrType.Void, [parameter]));
    callee.HasErrorHandler = errorHandler;
    callee.HasInlineAsm = inlineAsm;
    callee.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.Void, callee, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.Zero);
    Assert.That(callee.Parameters, Has.Count.EqualTo(1));
    Assert.That(call.ArgCount, Is.EqualTo(1));
  }
}
