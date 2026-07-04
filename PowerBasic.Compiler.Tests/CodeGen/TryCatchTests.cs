using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// PB 3.6 structured exception handling - TRY / CATCH / FINALLY / END TRY
/// (no RESUME). Front-end tests (parse / bind / dialect gate) always run;
/// the behavioral tests compile and run under DOSBox and observe the
/// body -> catch -> finally ordering, ERR on the caught path and that the
/// previously armed ON ERROR handler is restored after END TRY. The latter
/// are skipped when DOSBox is unavailable.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TryCatchTests {

  #region helpers

  private static TryStmt ParseTry(string source) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36);
    var unit = Parser.Parse(tokens, "TEST.BAS", Dialect.Pb36);
    Assert.That(unit.Statements, Has.Count.EqualTo(1));
    Assert.That(unit.Statements[0], Is.InstanceOf<TryStmt>());
    return (TryStmt)unit.Statements[0];
  }

  private static IReadOnlyList<Diagnostic> CompileErrors(string source, Dialect dialect) {
    try {
      var tokens = Lexer.Tokenize(source, "TEST.BAS", dialect);
      var unit = Parser.Parse(tokens, "TEST.BAS", dialect);
      return Binder.Bind(unit, dialect).Errors;
    } catch (ParserException) {
      return [new Diagnostic(new("TEST.BAS", 0, 0), "parser-rejected")];
    }
  }

  private static string Run(string source) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36);
    var unit = Parser.Parse(tokens, "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  #endregion

  #region parse structure (equivalence classes: catch+finally / catch-only / finally-only / nested)

  [Test]
  public void Parse_GivenTryCatchFinally_WhenParsed_ThenAllThreeBlocksFilled() {
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      CATCH
        PRINT "catch"
      FINALLY
        PRINT "fin"
      END TRY
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Body, Has.Count.EqualTo(1));
      Assert.That(stmt.Catch, Has.Count.EqualTo(1));
      Assert.That(stmt.Finally, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenTryCatchOnly_WhenParsed_ThenFinallyIsNull() {
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      CATCH
        PRINT "catch"
      END TRY
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Catch, Has.Count.EqualTo(1));
      Assert.That(stmt.Finally, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenTryFinallyOnly_WhenParsed_ThenCatchIsNull() {
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      FINALLY
        PRINT "fin"
      END TRY
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Catch, Is.Null);
      Assert.That(stmt.Finally, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenPlainCatch_WhenParsed_ThenBodyIsUnchangedNotAnIf() {
    // a single unfiltered CATCH must keep its bare body (byte-identical to the pre-filter lowering)
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      CATCH
        PRINT "catch"
      END TRY
      """);
    Assert.That(stmt.Catch![0], Is.Not.InstanceOf<IfStmt>());
  }

  [Test]
  public void Parse_GivenFilteredCatches_WhenParsed_ThenFoldToIfChainWithReraiseElse() {
    // CATCH n / CATCH n WHEN c fold into one catch body: an IF/ELSEIF whose ELSE re-raises ERR
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      CATCH 5
        PRINT "five"
      CATCH 7 WHEN x = 1
        PRINT "seven"
      END TRY
      """);
    var iff = (IfStmt)stmt.Catch!.Single();
    Assert.Multiple(() => {
      Assert.That(iff.Condition, Is.InstanceOf<BinaryExpr>(), "first filter is ERR = 5");
      Assert.That(iff.ElseIfs, Has.Count.EqualTo(1), "the second filtered CATCH is an ELSEIF");
      Assert.That(iff.Else, Is.Not.Null);
      Assert.That(iff.Else!.OfType<ErrorStmt>().Any(), Is.True, "no catch-all -> the ELSE re-raises ERR");
    });
  }

  [Test]
  public void Parse_GivenFilteredThenCatchAll_WhenParsed_ThenCatchAllIsTheElse() {
    var stmt = ParseTry("""
      TRY
        PRINT "body"
      CATCH 5
        PRINT "five"
      CATCH
        PRINT "rest"
      END TRY
      """);
    var iff = (IfStmt)stmt.Catch!.Single();
    Assert.Multiple(() => {
      Assert.That(iff.Else, Is.Not.Null, "the unfiltered CATCH becomes the ELSE");
      Assert.That(iff.Else!.OfType<ErrorStmt>().Any(), Is.False, "a catch-all swallows - no re-raise");
    });
  }

  [Test]
  public void Parse_GivenDefer_WhenParsed_ThenWrapsRestOfBlockInTryFinally() {
    // DEFER lowers to TRY <rest> FINALLY <deferred>; a second DEFER nests inside (LIFO)
    var unit = Parser.Parse(Lexer.Tokenize(
      "PRINT \"a\"\nDEFER PRINT \"x\"\nPRINT \"b\"\nDEFER PRINT \"y\"\nPRINT \"c\"\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    Assert.That(unit.Statements[0], Is.InstanceOf<PrintStmt>(), "statements before the first DEFER stay");
    var outer = unit.Statements.OfType<TryStmt>().Single();
    Assert.Multiple(() => {
      Assert.That(outer.Catch, Is.Null);
      Assert.That(outer.Finally, Has.Count.EqualTo(1), "the first DEFER's body is the FINALLY");
      Assert.That(outer.Body.OfType<TryStmt>().Any(), Is.True, "the second DEFER nests a TRY/FINALLY inside (LIFO)");
    });
  }

  [Test]
  public void Parse_GivenDeferBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("DEFER PRINT \"x\"\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Parse_GivenNestedTry_WhenParsed_ThenInnerTryLivesInOuterBody() {
    var stmt = ParseTry("""
      TRY
        TRY
          PRINT "inner"
        CATCH
          PRINT "innercatch"
        END TRY
      CATCH
        PRINT "outercatch"
      END TRY
      """);
    Assert.That(stmt.Body[0], Is.InstanceOf<TryStmt>());
  }

  [Test]
  public void Parse_GivenEmptyBodyWithCatch_WhenParsed_ThenBodyIsEmpty() {
    var stmt = ParseTry("""
      TRY
      CATCH
        PRINT "c"
      END TRY
      """);
    Assert.That(stmt.Body, Is.Empty);
  }

  #endregion

  #region dialect gate & well-formedness (boundary + exceptional cases)

  [Test]
  public void Compile_GivenTryUnderPb35_WhenCompiled_ThenRejected() {
    var errors = CompileErrors("""
      TRY
        PRINT "body"
      CATCH
        PRINT "c"
      END TRY
      """, Dialect.Pb35);
    Assert.That(errors, Is.Not.Empty, "pb35 must reject TRY");
  }

  [Test]
  public void Compile_GivenTryUnderPb36_WhenCompiled_ThenAccepted() {
    var errors = CompileErrors("""
      TRY
        PRINT "body"
      CATCH
        PRINT "c"
      END TRY
      """, Dialect.Pb36);
    Assert.That(errors, Is.Empty, "bind: " + string.Join("; ", errors.Select(e => e.Message)));
  }

  [Test]
  public void Compile_GivenTryWithoutCatchOrFinally_WhenParsed_ThenRejected() {
    var errors = CompileErrors("""
      TRY
        PRINT "body"
      END TRY
      """, Dialect.Pb36);
    Assert.That(errors, Is.Not.Empty, "a bare TRY/END TRY must be rejected");
  }

  #endregion

  #region behavioral (DOSBox; observable body -> catch -> finally ordering)

  [Test]
  public void Execute_GivenTryNoError_WhenRun_ThenBodyAndFinallyRunCatchSkipped() {
    const string source = """
      TRY
        PRINT "body"
      CATCH
        PRINT "catch"
      FINALLY
        PRINT "fin"
      END TRY
      PRINT "after"
      """;
    Assert.That(Run(source), Is.EqualTo("body\nfin\nafter\n"));
  }

  [Test]
  public void Execute_GivenTryWithError_WhenRun_ThenCatchRunsWithErrThenFinally() {
    const string source = """
      TRY
        ERROR 5
        PRINT "unreached"
      CATCH
        PRINT "catch"; ERR
      FINALLY
        PRINT "fin"
      END TRY
      PRINT "after"
      """;
    Assert.That(Run(source), Is.EqualTo("catch 5\nfin\nafter\n"));
  }

  [Test]
  public void Execute_GivenTryFinallyOnlyNoError_WhenRun_ThenBodyThenFinally() {
    const string source = """
      TRY
        PRINT "body"
      FINALLY
        PRINT "fin"
      END TRY
      PRINT "after"
      """;
    Assert.That(Run(source), Is.EqualTo("body\nfin\nafter\n"));
  }

  [Test]
  public void Execute_GivenTryCatchOnlyWithError_WhenRun_ThenCatchSwallowsAndContinues() {
    const string source = """
      TRY
        ERROR 7
        PRINT "unreached"
      CATCH
        PRINT "caught"; ERR
      END TRY
      PRINT "after"
      """;
    Assert.That(Run(source), Is.EqualTo("caught 7\nafter\n"));
  }

  [Test]
  public void Execute_GivenNestedTryInnerError_WhenRun_ThenInnerCatchHandlesOuterUntouched() {
    const string source = """
      TRY
        PRINT "outerbody"
        TRY
          ERROR 9
        CATCH
          PRINT "innercatch"; ERR
        FINALLY
          PRINT "innerfin"
        END TRY
        PRINT "outerresume"
      CATCH
        PRINT "outercatch"
      FINALLY
        PRINT "outerfin"
      END TRY
      PRINT "after"
      """;
    Assert.That(Run(source), Is.EqualTo("outerbody\ninnercatch 9\ninnerfin\nouterresume\nouterfin\nafter\n"));
  }

  [Test]
  public void Execute_GivenFinallyHandlingItsOwnError_WhenBodyFaults_ThenOriginalErrReRaised() {
    // the FINALLY body catches an unrelated error (overwriting the runtime error cell); the
    // re-raise after FINALLY must still propagate the ORIGINAL code to the outer trap
    const string source = """
      ON ERROR GOTO Trap
      TRY
        ERROR 11
      FINALLY
        TRY
          ERROR 5
        CATCH
          PRINT "inner"; ERR
        END TRY
        PRINT "fin"
      END TRY
      PRINT "unreached"
      GOTO Done
      Trap:
        PRINT "trap"; ERR
      Done:
      PRINT "done"
      """;
    Assert.That(Run(source), Is.EqualTo("inner 5\nfin\ntrap 11\ndone\n"));
  }

  [Test]
  public void Emit_GivenTryWithFinally_WhenCompiled_ThenFinallyBodyEmittedOnce() {
    // the FINALLY body is shared via jumps between the normal and fault/catch edges,
    // not duplicated per edge; pinned by counting the distinctive constant store
    // MOV word [X], 19229 (C7 06 addr 1D 4B) in the final image
    static int CountFinallyStore(string source) {
      var tokens = Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36);
      var unit = Parser.Parse(tokens, "TEST.BAS", Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model);
      var exe = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      var count = 0;
      for (var i = 0; i + 1 < exe.Length; ++i)
        if (exe[i] == 0x1D && exe[i + 1] == 0x4B)
          ++count;
      return count;
    }

    Assert.Multiple(() => {
      Assert.That(CountFinallyStore("DIM X AS INTEGER\nTRY\n  PRINT \"b\"\nFINALLY\n  X = 19229\nEND TRY\nPRINT X\n"),
        Is.EqualTo(1), "finally-only TRY: one emission shared by the normal and fault edges");
      Assert.That(CountFinallyStore("DIM X AS INTEGER\nTRY\n  PRINT \"b\"\nCATCH\n  PRINT \"c\"\nFINALLY\n  X = 19229\nEND TRY\nPRINT X\n"),
        Is.EqualTo(1), "TRY/CATCH/FINALLY: one emission shared by the normal and caught edges");
    });
  }

  [Test]
  public void Execute_GivenOnErrorHandlerThenTry_WhenErrorAfterEndTry_ThenPriorHandlerRestored() {
    // Arms an ON ERROR handler, runs a clean TRY, then faults after END TRY:
    // the fault must reach the original ON ERROR handler (proving the TRY
    // restored the previous trap on its normal-completion path).
    const string source = """
      ON ERROR GOTO Trap
      TRY
        PRINT "trybody"
      CATCH
        PRINT "trycatch"
      END TRY
      ERROR 13
      PRINT "unreached"
      GOTO Done
      Trap:
        PRINT "trap"; ERR
      Done:
      PRINT "done"
      """;
    Assert.That(Run(source), Is.EqualTo("trybody\ntrap 13\ndone\n"));
  }

  #endregion
}
