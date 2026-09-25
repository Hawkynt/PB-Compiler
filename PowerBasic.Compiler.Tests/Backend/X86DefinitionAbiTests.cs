using PowerBasic.Compiler.Asm;
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
  public void Layout_GivenRegisterConventionWithALongInARegisterPosition_ThenItDeclines(IrCallConvention convention) {
    // the LONG is the second parameter, which a register convention passes in DX: two words do not fit
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Function(convention), out _, out var decline), Is.False);
    Assert.That(decline, Does.Contain("not one word"));
  }

  private static IrFunction Words(IrCallConvention convention, int count) => new("f", IrType.Void,
    [.. Enumerable.Range(0, count).Select(i => new IrArgument(IrType.I16, i, $"w{i}"))]) {
      Convention = convention,
    };

  /// <summary>
  /// The prologue pushes the argument registers in parameter order, so parameter 0 is at [BP-2];
  /// what the registers cannot hold is on the stack in the convention's order, above the return.
  /// </summary>
  [Test]
  public void Layout_GivenWatcallWithFiveWords_ThenFourSpillBelowBpAndOneIsOnTheStack() {
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Words(IrCallConvention.Watcall, 5), out var layout, out var decline), Is.True, decline);
    Assert.Multiple(() => {
      Assert.That(layout.ParameterOffsets, Is.EqualTo(new[] { -2, -4, -6, -8, 4 }));
      Assert.That(layout.ParameterBytes, Is.EqualTo(2), "only the stack argument is the callee's to pop");
      Assert.That(layout.Spills, Is.EqualTo(new[] { Reg.AX, Reg.DX, Reg.BX, Reg.CX }));
    });
  }

  [Test]
  public void Layout_GivenFastcallWithFourWords_ThenThreeSpillBelowBp() {
    Assert.That(X86CallAbi.TryDefinitionStackLayout(Words(IrCallConvention.Fastcall, 4), out var layout, out var decline), Is.True, decline);
    Assert.Multiple(() => {
      Assert.That(layout.ParameterOffsets, Is.EqualTo(new[] { -2, -4, -6, 4 }));
      Assert.That(layout.Spills, Is.EqualTo(new[] { Reg.AX, Reg.DX, Reg.BX }));
    });
  }
}
