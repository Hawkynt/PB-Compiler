using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Performance;

/// <summary>
/// Deterministic advisory dispatch-count comparisons. REP STOSW/STOSD count as
/// one interpreter dispatch, not one hardware cycle: this measures how much
/// scalar runtime work disappeared, not absolute throughput on a real CPU.
/// </summary>
[TestFixture]
[Category("Performance")]
public sealed class GraphicsSpanDispatchTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu) {
    var source = cpu + "\nSCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(
      Lexer.Tokenize(source, "SPAN.BAS", Dialect.Pb36), "SPAN.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var compiler = new CodeGenerator(model) { Optimize = optimize };
    var image = compiler.EmitExecutable();
    Assert.That(compiler.Errors, Is.Empty, string.Join("; ", compiler.Errors));
    Assert.That(compiler.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("$CPU 8086")]
  [TestCase("$CPU 80386")]
  public void FilledRectangle_GivenPackedMode_ThenReducesInterpreterDispatches(string cpu) {
    const string program = """
      LINE (0,40)-(319,59),11,BF
      PRINT POINT(0,40);POINT(319,59);POINT(0,60)
      """;
    var baseline = Execute(program, optimize: false, cpu);
    var improved = Execute(program, optimize: true, cpu);
    Assert.That(improved.Output, Is.EqualTo(baseline.Output));
    TestContext.Out.WriteLine($"{cpu}: BF 320x20, baseline={baseline.ExecutedInstructions}, optimized={improved.ExecutedInstructions} dispatches");
    Assert.That(improved.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions));
  }

  [TestCase("$CPU 8086")]
  [TestCase("$CPU 80386")]
  public void HorizontalLine_GivenSolidStyle_ThenReducesInterpreterDispatches(string cpu) {
    const string program = """
      LINE (0,100)-(319,100),7
      PRINT POINT(0,100);POINT(319,100);POINT(1,101)
      """;
    var baseline = Execute(program, optimize: false, cpu);
    var improved = Execute(program, optimize: true, cpu);
    Assert.That(improved.Output, Is.EqualTo(baseline.Output));
    TestContext.Out.WriteLine($"{cpu}: LINE 320, baseline={baseline.ExecutedInstructions}, optimized={improved.ExecutedInstructions} dispatches");
    Assert.That(improved.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions));
  }
}
