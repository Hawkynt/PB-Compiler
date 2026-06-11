using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserCommandTests {

  #region graphics primitives

  [Test]
  public void Parse_GivenPset_WhenParsed_ThenPointAndColorAreCaptured() {
    var stmt = ParseSingle<PsetStmt>("PSET (x, y), c");
    Assert.Multiple(() => {
      Assert.That(stmt.IsPreset, Is.False);
      Assert.That(((NameExpr)stmt.Point.X).Name, Is.EqualTo("x"));
      Assert.That(stmt.Color, Is.Not.Null);
    });
  }

  [Test]
  public void Parse_GivenPresetWithoutColor_WhenParsed_ThenColorIsNull() {
    var stmt = ParseSingle<PsetStmt>("PRESET (1, 2)");
    Assert.Multiple(() => {
      Assert.That(stmt.IsPreset, Is.True);
      Assert.That(stmt.Color, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenFullLineStatement_WhenParsed_ThenBothPointsAreCaptured() {
    var stmt = ParseSingle<LineStmt>("LINE (x1, y1)-(x2, y2), c");
    Assert.Multiple(() => {
      Assert.That(stmt.From, Is.Not.Null);
      Assert.That(stmt.Color, Is.Not.Null);
      Assert.That(stmt.Box, Is.False);
    });
  }

  [Test]
  public void Parse_GivenLineContinuationForm_WhenParsed_ThenFromIsNull()
    => Assert.That(ParseSingle<LineStmt>("LINE -(x2, y2)").From, Is.Null);

  [Test]
  public void Parse_GivenLineBoxFill_WhenParsed_ThenFlagsAreSet() {
    var stmt = ParseSingle<LineStmt>("LINE (0, 0)-(10, 10), c, BF");
    Assert.Multiple(() => {
      Assert.That(stmt.Box, Is.True);
      Assert.That(stmt.Fill, Is.True);
    });
  }

  [Test]
  public void Parse_GivenLineBoxWithStyle_WhenParsed_ThenStyleIsKept() {
    var stmt = ParseSingle<LineStmt>("LINE (0, 0)-(10, 10), , B, &HAAAA");
    Assert.Multiple(() => {
      Assert.That(stmt.Color, Is.Null);
      Assert.That(stmt.Box, Is.True);
      Assert.That(stmt.Fill, Is.False);
      // radix literals read signed (PB 3.1+)
      Assert.That(((IntegerLiteralExpr)stmt.Style!).Value, Is.EqualTo(unchecked((short)0xAAAA)));
    });
  }

  [Test]
  public void Parse_GivenCircleWithOmittedMiddleArguments_WhenParsed_ThenGapsAreNull() {
    var stmt = ParseSingle<CircleStmt>("CIRCLE (x, y), r, , , , aspect");
    Assert.Multiple(() => {
      Assert.That(stmt.Color, Is.Null);
      Assert.That(stmt.Start, Is.Null);
      Assert.That(stmt.End, Is.Null);
      Assert.That(((NameExpr)stmt.Aspect!).Name, Is.EqualTo("aspect"));
    });
  }

  [Test]
  public void Parse_GivenCircleWithColor_WhenParsed_ThenColorIsKept()
    => Assert.That(ParseSingle<CircleStmt>("CIRCLE (160, 100), 50, 14").Color, Is.Not.Null);

  [Test]
  public void Parse_GivenGraphicsGet_WhenParsed_ThenBothPointsAndArrayAreCaptured() {
    var stmt = ParseSingle<GetPutGraphicsStmt>("GET (x1, y1)-(x2, y2), buffer");
    Assert.Multiple(() => {
      Assert.That(stmt.IsGet, Is.True);
      Assert.That(stmt.To, Is.Not.Null);
      Assert.That(((NameExpr)stmt.Array).Name, Is.EqualTo("buffer"));
      Assert.That(stmt.Verb, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenGraphicsPutWithVerb_WhenParsed_ThenVerbIsKept() {
    var stmt = ParseSingle<GetPutGraphicsStmt>("PUT (x, y), sprite, XOR");
    Assert.Multiple(() => {
      Assert.That(stmt.IsGet, Is.False);
      Assert.That(stmt.To, Is.Null);
      Assert.That(stmt.Verb, Is.EqualTo("XOR"));
    });
  }

  #endregion

  #region generic commands

  [Test]
  public void Parse_GivenBeep_WhenParsed_ThenNoArguments() {
    var stmt = ParseSingle<CommandStmt>("BEEP");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("BEEP"));
      Assert.That(stmt.Arguments, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenLocateWithOmittedFirstArgument_WhenParsed_ThenGapIsNull() {
    var stmt = ParseSingle<CommandStmt>("LOCATE , 5");
    Assert.Multiple(() => {
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
      Assert.That(stmt.Arguments[0], Is.Null);
      Assert.That(((IntegerLiteralExpr)stmt.Arguments[1]!).Value, Is.EqualTo(5));
    });
  }

  [Test]
  public void Parse_GivenSoundWithTwoArguments_WhenParsed_ThenBothAreCaptured()
    => Assert.That(ParseSingle<CommandStmt>("SOUND 440, 18").Arguments, Has.Count.EqualTo(2));

  [Test]
  public void Parse_GivenPokeWithExpressions_WhenParsed_ThenArgumentsAreExpressions() {
    var stmt = ParseSingle<CommandStmt>("POKE Y * ctx.BytesPerLine + X, colorVal");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("POKE"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
      Assert.That(stmt.Arguments[0], Is.InstanceOf<BinaryExpr>());
    });
  }

  [Test]
  public void Parse_GivenOutWithParenthesizedPort_WhenParsed_ThenItIsNotAPoint() {
    var stmt = ParseSingle<CommandStmt>("OUT (&H3D4 + 1), v");
    Assert.Multiple(() => {
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
      Assert.That(stmt.Arguments[0], Is.InstanceOf<BinaryExpr>());
    });
  }

  [Test]
  public void Parse_GivenRegStatement_WhenParsed_ThenItIsACommand()
    => Assert.That(ParseSingle<CommandStmt>("REG 1, &H4F05").Arguments, Has.Count.EqualTo(2));

  [Test]
  public void Parse_GivenWaitWithThreeArguments_WhenParsed_ThenAllAreCaptured()
    => Assert.That(ParseSingle<CommandStmt>("WAIT &H3DA, 8, 8").Arguments, Has.Count.EqualTo(3));

  [Test]
  public void Parse_GivenScreenCommand_WhenParsed_ThenArgumentIsKept()
    => Assert.That(ParseSingle<CommandStmt>("SCREEN 13").Arguments, Has.Count.EqualTo(1));

  [Test]
  public void Parse_GivenColorWithTwoArguments_WhenParsed_ThenBothAreKept()
    => Assert.That(ParseSingle<CommandStmt>("COLOR 7, 0").Arguments, Has.Count.EqualTo(2));

  [Test]
  public void Parse_GivenRandomizeWithoutArgument_WhenParsed_ThenArgumentsAreEmpty()
    => Assert.That(ParseSingle<CommandStmt>("RANDOMIZE").Arguments, Is.Empty);

  [Test]
  public void Parse_GivenNameAsForm_WhenParsed_ThenBothNamesAreArguments() {
    var stmt = ParseSingle<CommandStmt>("NAME a$ AS b$");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("NAME"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenViewWithBoxCoordinates_WhenParsed_ThenPointsAreFlattened() {
    var stmt = ParseSingle<CommandStmt>("VIEW (10, 10)-(300, 180), 1, 2");
    Assert.That(stmt.Arguments, Has.Count.EqualTo(6));
  }

  [Test]
  public void Parse_GivenViewScreenForm_WhenParsed_ThenKeywordCarriesScreen()
    => Assert.That(ParseSingle<CommandStmt>("VIEW SCREEN (0, 0)-(100, 100)").Keyword, Is.EqualTo("VIEW SCREEN"));

  [Test]
  public void Parse_GivenPaletteUsing_WhenParsed_ThenKeywordCarriesUsing() {
    var stmt = ParseSingle<CommandStmt>("PALETTE USING pal(0)");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("PALETTE USING"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenShellWithCommand_WhenParsed_ThenArgumentIsKept()
    => Assert.That(ParseSingle<CommandStmt>("SHELL \"DIR\"").Arguments, Has.Count.EqualTo(1));

  [Test]
  public void Parse_GivenDelayCommand_WhenParsed_ThenArgumentIsKept()
    => Assert.That(ParseSingle<CommandStmt>("DELAY 1").Arguments, Has.Count.EqualTo(1));

  [Test]
  public void Parse_GivenShiftLeft_WhenParsed_ThenDirectionIsInTheKeyword() {
    var stmt = ParseSingle<CommandStmt>("SHIFT LEFT planeMask, pl");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("SHIFT LEFT"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
      Assert.That(((NameExpr)stmt.Arguments[0]!).Name, Is.EqualTo("planeMask"));
    });
  }

  [Test]
  public void Parse_GivenRotateRight_WhenParsed_ThenDirectionIsInTheKeyword()
    => Assert.That(ParseSingle<CommandStmt>("ROTATE RIGHT b, 1").Keyword, Is.EqualTo("ROTATE RIGHT"));

  #endregion

  #region keyword/identifier collisions

  [Test]
  public void Parse_GivenVariableNamedLikeCommand_WhenAssigned_ThenItIsAnAssignment()
    => Assert.That(ParseSingle("Width = 5"), Is.InstanceOf<AssignStmt>());

  [Test]
  public void Parse_GivenArrayNamedLikeCommand_WhenAssigned_ThenItIsAnAssignment()
    => Assert.That(ParseSingle("Sound(1) = 2"), Is.InstanceOf<AssignStmt>());

  [Test]
  public void Parse_GivenUnknownKeywordStatement_WhenParsed_ThenItFallsBackToCall() {
    var stmt = ParseSingle<CallStmt>("Test_BeginSuite \"CURSOR\"");
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("Test_BeginSuite"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(1));
      Assert.That(stmt.UsedCallKeyword, Is.False);
    });
  }

  #endregion
}
