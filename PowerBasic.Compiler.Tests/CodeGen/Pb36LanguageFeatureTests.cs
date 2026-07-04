using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// End-to-end tests for the PB 3.6 new-syntax surface (docs/PB36.md): source is
/// compiled with <c>--dialect pb36</c> through the full pipeline and run under
/// DOSBox. Skipped when DOSBox is unavailable. These prove the new sugar lowers
/// to the same observable behavior the hand-written equivalent would produce.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Pb36LanguageFeatureTests {

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

  [Test]
  public void Execute_GivenNamedArguments_WhenRun_ThenReorderedToParameters() {
    const string source = """
      DECLARE FUNCTION Box&(BYVAL w AS LONG, BYVAL h AS LONG, BYVAL d AS LONG)
      PRINT Box&(2, d := 5, h := 3)
      PRINT Box&(w := 4, h := 4, d := 4)
      FUNCTION Box&(BYVAL w AS LONG, BYVAL h AS LONG, BYVAL d AS LONG)
        Box& = w * h * d
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 30\n 64\n"));
  }

  [Test]
  public void Execute_GivenNamedArgumentWithDefault_WhenRun_ThenGapFilledByDefault() {
    const string source = """
      DECLARE SUB Show(BYVAL a AS LONG, BYVAL b AS LONG, BYVAL c AS LONG)
      Show 1, c := 9
      SUB Show(BYVAL a AS LONG, BYVAL b AS LONG = 5, BYVAL c AS LONG = 0)
        PRINT a + b + c
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 15\n"));
  }

  [Test]
  public void Execute_GivenDefaultParameter_WhenRun_ThenOmittedArgUsesDefault() {
    const string source = """
      DECLARE FUNCTION Inc&(BYVAL x AS LONG, BYVAL by AS LONG)
      PRINT Inc&(5)
      PRINT Inc&(5, 10)
      FUNCTION Inc&(BYVAL x AS LONG, BYVAL by AS LONG = 1)
        Inc& = x + by
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 6\n 15\n"));
  }

  [Test]
  public void Execute_GivenDefaultParameterSub_WhenRun_ThenOmittedArgUsesDefault() {
    const string source = """
      DECLARE SUB Greet(BYVAL n AS LONG, BYVAL times AS LONG)
      Greet 5
      Greet 7, 3
      SUB Greet(BYVAL n AS LONG, BYVAL times AS LONG = 2)
        PRINT n * times
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 10\n 21\n"));
  }

  [Test]
  public void Execute_GivenWithBlock_WhenRun_ThenDotMembersHitTheSubject() {
    const string source = """
      TYPE Point
        X AS INTEGER
        Y AS INTEGER
      END TYPE
      DIM p AS Point
      WITH p
        .X = 3
        .Y = .X + 4
      END WITH
      PRINT p.X
      PRINT p.Y
      """;
    Assert.That(Run(source), Is.EqualTo(" 3\n 7\n"));
  }

  [Test]
  public void Execute_GivenNestedWith_WhenRun_ThenInnermostSubjectWins() {
    const string source = """
      TYPE Inner
        V AS INTEGER
      END TYPE
      TYPE Outer
        A AS INTEGER
        I AS Inner
      END TYPE
      DIM o AS Outer
      WITH o
        .A = 1
        WITH .I
          .V = 9
        END WITH
      END WITH
      PRINT o.A
      PRINT o.I.V
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\n 9\n"));
  }

  [Test]
  public void Execute_GivenArrayInitializerAutoSized_WhenRun_ThenElementsAndBound() {
    const string source = """
      DIM a%() = {10, 20, 30}
      PRINT a%(0)
      PRINT a%(2)
      PRINT UBOUND(a%)
      """;
    Assert.That(Run(source), Is.EqualTo(" 10\n 30\n 2\n"));
  }

  [Test]
  public void Execute_GivenReflectionInPrint_WhenRun_ThenFoldedLiteralsCarryTheirStaticType() {
    // SIZEOF reflects as LONG - the folded literal must be LONG-typed too, else the 32-bit
    // print path reads a stale high word (regression: printed 0x01A30006 instead of 6)
    const string source = """
      TYPE Point
        X AS INTEGER
        Y AS LONG
      END TYPE
      DIM p AS Point
      PRINT SIZEOF(Point)
      PRINT TYPEOF$(p)
      PRINT FIELDCOUNT(Point); FIELDOFFSET(Point, Y); FIELDSIZE(Point, Y)
      """;
    Assert.That(Run(source), Is.EqualTo(" 6\nPoint\n 2  2  4\n"));
  }

  [Test]
  public void Execute_GivenArrayInitializerRange_WhenRun_ThenRangeExpands() {
    const string source = """
      DIM r%() = {5 TO 8}
      PRINT r%(0)
      PRINT r%(3)
      """;
    Assert.That(Run(source), Is.EqualTo(" 5\n 8\n"));
  }

  [Test]
  public void Execute_GivenArrayInitializerSpread_WhenRun_ThenStaticArrayFlattened() {
    const string source = """
      DIM base%(2)
      base%(0) = 1
      base%(1) = 2
      base%(2) = 3
      DIM combo%() = {..base%, 99}
      PRINT combo%(0)
      PRINT combo%(2)
      PRINT combo%(3)
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\n 3\n 99\n"));
  }

  [Test]
  public void Execute_GivenFromEndIndexStatic_WhenRun_ThenCountsFromEnd() {
    const string source = """
      DIM a%(5)
      a%(5) = 99
      a%(4) = 88
      PRINT a%(^1)
      PRINT a%(^2)
      """;
    Assert.That(Run(source), Is.EqualTo(" 99\n 88\n"));
  }

  [Test]
  public void Execute_GivenFromEndIndexAsLValueAndDynamic_WhenRun_ThenWritesAndReadsEnd() {
    const string source = """
      DIM DYNAMIC b%(7)
      b%(^1) = 42
      b%(^3) = 24
      PRINT b%(7)
      PRINT b%(5)
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n 24\n"));
  }

  [TestCase("EMS")]
  [TestCase("XMS")]
  public void Execute_GivenExternalMemoryArray_WhenRun_ThenStoresAndLoads(string kind) {
    // EMS uses the EMS-paged allocator; XMS is routed through it as a stand-in until
    // the optimizer instance lands a true XMS runtime - both must round-trip values.
    var source = $"""
      DIM {kind} a&(10)
      a&(3) = 1234
      a&(7) = 5678
      PRINT a&(3)
      PRINT a&(7)
      """;
    Assert.That(Run(source), Is.EqualTo(" 1234\n 5678\n"));
  }

  [Test]
  public void Execute_GivenNonCapturingLambda_WhenCalledViaPointer_ThenRuns() {
    // the lambda is lifted to an anonymous proc; its value is a code pointer called
    // (for its side effect) via CALL DWORD.
    const string source = """
      DECLARE FUNCTION DoShout&()
      DIM f???
      f??? = FUNCTION() AS LONG => DoShout&()
      CALL DWORD f??? BDECL()
      FUNCTION DoShout&()
        PRINT "hi"
        DoShout& = 0
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo("hi\n"));
  }

  [Test]
  public void Execute_GivenNestedSubCapturingOuterLocal_WhenRun_ThenStackCaptureByRef() {
    // Bump captures the outer local x (stack capture via a hidden BYREF parameter);
    // each call mutates the outer x.
    const string source = """
      DECLARE SUB Outer()
      Outer
      SUB Outer()
        DIM x AS LONG
        x = 10
        SUB Bump()
          x = x + 5
        END SUB
        Bump
        Bump
        PRINT x
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 20\n"));
  }

  [Test]
  public void Execute_GivenNestedFunctionWithParamAndCapture_WhenRun_ThenBoth() {
    const string source = """
      DECLARE FUNCTION Compute&()
      PRINT Compute&()
      FUNCTION Compute&()
        DIM base AS LONG
        base = 100
        FUNCTION AddBase&(BYVAL n AS LONG)
          AddBase& = n + base
        END FUNCTION
        Compute& = AddBase&(7)
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 107\n"));
  }

  [Test]
  public void Execute_GivenEnumAutoAndExplicit_WhenRun_ThenMembersAreConstants() {
    const string source = """
      ENUM Status
        Red
        Green
        Blue = 5
        Cyan
      END ENUM
      PRINT Red
      PRINT Green
      PRINT Blue
      PRINT Cyan
      """;
    Assert.That(Run(source), Is.EqualTo(" 0\n 1\n 5\n 6\n"));
  }

  [Test]
  public void Execute_GivenEnumTypeAliasAndCompare_WhenRun_ThenUsableAsValue() {
    const string source = """
      ENUM Color
        Red
        Green
        Blue
      END ENUM
      DIM c AS Color
      c = Green
      PRINT c
      IF c = Green THEN PRINT "yes" ELSE PRINT "no"
      PRINT Red + Blue
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\nyes\n 2\n"));
  }

  [Test]
  public void Execute_GivenExpressionBodiedFunction_WhenRun_ThenResultMatchesEquivalentBody() {
    const string source = """
      DECLARE FUNCTION Sq&(BYVAL x AS LONG)
      PRINT Sq&(7)
      FUNCTION Sq&(BYVAL x AS LONG) = x * x
      """;
    Assert.That(Run(source), Is.EqualTo(" 49\n"));
  }

  [Test]
  public void Execute_GivenCompoundArithmetic_WhenRun_ThenAccumulates() {
    const string source = """
      n% = 10
      n% += 5
      n% *= 3
      n% -= 1
      PRINT n%
      """;
    Assert.That(Run(source), Is.EqualTo(" 44\n"));
  }

  [Test]
  public void Execute_GivenCompoundConcat_WhenRun_ThenStringGrows() {
    const string source = """
      s$ = "ab"
      s$ &= "cd"
      s$ &= "ef"
      PRINT s$
      """;
    Assert.That(Run(source), Is.EqualTo("abcdef\n"));
  }

  [Test]
  public void Execute_GivenDimInferredInteger_WhenRun_ThenDeclaresAndInitializes() {
    const string source = """
      DIM x = 7
      PRINT x
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n"));
  }

  [Test]
  public void Execute_GivenDimInferredString_WhenRun_ThenStringStored() {
    const string source = """
      DIM s = "hello"
      PRINT s
      """;
    Assert.That(Run(source), Is.EqualTo("hello\n"));
  }

  [Test]
  public void Execute_GivenDimInferredLargeLiteral_WhenRun_ThenInfersWideEnoughType() {
    // 100000 does not fit INTEGER; inference must pick LONG so the value survives.
    const string source = """
      DIM big = 100000
      PRINT big
      """;
    Assert.That(Run(source), Is.EqualTo(" 100000\n"));
  }

  [Test]
  public void Execute_GivenDimTypedInitializer_WhenRun_ThenUsesExplicitType() {
    const string source = """
      DIM n AS LONG = 100000
      PRINT n * 2
      """;
    Assert.That(Run(source), Is.EqualTo(" 200000\n"));
  }

  [TestCase("PRINT IF(1, 10, 20)", " 10\n")]
  [TestCase("PRINT IF(0, 10, 20)", " 20\n")]
  [TestCase("PRINT IF(5 > 3, 99, -1)", " 99\n")]
  public void Execute_GivenTernaryIf_WhenRun_ThenSelectsBranch(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenTernaryStringBranches_WhenRun_ThenStringResult() {
    Assert.That(Run("PRINT IF(5 > 3, \"yes\", \"no\")"), Is.EqualTo("yes\n"));
  }

  [Test]
  public void Execute_GivenTernaryIf_WhenRun_ThenUntakenBranchNotEvaluated() {
    // If the false branch (100 \ x%) were evaluated with x% = 0 it would raise the
    // genuine division-by-zero error 11; short-circuit must skip it and print 42.
    const string source = """
      x% = 0
      PRINT IF(x% = 0, 42, 100 \ x%)
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n"));
  }

  [TestCase("PRINT (1 ANDALSO 1)", "-1\n")]
  [TestCase("PRINT (1 ANDALSO 0)", " 0\n")]
  [TestCase("PRINT (0 ANDALSO 1)", " 0\n")]
  [TestCase("PRINT (0 ORELSE 1)", "-1\n")]
  [TestCase("PRINT (1 ORELSE 0)", "-1\n")]
  [TestCase("PRINT (0 ORELSE 0)", " 0\n")]
  public void Execute_GivenShortCircuitOps_WhenRun_ThenNormalizedTruth(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenAndAlso_WhenRun_ThenRightOperandSkippedOnFalseLeft() {
    // (100 \ x%) would raise division-by-zero error 11 if evaluated; ANDALSO must
    // skip it because the left operand is false.
    const string source = """
      x% = 0
      PRINT (x% <> 0 ANDALSO (100 \ x%) > 0)
      """;
    Assert.That(Run(source), Is.EqualTo(" 0\n"));
  }

  [Test]
  public void Execute_GivenOrElse_WhenRun_ThenRightOperandSkippedOnTrueLeft() {
    const string source = """
      x% = 0
      PRINT (x% = 0 ORELSE (100 \ x%) > 0)
      """;
    Assert.That(Run(source), Is.EqualTo("-1\n"));
  }

  [Test]
  public void Execute_GivenDimFromTernary_WhenRun_ThenInferredFromResult() {
    const string source = """
      DIM m = IF(7 > 3, 7, 3)
      PRINT m
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n"));
  }

  [TestCase("PRINT 1 << 4", " 16\n")]
  [TestCase("PRINT 256 >> 2", " 64\n")]
  [TestCase("PRINT 6 <<> 1", " 12\n")]              // rotate left
  [TestCase("PRINT 1 <>> 1", "-32768\n")]           // rotate right: bit0 -> bit15
  [TestCase("PRINT 12 | 1", " 13\n")]               // bitwise OR
  public void Execute_GivenShiftRotate16_WhenRun_ThenComputesPerOperator(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenSignedShiftRight16_WhenRun_ThenArithmeticVsLogicalDiffer() {
    // the width follows the left operand's type: an INTEGER variable shifts 16-bit,
    // so >> (arithmetic) keeps the sign while >>> (logical) zero-fills.
    const string source = """
      i% = -16
      PRINT i% >> 2
      PRINT i% >>> 2
      """;
    Assert.That(Run(source), Is.EqualTo("-4\n 16380\n"));
  }

  [Test]
  public void Execute_GivenShiftRotateCompound_WhenRun_ThenUpdatesInPlace() {
    const string source = """
      x% = 1
      x% <<= 4
      PRINT x%
      x% |= 1
      PRINT x%
      """;
    Assert.That(Run(source), Is.EqualTo(" 16\n 17\n"));
  }

  [Test]
  public void Execute_GivenShift32_WhenRun_ThenLongWidthLoop() {
    const string source = """
      DIM n AS LONG = 1
      PRINT n << 20
      DIM m AS LONG = 1
      PRINT m <>> 1
      """;
    Assert.That(Run(source), Is.EqualTo(" 1048576\n-2147483648\n"));
  }

  [Test]
  public void Execute_GivenOverloadedFunctionByArity_WhenRun_ThenResolvesPerArgCount() {
    const string source = """
      DECLARE FUNCTION Area&(BYVAL r AS LONG)
      DECLARE FUNCTION Area&(BYVAL w AS LONG, BYVAL h AS LONG)
      PRINT Area&(5)
      PRINT Area&(4, 6)
      FUNCTION Area&(BYVAL r AS LONG)
        Area& = r * r
      END FUNCTION
      FUNCTION Area&(BYVAL w AS LONG, BYVAL h AS LONG)
        Area& = w * h
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 25\n 24\n"));
  }

  [Test]
  public void Execute_GivenOverloadedSubByArity_WhenRun_ThenResolvesPerArgCount() {
    const string source = """
      DECLARE SUB Show(BYVAL n AS LONG)
      DECLARE SUB Show(BYVAL a AS LONG, BYVAL b AS LONG)
      Show 7
      Show 3, 4
      SUB Show(BYVAL n AS LONG)
        PRINT n
      END SUB
      SUB Show(BYVAL a AS LONG, BYVAL b AS LONG)
        PRINT a * b
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n 12\n"));
  }

  [Test]
  public void Execute_GivenOverloadedFunctionByType_WhenRun_ThenResolvesPerArgType() {
    const string source = """
      DECLARE FUNCTION Kind&(BYVAL n AS LONG)
      DECLARE FUNCTION Kind&(BYVAL s AS STRING)
      PRINT Kind&(42&)
      PRINT Kind&("x")
      FUNCTION Kind&(BYVAL n AS LONG)
        Kind& = 1
      END FUNCTION
      FUNCTION Kind&(BYVAL s AS STRING)
        Kind& = 2
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\n 2\n"));
  }

  // ---- optimizer interaction (the optimizer is on by default under pb36) ----

  [TestCase("PRINT IF(7 > 3, 7, 3)", " 7\n")]       // constant-condition ternary folds to taken branch
  [TestCase("PRINT IF(2 > 5, 100, 200)", " 200\n")] // constant-condition false folds to the other branch
  [TestCase("DIM x = 5 << 2 : PRINT x", " 20\n")]   // shift-left constant folds
  public void Execute_GivenConstantFoldableNewSyntax_WhenOptimized_ThenFoldsCorrectly(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenProvenVarInTernary_WhenOptimized_ThenSsaStaysConsistent() {
    // x% is SCCP-proven 5 and read inside the ternary; the SSA/SCCP/DSE chain must
    // stay consistent through the ternary (a stale read or wrongly-dropped store
    // would print garbage instead of 5).
    const string source = """
      x% = 5
      y% = IF(x% > 0, x%, 0)
      PRINT y%
      """;
    Assert.That(Run(source), Is.EqualTo(" 5\n"));
  }

  [Test]
  public void Execute_GivenTernaryOnNonConstantParam_WhenOptimized_ThenRuntimeBranchKept() {
    // k is a (non-constant) parameter, so the ternary cannot fold; the store it
    // feeds must survive DSE and the runtime branch must select correctly.
    const string source = """
      DECLARE FUNCTION Pick&(BYVAL k AS LONG)
      PRINT Pick&(0)
      PRINT Pick&(1)
      FUNCTION Pick&(BYVAL k AS LONG)
        DIM r AS LONG = IF(k = 0, 111, 222)
        Pick& = r
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 111\n 222\n"));
  }

  [Test]
  public void Execute_GivenScaledPointerArith_WhenRun_ThenScalesByElementSize() {
    // p +* i scales i by the target size (2 for INTEGER), matching @p[i].
    const string source = """
      DIM a%(3)
      a%(0) = 10
      a%(1) = 20
      a%(2) = 30
      DIM p AS INTEGER PTR
      p = VARPTR32(a%(0))
      DIM q AS INTEGER PTR
      q = p +* 1
      PRINT @q
      q = p +* 2
      PRINT @q
      PRINT @p[1]
      """;
    Assert.That(Run(source), Is.EqualTo(" 20\n 30\n 20\n"));
  }

  [Test]
  public void Execute_GivenScaledPointerArithLong_WhenRun_ThenScalesByFour() {
    // a LONG PTR scales by 4; p -* brings it back down again.
    const string source = """
      DIM b&(3)
      b&(0) = 100
      b&(1) = 200
      b&(2) = 300
      DIM p AS LONG PTR
      p = VARPTR32(b&(0))
      DIM q AS LONG PTR
      q = p +* 2
      PRINT @q
      q = q -* 1
      PRINT @q
      """;
    Assert.That(Run(source), Is.EqualTo(" 300\n 200\n"));
  }

  [Test]
  public void Execute_GivenObjectInitializer_WhenRun_ThenListedFieldsSetAndOthersZero() {
    // Z is not listed, so it must keep its zero-initialized value.
    const string source = """
      TYPE Point
        X AS INTEGER
        Y AS INTEGER
        Z AS INTEGER
      END TYPE
      DIM p = NEW Point { .X = 3, .Y = 4 }
      PRINT p.X
      PRINT p.Y
      PRINT p.Z
      """;
    Assert.That(Run(source), Is.EqualTo(" 3\n 4\n 0\n"));
  }

  [Test]
  public void Execute_GivenDimInitializerInProcedure_WhenRun_ThenLocalInferred() {
    const string source = """
      DECLARE FUNCTION Cube&(BYVAL x AS LONG)
      PRINT Cube&(4)
      FUNCTION Cube&(BYVAL x AS LONG)
        DIM r AS LONG = x * x
        r = r * x
        Cube& = r
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 64\n"));
  }

  [Test]
  public void Execute_GivenTypedProcPointerToLambda_WhenCalled_ThenCoercesArgsAndReturns() {
    // a typed FUNCTION-pointer carries the signature, so the indirect call passes
    // the LONG argument at the right width and reads the LONG result - the untyped
    // CALL DWORD path could not (it pushed the bare value with no coercion).
    const string source = """
      DIM f AS FUNCTION(LONG) AS LONG
      f = FUNCTION(BYVAL x AS LONG) AS LONG => x * x
      PRINT f(7)
      """;
    Assert.That(Run(source), Is.EqualTo(" 49\n"));
  }

  [Test]
  public void Execute_GivenTypedProcPointerReassigned_WhenCalled_ThenLatestTargetRuns() {
    // the pointer variable is plain storage: reassigning it switches the callee.
    const string source = """
      DIM op AS FUNCTION(LONG, LONG) AS LONG
      op = FUNCTION(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG => a + b
      PRINT op(20, 3)
      op = FUNCTION(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG => a * b
      PRINT op(20, 3)
      """;
    Assert.That(Run(source), Is.EqualTo(" 23\n 60\n"));
  }

  [Test]
  public void Execute_GivenTypedProcPointerToNamedFunction_WhenCalled_ThenCallsThroughThunk() {
    // CODEPTR32 of a named FUNCTION yields a far thunk pointer; assigned to a typed
    // pointer it is callable like a lambda, now with argument coercion.
    const string source = """
      DECLARE FUNCTION Triple&(BYVAL n AS LONG)
      DIM f AS FUNCTION(LONG) AS LONG
      f = CODEPTR32(Triple&)
      PRINT f(9)
      FUNCTION Triple&(BYVAL n AS LONG)
        Triple& = n * 3
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 27\n"));
  }

  [Test]
  public void Execute_GivenNamedDelegateFromDeclare_WhenCalled_ThenSignatureReused() {
    // a DECLAREd FUNCTION prototype doubles as a named delegate type: DIM cmp AS
    // Comparator declares a typed pointer carrying that prototype's signature.
    const string source = """
      DECLARE FUNCTION Comparator(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
      DIM cmp AS Comparator
      cmp = FUNCTION(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG => a - b
      PRINT cmp(7, 2)
      """;
    Assert.That(Run(source), Is.EqualTo(" 5\n"));
  }

  [Test]
  public void Execute_GivenNamedDelegateAsParameterType_WhenPassed_ThenHigherOrderCallRuns() {
    // the named delegate is usable as a parameter type, so a procedure can accept a
    // proc pointer type-safely and invoke it (a higher-order call).
    const string source = """
      DECLARE FUNCTION IntOp(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
      DECLARE FUNCTION Apply&(BYVAL f AS IntOp, BYVAL x AS LONG, BYVAL y AS LONG)
      DIM addOp AS IntOp
      addOp = FUNCTION(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG => a + b
      PRINT Apply&(addOp, 8, 5)
      FUNCTION Apply&(BYVAL f AS IntOp, BYVAL x AS LONG, BYVAL y AS LONG)
        Apply& = f(x, y)
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 13\n"));
  }

  [Test]
  public void Execute_GivenConciseLambdaInferredFromDelegate_WhenAssigned_ThenTypesInferred() {
    // (a, b) => expr omits FUNCTION, parameter types and the result type; all are
    // inferred from the delegate the lambda is assigned to.
    const string source = """
      DECLARE FUNCTION Comparator(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
      DIM cmp AS Comparator
      cmp = (a, b) => a - b
      PRINT cmp(7, 2)
      """;
    Assert.That(Run(source), Is.EqualTo(" 5\n"));
  }

  [Test]
  public void Execute_GivenConciseLambdaInDimInitializer_WhenInferred_ThenRuns() {
    // the user's exact shape: a named delegate declared and initialized in one DIM,
    // the lambda's parameter types inferred from it.
    const string source = """
      DECLARE FUNCTION IntOp(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
      DIM addOp AS IntOp = (a, b) => a + b
      PRINT addOp(40, 2)
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n"));
  }

  [Test]
  public void Execute_GivenBareSingleParamLambda_WhenInferred_ThenParensOmitted() {
    // a single-parameter lambda may drop the parentheses entirely: x => 2 * x. The
    // '=>' arrow is a distinct token from the '>=' comparison, so it is unambiguous.
    const string source = """
      DECLARE FUNCTION DoDouble(BYVAL x AS LONG) AS LONG
      DIM ptr AS DoDouble = x => 2 * x
      PRINT ptr(21)
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n"));
  }

  [Test]
  public void Execute_GivenComparisonAfterDimInitializer_WhenPb36_ThenGreaterEqualStillCompares() {
    // '>=' remains the comparison operator, distinct from the '=>' lambda arrow.
    const string source = """
      DIM a AS LONG = 7
      IF a >= 2 * 3 THEN PRINT "yes" ELSE PRINT "no"
      """;
    Assert.That(Run(source), Is.EqualTo("yes\n"));
  }

  [Test]
  public void Execute_GivenCapturingLambda_WhenCalledInScope_ThenReadsOuterLocal() {
    // a stage-1 stack closure: the lambda captures the enclosing local 'bonus' by
    // reference through its environment pointer and reads it when called.
    const string source = """
      DECLARE SUB Demo()
      Demo
      SUB Demo()
        DIM bonus AS LONG
        bonus = 100
        DIM addBonus AS FUNCTION(LONG) AS LONG
        addBonus = FUNCTION(BYVAL x AS LONG) AS LONG => x + bonus
        PRINT addBonus(5)
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 105\n"));
  }

  [Test]
  public void Execute_GivenCapturingLambdaPassedToHigherOrder_WhenCalledThere_ThenEnvTravels() {
    // the closure is passed to another procedure and invoked there; its environment
    // (the captured outer local) travels with the fat delegate value, so it still
    // sees the captured 'factor' while Demo's frame is live.
    const string source = """
      DECLARE FUNCTION IntFn(BYVAL x AS LONG) AS LONG
      DECLARE FUNCTION Apply&(BYVAL f AS IntFn, BYVAL x AS LONG)
      DECLARE SUB Demo()
      Demo
      FUNCTION Apply&(BYVAL f AS IntFn, BYVAL x AS LONG)
        Apply& = f(x)
      END FUNCTION
      SUB Demo()
        DIM factor AS LONG
        factor = 6
        DIM scale AS IntFn
        scale = FUNCTION(BYVAL x AS LONG) AS LONG => x * factor
        PRINT Apply&(scale, 7)
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n"));
  }

  [Test]
  public void Execute_GivenCapturingLambdaMutatingOuterLocal_WhenCalled_ThenByRefShared() {
    // capture is by reference (stage-1 stack env): the closure mutates the enclosing
    // local, and the change is visible back in the defining scope.
    const string source = """
      DECLARE SUB Demo()
      Demo
      SUB Demo()
        DIM total AS LONG
        total = 0
        DIM addUp AS FUNCTION(LONG) AS LONG
        addUp = FUNCTION(BYVAL x AS LONG) AS LONG => total
        total = total + 10
        PRINT addUp(0)
        total = total + 5
        PRINT addUp(0)
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 10\n 15\n"));
  }

  [Test]
  public void Execute_GivenEscapingCapturingLambda_WhenCalledAfterProducerExits_ThenHeapEnvSurvives() {
    // stage-2 ESCAPING closure: MakeAdder builds a capturing lambda and RETURNS it,
    // so the closure outlives MakeAdder's (dead) frame. Its environment is a heap
    // snapshot of 'n' taken at creation; calling the returned closure later still
    // reads the captured value through the heap env.
    const string source = """
      DECLARE FUNCTION Adder(BYVAL x AS LONG) AS LONG
      DECLARE FUNCTION MakeAdder(BYVAL n AS LONG) AS Adder
      DECLARE SUB Demo()
      Demo
      FUNCTION MakeAdder(BYVAL n AS LONG) AS Adder
        MakeAdder = FUNCTION(BYVAL x AS LONG) AS LONG => x + n
      END FUNCTION
      SUB Demo()
        DIM add10 AS Adder
        add10 = MakeAdder(10)
        PRINT add10(5)
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 15\n"));
  }

  [Test]
  public void Execute_GivenTwoEscapingClosures_WhenCalledLater_ThenEachKeepsItsOwnCapture() {
    // two escaping closures from the same producer get independent heap snapshots:
    // each remembers the 'n' it was created with, even after both frames are gone.
    const string source = """
      DECLARE FUNCTION Adder(BYVAL x AS LONG) AS LONG
      DECLARE FUNCTION MakeAdder(BYVAL n AS LONG) AS Adder
      DECLARE SUB Demo()
      Demo
      FUNCTION MakeAdder(BYVAL n AS LONG) AS Adder
        MakeAdder = FUNCTION(BYVAL x AS LONG) AS LONG => x + n
      END FUNCTION
      SUB Demo()
        DIM a AS Adder
        DIM b AS Adder
        a = MakeAdder(100)
        b = MakeAdder(200)
        PRINT a(1)
        PRINT b(1)
        PRINT a(1)
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 101\n 201\n 101\n"));
  }

  [Test]
  public void Execute_GivenCollectionLiteralRangeInDim_WhenRun_ThenArrayFilled() {
    // [lo TO hi] is a bracketed collection/range literal, equivalent to {lo TO hi}.
    const string source = """
      DIM a%() = [99 TO 102]
      PRINT a%(0)
      PRINT a%(3)
      PRINT UBOUND(a%)
      """;
    Assert.That(Run(source), Is.EqualTo(" 99\n 102\n 3\n"));
  }

  [Test]
  public void Execute_GivenForEachOverRange_WhenRun_ThenIteratesInclusive() {
    // FOR EACH v IN [lo TO hi] desugars to a counted loop over the inclusive range.
    const string source = """
      DIM total AS LONG
      total = 0
      FOR EACH i& IN [1 TO 5]
        total = total + i&
      NEXT
      PRINT total
      """;
    Assert.That(Run(source), Is.EqualTo(" 15\n"));
  }

  [Test]
  public void Execute_GivenForEachOverArray_WhenRun_ThenIteratesElements() {
    // FOR EACH v IN a() iterates each element (LBOUND..UBOUND), copying it into v.
    const string source = """
      DIM a%(1 TO 3)
      a%(1) = 10
      a%(2) = 20
      a%(3) = 30
      DIM s AS LONG
      s = 0
      FOR EACH e% IN a%()
        s = s + e%
      NEXT
      PRINT s
      """;
    Assert.That(Run(source), Is.EqualTo(" 60\n"));
  }

  [Test]
  public void Execute_GivenInterpolationWithNumericHole_WhenRun_ThenMatchesStrDollarConcat() {
    // $"a {x} b" desugars to "a " & STR$(x) & " b" - same observable output
    const string interpolated = """
      DIM x AS LONG
      x = 7
      PRINT $"a {x} b"
      """;
    const string explicitForm = """
      DIM x AS LONG
      x = 7
      PRINT "a " & STR$(x) & " b"
      """;
    Assert.That(Run(interpolated), Is.EqualTo(Run(explicitForm)));
    Assert.That(Run(interpolated), Is.EqualTo("a  7 b\n")); // STR$ keeps PB's leading space
  }

  [Test]
  public void Execute_GivenInterpolationWithStringHole_WhenRun_ThenStringConcatenatedDirectly() {
    const string source = """
      DIM s AS STRING
      s = "world"
      PRINT $"hello, {s}!"
      """;
    Assert.That(Run(source), Is.EqualTo("hello, world!\n"));
  }

  [Test]
  public void Execute_GivenInterpolationWithFormatHole_WhenRun_ThenMatchesPrintUsing() {
    // {x:###.##} reuses the PRINT USING formatter via USING$
    const string interpolated = """
      DIM x AS SINGLE
      x = 3.14159
      PRINT $"pi={x:###.##}"
      """;
    const string usingForm = """
      DIM x AS SINGLE
      x = 3.14159
      PRINT "pi=" & USING$("###.##", x)
      """;
    Assert.That(Run(interpolated), Is.EqualTo(Run(usingForm)));
  }

  [Test]
  public void Execute_GivenBraceEscapes_WhenRun_ThenLiteralBracesPrinted() {
    const string source = """
      DIM x AS LONG
      x = 5
      PRINT $"{{{x}}}"
      """;
    Assert.That(Run(source), Is.EqualTo("{ 5}\n"));
  }
}
