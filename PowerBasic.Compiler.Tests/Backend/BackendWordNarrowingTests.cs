using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Selecting a 32-bit value the target can PROVE is word-sized into ONE word register - the
/// <see cref="InstructionSelector"/> narrowing whose proof obligation lives in <c>WordSizedRange</c>.
///
/// <para>
/// Several runtime rows take an argument the IR types i32 in a single word register, because the same
/// declaration also feeds the C back end where a character code is just an <c>int</c>. Until the
/// selector could reason about a computed value, only three shapes reached those rows: a literal, a
/// <c>sext</c>/<c>zext</c> straight off a 16-bit value, and a runtime answer the ABI table declares
/// widened. <c>CHR$(64 + i%)</c> is none of them - it arrives as <c>add i32 64, (sext i16 %i)</c> -
/// so it only selected when the OPTIMIZER had unrolled the loop until the argument was a constant.
/// That made the pipeline a prerequisite for SELECTION rather than for code quality, which is the
/// thing these tests exist to stop being true.
/// </para>
///
/// <para>
/// Both directions are pinned, because a narrowing that is wrong is a silent miscompile rather than a
/// crash: the cases that narrow, the cases that must not, and the boundary between them - which sits
/// exactly where the value stops fitting in sixteen bits.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendWordNarrowingTests {

  /// <summary>The i32-argument row this fixture probes: "Chr: DL=char -&gt; AX", an <c>ArgKind.Word</c> slot.</summary>
  private static IrFunction Chr() => new("rt_str_chr", IrType.Ptr, [new IrArgument(IrType.I32, 0)]);

  /// <summary>A function that hands <paramref name="build"/>'s value to that row, and nothing else.</summary>
  private static IrFunction CallChrWith(Func<IrBasicBlock, IrArgument, IrArgument, IrValue> build) {
    var a = new IrArgument(IrType.I16, 0);
    var b = new IrArgument(IrType.I16, 1);
    var fn = new IrFunction("F", IrType.Void, [a, b]);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Ptr, Chr(), [build(entry, a, b)]));
    entry.Append(new IrRet());
    return fn;
  }

  private static IrValue Widen(IrBasicBlock block, IrValue value)
    => block.Append(new IrCast(IrCastOp.SExt, value, IrType.I32));

  private static IrValue Add(IrBasicBlock block, IrValue lhs, long rhs)
    => block.Append(new IrBinary(IrBinaryOp.Add, lhs, new IrConstantInt(IrType.I32, rhs)));

  /// <summary>
  /// The case the narrowing exists for: a loop counter widened to i32 and offset by a constant. The
  /// interval is <c>[-32704, 32831]</c> - it overhangs the SIGNED word and still fits sixteen bits, so
  /// the low half is the whole value and exactly one word reaches <c>DL</c>.
  /// </summary>
  [Test]
  public void TrySelect_GivenAConstantOffsetOnAWidenedCounter_ThenOneWordReachesTheArgumentRegister() {
    var fn = CallChrWith((entry, a, _) => Add(entry, Widen(entry, a), 64));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var pinned = m!.AllInstructions
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .ToList();
    Assert.That(pinned, Has.Count.EqualTo(1), "the ABI row takes ONE register, so one move pins one");
    var dest = ((MOperand.Register)pinned[0].Operands[0]).Reg;
    Assert.That(dest.Physical, Is.EqualTo(Reg.DX));
    Assert.That(dest.Size, Is.EqualTo(MRegSize.Word), "narrowed, not half of a pair moved twice");
  }

  /// <summary>
  /// The same sum with the constant still WIDENED - <c>add i32 (sext i16 64), (sext i16 %a)</c> - which
  /// is how it comes out of the lowering, and how it stays until <c>instcombine</c> folds the cast.
  ///
  /// <para>
  /// The leaf used to contribute the whole SPAN of the type it widens whatever it was widening, so
  /// <c>sext i16 64</c> read as <c>[-32768, 32767]</c>, the sum reached <c>[-65536, 65534]</c> and the
  /// argument declined. It is the same program as the test above and the same proof; the only
  /// difference is which pass has run first, and selection is not allowed to depend on that
  /// (<c>WidenedRange</c>).
  /// </para>
  /// </summary>
  [Test]
  public void TrySelect_GivenTheOffsetStillWidened_ThenTheConstantContributesItsValueAndNotItsTypesSpan() {
    var fn = CallChrWith((entry, a, _) => entry.Append(new IrBinary(IrBinaryOp.Add,
      entry.Append(new IrCast(IrCastOp.SExt, new IrConstantInt(IrType.I16, 64), IrType.I32)),
      Widen(entry, a))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
  }

  /// <summary>
  /// The first of the two signedness mismatches, and the reason reading the operand's interval is
  /// guarded rather than unconditional. A <c>sext</c> of an UNSIGNED source does not reproduce its
  /// operand: the top bit it reads as a sign is a value bit, so a <c>WORD</c> holding 40000 comes out
  /// -25536. Believing the operand here would put the interval 65536 too high, and
  /// <c>40000 - 10000 = 30000</c> would look like a word where the truth, -35536, is not one - which
  /// is a value the caller would read as 30000 whichever sign it chose.
  ///
  /// <para>
  /// This one does not fail on the tree before <c>WidenedRange</c>: the old code could not get it
  /// wrong because it never looked at the operand at all. It pins the risk the change introduces, not
  /// the bug it fixed.
  /// </para>
  /// </summary>
  [Test]
  public void TrySelect_GivenASignExtendedUnsignedConstant_ThenTheOperandsIntervalIsNotBelieved() {
    var fn = CallChrWith((entry, _, __) => entry.Append(new IrBinary(IrBinaryOp.Add,
      entry.Append(new IrCast(IrCastOp.SExt, new IrConstantInt(IrType.U16, 40000), IrType.I32)),
      new IrConstantInt(IrType.I32, -10000))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Null, "sign-extending 40000 gives -25536, so the sum is -35536 and not 30000");
    Assert.That(reason, Does.Contain("the IR types it 32-bit"));
  }

  /// <summary>
  /// The mirror mismatch: a <c>zext</c> of a SIGNED source does not reproduce its operand either -
  /// -1 comes out 65535. Believing the operand would make <c>-1 + 1</c> read as <c>[0, 0]</c>, where
  /// the honest answer is <c>[1, 65536]</c> and overflows the word by exactly one.
  ///
  /// <para>
  /// Like its twin above, this passes on the tree before <c>WidenedRange</c> for the same reason, and
  /// is here for the same one.
  /// </para>
  /// </summary>
  [Test]
  public void TrySelect_GivenAZeroExtendedSignedConstant_ThenTheOperandsIntervalIsNotBelieved() {
    var fn = CallChrWith((entry, _, __) => entry.Append(new IrBinary(IrBinaryOp.Add,
      entry.Append(new IrCast(IrCastOp.ZExt, new IrConstantInt(IrType.I16, -1), IrType.I32)),
      new IrConstantInt(IrType.I32, 1))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Null, "zero-extending -1 gives 65535, so the sum is 65536 and not 0");
    Assert.That(reason, Does.Contain("the IR types it 32-bit"));
  }

  /// <summary>
  /// A mask, which is the other way a 32-bit value becomes word-sized: <c>x AND 255</c> is in
  /// <c>[0, 255]</c> whatever x is - even a LONG read out of memory, whose high half is real data.
  /// Narrowing here is sound because the AND has already discarded everything the narrowing would.
  /// </summary>
  [Test]
  public void TrySelect_GivenAMaskedLongLoad_ThenTheMaskIsWhatMakesItNarrow() {
    var fn = new IrFunction("F", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var cell = entry.Append(new IrAlloca(IrType.I32));
    var loaded = entry.Append(new IrLoad(IrType.I32, cell));
    var masked = entry.Append(new IrBinary(IrBinaryOp.And, loaded, new IrConstantInt(IrType.I32, 255)));
    entry.Append(new IrCall(IrType.Ptr, Chr(), [masked]));
    entry.Append(new IrRet());

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
  }

  /// <summary>
  /// The same LONG WITHOUT the mask: a genuine 32-bit quantity, whose high half is data and not sign.
  /// This is the case the whole rule is protecting - dropping the top word here answers with a
  /// different number, and the selector must decline rather than guess.
  /// </summary>
  [Test]
  public void TrySelect_GivenAGenuineLongLoad_ThenTheWordArgumentDeclines() {
    var fn = new IrFunction("F", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var cell = entry.Append(new IrAlloca(IrType.I32));
    var loaded = entry.Append(new IrLoad(IrType.I32, cell));
    entry.Append(new IrCall(IrType.Ptr, Chr(), [loaded]));
    entry.Append(new IrRet());

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Null, "a LONG read out of storage carries information in its high half");
    Assert.That(reason, Does.Contain("the IR types it 32-bit"));
  }

  /// <summary>
  /// A subtraction of two widened words, which BORROWS: <c>i% - j%</c> spans <c>[-65535, 65535]</c>,
  /// so <c>-30000 - 30000</c> is -60000 and its low word reads 5536. The addition of two widened words
  /// overflows the same way in the other direction; both must keep their register pair.
  /// </summary>
  [Test]
  public void TrySelect_GivenASubtractionOfTwoWidenedWords_ThenTheBorrowKeepsItFromNarrowing() {
    var fn = CallChrWith((entry, a, b) =>
      entry.Append(new IrBinary(IrBinaryOp.Sub, Widen(entry, a), Widen(entry, b))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Null, "the difference of two 16-bit values needs seventeen bits");
    Assert.That(reason, Does.Contain("the IR types it 32-bit"));
  }

  /// <summary>
  /// The boundary itself, from both sides. A widened counter plus 32768 tops out at 65535 - the last
  /// value one word holds - and narrows; plus 32769 tops out at 65536 and does not. Nothing between
  /// the two cases differs except the constant.
  /// </summary>
  [Test]
  public void TrySelect_GivenAnOffsetAtTheWordBoundary_ThenOneMoreDeclines() {
    var fits = InstructionSelector.TrySelect(CallChrWith((entry, a, _) => Add(entry, Widen(entry, a), 32768)),
      out var fitsReason);
    var overflows = InstructionSelector.TrySelect(CallChrWith((entry, a, _) => Add(entry, Widen(entry, a), 32769)),
      out _);

    Assert.That(fits, Is.Not.Null, $"[0, 65535] is exactly one word: {fitsReason}");
    Assert.That(overflows, Is.Null, "[1, 65536] is not");
  }

  /// <summary>
  /// The end-to-end proof, on a loop the optimizer cannot unroll away: forty characters computed as
  /// <c>CHR$(64 + i%)</c>, run through both back ends and compared. The values matter as much as the
  /// agreement - a narrowing that took the wrong half would still make the two paths agree if both
  /// were wrong, which they are not, because only one of them narrows anything.
  /// </summary>
  [Test]
  public void Run_GivenACharacterCodeComputedFromALoopCounter_ThenBothBackEndsPrintTheSameLetters() {
    const string source = """
      DIM s(1 TO 40) AS STRING
      FOR i% = 1 TO 40
        s(i%) = CHR$(64 + i%)
      NEXT i%
      FOR i% = 1 TO 26 : PRINT s(i%); : NEXT i%
      PRINT ""
      """;
    foreach (var optimize in new[] { true, false }) {
      var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
      var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
      var directImage = direct.EmitExecutable();
      var routedImage = routed.EmitExecutable();
      Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
        $"the back end did not take the module body, so nothing narrowed (optimize={optimize})");

      string Execute(byte[] image, string which) {
        try {
          return Cpu8086.Run(image).Output.Replace("\r", "");
        } catch (Cpu8086Exception e) {
          Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
          return "";
        }
      }

      var routedOutput = Execute(routedImage, "routed");
      Assert.That(routedOutput, Is.EqualTo(Execute(directImage, "direct")),
        $"the two back ends disagree (optimize={optimize})");
      Assert.That(routedOutput.Split('\n')[0], Is.EqualTo("ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        $"...and the answer both give is not the one BASIC gives (optimize={optimize})");
    }
  }

  /// <summary>
  /// What the narrowing buys beyond one program: selection no longer DEPENDS on the optimizer.
  ///
  /// <para>
  /// Every program <c>BackendArrayElementTests</c> runs is put through a deliberately weak pipeline -
  /// promote, combine, propagate, collect, tidy the CFG, and no unrolling - and still has to select
  /// and allocate. Four of the seven did not before, all four on the same
  /// <c>rt_str_chr takes a 32-bit value in a word register</c> decline: the full pipeline only got
  /// past it by unrolling each loop until the character code was a literal. A back end whose coverage
  /// is a function of how hard the optimizer tried is one whose coverage cannot be reasoned about.
  /// </para>
  ///
  /// <para>
  /// <see cref="IntegerRecovery"/> is NOT part of what is being weakened here, and runs on both sides
  /// of the reduced pipeline exactly as <c>CodeGenerator.BackendProcs</c> runs it. It is not an
  /// optimization: PB's integral arithmetic is float-shaped as it comes out of the lowering, and
  /// recovery is how the IR reaches the integer form at all.
  /// </para>
  /// </summary>
  [Test]
  public void TrySelect_GivenTheArrayElementProgramsAndAReducedPipeline_ThenTheyStillSelectAndAllocate() {
    string[] programs = [
      """
      DIM s(1 TO 4) AS STRING
      FOR i% = 1 TO 4
        s(i%) = CHR$(64 + i%) + "-" + CHR$(48 + i%)
      NEXT i%
      k% = 1
      k% = k% + 2
      PRINT s(k%)
      """,
      """
      DIM s(0 TO 3) AS STRING
      FOR i% = 0 TO 3 : s(i%) = "x" + CHR$(48 + i%) : NEXT i%
      PRINT s(0); s(3)
      """,
      """
      DIM t(-2 TO 2) AS STRING
      FOR i% = -2 TO 2 : t(i%) = "n" + CHR$(48 + i% + 2) : NEXT i%
      PRINT t(0)
      """,
      """
      DIM g(1 TO 2, 1 TO 3) AS STRING
      FOR r% = 1 TO 2
        FOR c% = 1 TO 3
          g(r%, c%) = CHR$(64 + r%) + CHR$(48 + c%)
        NEXT c%
      NEXT r%
      PRINT g(2, 3); g(1, 1)
      """,
    ];

    foreach (var program in programs) {
      var module = IrLowering.TryLowerModule(Bind(program), out var why);
      Assert.That(module, Is.Not.Null, why);
      for (var round = 0; round < 2; ++round) {
        foreach (var f in module!.Functions)
          if (!f.IsDeclaration)
            IntegerRecovery.Run(f);
        Reduced().RunOnModule(module);
      }

      var main = module!.Functions.Single(f => !f.IsDeclaration && f.Name == "main");
      var machine = InstructionSelector.TrySelect(main, out var reason);
      Assert.That(machine, Is.Not.Null, $"declined under the reduced pipeline: {reason}");
      MachineScheduler.Schedule(machine!);
      Assert.That(LinearScanAllocator.Allocate(machine!, out var noRegisters), Is.Not.Null, noRegisters);
    }
  }

  /// <summary>
  /// The same invariant one pass further down, and the one that says what the proof is FOR.
  ///
  /// <para>
  /// <see cref="IrPassManager.Legalize"/> is the set a routed <c>--no-optimize</c> build runs, and its
  /// own documentation justifies <c>instcombine</c> by exactly this program: without it
  /// <c>CHR$(64 + r%)</c> declines and the whole module body goes back to the direct emitter, 234
  /// functions of 262 instead of 258. That measurement is still true of the pass and no longer true of
  /// the SELECTOR - the argument arrives as <c>add i32 (sext i16 64), (sext i16 %r)</c> whether or not
  /// anything folded the cast, and the proof reads the constant either way.
  /// </para>
  ///
  /// <para>
  /// Which is the whole point: <c>instcombine</c> earns its place in the legalization set on its own
  /// merits, and selection is not allowed to be one of them. The subject comes from <c>INPUT</c> so
  /// there is no trip count to unroll and nothing to fold it away - the shape this fixture exists for.
  /// </para>
  /// </summary>
  [Test]
  public void TrySelect_GivenTheLegalizationSetWithoutInstCombine_ThenTheCharacterCodeStillSelects() {
    var module = IrLowering.TryLowerModule(Bind("INPUT r%\nPRINT CHR$(64 + r%)\nEND"), out var why);
    Assert.That(module, Is.Not.Null, why);
    for (var round = 0; round < 2; ++round) {
      foreach (var f in module!.Functions)
        if (!f.IsDeclaration)
          IntegerRecovery.Run(f);
      LegalizeWithoutCombining().RunOnModule(module);
    }

    var main = module!.Functions.Single(f => !f.IsDeclaration && f.Name == "main");
    var machine = InstructionSelector.TrySelect(main, out var reason);

    Assert.That(machine, Is.Not.Null, $"declined with no canonicalizer in front of it: {reason}");
  }

  /// <summary>
  /// <see cref="IrPassManager.Legalize"/> with its canonicalizer removed - the one pass whose absence
  /// leaves the widened constant standing for the selector to reason about.
  /// </summary>
  private static IrPassManager LegalizeWithoutCombining() => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);

  /// <summary>The optimizer with everything that rewrites loops or reassociates arithmetic taken out.</summary>
  private static IrPassManager Reduced() => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    .Add("instcombine", InstCombine.Run)
    .Add("sccp", Sccp.Run)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }
}
