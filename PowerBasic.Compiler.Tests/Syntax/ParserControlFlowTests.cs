using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserControlFlowTests {

  #region IF

  [Test]
  public void Parse_GivenSingleLineIf_WhenParsed_ThenThenBranchHoldsTheStatement() {
    var stmt = ParseSingle<IfStmt>("IF x > 0 THEN y = 1");
    Assert.Multiple(() => {
      Assert.That(stmt.Then, Has.Count.EqualTo(1));
      Assert.That(stmt.ElseIfs, Is.Empty);
      Assert.That(stmt.Else, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenSingleLineIfElse_WhenParsed_ThenBothBranchesAreFilled() {
    var stmt = ParseSingle<IfStmt>("IF a THEN x = 1 ELSE x = 2");
    Assert.Multiple(() => {
      Assert.That(stmt.Then, Has.Count.EqualTo(1));
      Assert.That(stmt.Else, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenSingleLineIfWithColonSeparatedStatements_WhenParsed_ThenAllLandInThen() {
    var stmt = ParseSingle<IfStmt>("IF a THEN x = 1 : y = 2 : z = 3");
    Assert.That(stmt.Then, Has.Count.EqualTo(3));
  }

  [Test]
  public void Parse_GivenSingleLineIfWithColonsAndElse_WhenParsed_ThenElseTakesTheRest() {
    var stmt = ParseSingle<IfStmt>("IF a THEN x = 1 : y = 2 ELSE z = 3");
    Assert.Multiple(() => {
      Assert.That(stmt.Then, Has.Count.EqualTo(2));
      Assert.That(stmt.Else, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenIfGotoForm_WhenParsed_ThenThenBranchIsAGoto() {
    var stmt = ParseSingle<IfStmt>("IF err THEN GOTO FileOpenError");
    Assert.That(((GotoStmt)stmt.Then[0]).Target, Is.EqualTo("FileOpenError"));
  }

  [Test]
  public void Parse_GivenIfGotoWithoutThen_WhenParsed_ThenThenBranchIsAGoto() {
    var stmt = ParseSingle<IfStmt>("IF err GOTO CleanUp");
    Assert.That(((GotoStmt)stmt.Then[0]).Target, Is.EqualTo("CleanUp"));
  }

  [Test]
  public void Parse_GivenThenLineNumber_WhenParsed_ThenItBecomesAGoto() {
    var stmt = ParseSingle<IfStmt>("IF x THEN 100 ELSE 200");
    Assert.Multiple(() => {
      Assert.That(((GotoStmt)stmt.Then[0]).Target, Is.EqualTo("100"));
      Assert.That(((GotoStmt)stmt.Else![0]).Target, Is.EqualTo("200"));
    });
  }

  [Test]
  public void Parse_GivenBlockIf_WhenParsed_ThenBodyAndElseAreCaptured() {
    var stmt = ParseSingle<IfStmt>("""
      IF ok <> 0 THEN
          PRINT "yes"
      ELSE
          PRINT "no"
      END IF
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Then, Has.Count.EqualTo(1));
      Assert.That(stmt.Else, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenBlockIfWithElseIfChain_WhenParsed_ThenAllArmsAreCaptured() {
    var stmt = ParseSingle<IfStmt>("""
      IF a THEN
          x = 1
      ELSEIF b THEN
          x = 2
      ELSEIF c THEN
          x = 3
      ELSE
          x = 4
      END IF
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.ElseIfs, Has.Count.EqualTo(2));
      Assert.That(stmt.Else, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenEmptyBlockIf_WhenParsed_ThenThenIsEmpty() {
    var stmt = ParseSingle<IfStmt>("""
      IF a THEN
      END IF
      """);
    Assert.That(stmt.Then, Is.Empty);
  }

  [Test]
  public void Parse_GivenNestedBlockIf_WhenParsed_ThenInnerIfIsInsideOuterThen() {
    var stmt = ParseSingle<IfStmt>("""
      IF a THEN
          IF b THEN
              x = 1
          END IF
      END IF
      """);
    Assert.That(stmt.Then[0], Is.InstanceOf<IfStmt>());
  }

  [Test]
  public void Parse_GivenParenthesizedIfCondition_WhenParsed_ThenItIsNotMistakenForAssignment() {
    var stmt = ParseSingle<IfStmt>("IF (x) = 5 THEN y = 1");
    Assert.That(((BinaryExpr)stmt.Condition).Op, Is.EqualTo(BinaryOp.Equal));
  }

  [Test]
  public void Parse_GivenEndProgramInsideIfBlock_WhenParsed_ThenEndStmtDoesNotCloseTheBlock() {
    var stmt = ParseSingle<IfStmt>("""
      IF a THEN
          END
      END IF
      """);
    Assert.That(stmt.Then[0], Is.InstanceOf<EndStmt>());
  }

  #endregion

  #region SELECT CASE

  [Test]
  public void Parse_GivenSelectWithValueListRangeAndIs_WhenParsed_ThenSelectorsAreTyped() {
    var stmt = ParseSingle<SelectStmt>("""
      SELECT CASE n
          CASE 1, 2
              x = 1
          CASE 5 TO 9
              x = 2
          CASE IS > 100
              x = 3
          CASE ELSE
              x = 4
      END SELECT
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Arms, Has.Count.EqualTo(4));
      Assert.That(stmt.Arms[0].Selectors, Has.Count.EqualTo(2));
      Assert.That(stmt.Arms[1].Selectors[0].RangeUpper, Is.Not.Null);
      Assert.That(stmt.Arms[2].Selectors[0].IsComparison, Is.EqualTo(CaseComparison.Greater));
      Assert.That(stmt.Arms[3].Selectors, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenCaseElseWithColonBody_WhenParsed_ThenBodyIsOnTheSameLine() {
    var stmt = ParseSingle<SelectStmt>("""
      SELECT CASE st
          CASE 1: x = 1
          CASE ELSE : x = 2
      END SELECT
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Arms[0].Body, Has.Count.EqualTo(1));
      Assert.That(stmt.Arms[1].Body, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenStringAndEquateSelectors_WhenParsed_ThenValuesAreExpressions() {
    var stmt = ParseSingle<SelectStmt>("""
      SELECT CASE k$
          CASE "a", %SOME_KEY
      END SELECT
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Arms[0].Selectors[0].Value, Is.InstanceOf<StringLiteralExpr>());
      Assert.That(stmt.Arms[0].Selectors[1].Value, Is.InstanceOf<NamedConstantExpr>());
    });
  }

  [Test]
  public void Parse_GivenNestedSelect_WhenParsed_ThenInnerSelectIsInsideArm() {
    var stmt = ParseSingle<SelectStmt>("""
      SELECT CASE a
          CASE 1
              SELECT CASE b
                  CASE 2
                      x = 1
              END SELECT
      END SELECT
      """);
    Assert.That(stmt.Arms[0].Body[0], Is.InstanceOf<SelectStmt>());
  }

  #endregion

  #region FOR / NEXT

  [Test]
  public void Parse_GivenSimpleForLoop_WhenParsed_ThenBoundsAndBodyAreCaptured() {
    var stmt = ParseSingle<ForStmt>("""
      FOR i = 1 TO 10
          x = x + i
      NEXT i
      """);
    Assert.Multiple(() => {
      Assert.That(((NameExpr)stmt.Variable).Name, Is.EqualTo("i"));
      Assert.That(((IntegerLiteralExpr)stmt.To).Value, Is.EqualTo(10));
      Assert.That(stmt.Step, Is.Null);
      Assert.That(stmt.Body, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenForWithStep_WhenParsed_ThenStepIsKept() {
    var stmt = ParseSingle<ForStmt>("""
      FOR i = 10 TO 0 STEP -2
      NEXT
      """);
    Assert.That(((UnaryExpr)stmt.Step!).Op, Is.EqualTo(UnaryOp.Negate));
  }

  [Test]
  public void Parse_GivenEmptyForBody_WhenParsed_ThenBodyIsEmpty()
    => Assert.That(ParseSingle<ForStmt>("FOR i = 1 TO 3\nNEXT i").Body, Is.Empty);

  [Test]
  public void Parse_GivenNextWithMultipleVariables_WhenParsed_ThenItClosesMultipleFors() {
    var stmt = ParseSingle<ForStmt>("""
      FOR i = 1 TO 3
          FOR j = 1 TO 3
              x = i * j
      NEXT j, i
      """);
    var inner = (ForStmt)stmt.Body[0];
    Assert.Multiple(() => {
      Assert.That(((NameExpr)stmt.Variable).Name, Is.EqualTo("i"));
      Assert.That(((NameExpr)inner.Variable).Name, Is.EqualTo("j"));
      Assert.That(inner.Body, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenNextWithoutFor_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => Parse("NEXT i"));

  [Test]
  public void Parse_GivenNextClosingMoreForsThanOpen_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => Parse("FOR i = 1 TO 3\nNEXT i, j"));

  #endregion

  #region DO / LOOP / WHILE

  [Test]
  public void Parse_GivenDoWhilePreTest_WhenParsed_ThenPreConditionIsSet() {
    var stmt = ParseSingle<DoLoopStmt>("""
      DO WHILE x < 10
          INCR x
      LOOP
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.PreTest, Is.EqualTo(LoopTestKind.While));
      Assert.That(stmt.PostTest, Is.EqualTo(LoopTestKind.None));
      Assert.That(stmt.Body, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenDoLoopUntilPostTest_WhenParsed_ThenPostConditionIsSet() {
    var stmt = ParseSingle<DoLoopStmt>("""
      DO
          INCR x
      LOOP UNTIL x >= 10
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.PreTest, Is.EqualTo(LoopTestKind.None));
      Assert.That(stmt.PostTest, Is.EqualTo(LoopTestKind.Until));
    });
  }

  [Test]
  public void Parse_GivenPlainDoLoop_WhenParsed_ThenNoConditionsAreSet() {
    var stmt = ParseSingle<DoLoopStmt>("DO\nLOOP");
    Assert.Multiple(() => {
      Assert.That(stmt.PreTest, Is.EqualTo(LoopTestKind.None));
      Assert.That(stmt.PostTest, Is.EqualTo(LoopTestKind.None));
      Assert.That(stmt.Body, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenWhileWend_WhenParsed_ThenItMapsToPreTestedDoLoop() {
    var stmt = ParseSingle<DoLoopStmt>("""
      WHILE x < 0
          x = x + w
      WEND
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.PreTest, Is.EqualTo(LoopTestKind.While));
      Assert.That(stmt.Body, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenLoopWithoutDo_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => Parse("LOOP"));

  #endregion

  #region EXIT

  [TestCase("EXIT FOR", ExitKind.For)]
  [TestCase("EXIT DO", ExitKind.Do)]
  [TestCase("EXIT LOOP", ExitKind.Loop)]
  [TestCase("EXIT SUB", ExitKind.Sub)]
  [TestCase("EXIT FUNCTION", ExitKind.Function)]
  [TestCase("EXIT DEF", ExitKind.Def)]
  [TestCase("EXIT SELECT", ExitKind.Select)]
  [TestCase("EXIT IF", ExitKind.If)]
  public void Parse_GivenExitForm_WhenParsed_ThenKindMatches(string source, ExitKind expected)
    => Assert.That(ParseSingle<ExitStmt>(source).Kind, Is.EqualTo(expected));

  [Test]
  public void Parse_GivenExitInsideNestedLoops_WhenParsed_ThenItStaysInTheInnerBody() {
    var stmt = ParseSingle<ForStmt>("""
      FOR i = 1 TO 3
          DO
              IF x THEN EXIT DO
          LOOP
      NEXT i
      """);
    var doLoop = (DoLoopStmt)stmt.Body[0];
    var innerIf = (IfStmt)doLoop.Body[0];
    Assert.That(((ExitStmt)innerIf.Then[0]).Kind, Is.EqualTo(ExitKind.Do));
  }

  #endregion

  #region GOTO / GOSUB / ON / RESUME / ERROR

  [Test]
  public void Parse_GivenGotoNumericTarget_WhenParsed_ThenTargetIsTheNumber()
    => Assert.That(ParseSingle<GotoStmt>("GOTO 100").Target, Is.EqualTo("100"));

  [Test]
  public void Parse_GivenGosub_WhenParsed_ThenTargetIsKept()
    => Assert.That(ParseSingle<GosubStmt>("GOSUB DrawIt").Target, Is.EqualTo("DrawIt"));

  [Test]
  public void Parse_GivenBareReturn_WhenParsed_ThenTargetIsNull()
    => Assert.That(ParseSingle<ReturnStmt>("RETURN").Target, Is.Null);

  [Test]
  public void Parse_GivenReturnWithLabel_WhenParsed_ThenTargetIsKept()
    => Assert.That(ParseSingle<ReturnStmt>("RETURN Done").Target, Is.EqualTo("Done"));

  [Test]
  public void Parse_GivenOnGoto_WhenParsed_ThenAllTargetsAreCaptured() {
    var stmt = ParseSingle<OnGotoStmt>("ON n GOTO First, Second, 300");
    Assert.Multiple(() => {
      Assert.That(stmt.IsGosub, Is.False);
      Assert.That(stmt.Targets, Is.EqualTo(new[] { "First", "Second", "300" }));
    });
  }

  [Test]
  public void Parse_GivenOnGosub_WhenParsed_ThenGosubFlagIsSet()
    => Assert.That(ParseSingle<OnGotoStmt>("ON n GOSUB A, B").IsGosub, Is.True);

  [Test]
  public void Parse_GivenOnErrorGotoLabel_WhenParsed_ThenTargetIsKept() {
    var stmt = ParseSingle<OnErrorStmt>("ON ERROR GOTO FileOpenError");
    Assert.Multiple(() => {
      Assert.That(stmt.Target, Is.EqualTo("FileOpenError"));
      Assert.That(stmt.ResumeNext, Is.False);
    });
  }

  [Test]
  public void Parse_GivenOnErrorGotoZero_WhenParsed_ThenTargetIsNull()
    => Assert.That(ParseSingle<OnErrorStmt>("ON ERROR GOTO 0").Target, Is.Null);

  [Test]
  public void Parse_GivenOnErrorResumeNext_WhenParsed_ThenResumeNextFlagIsSet()
    => Assert.That(ParseSingle<OnErrorStmt>("ON ERROR RESUME NEXT").ResumeNext, Is.True);

  [TestCase("RESUME", ResumeKind.SameStatement, null)]
  [TestCase("RESUME 0", ResumeKind.SameStatement, null)]
  [TestCase("RESUME NEXT", ResumeKind.Next, null)]
  [TestCase("RESUME Retry", ResumeKind.Label, "Retry")]
  public void Parse_GivenResumeForm_WhenParsed_ThenKindAndTargetMatch(string source, ResumeKind kind, string? target) {
    var stmt = ParseSingle<ResumeStmt>(source);
    Assert.Multiple(() => {
      Assert.That(stmt.Kind, Is.EqualTo(kind));
      Assert.That(stmt.Target, Is.EqualTo(target));
    });
  }

  [Test]
  public void Parse_GivenErrorStatement_WhenParsed_ThenCodeIsKept()
    => Assert.That(((IntegerLiteralExpr)ParseSingle<ErrorStmt>("ERROR 53").Code).Value, Is.EqualTo(53));

  #endregion

  #region events

  [Test]
  public void Parse_GivenOnTimerGosub_WhenParsed_ThenEventIsRegistered() {
    var stmt = ParseSingle<OnEventStmt>("ON TIMER(2) GOSUB Tick");
    Assert.Multiple(() => {
      Assert.That(stmt.EventKind, Is.EqualTo("TIMER"));
      Assert.That(((IntegerLiteralExpr)stmt.Index!).Value, Is.EqualTo(2));
      Assert.That(stmt.Target, Is.EqualTo("Tick"));
    });
  }

  [Test]
  public void Parse_GivenOnKeyGosub_WhenParsed_ThenEventKindIsKey()
    => Assert.That(ParseSingle<OnEventStmt>("ON KEY(1) GOSUB HandleKey").EventKind, Is.EqualTo("KEY"));

  [Test]
  public void Parse_GivenKeyIndexOn_WhenParsed_ThenEventControlIsProduced() {
    var stmt = ParseSingle<EventControlStmt>("KEY(1) ON");
    Assert.Multiple(() => {
      Assert.That(stmt.EventKind, Is.EqualTo("KEY"));
      Assert.That(((IntegerLiteralExpr)stmt.Index!).Value, Is.EqualTo(1));
      Assert.That(stmt.Mode, Is.EqualTo("ON"));
    });
  }

  [Test]
  public void Parse_GivenTimerOff_WhenParsed_ThenIndexIsNull() {
    var stmt = ParseSingle<EventControlStmt>("TIMER OFF");
    Assert.Multiple(() => {
      Assert.That(stmt.Index, Is.Null);
      Assert.That(stmt.Mode, Is.EqualTo("OFF"));
    });
  }

  [Test]
  public void Parse_GivenKeyAssignmentForm_WhenParsed_ThenItIsACommand() {
    var stmt = ParseSingle<CommandStmt>("KEY 1, \"HELP\"");
    Assert.That(stmt.Keyword, Is.EqualTo("KEY"));
  }

  [Test]
  public void Parse_GivenKeyArrayAssignment_WhenParsed_ThenItIsAnAssignment()
    => Assert.That(ParseSingle("Key(1) = 2"), Is.InstanceOf<AssignStmt>());

  #endregion

  #region END / STOP / SYSTEM

  [Test]
  public void Parse_GivenBareEnd_WhenParsed_ThenExitCodeIsNull()
    => Assert.That(ParseSingle<EndStmt>("END").ExitCode, Is.Null);

  [Test]
  public void Parse_GivenEndWithExitCode_WhenParsed_ThenCodeIsKept()
    => Assert.That(((IntegerLiteralExpr)ParseSingle<EndStmt>("END 1").ExitCode!).Value, Is.EqualTo(1));

  [TestCase("STOP")]
  [TestCase("SYSTEM")]
  public void Parse_GivenProgramTermination_WhenParsed_ThenEndStmtIsProduced(string source)
    => Assert.That(ParseSingle(source), Is.InstanceOf<EndStmt>());

  [Test]
  public void Parse_GivenStrayEndIf_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => Parse("END IF"));

  [Test]
  public void Parse_GivenUnclosedSub_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => Parse("SUB Foo\nx = 1"));

  #endregion
}
