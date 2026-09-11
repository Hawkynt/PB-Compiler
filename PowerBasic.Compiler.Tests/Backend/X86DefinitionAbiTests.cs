using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class X86DefinitionAbiTests {

  private static IrFunction Function(IrCallConvention convention) => new("f", IrType.Void,
    [new IrArgument(IrType.I16, 0, "word"), new IrArgument(IrType.I32, 1, "long")]) {
      Convention = convention,
    };

  [TestCase(IrCallConvention.Basic)]
  [TestCase(IrCallConvention.Pascal)]
  public void Layout_GivenLeftToRightStackConvention_ThenLastArgumentIsNearestReturnAddress(IrCallConvention convention) {
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Function(convention), out var layout, out var decline), Is.True, decline);
    Assert.Multiple(() => {
      Assert.That(layout.ParameterOffsets, Is.EqualTo(new[] { 8, 4 }));
      Assert.That(layout.ParameterBytes, Is.EqualTo(6));
      Assert.That(X86CallAbi.For(convention).StackCleanup, Is.EqualTo(X86StackCleanup.Callee));
    });
  }

  [TestCase(IrCallConvention.Cdecl, X86StackCleanup.Caller)]
  [TestCase(IrCallConvention.Stdcall, X86StackCleanup.Callee)]
  public void Layout_GivenRightToLeftStackConvention_ThenFirstArgumentIsNearestReturnAddress(
      IrCallConvention convention, X86StackCleanup cleanup) {
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Function(convention), out var layout, out var decline), Is.True, decline);
    Assert.Multiple(() => {
      Assert.That(layout.ParameterOffsets, Is.EqualTo(new[] { 4, 6 }));
      Assert.That(layout.ParameterBytes, Is.EqualTo(6));
      Assert.That(X86CallAbi.For(convention).StackCleanup, Is.EqualTo(cleanup));
    });
  }

  [TestCase(IrCallConvention.Fastcall)]
  [TestCase(IrCallConvention.Watcall)]
  public void Layout_GivenRegisterConvention_ThenDeclinesUntilDefinitionRegisterPlanExists(IrCallConvention convention) {
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Function(convention), out _, out var decline), Is.False);
    Assert.That(decline, Does.Contain("register definition ABI"));
  }
}
