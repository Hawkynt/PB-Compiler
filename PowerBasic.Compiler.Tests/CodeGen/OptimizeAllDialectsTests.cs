using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The optimizer is a dialect-agnostic axis: it is only on by default for pb36, but EVERY
/// EXE-producing dialect must be able to run the full optimization pipeline (forced via
/// <see cref="CodeGenerator.Optimize"/>) and still emit a valid program. This pins
/// "every dialect may be fully optimized" so a future dialect-specific codegen path cannot
/// silently regress it.
/// </summary>
[TestFixture]
public sealed class OptimizeAllDialectsTests {

  // The compiling (EXE-producing) dialects across the three families; the interpreter dialects
  // (BASICA/GW/QBasic) emit no EXE and are excluded.
  private static readonly Dialect[] _compilingDialects = [
    Dialect.Tb10, Dialect.Tb11, Dialect.Pb21, Dialect.Pb30, Dialect.Pb32, Dialect.Pb35, Dialect.Pb36,
    Dialect.Qb10, Dialect.Qb45, Dialect.Pds70, Dialect.Pds71,
  ];

  // Syntax in the common subset of every listed dialect.
  private const string Source = """
    A% = 2
    B% = A% * 3 + 1
    OPEN "R.TXT" FOR OUTPUT AS #1
    PRINT #1, B%
    CLOSE #1
    END
    """;

  [TestCaseSource(nameof(_compilingDialects))]
  public void EmitExecutable_WithOptimizerForcedOn_SucceedsForEveryDialect(Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(Source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, $"{dialect} bind: " + string.Join("; ", model.Errors));

    var generator = new CodeGenerator(model) { Optimize = true };
    var exe = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, $"{dialect} optimized codegen: " + string.Join("; ", generator.Errors));
      Assert.That(exe, Is.Not.Empty, $"{dialect} produced an empty EXE under the optimizer");
    });
  }
}
