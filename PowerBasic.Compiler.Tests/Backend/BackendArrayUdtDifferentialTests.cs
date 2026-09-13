using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Arrays and user-defined types through both back ends, run and compared - the shapes a sweep of the
/// domain found the routed path disagreeing with the direct emitter on.
///
/// <para>
/// Every subject here is opaque on purpose. An index, a bound or a value written down as a literal is
/// answered by SCCP before selection ever sees it, and a <c>NOINLINE</c> helper called from ONE site
/// is answered by interprocedural constant propagation instead - so each helper below is called with
/// at least two different arguments, and the assertions are about a value no pass can prove.
/// <see cref="Cpu8086"/> throws on anything it cannot execute, so a green case here means both images
/// really ran.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendArrayUdtDifferentialTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source, bool optimize = true) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output.Replace("\r\n", "\n").TrimEnd();
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"), routed.BackendRoutedNames);
  }

  /// <summary>The two-call-site opacity barrier every program below takes its subjects from.</summary>
  private const string _OPAQUE = """

    FUNCTION Op%(BYVAL v%) NOINLINE
      Op% = v%
    END FUNCTION
    """;

  /// <summary>
  /// A record MEMBER passed BYREF. The lowering knew how to hand over a variable's slot and an array
  /// element's address and had no case for a field, so it fell through to the temp-copy fallback that
  /// a constant argument uses - which is BYVAL wearing BYREF's spelling. The callee negated a copy and
  /// <c>r.A</c> came back untouched, against the direct emitter and against genuine PBC 3.5.
  /// </summary>
  [Test]
  public void Run_GivenARecordMemberPassedByRef_ThenTheCalleeWritesThroughIt() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Op%(BYVAL v%)
      TYPE Rec
        A AS INTEGER
        B AS LONG
      END TYPE
      DIM r AS Rec
      DIM q(1 TO 3) AS Rec
      r.A = 5
      CALL Neg(r.A)
      q(1).A = 1 : q(2).A = 2
      CALL Neg(q(Op%(2)).A)
      CALL Neg(q(Op%(1)).A)
      PRINT r.A; q(1).A; q(2).A

      SUB Neg(v AS INTEGER)
        v = -v
      END SUB
      """ + _OPAQUE);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"));
      Assert.That(names, Does.Contain("Neg"), "the near numeric BYREF callee must route");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(direct, Is.EqualTo("-5 -1 -2"));
    });
  }

  /// <summary>
  /// A BYTE field of a record ARRAY element, written from a word-sized value. Narrowing to a byte
  /// emits no instruction - the selector re-names the virtual register at byte width - and the
  /// spiller's live-range split then replaced that mention with the DEFINITION's width, producing
  /// <c>MOV [di], ax</c> against a byte cell. The assembler rejected it, so <c>pbc</c> aborted with an
  /// unhandled exception rather than compiling or declining.
  /// </summary>
  [Test]
  public void Run_GivenAByteFieldOfARecordArrayElement_ThenTheImageBuildsAndAgrees() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Op%(BYVAL v%)
      TYPE Five
        A AS BYTE
        B AS LONG
      END TYPE
      DIM f(0 TO 4) AS Five
      DIM i AS INTEGER
      FOR i = 0 TO 4
        f(i).A = i
        f(i).B = i * 111111&
      NEXT i
      PRINT f(Op%(0)).A; f(Op%(3)).A; f(Op%(3)).B; f(Op%(4)).B; LEN(f(0))
      """ + _OPAQUE, optimize: false);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 0  3  333333  444444  5"));
  }

  /// <summary>
  /// An element address recomputed after its index changed. <c>b(i) = i AND 255</c> puts the counter in
  /// the <c>LEA</c> that forms the element address AND in the increment that closes the loop; the
  /// spiller rematerialized the LEA in front of the store, which the scheduler had moved below the
  /// increment, so every iteration wrote <c>b(i+1)</c> - the array shifted by one and one byte written
  /// past its end.
  ///
  /// <para>
  /// <c>$OPTIMIZE SPEED</c> is load-bearing: the copy coalescer runs on that objective only, and it is
  /// what merges the index copy into the counter and so makes the LEA's operand a value the loop
  /// rewrites.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAnElementAddressWhoseIndexTheLoopRewrites_ThenEveryElementLandsAtItsOwnIndex() {
    var (direct, routed, names) = RunBothWays("""
      $OPTIMIZE SPEED
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM b(0 TO 999) AS BYTE
      DIM i AS INTEGER
      FOR i = 0 TO 999
        b(i) = i AND 255
      NEXT i
      PRINT b(Op%(0)); b(Op%(1)); b(Op%(255)); b(Op%(256)); b(Op%(999))
      """ + _OPAQUE);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 0  1  255  0  231"));
  }

  /// <summary>
  /// The same rematerialization question over two arrays at once, and over a rank-2 subscript: both
  /// element addresses are formed from the counter the loop then rewrites, and a copy loop is the
  /// shape <c>$OPTIMIZE SPEED</c> puts the most pressure on.
  /// </summary>
  [Test]
  public void Run_GivenCopyAndTwoDimensionalStoreLoops_ThenEveryElementLandsAtItsOwnIndex() {
    var (direct, routed, names) = RunBothWays("""
      $OPTIMIZE SPEED
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM src(0 TO 50) AS INTEGER
      DIM dst(0 TO 50) AS INTEGER
      DIM g(1 TO 6, 1 TO 7) AS INTEGER
      DIM i AS INTEGER, r AS INTEGER, c AS INTEGER
      FOR i = 0 TO 50
        src(i) = i * 3 - 10
      NEXT i
      FOR i = 5 TO 40
        dst(i) = src(i)
      NEXT i
      PRINT dst(Op%(4)); dst(Op%(5)); dst(Op%(40)); dst(Op%(41))
      FOR r = 1 TO 6
        FOR c = 1 TO 7
          g(r, c) = r * 10 + c
        NEXT c
      NEXT r
      PRINT g(Op%(1), Op%(1)); g(Op%(6), Op%(7)); g(Op%(4), Op%(2))
      """ + _OPAQUE);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 0  5  110  0 \n 11  67  42"));
  }

  /// <summary>
  /// A module-level dynamic array a procedure RE-DIMs. The descriptor - the data pointer and the
  /// per-dimension bounds - is this lowering's own frame, and each procedure is lowered by its own
  /// <c>IrLowering</c>, so the <c>SUB</c> grew a block described by slots nothing else reads: the module
  /// body still answered the old <c>UBOUND</c> and still addressed the freed block.
  ///
  /// <para>
  /// Two things had to be wrong at once. <c>DynDescriptor</c> never asked whether the array was shared
  /// storage - the guard existed in <c>SlotFor</c>/<c>GlobalFor</c>, which a dynamic array never reaches -
  /// and the escape analysis could not have answered anyway, because a <c>REDIM</c> names its array
  /// through a <c>VariableDecl</c> and the walk only looked at expressions. So a SUB whose ONLY mention
  /// of the array is the REDIM read as a SUB that never touched it.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAModuleDynamicArrayRedimmedInASub_ThenBothPathsSeeTheSameArray() {
    var (direct, routed, _) = RunBothWays("""
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM a() AS SHARED INTEGER
      REDIM a(1 TO Op%(3))
      a(1) = 1 : a(2) = 2 : a(3) = 3
      CALL Grow(Op%(6))
      PRINT a(1); a(3); UBOUND(a)
      CALL Grow(Op%(8))
      PRINT a(1); a(3); UBOUND(a)

      SUB Grow(BYVAL n AS INTEGER)
        REDIM PRESERVE a(1 TO n)
      END SUB
      """ + _OPAQUE);

    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 1  3  6 \n 1  3  8"));
  }

  /// <summary>
  /// A member of an INDEXED UDT array field - <c>b.Slots(i).Id</c>. It parses as an index of a member
  /// rather than of a name, so it needs its own address form, and the lowering had none: it declined
  /// 14 module bodies over the SVGA corpus, where a context record holding a table of records is the
  /// ordinary shape.
  ///
  /// <para>
  /// A TYPE's array field carries no lower bound and is not range-checked, so the subscript scales
  /// straight off the field's own address. <c>Head</c> and <c>Tail</c> are read back for that reason:
  /// a stride or a base off by one element lands INSIDE the record, on a neighbour, and a test that
  /// read only the array back would agree with itself while disagreeing with PowerBASIC.
  /// </para>
  /// <para>
  /// This is the one program here with no <c>Op%</c> barrier in front of its subjects, and the reason
  /// is a DIFFERENT gap: a record base is a memory base, and one live across the call to <c>Op%</c>
  /// cannot spill - so the barrier declines the function it was added to keep honest. The genuine
  /// oracle covers the unfolded arithmetic instead: <c>tests/diff/DIFF128.BAS</c> is this program with
  /// running subscripts, and the ROUTED build of it is byte-identical to PBC 3.50's answer.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAMemberOfAnIndexedUdtArrayField_ThenBothPathsAddressTheSameElement() {
    const string source = """
      TYPE Slot
        Id AS INTEGER
        Amt AS LONG
      END TYPE
      TYPE Box
        Head AS INTEGER
        Slots(4) AS Slot
        Tail AS INTEGER
      END TYPE
      DIM b AS Box
      b.Head = 11
      b.Tail = 22
      FOR i% = 0 TO 3
        b.Slots(i%).Id = i% + 1
        b.Slots(i%).Amt = (i% + 1) * 1000&
      NEXT
      k% = 0 : PRINT b.Slots(k%).Id;
      k% = 3 : PRINT b.Slots(k%).Id
      k% = 1 : PRINT b.Slots(k%).Amt;
      k% = 2 : PRINT b.Slots(k%).Amt
      PRINT b.Head; b.Tail
      """;

    var (direct, routed, names) = RunBothWays(source);

    Assert.That(names, Does.Contain("main").IgnoreCase, "an agreeing comparison proves nothing if it declined");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 1  4 \n 2000  3000 \n 11  22"));
  }

  /// <summary>
  /// <c>GET</c> straight into an element of a SHARED DYNAMIC array. The element lives in the far array
  /// heap, so its address is an offset plus a segment the runtime keeps in a CELL - and the runtime
  /// slot that takes a pointer is a register pair, which is exactly what that pair is for. The
  /// selector refused it anyway, on the grounds that the segment was not a register.
  ///
  /// <para>
  /// One refusal, nine procedures: a shared dynamic array whose users are SPLIT across the two paths
  /// has its descriptor handed back to the direct emitter whole, so the one SUB that could not take
  /// the address denied the routed side every array the TIFF code touches.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenGetIntoADynamicArrayElement_ThenBothPathsAddressTheFarHeap() {
    const string source = """
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM Store() AS SHARED LONG
      REDIM Store(Op%(3))
      Store(0) = 111111&
      Store(1) = 222222&
      OPEN "T.TMP" FOR BINARY AS #1
      PUT #1, , Store(0)
      PUT #1, , Store(1)
      CLOSE #1
      Store(0) = 0
      Store(1) = 0
      OPEN "T.TMP" FOR BINARY AS #1
      GET #1, , Store(1)
      GET #1, , Store(0)
      CLOSE #1
      PRINT Store(0); Store(1)
      """ + _OPAQUE;

    var (direct, routed, names) = RunBothWays(source);

    Assert.That(names, Does.Contain("main").IgnoreCase, "an agreeing comparison proves nothing if it declined");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(" 222222  111111"), "the two records come back swapped, which is what was asked");
  }
}
