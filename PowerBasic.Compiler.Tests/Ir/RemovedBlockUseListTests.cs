using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// A value's use-list must name only readers that can actually run.
///
/// <para>
/// Deleting a block used to leave its instructions registered in their operands' use-lists, still
/// naming the deleted block as their parent. Nothing about the IR looked wrong afterwards - the
/// printed function was correct, the verifier was satisfied, and every program produced the right
/// answer - because a reader that cannot run cannot change one. What it changed was what the
/// optimizer was WILLING to do: every pass guarded by "is this value read outside the region" or
/// "does this have exactly one user" got its answer from a phantom and declined.
/// </para>
/// <para>
/// That failure mode leaves no trace, which is why the invariant is asserted directly here rather
/// than only through the transforms it enables. A pass that silently does nothing looks exactly like
/// a pass that had nothing to do.
/// </para>
/// </summary>
[TestFixture]
public sealed class RemovedBlockUseListTests {

  /// <summary>
  /// Shapes that make passes delete blocks: unrolling discards the original loop, SCCP discards the
  /// arm it proved unreachable, if-conversion and CFG simplification discard what they merged.
  /// </summary>
  private static readonly (string Name, string Source)[] _programs = [
    ("nested loops", """
      DIM i AS INTEGER
      DIM j AS INTEGER
      DIM t AS INTEGER
      FOR i = 1 TO 3
        FOR j = 1 TO 3
          t = t + 1
        NEXT j
      NEXT i
      PRINT t
      END
      """),
    ("folded branch", """
      DIM n AS INTEGER
      DIM t AS INTEGER
      n = 3
      IF n > 10 THEN
        t = 1
      ELSE
        t = 2
      END IF
      PRINT t
      END
      """),
    ("loop and branch", """
      DIM i AS INTEGER
      DIM s AS INTEGER
      FOR i = 1 TO 4
        IF i < 3 THEN
          s = s + i
        ELSE
          s = s - i
        END IF
      NEXT i
      PRINT s
      END
      """),
  ];

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Lowers and optimizes exactly as <see cref="CodeGenerator"/> does, recovery sweeps and all.</summary>
  private static IrModule Optimized(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Recover(module!);
    IrPassManager.Standard().RunOnModule(module!);
    Recover(module!);
    IrPassManager.Standard().RunOnModule(module!);
    return module!;

    static void Recover(IrModule m) {
      foreach (var fn in m.Functions)
        if (!fn.IsDeclaration)
          IntegerRecovery.Run(fn);
    }
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [Test]
  public void UseList_GivenBlocksTheOptimizerDeleted_ThenNoValueIsStillReadFromOne() {
    foreach (var (name, source) in _programs) {
      var module = Optimized(source);
      foreach (var fn in module.Functions) {
        if (fn.IsDeclaration)
          continue;
        var live = new HashSet<IrBasicBlock>(fn.Blocks, ReferenceEqualityComparer.Instance);
        var phantoms = fn.AllInstructions
          .SelectMany(instruction => instruction.Users)
          .Where(user => user.Parent is null || !live.Contains(user.Parent))
          .ToList();
        Assert.That(phantoms, Is.Empty,
          $"'{name}': {fn.Name} keeps {phantoms.Count} use(s) by instructions no longer in the function");
      }
    }
  }

  /// <summary>
  /// The transform the phantoms were blocking. An inner loop unrolls into its parent's body, which
  /// leaves the outer loop a counted loop over one integer add - and the outer loop stayed forever,
  /// because the dead float shadow of the accumulator, in a block unrolling had already deleted,
  /// still counted as reading it.
  /// </summary>
  [TestCase("DIM i AS INTEGER\nDIM j AS INTEGER\nDIM t AS INTEGER\nFOR i = 1 TO 3\n FOR j = 1 TO 3\n  t = t + 1\n NEXT j\nNEXT i\nPRINT t\nEND", 9)]
  [TestCase("DIM i AS INTEGER\nDIM j AS INTEGER\nDIM k AS INTEGER\nDIM t AS INTEGER\nFOR i = 1 TO 2\n FOR j = 1 TO 2\n  FOR k = 1 TO 2\n   t = t + 1\n  NEXT k\n NEXT j\nNEXT i\nPRINT t\nEND", 8)]
  public void NestedConstantLoop_WhenOptimized_ThenItBecomesTheConstantItComputes(string source, int expected) {
    var main = Optimized(source).Functions.Single(fn => fn.Name == "main");
    Assert.That(main.Blocks, Has.Count.EqualTo(1), "the loops should be gone entirely:\n" + IrPrinter.Print(main));
    Assert.That(IrPrinter.Print(main), Does.Contain($"i16 {expected}"), "the accumulator's final value should be folded in");
  }

  /// <summary>
  /// And it still prints what it printed. Folding a loop away is only correct if the answer survives,
  /// so the IR is rendered back to BASIC and run against the program compiled directly.
  /// </summary>
  [Test]
  public void Optimized_GivenTheseShapes_ThenTheProgramStillPrintsTheSame() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(IrBasicWriter.Write(Optimized(source))), Is.EqualTo(Run(source)), $"program '{name}'");
  }
}
