using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 dense SELECT CASE -> jump table. The byte-identical output contract is enforced
/// by the differential harness (tests/diff/DIFF62.BAS under pb35 chain and pb36 table);
/// this pins that the table actually replaces the compare chain (an indexed indirect
/// jump appears, and the dispatch is more compact than the chain).
///
/// <para>
/// Every subject here comes from <c>INPUT</c>, and that is a requirement rather than a
/// style: a subject assigned a literal is a value the optimizer can prove, so the whole
/// SELECT folds to the one arm that can run and there is no dispatch left to assert
/// about. A test written that way passes for a reason unrelated to its name - and the
/// negative ones (no jump table, no mask, no tree) pass vacuously, which is worse.
/// </para>
/// </summary>
[TestFixture]
public sealed class SelectJumpTableTests {

  private static byte[] Compile(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  private static bool Contains(byte[] image, params byte[] needle) {
    for (var i = 0; i + needle.Length <= image.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (image[i + j] != needle[j]) { match = false; break; }
      if (match)
        return true;
    }
    return false;
  }

  private const string _DenseSelect =
    "DIM x%\nINPUT x%\n" +
    "SELECT CASE x%\n" +
    "CASE 1\n PRINT \"a\"\n" +
    "CASE 2\n PRINT \"b\"\n" +
    "CASE 3\n PRINT \"c\"\n" +
    "CASE 4\n PRINT \"d\"\n" +
    "CASE 5\n PRINT \"e\"\n" +
    "CASE ELSE\n PRINT \"z\"\n" +
    "END SELECT\nEND";

  [Test]
  public void Emit_GivenDenseSelect_WhenPb36_ThenDispatchesThroughAnIndexedJump() {
    var pb36 = Compile(_DenseSelect, Dialect.Pb36);
    // JMP word [BX + disp16] is FF A7 - the jump-table dispatch the chain never emits
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.True, "expected an indexed indirect JMP (jump table)");
  }

  [Test]
  public void Emit_GivenSparseSelect_WhenPb36_ThenKeepsTheCompareChain() {
    // only two cases - below the table threshold, so the compare chain stays (no FF A7)
    var pb36 = Compile("DIM x%\nINPUT x%\nSELECT CASE x%\nCASE 1\n PRINT \"a\"\nCASE 9\n PRINT \"b\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.False);
  }

  [Test]
  public void Emit_GivenWideSpanFewArmSelect_WhenPb36Size_ThenCompressesToAByteIndexTable() {
    // O0101: a dense SELECT with a wide span but few distinct arms (12 values -> 3 arms + default)
    // uses, under $OPTIMIZE SIZE, a byte index table into a small address table (MOV BL, [BX+table]
    // = 8A 9F) instead of a word entry per value - span + 2*K bytes rather than 2*span. Under
    // $OPTIMIZE SPEED the plain word table stays (one extra load per dispatch is not worth the bytes).
    const string sel = "DIM x%\nINPUT x%\nSELECT CASE x%\n" +
      "CASE 0, 4, 8, 12\n PRINT \"a\"\nCASE 1, 5, 9, 13\n PRINT \"b\"\nCASE 2, 6, 10, 14\n PRINT \"c\"\n" +
      "CASE ELSE\n PRINT \"z\"\nEND SELECT\nEND";
    var size = Compile("$OPTIMIZE SIZE\n" + sel, Dialect.Pb36);
    var speed = Compile("$OPTIMIZE SPEED\n" + sel, Dialect.Pb36);
    Assert.That(Contains(size, 0x8A, 0x9F), Is.True, "SIZE compresses to a byte index table (MOV BL, [BX+table])");
    Assert.That(Contains(speed, 0x8A, 0x9F), Is.False, "SPEED keeps the plain word table");
  }

  private const string _DenseLongSelect =
    "DIM x AS LONG\nINPUT x\n" +
    "SELECT CASE x\n" +
    "CASE 100000\n PRINT \"a\"\n" +
    "CASE 100001\n PRINT \"b\"\n" +
    "CASE 100002\n PRINT \"c\"\n" +
    "CASE 100003\n PRINT \"d\"\n" +
    "CASE 100004\n PRINT \"e\"\n" +
    "CASE ELSE\n PRINT \"z\"\n" +
    "END SELECT\nEND";

  [Test]
  public void Emit_GivenDenseLongSelect_WhenPb36_ThenDispatchesThroughAnIndexedJump() {
    // A dense SELECT CASE over a LONG subject with >=4 consecutive arms must also
    // emit the indexed indirect jump (FF A7) rather than a compare chain.
    var pb36 = Compile(_DenseLongSelect, Dialect.Pb36);
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.True, "expected an indexed indirect JMP (jump table) for LONG subject");
  }

  [Test]
  public void Emit_GivenDenseLongSelect_WhenPb35_ThenKeepsTheCompareChain() {
    // pb35 never uses jump tables - the compare chain must remain regardless of density
    var pb35 = Compile(_DenseLongSelect, Dialect.Pb35);
    Assert.That(Contains(pb35, 0xFF, 0xA7), Is.False, "pb35 must not emit a jump table");
  }

