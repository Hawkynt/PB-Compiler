using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The observable contract, made checkable: an optimization pass may rewrite a program however it
/// likes, and the rewritten program must still PRINT the same thing.
///
/// This is what <see cref="IrBasicWriter"/> was for. Rendering the IR back to BASIC gives a pass a
/// before and an after that are both runnable programs, so "did this pass preserve behaviour" stops
/// being an argument about instruction counts and becomes a comparison of output. Byte-identity is
/// the wrong question here and always will be - a pass that changes the code is supposed to change
/// the code - so this is the strongest correctness statement available at this level.
///
/// Each pass is checked ON ITS OWN, on top of mem2reg (without which most of them see nothing to do),
/// so a failure names the pass rather than the pipeline. The whole pipeline is checked as well,
/// because passes can interact in ways none of them does alone.
/// </summary>
[TestFixture]
public sealed class IrPassObservableEquivalenceTests {

  /// <summary>The pipeline's passes, individually, so a failure can name one.</summary>
  private static readonly (string Name, Func<IrFunction, int> Run)[] _passes = [
    ("instcombine", InstCombine.Run),
    ("sccp", Sccp.Run),
    ("correlate", CorrelatedValueProp.Run),
    ("gvn", Gvn.Run),
    ("memopt", RedundantMemory.Run),
    ("dse", DeadStoreElim.Run),
    ("licm", Licm.Run),
    ("dce", Dce.Run),
    ("ifconv", IfConversion.Run),
    ("simplifycfg", SimplifyCfg.Run),
    ("unroll", LoopUnroll.Run),
    ("sroa", ScalarReplaceArrays.Run),
    ("reassociate", Reassociate.Run),
    ("phicong", PhiCongruence.Run),
    ("demote", FloatDemotion.Run),
    ("unswitch", LoopUnswitch.Run),
  ];

  /// <summary>The interprocedural passes, which need the whole module rather than one function.</summary>
  private static readonly (string Name, Func<IrModule, int> Run)[] _modulePasses = [
    ("ipconstprop", IpConstantProp.Run),
    ("readonly-globals", ReadOnlyGlobals.Run),
    ("localize-globals", LocalizeGlobals.Run),
  ];

