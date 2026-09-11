using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrCallingConventionTests {

  [Test]
  public void Builder_GivenDirectConvention_WhenDefinitionWasUnbound_ThenBindsDefinitionAbi() {
    var callee = new IrFunction("f", IrType.Void);
    callee.CreateBlock("entry").Append(new IrRet());
    var caller = new IrFunction("caller", IrType.Void);
    var builder = new IrBuilder(caller.CreateBlock("entry"));

    builder.Call(IrType.Void, callee, IrCallConvention.Cdecl, []);
    builder.Ret();

    Assert.Multiple(() => {
      Assert.That(callee.Convention, Is.EqualTo(IrCallConvention.Cdecl));
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  [Test]
  public void Verify_GivenDirectCallConventionDifferentFromBoundDefinition_ThenRejectsAbiMismatch() {
    var callee = new IrFunction("f", IrType.Void) { Convention = IrCallConvention.Cdecl };
    callee.CreateBlock("entry").Append(new IrRet());
    var caller = new IrFunction("caller", IrType.Void);
    var entry = caller.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Void, callee, [], IrCallConvention.Stdcall));
    entry.Append(new IrRet());

    var errors = IrVerifier.Verify(caller);
    Assert.Multiple(() => {
      Assert.That(errors, Has.Some.Contains("direct call convention Stdcall"));
      Assert.That(errors, Has.Some.Contains("callee 'f' convention Cdecl"));
    });
  }

  [Test]
  public void Verify_GivenMatchingDirectCallAndDefinitionConvention_ThenAcceptsAbi() {
    var callee = new IrFunction("f", IrType.Void) { Convention = IrCallConvention.Stdcall };
    callee.CreateBlock("entry").Append(new IrRet());
    var caller = new IrFunction("caller", IrType.Void);
    var entry = caller.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Void, callee, [], IrCallConvention.Stdcall));
    entry.Append(new IrRet());

    Assert.That(IrVerifier.Verify(caller), Is.Empty);
  }

  [Test]
  public void Print_GivenDefinitionConvention_ThenDumpMakesAbiVisible() {
    var function = new IrFunction("f", IrType.Void) { Convention = IrCallConvention.Cdecl };
    function.CreateBlock("entry").Append(new IrRet());

    Assert.That(IrPrinter.Print(function), Does.StartWith("define cdecl void @f()"));
  }
}
