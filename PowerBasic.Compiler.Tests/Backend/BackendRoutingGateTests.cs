using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// One tiny program per construct, compiled twice - with routing on and with routing off - so that a
/// construct which silently STOPS routing is a red test rather than a quiet fallback to the direct
/// emitter.
///
/// <para>
/// The corpus census (<see cref="BackendCoverageTests"/>) ranks what the back end refuses, but it can
/// only rank what the corpus contains, and the corpus has no procedure with a QUAD parameter, a BYTE
/// one, a UDT one or a WATCALL convention. Those classes are just as real: each is a compile failure
/// waiting for the day <c>CodeGen/</c> is deleted. This fixture is where they are written down, one
/// program each, with the routing's own recorded reason - so the list below is the roadmap, and
/// closing an item means moving its row from <see cref="_declines"/> to <see cref="_routes"/>.
/// </para>
///
/// <para>
/// <b>Two signals, on purpose.</b> <c>BackendRoutedNames</c> is the honest question - did the back
/// end take this function - and execution equivalence is the observable one. A declined procedure
/// may coexist with a routed caller when their stack ABI is shared, so byte identity no longer means
/// whole-program fallback. Both signals are asserted: a routing table can name a function the emitter
/// never reached, while a mixed routed/direct image must still behave like the direct build.
/// </para>
///
/// <para>
/// <b>The optimizer is OFF here, and that is load-bearing.</b> It keeps the call standing so the test
/// observes the routed/direct ABI boundary rather than an inliner absorbing the declined procedure.
/// It is also the state the historic dialects compile in.
/// </para>
///
/// <para>
/// A construct that THROWS out of the back end fails these tests by throwing, which is deliberate: a
/// throw is neither routed nor declined and must never be counted as either.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendRoutingGateTests {

  /// <param name="Label">how the case reads in the test list.</param>
  /// <param name="Source">the whole program - small enough that the construct is the only variable.</param>
  /// <param name="Subject">the procedure the claim is about, or <c>main</c> for the module body.</param>
  /// <param name="Reason">the routing's own recorded decline reason; unused by the routing cases.</param>
  public sealed record Construct(string Label, string Source, string Subject, string Reason = "") {
    public override string ToString() => this.Label;
  }

  /// <summary>Constructs the back end takes today. A row that stops routing is a coverage regression.</summary>
  private static readonly Construct[] _routes = [
    new("INTEGER parameter and result", """
      FUNCTION F(BYVAL a%) AS INTEGER
        F = a% + 1
      END FUNCTION
      PRINT F(1)
      """, "F"),
    new("LONG parameter and result", """
      FUNCTION F(BYVAL a&) AS LONG
        F = a& + 1
      END FUNCTION
      PRINT F(1)
      """, "F"),
    new("SINGLE parameter and result", """
      FUNCTION F(BYVAL a!) AS SINGLE
        F = a! + 1
      END FUNCTION
      PRINT F(1)
      """, "F"),
    new("DOUBLE parameter and result", """
      FUNCTION F(BYVAL a#) AS DOUBLE
        F = a# + 1
      END FUNCTION
      PRINT F(1)
      """, "F"),
    new("BYREF INTEGER parameter", """
      FUNCTION F(a%) AS INTEGER
        a% = a% + 1
        F = a%
      END FUNCTION
      DIM v%
      v% = 1
      PRINT F(v%); v%
      """, "F"),
    new("BYVAL STRING parameter", """
      FUNCTION F(BYVAL a$) AS INTEGER
        F = LEN(a$)
      END FUNCTION
      PRINT F("ab")
      """, "F"),
    new("BYREF STRING parameter", """
      SUB S(a$)
        a$ = a$ + "!"
      END SUB
      DIM v AS STRING
      v = "ab"
      S v
      PRINT v
      """, "S"),
    new("STRING result", """
      FUNCTION F(BYVAL a%) AS STRING
        F = "x"
      END FUNCTION
      PRINT F(1)
      """, "F"),
    new("a SUB with no parameters", """
      SUB S
        PRINT 7
      END SUB
      S
      """, "S"),
    new("module body: INTEGER arithmetic", """
      DIM n AS INTEGER
      n = 6
      PRINT n * 7
      """, "main"),
    new("module body: QUAD arithmetic", """
      DIM q AS QUAD
      q = 5
      q = q + 2
      PRINT q
      """, "main"),
    new("module body: BYTE local", """
      DIM b AS BYTE
      b = 200
      PRINT b
      """, "main"),
    new("module body: string concatenation", """
      DIM s AS STRING
      s = "ab" + "cd"
      PRINT s
      """, "main"),
    new("module body: an array", """
      DIM a(1 TO 4) AS INTEGER
      FOR i% = 1 TO 4 : a(i%) = i% * 2 : NEXT
      PRINT a(3)
      """, "main"),
    // RND and TIMER written WITHOUT parentheses. The binder leaves a bare intrinsic a NameExpr, so
    // these do not reach the intrinsic-call path at all - they used to arrive as "unbound name RND"
    // and take the whole module with them, while RND(0) a line above routed perfectly well.
    new("module body: bare RND", """
      DIM x AS SINGLE
      x = RND
      PRINT x < 1
      """, "main"),
    new("module body: bare TIMER", """
      DIM t AS SINGLE
      t = TIMER
      PRINT t >= 0
      """, "main"),
    // RANDOMIZE in both spellings: a seed the program supplies is a store into the runtime's own
    // seed cell, and the argumentless form is the routine that reads the BIOS clock into it.
    new("module body: RANDOMIZE with a seed", """
      RANDOMIZE 7
      PRINT RND(1, 6) >= 1
      """, "main"),
    new("module body: RANDOMIZE with no seed", """
      RANDOMIZE
      PRINT RND(1, 6) >= 1
      """, "main"),
    // A record has no single value to load, so SWAP of one is three block copies through a frame
    // temporary rather than the load/store pair a scalar gets.
    new("module body: SWAP of a record", """
      TYPE R
        a AS INTEGER
      END TYPE
      DIM p AS R, q AS R
      p.a = 1 : q.a = 2
      SWAP p, q
      PRINT p.a; q.a
      """, "main"),
    // READ into a FIXED-length string: a buffer to pad into, not a handle cell to store
    new("module body: READ into a fixed string", """
      DATA ab
      DIM s AS STRING * 4
      READ s
      PRINT s
      """, "main"),
    // CLS, which had no lowering at all and therefore took the whole module with it. Ordinary enough
    // that its absence is worth a row: no corpus program reaches it in a routable position, so the
    // census read 321/321 while `CLS` on its own line declined everything around it.
    new("module body: CLS", """
      CLS
      PRINT 7
      """, "main"),
    // A module-level DYNAMIC array a procedure also REDIMs. Both sides route, and they must, for the
    // reason the DATA pool has: the descriptor is the routed path's own cells and the direct
    // emitter's is a packed block, so a REDIM on one side and an UBOUND on the other would consult
    // two descriptions of one array. It used to take the WHOLE MODULE off the routed path, because
    // the descriptor was frame slots and every procedure is lowered by its own IrLowering - a routed
    // REDIM PRESERVE inside a SUB wrote the new bounds into the SUB's frame and the module body went
    // on describing the old block. Naming the fields as module globals removes the cause; a split set
    // of users is refused by SharedDynArrayUsersRouteTogether rather than by declining in advance.
    new("shared dynamic array a procedure REDIMs", """
      $DYNAMIC
      DECLARE SUB Grow(BYVAL n%)
      DIM a() AS SHARED INTEGER
      REDIM a(1 TO 2)
      a(1) = 7 : a(2) = 8
      Grow 5
      PRINT LBOUND(a); UBOUND(a); a(1); a(2)
      END
      SUB Grow(BYVAL n%)
        REDIM PRESERVE a(1 TO n%)
      END SUB
      """, "main"),
    // READ inside a PROCEDURE. Both the SUB and the module body route, and they must: one pool and
    // one cursor means every reader has to be on the same side.
    new("procedure: READ from DATA", """
      DATA 1, 2
      SUB S
        DIM i%
        READ i%
        PRINT i%
      END SUB
      S
      """, "S"),
    // The last decline the SELECTOR made rather than the filter, and it was a lowering fault wearing
    // a selection message. A FIX cell is a scaled int64, so `MapType` gives `1.5@` the type i64 and
    // `LowerExpr` built it as an `IrConstantFloat` - a float constant carrying an integer type, which
    // no cell can be staged from. The literal now lowers at the width it is COMPUTED at and reaches
    // the cell through the same `rt_fix_up` every other value stored to one goes through.
    new("module body: FIX arithmetic", """
      DIM v@
      v@ = 1.5@
      PRINT v@
      """, "main"),
    // An INTEGER widened straight to a QUAD. There were arms for 16->32 and 32->64 and this is
    // neither, so it declined - and only with the optimizer OFF, because instcombine otherwise splits
    // it into the two steps that exist. The sign matters and is what the negative helper value is
    // here for: a zero-extension would answer 65534 for -2.
    new("module body: INTEGER widened to QUAD", """
      DECLARE FUNCTION N%(BYVAL k%)
      DIM q AS QUAD
      q = N%(0)
      PRINT q
      END
      FUNCTION N%(BYVAL k%) NOINLINE
        N% = k% * 3 - 2
      END FUNCTION
      """, "main"),
    // ABS of a float clears the sign bit through an integer of the same width, so the lowering emits
    // a BitCast pair. That bit-twiddle is what --emit-c and --emit-llvm render, so the shape stays
    // and the selector learns it rather than the middle end being changed to suit one target: the
    // value is stored at SINGLE width - which is the only point its 32-bit pattern exists, an
    // intermediate float otherwise living in a TBYTE cell - and read back as a word pair.
    //
    // The negative input is the assertion. ABS of a positive number is the identity, so a broken
    // sign-clear passes on it.
    new("module body: ABS of a SINGLE", """
      DECLARE FUNCTION R!(BYVAL k%)
      PRINT ABS(R!(0)); ABS(R!(9))
      END
      FUNCTION R!(BYVAL k%) NOINLINE
        R! = (k% - 4) / 3
      END FUNCTION
      """, "main"),
    // The DOUBLE twin. Its AND against the sign mask is a 64-bit bitwise operation, which on a target
    // without an optimized 386 now reaches the direct emitter's own rt_qand/rt_qor/rt_qxor rather than
    // declining the whole module - the routine is the same one either path calls, so there is still
    // one shape for the operation in the image.
    new("module body: ABS of a DOUBLE", """
      DECLARE FUNCTION D#(BYVAL k%)
      DIM v AS DOUBLE
      v = D#(0)
      PRINT ABS(v)
      END
      FUNCTION D#(BYVAL k%) NOINLINE
        D# = (k% - 4) / 3
      END FUNCTION
      """, "main"),
    // QUAD add, subtract, multiply, divide and remainder. The first three go on the x87, which is
    // where the direct emitter does them too - an 80-bit mantissa carries a 64-bit integer exactly -
    // and the last two take the runtime's rt_qdiv/rt_qmod, because BASIC's \ truncates toward zero
    // and that is not an x87 operation. The negative operand is the assertion for the pair: it pins
    // the truncation direction and the remainder taking the DIVIDEND's sign.
    new("module body: QUAD arithmetic on a runtime value", """
      DECLARE FUNCTION N%(BYVAL k%)
      DIM q AS QUAD, r AS QUAD
      q = 1234567890
      q = q * 3 + N%(2)
      r = 0 - q
      PRINT q; r; q \ 5; q MOD 7; r \ 5; r MOD 7
      END
      FUNCTION N%(BYVAL k%) NOINLINE
        N% = k% * 3 - 2
      END FUNCTION
      """, "main"),
  ];

  /// <summary>
  /// Constructs the routing refuses, with the reason it recorded. Ordered the way the work is: the
  /// remaining ABI classes first (a parameter or result shape the routed calling sequence cannot
  /// express),
  /// then the calling conventions, then the two that are not about the ABI at all.
  ///
  /// <para>Every row must decline with the recorded reason and remain behaviorally equivalent to the
  /// direct build. A BASIC/PASCAL procedure may be emitted directly while its caller routes through
  /// their shared stack ABI; unsupported conventions still strand the caller.</para>
  /// </summary>
  private static readonly Construct[] _declines = [
    new("QUAD parameter and result", """
      FUNCTION F(BYVAL a&&) AS QUAD
        F = a&& + 1
      END FUNCTION
      PRINT F(1)
      """, "F", "filter: return type outside the routed ABI (QUAD)"),
    new("QUAD result alone", """
      FUNCTION F(BYVAL a%) AS QUAD
        F = a%
      END FUNCTION
      PRINT F(3)
      """, "F", "filter: return type outside the routed ABI (QUAD)"),
    new("BYTE parameter and result", """
      FUNCTION F(BYVAL a AS BYTE) AS BYTE
        F = a + 1
      END FUNCTION
      DIM b AS BYTE
      b = 3
      PRINT F(b)
      """, "F", "filter: return type outside the routed ABI (BYTE)"),
    new("BYTE result alone", """
      FUNCTION F(BYVAL a%) AS BYTE
        F = a%
      END FUNCTION
      PRINT F(3)
      """, "F", "filter: return type outside the routed ABI (BYTE)"),
    // BYREF, because BYVAL of a record is refused by the DIRECT emitter too ("not yet generated:
    // load of UdtType") - it is not a routing class at all, and a gate case that fails on both paths
    // would measure the front end. BYREF is how the corpus passes a record, and it is the shape the
    // routed ABI has no convention for.
    new("record parameter", """
      TYPE T
        a AS INTEGER
      END TYPE
      FUNCTION F(p AS T) AS INTEGER
        F = p.a
      END FUNCTION
      DIM q AS T
      q.a = 2
      PRINT F(q)
      """, "F", "filter: BYREF parameter (TYPE)"),
    new("FIX parameter", """
      FUNCTION F(BYVAL a@) AS INTEGER
        F = 1
      END FUNCTION
      DIM v@
      PRINT F(v@)
      """, "F", "filter: parameter type outside the routed ABI (FIX)"),
    new("EXT parameter", """
      FUNCTION F(BYVAL a##) AS INTEGER
        F = 1
      END FUNCTION
      DIM v##
      PRINT F(v##)
      """, "F", "filter: parameter type outside the routed ABI (EXT)"),
    new("EXT result", """
      FUNCTION F(BYVAL a%) AS EXT
        F = a%
      END FUNCTION
      PRINT F(3)
      """, "F", "filter: return type outside the routed ABI (EXT)"),
    new("CDECL convention", """
      SUB S CDECL (BYVAL a%)
        PRINT a%
      END SUB
      S 1
      """, "S", "filter: calling convention outside the routed ABI (Cdecl)"),
    new("STDCALL convention", """
      SUB S STDCALL (BYVAL a%)
        PRINT a%
      END SUB
      S 1
      """, "S", "filter: calling convention outside the routed ABI (Stdcall)"),
    new("FASTCALL convention", """
      SUB S FASTCALL (BYVAL a%)
        PRINT a%
      END SUB
      S 1
      """, "S", "filter: calling convention outside the routed ABI (Fastcall)"),
    new("WATCALL convention", """
      SUB S WATCALL (BYVAL a%)
        PRINT a%
      END SUB
      S 1
      """, "S", "filter: calling convention outside the routed ABI (Watcall)"),
    // not about the ABI: the direct path saves and restores the caller's handler triple around such
    // a body, and the routed prologue/epilogue has no equivalent bookkeeping. The module body is a
    // different case and DOES route with a handler armed (BackendMainRoutingTests).
    new("error handling in a procedure body", """
      SUB S
        ON ERROR GOTO H
        PRINT 1
        EXIT SUB
      H:
        RESUME NEXT
      END SUB
      S
      """, "S", "filter: error handling in a procedure body (ON ERROR / RESUME / TRY)"),
    // ...and not a filter at all: an array parameter stops the whole MODULE from lowering, which is
    // a level above the filter and costs the module body too
    new("array parameter", """
      SUB S(a%())
        PRINT a%(1)
      END SUB
      DIM v%(1 TO 2)
      v%(1) = 9
      S v%()
      """, "S", "lowering: call to unsupported procedure S"),
  ];

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Compiles the program twice, routed and direct, and reports what the routing did.</summary>
  private static (IReadOnlyList<string> Routed, string? Reason, bool ImagesIdentical,
    byte[] DirectImage, byte[] RoutedImage) Compile(Construct construct) {
    var generator = new CodeGenerator(Bind(construct.Source)) { Optimize = false, UseExperimentalBackend = true };
    var routedImage = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "routed: " + string.Join("; ", generator.Errors));
    var direct = new CodeGenerator(Bind(construct.Source)) { Optimize = false, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
    var reason = generator.BackendDeclines
      .Where(d => d.Name.Equals(construct.Subject, StringComparison.OrdinalIgnoreCase))
      .Select(d => d.Reason)
      .FirstOrDefault();
    return (generator.BackendRoutedNames.ToList(), reason, routedImage.SequenceEqual(directImage),
      directImage, routedImage);
  }

  [TestCaseSource(nameof(_routes))]
  public void Compile_GivenARoutedConstruct_WhenRoutingIsEnabled_ThenTheBackEndTakesItAndTheImageChanges(Construct construct) {
    var (routed, _, identical, _, _) = Compile(construct);

    Assert.Multiple(() => {
      Assert.That(routed.Contains(construct.Subject, StringComparer.OrdinalIgnoreCase), Is.True,
        $"'{construct.Subject}' no longer routes; the back end took: {string.Join(", ", routed)}");
      Assert.That(identical, Is.False,
        $"'{construct.Subject}' is named as routed but the image is byte-identical to the unrouted "
        + "build - the routing table says one thing and the emitted program another");
    });
  }

  [TestCaseSource(nameof(_declines))]
  public void Compile_GivenAConstructTheRoutingRefuses_WhenRoutingIsEnabled_ThenItSaysWhyAndRemainsEquivalent(
    Construct construct) {
    var (routed, reason, identical, directImage, routedImage) = Compile(construct);
    var direct = Cpu8086.Run(directImage);
    var mixed = Cpu8086.Run(routedImage);

    Assert.Multiple(() => {
      Assert.That(routed.Contains(construct.Subject, StringComparer.OrdinalIgnoreCase), Is.False,
        $"'{construct.Subject}' routes now - move this row into the routing list above, where it will "
        + "be held to routing rather than merely to declining");
      Assert.That(reason, Is.EqualTo(construct.Reason),
        $"'{construct.Subject}' still does not route, but for a different reason than recorded");
      if (routed.Count == 0)
        Assert.That(identical, Is.True,
          "when the back end takes no function, the executable must remain the direct build");
      Assert.That((mixed.Output, mixed.ExitCode), Is.EqualTo((direct.Output, direct.ExitCode)),
        $"'{construct.Subject}' declined, but the mixed routed/direct image changed behavior");
    });
  }
}
