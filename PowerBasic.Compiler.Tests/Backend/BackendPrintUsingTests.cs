using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>PRINT USING</c> and <c>LPRINT</c> through the retargetable path, executed and read.
///
/// <para>
/// Every case here asserts TWICE: that the routed image behaves exactly as the direct emitter's
/// does, and separately that the text is the text PowerBASIC's formatter produces. The first check
/// alone would pass for two back ends sharing one misunderstanding of the format - which is a real
/// risk here, because they now share the format PARSER
/// (<see cref="PowerBasic.Compiler.Runtime.UsingFormat"/>) - and the second alone would not notice
/// the routed path taking a different road to the same string on one input and a different string
/// on the next. Field widths, digit positions, decimal alignment, sign, grouping and a value too
/// wide for its field each get their own reading.
/// </para>
///
/// <para>
/// The format surface is the DOS runtime's, which is narrower than genuine PowerBASIC's: only
/// <c>#</c> digit runs, an optional <c>.</c> fraction and commas inside the digit run mean anything.
/// <c>$$</c>, <c>**</c>, <c>+</c>, <c>^^^^</c> and the string fields are literal text on BOTH paths,
/// and there are cases below pinning that, because "the two emitters agree" has to include agreeing
/// about what they do not implement.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendPrintUsingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>What one run was observed to do: the screen, the printer, and any file it wrote.</summary>
  private sealed record Behaviour(string Screen, string Printer, string? File);

  /// <summary>Compiles both ways, runs both images, and asserts the module body really was routed.</summary>
  private static (Behaviour Direct, Behaviour Routed) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
      "the back end did not take the module body, so this compares the direct emitter with itself");

    Behaviour Execute(byte[] image, string which) {
      try {
        var cpu = Cpu8086.Run(image);
        return new(cpu.Output, cpu.PrinterOutput, cpu.FileContent("OUT.TXT"));
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return new("", "", null);
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"));
  }

  /// <summary>
  /// One statement, both optimization settings. They are different emitters - with the optimizer off
  /// there is no constant folding, so a USING field's scale-and-round happens at RUNTIME on the x87
  /// rather than in <c>IrConstFold</c>, and only running both says the two agree about it.
  /// </summary>
  private static void Prints(string statements, string expected)
    => Behaves(statements, expected, null, null);

  /// <summary>...and the same for a program whose destination is the printer or a file.</summary>
  private static void Behaves(string statements, string screen, string? printer, string? file) {
    foreach (var optimize in new[] { true, false }) {
      var (direct, routed) = RunBothWays(statements, optimize);
      Assert.That(routed, Is.EqualTo(direct), $"the two back ends disagree (optimize={optimize})");
      Assert.That(Normalize(routed.Screen), Is.EqualTo(screen), $"screen (optimize={optimize})");
      if (printer is not null)
        Assert.That(Normalize(routed.Printer), Is.EqualTo(printer), $"printer (optimize={optimize})");
      if (file is not null)
        Assert.That(Normalize(routed.File ?? "<no file>"), Is.EqualTo(file), $"file (optimize={optimize})");
    }
  }

  private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

  #region field width, digit positions, decimal alignment

  [Test]
  public void Using_GivenAFractionalField_ThenTheValueIsRoundedAndRightAligned()
    // "##.##" is five characters wide; 3.14159 scaled by 100 and rounded is 314, which fills four of
    // them, so one blank leads
    => Prints("""PRINT USING "##.##"; 3.14159""", " 3.14\n");

  [Test]
  public void Using_GivenAnIntegerFieldWiderThanTheValue_ThenItIsBlankPadded()
    => Prints("""PRINT USING "Total: ####"; 42""", "Total:   42\n");

  [Test]
  public void Using_GivenTwoFieldsWithLiteralTextBetween_ThenEachAlignsInItsOwnField()
    // and the second value pins the rounding independently of the first: 1.26 -> 1.3, 20.5 -> 20.5
    => Prints("""PRINT USING "x=###.#, y=###.#"; 1.26; 20.5""", "x=  1.3, y= 20.5\n");

  [Test]
  public void Using_GivenAFieldNarrowerThanTheFraction_ThenTheLeadingZeroIsSupplied()
    // 0.05 scaled by 100 is 5, one digit where the field wants two after the point - so the
    // formatter pads zeros until there are more digits than decimals, giving "0.05" rather than ".5"
    => Prints("""PRINT USING "#.##"; 0.05""", "0.05\n");

  [Test]
  public void Using_GivenLiteralTextOnBothSidesOfTheField_ThenBothAreEmitted()
    => Prints("""PRINT USING "[##]"; 7""", "[ 7]\n");

  [Test]
  public void Using_GivenAnIntegerVariable_ThenItIsConvertedAndPlacedLikeAReal() {
    Prints("""
      n% = 42
      PRINT USING "#####"; n%
      """, "   42\n");
  }

  #endregion

  #region sign, grouping, overflow

  [Test]
  public void Using_GivenANegativeValue_ThenTheSignTakesAColumnInsideTheField()
    // "####.##" is seven wide; -12.50 prints as "-12.50", six characters, so one blank leads
    => Prints("""PRINT USING "####.##"; -12.5""", " -12.50\n");

  [Test]
  public void Using_GivenANegativeValueThatFillsTheField_ThenNothingPads()
    => Prints("""PRINT USING "##.##"; -3.14159""", "-3.14\n");

  [Test]
  public void Using_GivenCommasInTheDigitRun_ThenThousandsAreGrouped()
    // "###,###,###" is eleven wide: nine digit positions and two separators. 1234567 needs seven
    // digits and two commas, so two blanks lead
    => Prints("""PRINT USING "###,###,###"; 1234567""", "  1,234,567\n");

  [Test]
  public void Using_GivenGroupingAndAValueUnderAThousand_ThenNoSeparatorAppears()
    => Prints("""PRINT USING "###,###"; 42""", "     42\n");

  [Test]
  public void Using_GivenAValueTooWideForItsField_ThenItOverflowsTheFieldRatherThanBeingTruncated()
    // PowerBASIC proper marks an overflowing field with a leading '%'. The DOS runtime does not, and
    // this pins the answer both back ends actually give rather than the one the manual describes -
    // the digits are all there, the column alignment is what is lost
    => Prints("""PRINT USING "##"; 12345""", "12345\n");

  [Test]
  public void Using_GivenAHalfwayValue_ThenItRoundsToEvenLikeTheX87Does() {
    // 0.25 and 0.75 scaled by ten are exactly 2.5 and 7.5. FISTP rounds to nearest with ties to
    // EVEN, so they go to 2 and 8 - not to 3 and 8, which is what half-away-from-zero would give.
    // The IR path must fold the constant the same way it would compute it.
    Prints("""PRINT USING "#.#"; 0.25""", "0.2\n");
    Prints("""PRINT USING "#.#"; 0.75""", "0.8\n");
  }

  [Test]
  public void Using_GivenZero_ThenTheDigitIsPrintedRatherThanNothing()
    => Prints("""PRINT USING "##.##"; 0.0""", " 0.00\n");

  [Test]
  public void Using_GivenTheLargestValueTheFormatterCanHold_ThenItStillRenders() {
    // rt_usefmt takes the SCALED value in DX:AX, so a two-decimal field tops out at 21474836.47 on
    // both paths. This pins the ceiling from below - a change that lowered it would break here
    // rather than in whatever program first exceeded it. Above it the two back ends produce
    // different wrong answers; see LowerPrintUsing for why there is no rule that fixes both.
    Prints("""PRINT USING "###,###,###.##"; 21474836.47#""", " 21,474,836.47\n");
  }

  #endregion

  #region items, separators and the format's own edges

  [Test]
  public void Using_GivenATrailingSemicolon_ThenNoNewlineIsWritten() {
    Prints("""
      PRINT USING "##"; 1;
      PRINT "X"
      """, " 1X\n");
  }

  [Test]
  public void Using_GivenFewerValuesThanFields_ThenLiteralTextUpToTheNextFieldStillPrints()
    // the format is NOT recycled and the unfilled field prints nothing, so "##-##" with one value
    // gives the number, the separator, and then a stop
    => Prints("""PRINT USING "##-##"; 5""", " 5-\n");

  [Test]
  public void Using_GivenACommaBetweenValues_ThenItIsNotAPrintZone()
    // outside USING a comma advances to the next 14-column zone. Inside one the format decides the
    // spacing and the separator only says where one value ends, which is what both paths do with it
    => Prints("""PRINT USING "## ##"; 1, 2""", " 1  2\n");

  [Test]
  public void Using_GivenAStringValueInANumericField_ThenItPrintsAsItselfUnpadded()
    // PB's '&' approximation: the runtime's numeric formatter has nothing to do with a string, so
    // the direct emitter prints it verbatim and this path does the same
    => Prints("""PRINT USING "###"; "ab" """, "ab\n");

  [Test]
  public void Using_GivenACurrencyOrFillPrefix_ThenItIsLiteralTextOnBothPaths() {
    // $$ and ** are floating-currency and asterisk-fill in genuine PB and are NOT implemented by the
    // DOS runtime's formatter. Both back ends print them as the characters they are; this fixes that
    // agreed answer so a future implementation has to change it deliberately.
    Prints("""PRINT USING "$$##.##"; 1.5""", "$$ 1.50\n");
    Prints("""PRINT USING "**##"; 7""", "** 7\n");
  }

  [Test]
  public void Using_GivenAFileNumber_ThenTheFormattedTextGoesToTheFileAndTheScreenIsRestored() {
    // the select/restore is per CALL on this path and per STATEMENT on the other, so both halves are
    // read: the literal and the field both land in the file, and the PRINT after still reaches the
    // screen
    Behaves("""
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      PRINT #1, USING "pi=##.##"; 3.14159
      CLOSE #1
      PRINT "back"
      """, "back\n", "", "pi= 3.14\n");
  }

  #endregion

  #region LPRINT

  [Test]
  public void LPrint_GivenAPrinterLine_ThenItGoesToThePrinterAndTheNextPrintGoesToTheScreen() {
    // both halves in one reading: the line really reaches DOS handle 4 rather than being written
    // somewhere and lost, and the console is put back afterwards rather than left pointing at PRN
    Behaves("""
      LPRINT "printer"
      PRINT "screen"
      """, "screen\n", "printer\n", null);
  }

  [Test]
  public void LPrint_GivenAPrinterLine_ThenTheScreenColumnIsUntouched() {
    // POS reads the SCREEN column; LPRINT counts through the printer's. Six characters sent to the
    // printer must leave POS where "ab" put it, which is column 3
    Behaves("""
      PRINT "ab";
      LPRINT "cdefgh";
      PRINT POS(0)
      """, "ab 3 \n", "cdefgh", null);
  }

  [Test]
  public void LPrint_GivenACommaSeparator_ThenTheZoneIsCountedInThePrintersOwnColumn()
    // the comma advances to the next 14-column zone of whichever column is active, so "a" then a
    // zone puts "b" at printer column 15 - and the screen, which never moved, is untouched by it
    => Behaves("""
      LPRINT "a", "b"
      PRINT "done"
      """, "done\n", "a             b\n", null);

  [Test]
  public void LPrint_GivenAUsingClause_ThenTheFormatterFollowsTheOutputToThePrinter()
    => Behaves("""
      LPRINT USING "pi=##.##"; 3.14159
      PRINT "after"
      """, "after\n", "pi= 3.14\n", null);

  #endregion

  #region USING$

  /// <summary>
  /// <c>USING$</c> is the same formatter with the text captured into a string. The whole point of it
  /// is that it does NOT print, so every case here brackets the value with characters of its own and
  /// puts an unrelated line on the screen FIRST: if the capture leaked, the formatted text would
  /// appear before that line and outside the brackets, and both readings would fail rather than one.
  /// </summary>
  [Test]
  public void UsingString_GivenAnIntegerField_ThenTheFieldWidthIsHeldAndNothingIsPrinted()
    => Prints("""
      PRINT "screen"
      s$ = USING$("Total: ####", 42)
      PRINT "["; s$; "]"
      """, "screen\n[Total:   42]\n");

  [Test]
  public void UsingString_GivenAFractionalField_ThenTheDecimalsAlignAsTheyDoWhenPrinted()
    // 3.456 scaled by 100 and rounded to nearest is 346, four digits in a five-wide field
    => Prints("""
      s$ = USING$("##.##", 3.456)
      PRINT "["; s$; "]"
      """, "[ 3.46]\n");

  [Test]
  public void UsingString_GivenGroupingAndSeveralFields_ThenEachIsPlacedInItsOwnColumns()
    => Prints("""
      PRINT "["; USING$("###,###,###", 1234567); "]"
      PRINT "["; USING$("x=## y", 42); "]"
      """, "[  1,234,567]\n[x=42 y]\n");

  [Test]
  public void UsingString_GivenAValueTooWideForItsField_ThenTheStringOverflowsRatherThanTruncating()
    // the same answer the printed form gives: every digit is there, the column alignment is what is
    // lost. A truncating formatter would give "45" and look tidier while being wrong.
    => Prints("""
      s$ = USING$("##", 12345)
      PRINT "["; s$; "]"; LEN(s$)
      """, "[12345] 5 \n");

  [Test]
  public void UsingString_GivenTwoCallsInARow_ThenTheSecondStartsFromAnEmptyBuffer()
    // the capture is ONE buffer and one length, so the second call has to reset the length rather
    // than append to what the first left there - which would give "[ 1][ 1][ 2]" for b$
    => Prints("""
      a$ = USING$("[##]", 1)
      b$ = USING$("[##]", 2)
      PRINT a$; b$
      """, "[ 1][ 2]\n");

  [Test]
  public void UsingString_GivenTheResultIsTrimmed_ThenItIsAnOrdinaryStringValue()
    // DIFF14's own use: the field's leading blanks are exactly what LTRIM$ removes, which only
    // works because what comes back is a real string handle rather than something printed
    => Prints("""
      d??? = 1234567
      PRINT "["; LTRIM$(USING$("###,###,###", d???)); "]"
      """, "[1,234,567]\n");

  [Test]
  public void UsingString_GivenAPrintAroundIt_ThenTheConsoleColumnIsUntouchedByTheCapture() {
    // POS reads the screen column. The capture writes nine characters, none of which are on the
    // screen, so POS after "ab" must still be 3 - a capture that wrote through the console would
    // have advanced it.
    Prints("""
      PRINT "ab";
      s$ = USING$("###,###", 1234)
      PRINT POS(0)
      """, "ab 3 \n");
  }

  #endregion

  #region what still declines

  [Test]
  public void Using_GivenANonLiteralFormat_ThenTheLoweringDeclines() {
    // there is nothing to read at compile time, and the direct emitter refuses it too
    var module = IrLowering.TryLowerModule(Bind("""
      f$ = "##.##"
      PRINT USING f$; 3.14159
      """), out var why);

    Assert.That(module, Is.Null);
    Assert.That(why, Does.Contain("non-literal PRINT USING format"));
  }

  [Test]
  public void Using_GivenMoreValuesThanFields_ThenTheLoweringDeclines() {
    // a value with no field would have to print somewhere; dropping it silently is the worse answer
    var module = IrLowering.TryLowerModule(Bind("""PRINT USING "##"; 1; 2"""), out var why);

    Assert.That(module, Is.Null);
    Assert.That(why, Does.Contain("more PRINT USING values than fields"));
  }

  [Test]
  public void LPrint_GivenAFileNumber_ThenTheLoweringDeclines() {
    // the printer and a file are two destinations for one statement, and this path selects per call
    var module = IrLowering.TryLowerModule(Bind("""
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      LPRINT #1, "x"
      CLOSE #1
      """), out var why);

    Assert.That(module, Is.Null);
    Assert.That(why, Does.Contain("LPRINT to a file number"));
  }

  [Test]
  public void Using_WhenEmittedAsC_ThenTheEmitterSaysTheCRuntimeHasNoFormatter() {
    // runtime/pbc_rt.c has no rt_using_field, so emitting the call would produce a translation unit
    // that does not LINK - which reads as "the emitted C is wrong" rather than "this target has no
    // formatter yet". The emitter declines instead, the way it declines an MBF type.
    var module = IrLowering.TryLowerModule(Bind("""PRINT USING "##.##"; 3.14159"""));
    Assert.That(module, Is.Not.Null);

    Assert.That(CEmitter.TryEmit(module!, out var why), Is.Null);
    Assert.That(why, Does.Contain("rt_using_field"));
  }

  [Test]
  public void UsingString_WhenEmittedAsC_ThenTheEmitterSaysTheCRuntimeHasNoCapture() {
    // and the capture pair is declined for a reason a stub could not paper over: what rt_capon MEANS
    // is that rt_print_* stop reaching stdout, which is a property of those routines rather than of
    // this one. An empty rt_capon would compile, link, and print the format to the screen.
    var module = IrLowering.TryLowerModule(Bind("""s$ = USING$("##.##", 3.14159)"""));
    Assert.That(module, Is.Not.Null);

    Assert.That(CEmitter.TryEmit(module!, out var why), Is.Null);
    Assert.That(why, Does.Contain("rt_capture_begin"));
  }

  #endregion
}
