using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Opaque-pointer safety requirements at the scalar/aggregate boundary of mem2reg.</summary>
[TestFixture]
public sealed class Mem2RegAggregateSafetyTests {

  [Test]
  public void Mem2Reg_GivenDifferentlyTypedUnionViewsAtOffsetZero_WhenRun_ThenPackedBackingIsNotPromoted() {
    // Given a UNION whose differently-sized fields both use the backing pointer directly at offset zero.
    const string source = """
      UNION Overlay
        I AS INTEGER
        L AS LONG
      END UNION
      PRINT Probe&(7)
      END
      FUNCTION Probe&(BYVAL x&)
        DIM u AS Overlay
        u.L = x&
        Probe& = u.I
      END FUNCTION
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var probe = module!.Functions.Single(f => f.Name.Equals("Probe", StringComparison.OrdinalIgnoreCase));
    var backing = probe.AllInstructions.OfType<IrAlloca>().Single(a => a.Allocated == IrType.I8 && a.Count == 4);

    // When mem2reg considers direct offset-zero accesses under opaque pointers.
    Mem2Reg.Run(probe);

    // Then the i8 backing survives because its i32 store/i16 load are not i8 scalar accesses.
    Assert.Multiple(() => {
      Assert.That(probe.AllInstructions.Contains(backing), Is.True);
      Assert.That(backing.Users.OfType<IrStore>().Any(s => s.Value.Type == IrType.I32), Is.True);
      Assert.That(backing.Users.OfType<IrLoad>().Any(l => l.Type == IrType.I16), Is.True);
      Assert.That(IrVerifier.Verify(probe), Is.Empty);
    });
  }
}
