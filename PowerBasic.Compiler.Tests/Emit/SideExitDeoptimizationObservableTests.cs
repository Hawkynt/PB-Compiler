using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// Execution-level O0310 regression. The failed assumption happens after an observable accumulator
/// update; restarting the generic iteration would therefore change the printed result.
/// </summary>
[TestFixture]
public sealed class SideExitDeoptimizationObservableTests {

  private const string _SOURCE = """
    DIM i AS INTEGER
    DIM s AS INTEGER
    FOR i = 0 TO 15
      s = s + 1
      IF s < 10 THEN
        s = s + 2
      ELSE
        s = s + 100
      END IF
    NEXT i
    PRINT s
    END
    """;

  [Test]
  public void Loop_GivenTheAssumptionEventuallyFails_ThenObservableBehaviorIsPreserved() {
    var expected = Run(_SOURCE);
    var module = IrLowering.TryLowerModule(Bind(_SOURCE), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);

    var guards = fn.Blocks
      .Select(b => b.Terminator)
      .OfType<IrCondBr>()
      .Where(b => b.Condition is IrCmp { IsSourceCondition: true })
      .ToList();
    var changed = false;
    foreach (var header in fn.Blocks.ToList()) {
      foreach (var guard in guards)
        if (SideExitDeoptimization.TryVersionLoop(fn, header, guard, assumedValue: true, out _)) {
          changed = true;
          break;
        }
      if (changed)
        break;
    }

    Assert.That(changed, Is.True, "the source IF should be a canonical side-exit candidate");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(Run(IrBasicWriter.Write(module)), Is.EqualTo(expected));
  }

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }
}