  private const string _SparseTreeSelect =
    "$OPTIMIZE SPEED\n" +
    "DIM x%\nINPUT x%\n" +
    "SELECT CASE x%\n" +
    "CASE 1\n PRINT \"a\"\n" +
    "CASE 100\n PRINT \"b\"\n" +
    "CASE 200\n PRINT \"c\"\n" +
    "CASE 300\n PRINT \"d\"\n" +
    "CASE 400\n PRINT \"e\"\n" +
    "CASE 500\n PRINT \"f\"\n" +
    "CASE 600\n PRINT \"g\"\n" +
    "CASE 556\n PRINT \"h\"\n" +   // 556 and 300 share a low byte (2Ch) -> no perfect low-bit hash, forcing the tree
    "CASE ELSE\n PRINT \"z\"\n" +
    "END SELECT\nEND";

  [Test]
  public void Emit_GivenSparseManyCaseSelect_WhenPb36Speed_ThenDispatchesThroughDecisionTree() {
    // O0098: 8 single-constant cases spanning 700 - too sparse for a dense jump table, so under
    // $OPTIMIZE SPEED a balanced binary decision tree dispatches. The subject stays in AX and is
    // compared against the case constants directly (CMP AX, 012Ch = 3D 2C 01 for the root median
    // 300), which the linear chain never emits (it loads each case value into AX and compares
    // against the subject cell). No jump table either.
    var img = Compile(_SparseTreeSelect, Dialect.Pb36);
    Assert.That(Contains(img, 0xFF, 0xA7), Is.False, "a sparse SELECT does not use a dense jump table");
    Assert.That(Contains(img, 0x3D, 0x2C, 0x01), Is.True, "the tree compares the AX-resident subject against 300 (CMP AX, 012Ch)");
  }

  [Test]
  public void Emit_GivenSparseValueListArm_WhenPb36Speed_ThenTestsMembershipWithABitMask() {
    // O0099: an arm listing >=3 point values in a <=16-wide window that the jump table declined
    // (CASE 1, 8, 15 - only 3 values, so below the table's count>=4 gate) tests membership with a
    // bit mask: MOV AX, 4081h (bits 0,7,14 for 1,8,15 normalized to min 1) then SHR AX, CL (D3 E8)
    // and a bit-0 test - no per-value compare.
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nSELECT CASE x%\nCASE 1, 8, 15\n PRINT \"a\"\nCASE ELSE\n PRINT \"z\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0xB8, 0x81, 0x40), Is.True, "the compile-time membership mask 4081h is loaded (MOV AX, 4081h)");
    Assert.That(Contains(img, 0xD3, 0xE8), Is.True, "the mask is shifted by the subject (SHR AX, CL)");
  }

  [Test]
  public void Emit_GivenWideWindowArm_WhenCpu386Speed_ThenUsesA32BitMask() {
    // O0099: a value list whose window is 16..31 wide (0, 5, 11, 17, 20 spans 20) needs a 32-bit mask,
    // so it lowers to the mask only under $CPU 80386 - SHR EAX, CL (66 D3 E8). Without 386 the window
    // is too wide for a native 16-bit mask, so the mask declines and the compare chain stays.
    const string sel = "DIM x%\nINPUT x%\nSELECT CASE x%\nCASE 0, 5, 11, 17, 20\n PRINT \"a\"\nCASE ELSE\n PRINT \"z\"\nEND SELECT\nEND";
    var with386 = Compile("$CPU 80386\n$OPTIMIZE SPEED\n" + sel, Dialect.Pb36);
    var no386 = Compile("$OPTIMIZE SPEED\n" + sel, Dialect.Pb36);
    Assert.That(Contains(with386, 0x66, 0xD3, 0xE8), Is.True, "the 386 path shifts a 32-bit mask (SHR EAX, CL)");
    Assert.That(Contains(no386, 0x66, 0xD3, 0xE8), Is.False, "without 386 a 16..31 window is too wide for the mask");
  }

