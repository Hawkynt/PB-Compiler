using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;
using FileMode = PowerBasic.Compiler.Syntax.Ast.FileMode;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserIoTests {

  #region PRINT

  [Test]
  public void Parse_GivenBarePrint_WhenParsed_ThenItemListIsEmpty() {
    var stmt = ParseSingle<PrintStmt>("PRINT");
    Assert.Multiple(() => {
      Assert.That(stmt.Items, Is.Empty);
      Assert.That(stmt.FileNumber, Is.Null);
      Assert.That(stmt.IsLPrint, Is.False);
    });
  }

  [Test]
  public void Parse_GivenPrintWithSeparators_WhenParsed_ThenSeparatorsAreKept() {
    var stmt = ParseSingle<PrintStmt>("PRINT a; b, c");
    Assert.Multiple(() => {
      Assert.That(stmt.Items, Has.Count.EqualTo(3));
      Assert.That(stmt.Items[0].Separator, Is.EqualTo(PrintSeparator.Semicolon));
      Assert.That(stmt.Items[1].Separator, Is.EqualTo(PrintSeparator.Comma));
      Assert.That(stmt.Items[2].Separator, Is.EqualTo(PrintSeparator.Newline));
    });
  }

  [Test]
  public void Parse_GivenPrintWithTrailingSemicolon_WhenParsed_ThenNoNewlineItemIsEmitted() {
    var stmt = ParseSingle<PrintStmt>("PRINT CHR$(c);");
    Assert.Multiple(() => {
      Assert.That(stmt.Items, Has.Count.EqualTo(1));
      Assert.That(stmt.Items[0].Separator, Is.EqualTo(PrintSeparator.Semicolon));
    });
  }

  [Test]
  public void Parse_GivenPrintToFile_WhenParsed_ThenFileNumberIsCaptured() {
    var stmt = ParseSingle<PrintStmt>("PRINT #9, \"[SUITE] \"; suiteName");
    var fileNumber = (FileNumberExpr)stmt.FileNumber!;
    Assert.Multiple(() => {
      Assert.That(((IntegerLiteralExpr)fileNumber.Number).Value, Is.EqualTo(9));
      Assert.That(stmt.Items, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenPrintUsing_WhenParsed_ThenFormatIsCaptured() {
    var stmt = ParseSingle<PrintStmt>("PRINT USING \"##.##\"; v");
    Assert.Multiple(() => {
      Assert.That(stmt.UsingFormat, Is.InstanceOf<StringLiteralExpr>());
      Assert.That(stmt.Items, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenQuestionMarkShorthand_WhenParsed_ThenItIsAPrint()
    => Assert.That(ParseSingle<PrintStmt>("? x").Items, Has.Count.EqualTo(1));

  [Test]
  public void Parse_GivenLPrint_WhenParsed_ThenLPrintFlagIsSet()
    => Assert.That(ParseSingle<PrintStmt>("LPRINT a$").IsLPrint, Is.True);

  [Test]
  public void Parse_GivenSpcAndTabItems_WhenParsed_ThenTheyAreCallExpressions() {
    var stmt = ParseSingle<PrintStmt>("PRINT SPC(5); TAB(20); x");
    Assert.Multiple(() => {
      Assert.That(((CallOrIndexExpr)stmt.Items[0].Value!).Name, Is.EqualTo("SPC"));
      Assert.That(((CallOrIndexExpr)stmt.Items[1].Value!).Name, Is.EqualTo("TAB"));
    });
  }

  [Test]
  public void Parse_GivenPrintWithLeadingComma_WhenParsed_ThenEmptyItemIsKept() {
    var stmt = ParseSingle<PrintStmt>("PRINT , x");
    Assert.Multiple(() => {
      Assert.That(stmt.Items[0].Value, Is.Null);
      Assert.That(stmt.Items[0].Separator, Is.EqualTo(PrintSeparator.Comma));
    });
  }

  #endregion

  #region INPUT

  [Test]
  public void Parse_GivenInputWithPrompt_WhenParsed_ThenPromptAndTargetsAreCaptured() {
    var stmt = ParseSingle<InputStmt>("INPUT \"Name\"; n$");
    Assert.Multiple(() => {
      Assert.That(stmt.Prompt, Is.EqualTo("Name"));
      Assert.That(stmt.PromptSemicolon, Is.True);
      Assert.That(stmt.Targets, Has.Count.EqualTo(1));
      Assert.That(stmt.IsLineInput, Is.False);
    });
  }

  [Test]
  public void Parse_GivenInputWithCommaPrompt_WhenParsed_ThenPromptSemicolonIsFalse()
    => Assert.That(ParseSingle<InputStmt>("INPUT \"Name\", n$").PromptSemicolon, Is.False);

  [Test]
  public void Parse_GivenInputFromFile_WhenParsed_ThenFileNumberIsCaptured() {
    var stmt = ParseSingle<InputStmt>("INPUT #FileHandle, InputLine");
    Assert.Multiple(() => {
      Assert.That(stmt.FileNumber, Is.InstanceOf<FileNumberExpr>());
      Assert.That(stmt.Targets, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenInputWithMultipleTargets_WhenParsed_ThenAllAreCaptured()
    => Assert.That(ParseSingle<InputStmt>("INPUT a, b$, c(1)").Targets, Has.Count.EqualTo(3));

  [Test]
  public void Parse_GivenLineInput_WhenParsed_ThenLineFlagIsSet() {
    var stmt = ParseSingle<InputStmt>("LINE INPUT \"path? \"; p$");
    Assert.Multiple(() => {
      Assert.That(stmt.IsLineInput, Is.True);
      Assert.That(stmt.Prompt, Is.EqualTo("path? "));
    });
  }

  [Test]
  public void Parse_GivenLineInputFromFile_WhenParsed_ThenFileNumberIsCaptured() {
    var stmt = ParseSingle<InputStmt>("LINE INPUT #1, l$");
    Assert.Multiple(() => {
      Assert.That(stmt.IsLineInput, Is.True);
      Assert.That(stmt.FileNumber, Is.InstanceOf<FileNumberExpr>());
    });
  }

  #endregion

  #region OPEN / CLOSE

  [Test]
  public void Parse_GivenModernOpen_WhenParsed_ThenAllPartsAreCaptured() {
    var stmt = ParseSingle<OpenStmt>("OPEN fileName FOR BINARY ACCESS READ AS #fileHandle");
    Assert.Multiple(() => {
      Assert.That(stmt.Mode, Is.EqualTo(FileMode.Binary));
      Assert.That(stmt.Access, Is.EqualTo("READ"));
      Assert.That(stmt.Lock, Is.Null);
      Assert.That(stmt.FileNumber, Is.InstanceOf<FileNumberExpr>());
      Assert.That(stmt.RecordLength, Is.Null);
    });
  }

  [TestCase("OPEN f$ FOR INPUT AS #1", FileMode.Input)]
  [TestCase("OPEN f$ FOR OUTPUT AS #1", FileMode.Output)]
  [TestCase("OPEN f$ FOR APPEND AS #1", FileMode.Append)]
  [TestCase("OPEN f$ FOR RANDOM AS #1", FileMode.Random)]
  [TestCase("OPEN f$ FOR BINARY AS #1", FileMode.Binary)]
  public void Parse_GivenOpenMode_WhenParsed_ThenModeMatches(string source, FileMode expected)
    => Assert.That(ParseSingle<OpenStmt>(source).Mode, Is.EqualTo(expected));

  [Test]
  public void Parse_GivenOpenWithLockAndLen_WhenParsed_ThenBothAreCaptured() {
    var stmt = ParseSingle<OpenStmt>("OPEN f$ FOR RANDOM LOCK SHARED AS #1 LEN = 128");
    Assert.Multiple(() => {
      Assert.That(stmt.Lock, Is.EqualTo("SHARED"));
      Assert.That(((IntegerLiteralExpr)stmt.RecordLength!).Value, Is.EqualTo(128));
    });
  }

  [Test]
  public void Parse_GivenOpenWithoutHash_WhenParsed_ThenFileNumberIsPlainExpression() {
    var stmt = ParseSingle<OpenStmt>("OPEN s$ FOR BINARY ACCESS READ AS fh");
    Assert.That(stmt.FileNumber, Is.InstanceOf<NameExpr>());
  }

  [Test]
  public void Parse_GivenLegacyOpen_WhenParsed_ThenModeLetterIsMapped() {
    var stmt = ParseSingle<OpenStmt>("OPEN \"I\", #FileHandle, fontFile");
    Assert.Multiple(() => {
      Assert.That(stmt.Mode, Is.EqualTo(FileMode.Input));
      Assert.That(stmt.FileNumber, Is.InstanceOf<FileNumberExpr>());
      Assert.That(((NameExpr)stmt.FileName).Name, Is.EqualTo("fontFile"));
    });
  }

  [Test]
  public void Parse_GivenLegacyOpenWithRecordLength_WhenParsed_ThenLengthIsKept() {
    var stmt = ParseSingle<OpenStmt>("OPEN \"R\", 1, f$, 64");
    Assert.Multiple(() => {
      Assert.That(stmt.Mode, Is.EqualTo(FileMode.Random));
      Assert.That(((IntegerLiteralExpr)stmt.RecordLength!).Value, Is.EqualTo(64));
    });
  }

  [Test]
  public void Parse_GivenBareClose_WhenParsed_ThenFileListIsEmpty()
    => Assert.That(ParseSingle<CloseStmt>("CLOSE").FileNumbers, Is.Empty);

  [Test]
  public void Parse_GivenCloseWithList_WhenParsed_ThenAllNumbersAreCaptured()
    => Assert.That(ParseSingle<CloseStmt>("CLOSE #1, 2, fh").FileNumbers, Has.Count.EqualTo(3));

  #endregion

  #region GET / PUT / SEEK / FIELD

  [Test]
  public void Parse_GivenFileGetWithOmittedRecord_WhenParsed_ThenRecordIsNull() {
    var stmt = ParseSingle<GetPutFileStmt>("GET fileHandle, , dirEntry.Count");
    Assert.Multiple(() => {
      Assert.That(stmt.IsGet, Is.True);
      Assert.That(stmt.RecordNumber, Is.Null);
      Assert.That(stmt.Variable, Is.InstanceOf<MemberExpr>());
    });
  }

  [Test]
  public void Parse_GivenFilePutWithRecord_WhenParsed_ThenRecordIsKept() {
    var stmt = ParseSingle<GetPutFileStmt>("PUT #1, 5, buffer$");
    Assert.Multiple(() => {
      Assert.That(stmt.IsGet, Is.False);
      Assert.That(((IntegerLiteralExpr)stmt.RecordNumber!).Value, Is.EqualTo(5));
    });
  }

  [Test]
  public void Parse_GivenFileGetWithOnlyFileNumber_WhenParsed_ThenRestIsNull() {
    var stmt = ParseSingle<GetPutFileStmt>("GET #1");
    Assert.Multiple(() => {
      Assert.That(stmt.RecordNumber, Is.Null);
      Assert.That(stmt.Variable, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenSeek_WhenParsed_ThenPositionExpressionIsKept() {
    var stmt = ParseSingle<SeekStmt>("SEEK FileHandle, FilePos + 12");
    Assert.That(stmt.Target, Is.InstanceOf<BinaryExpr>());
  }

  [Test]
  public void Parse_GivenField_WhenParsed_ThenAllFieldsAreCaptured() {
    var stmt = ParseSingle<FieldStmt>("FIELD #1, 20 AS name$, 4 AS score$");
    Assert.Multiple(() => {
      Assert.That(stmt.Fields, Has.Count.EqualTo(2));
      Assert.That(((IntegerLiteralExpr)stmt.Fields[0].Width).Value, Is.EqualTo(20));
    });
  }

  #endregion

  #region DATA / READ / RESTORE

  [Test]
  public void Parse_GivenDataWithMixedItems_WhenParsed_ThenItemsAreTrimmedStrings() {
    var stmt = ParseSingle<DataStmt>("DATA raw, 42, \"quoted, comma\"");
    Assert.That(stmt.Items, Is.EqualTo(new[] { "raw", "42", "quoted, comma" }));
  }

  [Test]
  public void Parse_GivenDataWithNegativeNumber_WhenParsed_ThenSignSticksToTheNumber()
    => Assert.That(ParseSingle<DataStmt>("DATA -5, 1.5").Items, Is.EqualTo(new[] { "-5", "1.5" }));

  [Test]
  public void Parse_GivenDataWithUnquotedWords_WhenParsed_ThenWordsKeepSpacing()
    => Assert.That(ParseSingle<DataStmt>("DATA hello world, x").Items, Is.EqualTo(new[] { "hello world", "x" }));

  [Test]
  public void Parse_GivenDataWithTrailingComma_WhenParsed_ThenEmptyItemIsKept()
    => Assert.That(ParseSingle<DataStmt>("DATA 1,").Items, Is.EqualTo(new[] { "1", "" }));

  [Test]
  public void Parse_GivenRead_WhenParsed_ThenTargetsAreCaptured()
    => Assert.That(ParseSingle<ReadStmt>("READ a, b$, arr(i)").Targets, Has.Count.EqualTo(3));

  [Test]
  public void Parse_GivenBareRestore_WhenParsed_ThenTargetIsNull()
    => Assert.That(ParseSingle<RestoreStmt>("RESTORE").Target, Is.Null);

  [Test]
  public void Parse_GivenRestoreWithLabel_WhenParsed_ThenTargetIsKept()
    => Assert.That(ParseSingle<RestoreStmt>("RESTORE Sprites").Target, Is.EqualTo("Sprites"));

  #endregion
}