  /// <summary>
  /// Programs whose IR the writer can render end to end. They are deliberately ordinary - loops,
  /// branches, arrays, calls, output - because a pass that breaks something breaks it on the shapes
  /// real programs are made of, not on a construct chosen to be difficult.
  /// </summary>
  private static readonly (string Name, string Source)[] _programs = [
    ("arithmetic", """
      x% = 7
      y% = x% * 3 + 1
      PRINT "y="; y%
      PRINT y% - x%
      END
      """),
    ("loop", """
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 6
        s = s + i * i
        PRINT i; s
      NEXT i
      END
      """),
    ("branches", """
      DIM i AS INTEGER
      FOR i = -2 TO 2
        IF i < 0 THEN
          PRINT "neg"; i
        ELSEIF i = 0 THEN
          PRINT "zero"
        ELSE
          PRINT "pos"; i
        END IF
      NEXT i
      END
      """),
    ("array", """
      DIM a(0 TO 9) AS INTEGER
      DIM i AS INTEGER
      FOR i = 0 TO 9
        a(i) = i * i - 3
      NEXT i
      FOR i = 9 TO 0 STEP -1
        PRINT a(i);
      NEXT i
      PRINT
      END
      """),
    ("calls", """
      PRINT Twice%(21)
      CALL Announce(3)
      END
      FUNCTION Twice%(BYVAL n%)
        Twice% = n% * 2
      END FUNCTION
      SUB Announce(BYVAL n%)
        PRINT "n="; n%
      END SUB
      """),
    ("nested loops", """
      DIM i AS INTEGER
      DIM j AS INTEGER
      DIM t AS INTEGER
      t = 0
      FOR i = 1 TO 4
        FOR j = 1 TO 3
          t = t + i * j
        NEXT j
        PRINT i; t
      NEXT i
      END
      """),
  ];

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Lowers, applies <paramref name="transform"/>, renders back to BASIC and runs it.</summary>
  private static string RunThrough(string source, Action<IrModule> transform) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    transform(module!);
    return Run(IrBasicWriter.Write(module!));
  }

  private static void RunOnEveryFunction(IrModule module, Func<IrFunction, int> pass) {
    foreach (var fn in module.Functions)
      if (!fn.IsDeclaration)
        pass(fn);
  }

  /// <summary>
  /// The baseline every pass is judged against: the program itself, compiled directly. Rendering the
  /// UNOPTIMIZED IR would be a weaker reference - it would only prove a pass agrees with the writer,
  /// not that either agrees with the language.
  /// </summary>
  [Test]
  public void Render_GivenTheUnoptimizedIr_ThenItAlreadyMatchesTheProgram() {
    foreach (var (name, source) in _programs)
      Assert.That(RunThrough(source, _ => { }), Is.EqualTo(Run(source)), $"program '{name}' before any pass");
  }

  [Test]
  public void Pass_GivenEachOptimizationOnItsOwn_ThenTheProgramStillPrintsTheSame() {
    var failures = new StringBuilder();
    foreach (var (program, source) in _programs) {
      var expected = Run(source);
      foreach (var (pass, run) in _passes) {
        string got;
        try {
          got = RunThrough(source, m => {
            RunOnEveryFunction(m, Mem2Reg.Run);   // most passes see nothing to do without it
            RunOnEveryFunction(m, run);
          });
        } catch (Exception e) {
          failures.AppendLine($"  {pass,-12} on '{program}': threw {e.GetType().Name}: {e.Message}");
          continue;
        }
        if (got != expected)
          failures.AppendLine($"  {pass,-12} on '{program}': expected <{expected}> got <{got}>");
      }
    }

    Assert.That(failures.ToString(), Is.Empty, "an optimization pass changed what a program prints:\n" + failures);
  }

  /// <summary>
  /// The same contract for the passes that reason across the call graph. They are checked separately
  /// because a per-function harness would never give them a second function to look at.
  /// </summary>
  [Test]
  public void ModulePass_GivenEachInterproceduralPass_ThenTheProgramStillPrintsTheSame() {
    var failures = new StringBuilder();
    foreach (var (program, source) in _programs) {
      var expected = Run(source);
      foreach (var (pass, run) in _modulePasses) {
        string got;
        try {
          got = RunThrough(source, m => {
            RunOnEveryFunction(m, Mem2Reg.Run);
            run(m);
          });
        } catch (Exception e) {
          failures.AppendLine($"  {pass,-12} on '{program}': threw {e.GetType().Name}: {e.Message}");
          continue;
        }
        if (got != expected)
          failures.AppendLine($"  {pass,-12} on '{program}': expected <{expected}> got <{got}>");
      }
    }

    Assert.That(failures.ToString(), Is.Empty, "an interprocedural pass changed what a program prints:\n" + failures);
  }

  [Test]
  public void Pipeline_GivenTheWholeStandardPipeline_ThenTheProgramStillPrintsTheSame() {
    var failures = new StringBuilder();
    foreach (var (program, source) in _programs) {
      var expected = Run(source);
      var got = RunThrough(source, m => IrPassManager.Standard().RunOnModule(m));
      if (got != expected)
        failures.AppendLine($"  '{program}': expected <{expected}> got <{got}>");
    }

    Assert.That(failures.ToString(), Is.Empty, "the optimization pipeline changed what a program prints:\n" + failures);
  }

  /// <summary>
  /// Running the pipeline twice must change nothing further that matters. A pass whose output it
  /// cannot itself handle shows up here and nowhere else.
  /// </summary>
  [Test]
  public void Pipeline_GivenItRunsTwice_ThenTheSecondRunChangesNothingObservable() {
    foreach (var (program, source) in _programs) {
      var once = RunThrough(source, m => IrPassManager.Standard().RunOnModule(m));
      var twice = RunThrough(source, m => {
        IrPassManager.Standard().RunOnModule(m);
        IrPassManager.Standard().RunOnModule(m);
      });
      Assert.That(twice, Is.EqualTo(once), $"program '{program}' differs when the pipeline runs twice");
    }
  }
}