  [Test]
  public void Emit_GivenConstantCaseRange_WhenPb36_ThenFoldsToOneUnsignedCompare() {
    // O0032 range fold in SELECT: a constant `CASE 0 TO 9` becomes one unsigned compare
    // (subject - lo) <=u (hi - lo) - `cmp ax, 9 / jbe` (83 F8 09 76) - instead of two signed compares
    // (cmp / jl + cmp / jle). The subject is loaded into AX; lo = 0 here so no subtract is emitted.
    // The polarity belongs to the arm order: the routed back end straightens `jbe arm / jmp else`
    // into `ja else` where the arm is laid out next, which is the same single test read the other way.
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nSELECT CASE x%\nCASE 0 TO 9\n PRINT \"a\"\nCASE ELSE\n PRINT \"z\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0x83, 0xF8, 0x09, 0x76) || Contains(img, 0x83, 0xF8, 0x09, 0x77), Is.True,
      "a constant CASE range folds to one unsigned compare (cmp ax, 9 / jbe or ja)");
  }

  [Test]
  public void Emit_GivenTwoValueArm_WhenPb36Speed_ThenKeepsTheCompareChain() {
    // below three values the bit mask declines and the compare chain stays (no SHR AX, CL dispatch).
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nSELECT CASE x%\nCASE 1, 15\n PRINT \"a\"\nCASE ELSE\n PRINT \"z\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0xB8, 0x81, 0x40), Is.False, "a two-value arm does not build a membership mask");
  }

  [Test]
  public void Emit_GivenOrChainEqualityIf_WhenPb36Speed_ThenTestsMembershipWithABitMask() {
    // O0099: IF k = 1 OR k = 8 OR k = 15 THEN is the same small-set membership as CASE 1, 8, 15 - the
    // OR chain lowers to the mask test (MOV AX, 4081h / SHR AX, CL) instead of a compare per value.
    // k% comes from INPUT so it is not a proven constant (which SCCP would fold the whole IF away).
    var img = Compile("$OPTIMIZE SPEED\nDIM k%\nINPUT k%\nIF k% = 1 OR k% = 8 OR k% = 15 THEN PRINT \"a\" ELSE PRINT \"b\"\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0xB8, 0x81, 0x40), Is.True, "the OR-equality chain builds the membership mask 4081h");
    Assert.That(Contains(img, 0xD3, 0xE8), Is.True, "and shifts it by the subject (SHR AX, CL)");
  }

  [Test]
  public void Emit_GivenAndChainOfInequalities_WhenPb36Speed_ThenTestsMembershipWithABitMask() {
    // O0099: the De Morgan complement `k <> 2 AND k <> 5 AND k <> 11` (exclusion) is the same small-set
    // membership as the OR-of-equalities, just branched on the NOT-in-set outcome - it lowers to the
    // mask test (MOV AX, mask / SHR AX, CL) too. Values 2,5,11 span 9 (<=15), so the mask fires.
    var img = Compile("$OPTIMIZE SPEED\nDIM k%\nINPUT k%\nIF k% <> 2 AND k% <> 5 AND k% <> 11 THEN PRINT \"y\" ELSE PRINT \"n\"\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0xD3, 0xE8), Is.True, "the AND-of-inequalities exclusion builds and shifts a membership mask (SHR AX, CL)");
  }

  [Test]
  public void Emit_GivenOrChainOfDifferentVariables_WhenPb36Speed_ThenNoBitMask() {
    // the mask requires ONE variable across the chain; mixed variables (k OR j) are not a set test.
    var img = Compile("$OPTIMIZE SPEED\nDIM k%, j%\nINPUT k%\nINPUT j%\nIF k% = 1 OR j% = 8 OR k% = 15 THEN PRINT \"a\"\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0xB8, 0x81, 0x40), Is.False, "a mixed-variable OR chain is not a membership mask");
  }

  [Test]
  public void Emit_GivenSparseSelectWithPerfectHash_WhenPb36Speed_ThenDispatchesThroughAMaskedTable() {
    // O0100: 8 sparse values (16, 33, ... 135) whose low 3 bits are all distinct - too wide for a
    // dense table, but AND 7 is a collision-free perfect hash. The dispatch masks the subject
    // (AND AX, 7 = 83 E0 07), indexes a key+jump table pair, verifies the key and takes the indexed
    // jump (FF A7) - constant time, no compare per value. The AND-mask is unique to this path (the
    // jump table normalizes with SUB, the tree compares, the chain loads each value).
    var img = Compile(
      "$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nSELECT CASE x%\n" +
      "CASE 16\n PRINT \"a\"\nCASE 33\n PRINT \"b\"\nCASE 50\n PRINT \"c\"\nCASE 67\n PRINT \"d\"\n" +
      "CASE 84\n PRINT \"e\"\nCASE 101\n PRINT \"f\"\nCASE 118\n PRINT \"g\"\nCASE 135\n PRINT \"h\"\n" +
      "CASE ELSE\n PRINT \"z\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0x83, 0xE0, 0x07), Is.True, "the perfect hash masks the subject (AND AX, 7)");
    Assert.That(Contains(img, 0xFF, 0xA7), Is.True, "and takes an indexed indirect jump through the table");
  }

  [Test]
  public void Emit_GivenFewCaseSparseSelect_WhenPb36Speed_ThenKeepsTheCompareChain() {
    // below the 8-distinct-value threshold the tree declines and the linear compare chain stays:
    // it loads each case value into AX (MOV AX, 012Ch = B8 2C 01) and compares against the subject
    // cell, so the CMP AX, 012Ch tree signature is absent.
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nSELECT CASE x%\nCASE 1\n PRINT \"a\"\nCASE 100\n PRINT \"b\"\nCASE 200\n PRINT \"c\"\nCASE 300\n PRINT \"d\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(img, 0x3D, 0x2C, 0x01), Is.False, "a few-case sparse SELECT keeps the compare chain, not the tree");
  }
}
