using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class AggregateSroaDiagnosticTests {
  [Test]
  public void Lower_GivenIndependentPackedFields_ThenReportBackingUsers() {
    var source = """
      TYPE Rec
        A AS BYTE
        B AS INTEGER
        C AS LONG
      END TYPE
      PRINT Probe&(3)
      END
      FUNCTION Probe&(BYVAL x&)
        DIM r AS Rec
        r.A = 7
        r.B = 1234
        r.C = x&
        Probe& = r.A + r.B + r.C
      END FUNCTION
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty);
    var module = IrLowering.TryLowerModule(model)!;
    var probe = module.Functions.Single(f => f.Name.Equals("Probe", StringComparison.OrdinalIgnoreCase));
    var backing = probe.AllInstructions.OfType<IrAlloca>().Single(a => a.Allocated == IrType.I8 && a.Count == 7);

    static string Describe(IrInstruction instruction) => instruction switch {
      IrLoad load => $"load {load.Type}",
      IrStore store => $"store {store.Value.Type}",
      IrGep { ElementType: var elementType, ByteOffset: IrConstantInt offset } gep
        => $"gep {(elementType is null ? "byte" : elementType.ToString())} {offset.Value} -> [{string.Join(", ", gep.Users.Select(Describe))}]",
      IrCall call when call.Callee is IrFunction callee => $"call {callee.Name}",
      _ => instruction.GetType().Name,
    };

    Assert.Fail($"HasErrorHandler={probe.HasErrorHandler}; HasInlineAsm={probe.HasInlineAsm}; backing users: {string.Join(" | ", backing.Users.Select(Describe))}");
  }
}
