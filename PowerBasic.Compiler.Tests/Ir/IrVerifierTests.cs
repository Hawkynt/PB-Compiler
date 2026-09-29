using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>The IR verifier: structural, SSA-dominance and type well-formedness.</summary>
[TestFixture]
public sealed class IrVerifierTests {

  [Test]
  public void Verify_GivenWellFormedAddFunction_ReportsNoErrors() {
    var a = new IrArgument(IrType.I32, 0, "a");
    var b = new IrArgument(IrType.I32, 1, "b");
    var fn = new IrFunction("add", IrType.I32, [a, b]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    builder.Ret(builder.Add(a, b));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Verify_GivenWellFormedDiamondWithPhi_ReportsNoErrors() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), t, e);
    new IrBuilder(t).Br(merge);
    new IrBuilder(e).Br(merge);
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(1), t);
    phi.AddIncoming(IrBuilder.ConstI32(2), e);
    bm.Ret(phi);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Verify_GivenBlockWithoutTerminator_ReportsError() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrBinary(IrBinaryOp.Add, IrBuilder.ConstI32(1), IrBuilder.ConstI32(2)));   // no terminator

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("does not end in a terminator"));
  }

  [Test]
  public void Verify_GivenOperandDefinedAfterUse_ReportsDominanceError() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var c = IrBuilder.ConstI32(1);
    var laterDef = new IrBinary(IrBinaryOp.Add, c, c);
    var use = new IrBinary(IrBinaryOp.Add, laterDef, c);          // uses a value defined below it
    entry.Append(use);
    entry.Append(laterDef);
    entry.Append(new IrRet());

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("defined after its use"));
  }

  [Test]
  public void Verify_GivenCrossBlockNonDominatingOperand_ReportsDominanceError() {
    // value defined in one diamond arm, used at the merge it does not dominate
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var b1 = fn.CreateBlock("b1");
    var b2 = fn.CreateBlock("b2");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), b1, b2);
    var bb1 = new IrBuilder(b1);
    var v = bb1.Add(IrBuilder.ConstI32(1), IrBuilder.ConstI32(2));   // defined only in b1
    bb1.Br(merge);
    new IrBuilder(b2).Br(merge);
    var bm = new IrBuilder(merge);
    bm.Add(v, v);                                                    // illegal use at merge
    bm.Ret();

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("does not dominate use"));
  }

  /// <summary>
  /// A block entered only by a NON-LOCAL jump - an error handler armed with its block address - uses an
  /// entry <c>alloca</c>. No CFG path reaches it, yet the entry has run before any block can: this is
  /// what <c>ON ERROR GOTO T ... T: x% = ERR</c> lowers to, and it is well-formed.
  /// </summary>
  [Test]
  public void Verify_GivenNonLocallyEnteredBlockUsingAnEntryValue_ReportsNoErrors() {
    var (fn, handler, entryValue, _) = NonLocalHandlerShape();
    var hb = new IrBuilder(handler);
    hb.Store(IrBuilder.ConstInt(IrType.I16, 5), entryValue);
    hb.Ret();

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  /// <summary>
  /// The other half, and the reason the rule is confined to the entry. A value defined in an ordinary
  /// block and used in the non-locally entered one has no path establishing what it holds when the
  /// jump lands - the stale-value bug the dominance check exists for - so it still fails.
  /// </summary>
  [Test]
  public void Verify_GivenNonLocallyEnteredBlockUsingANonEntryValue_ReportsDominanceError() {
    var (fn, handler, _, bodyValue) = NonLocalHandlerShape();
    var hb = new IrBuilder(handler);
    hb.Add(bodyValue, bodyValue);
    hb.Ret();

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("does not dominate use"));
  }

  /// <summary>entry defines an alloca and falls into body, which arms the handler by address and computes a value.</summary>
  private static (IrFunction Fn, IrBasicBlock Handler, IrValue EntryValue, IrValue BodyValue) NonLocalHandlerShape() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var body = fn.CreateBlock("body");
    var handler = fn.CreateBlock("handler");
    var eb = new IrBuilder(entry);
    var local = eb.Alloca(IrType.I16);
    eb.Br(body);
    var bb = new IrBuilder(body);
    var arm = new IrFunction("rt_onerr_arm", IrType.Void, [new IrArgument(IrType.Ptr, 0, "h")]);
    bb.Call(IrType.Void, arm, new IrBlockAddress(handler));
    var computed = bb.Add(IrBuilder.ConstI32(1), IrBuilder.ConstI32(2));
    bb.Ret();
    return (fn, handler, local, computed);
  }

  [Test]
  public void Verify_GivenTypeMismatchedBinary_ReportsTypeError() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrBinary(IrBinaryOp.Add, IrBuilder.ConstI32(1), IrBuilder.ConstInt(IrType.I16, 2)));
    entry.Append(new IrRet());

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("operand/result types disagree"));
  }

  [Test]
  public void Verify_GivenRetTypeMismatch_ReportsError() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(IrBuilder.ConstInt(IrType.I16, 1)));      // returns i16 from an i32 function

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("does not match function return type"));
  }

  [Test]
  public void Verify_GivenPhiPredecessorMismatch_ReportsError() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var other = fn.CreateBlock("other");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).Br(merge);
    new IrBuilder(other).Br(merge);                                  // 'other' is unreachable from entry
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(1), entry);
    phi.AddIncoming(IrBuilder.ConstI32(2), other);                   // not a reachable predecessor
    bm.Ret();

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("do not match its predecessors"));
  }

  [Test]
  public void Verify_GivenSwitchWithNonIntegerCondition_ReportsError() {
    var fn = SwitchFunction(IrType.F32, 1);

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("switch condition must be an integer"));
  }

  [Test]
  public void Verify_GivenSwitchCaseOutsideConditionWidth_ReportsError() {
    var fn = SwitchFunction(IrType.I16, ushort.MaxValue + 1L);

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("does not fit switch condition i16"));
  }

  [Test]
  public void Verify_GivenSwitchCasesWithDuplicateBitPatterns_ReportsError() {
    var fn = SwitchFunction(IrType.I16, -1, ushort.MaxValue);

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("duplicate switch case bit pattern"));
  }

  /// <summary>
  /// An operand whose definition is no longer in any block. It has no definition point, so no
  /// dominance question about it has an answer - and the verifier used to skip exactly this case in
  /// silence, alongside the constants and arguments that really do impose no constraint. That silence
  /// is what let an inlined body keep a reference to the callee's phi all the way to the back ends:
  /// while the callee still existed the dominance rule flagged it, and the moment <c>GlobalDce</c>
  /// removed the callee the operand's parent went null and the module verified clean.
  /// </summary>
  [Test]
  public void Verify_GivenAnOperandDetachedFromItsBlock_ReportsError() {
    var fn = new IrFunction("detached", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var value = b.Add(IrBuilder.ConstI32(1), IrBuilder.ConstI32(2));
    var reader = b.Add(value, IrBuilder.ConstI32(3));
    b.Ret();
    entry.Remove(value);                                             // detached, but 'reader' still names it
    Assert.That(reader.Lhs, Is.SameAs(value));

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("is detached"));
  }

  /// <summary>
  /// A float CONSTANT carrying an integer type. No target can produce one - there is no cell to stage
  /// it from - and a back end that names it anyway writes the double's bit pattern as an integer,
  /// which is what <c>1.5@</c> did: the FIX literal was built as the i64 CELL it is stored into rather
  /// than at the width it is computed at, and printed 4.6E+16.
  /// </summary>
  [Test]
  public void Verify_GivenAFloatConstantWithAnIntegerType_ReportsError() {
    var fn = new IrFunction("mistyped", IrType.Void);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Add(new IrConstantFloat(IrType.I64, 1.5), new IrConstantInt(IrType.I64, 1));
    b.Ret();

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("float constant carrying the non-float type"));
  }

  /// <summary>The same hole seen from the other side: the definition is in a block of ANOTHER function.</summary>
  [Test]
  public void Verify_GivenAnOperandDefinedInAnotherFunction_ReportsError() {
    var other = new IrFunction("other", IrType.Void);
    var otherEntry = other.CreateBlock("entry");
    var bo = new IrBuilder(otherEntry);
    var foreign = bo.Add(IrBuilder.ConstI32(1), IrBuilder.ConstI32(2));
    bo.Ret();

    var fn = new IrFunction("borrower", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Add(foreign, IrBuilder.ConstI32(3));
    b.Ret();

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("of another function"));
  }

  private static IrFunction SwitchFunction(IrType conditionType, params long[] cases) {
    var condition = new IrArgument(conditionType, 0, "condition");
    var fn = new IrFunction("switch_test", IrType.Void, [condition]);
    var entry = fn.CreateBlock("entry");
    var @default = fn.CreateBlock("default");
    new IrBuilder(@default).Ret();
    var sw = new IrSwitch(condition, @default);
    for (var i = 0; i < cases.Length; ++i) {
      var target = fn.CreateBlock($"case{i}");
      new IrBuilder(target).Ret();
      sw.AddCase(cases[i], target);
    }
    entry.Append(sw);
    return fn;
  }
}
