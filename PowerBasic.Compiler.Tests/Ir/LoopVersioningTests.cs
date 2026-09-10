using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0306 — guarded loop versioning. The original checked loop remains the fallback while the cloned
/// fast path may drop generated bounds/overflow checks and consume alias/alignment facts only after
/// the shared preheader guard proves them for the complete induction range.
/// </summary>
[TestFixture]
public sealed class LoopVersioningTests {

  private static IrFunction Lowered(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(f => f.Name.Equals("Work", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);
    // PB computes integral +/-/* in floating point for display precision, so a subscript such as
    // i + 1 lowers to fptosi.round(fadd(sitofp i, 1.0)) whatever the integer width. The routing runs
    // IntegerRecovery before the optimizer for exactly that reason (see CodeGenerator.Backend), and
    // it leaves the float form standing beside the recovered integer one - so DCE has to follow, or
    // the loop still holds the float shadow this pass is not asked to reason about.
    IntegerRecovery.Run(fn);
    Dce.Run(fn);
    Licm.Run(fn);
    return fn;
  }

  private static IEnumerable<IrCondBr> ErrorGuards(IrFunction fn, int errorCode, bool fast) {
    foreach (var block in fn.Blocks) {
      if (block.Label.StartsWith("ver.fast.", StringComparison.Ordinal) != fast
          || block.Terminator is not IrCondBr branch)
        continue;
      if (branch.IfTrue.Instructions.OfType<IrCall>().Any(call =>
            call.Callee is IrFunction { Name: "rt_error" }
            && call.Args.SingleOrDefault() is IrConstantInt code
            && code.ZeroExtended == (ulong)errorCode))
        yield return branch;
    }
  }

  private static (IrFunction Function, IrLoad Load, IrStore Store) RuntimeAliasingLoop() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var destination = new IrArgument(IrType.Ptr, 1, "destination");
    var limit = new IrArgument(IrType.I16, 2, "limit");
    var fn = new IrFunction("work", IrType.Void, [source, destination, limit]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("loop.head");
    var body = fn.CreateBlock("loop.body");
    var latch = fn.CreateBlock("loop.latch");
    var exit = fn.CreateBlock("loop.exit");

    entry.Append(new IrBr(header));
    var counter = header.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var inRange = header.Append(new IrCmp(IrCmpPred.Sle, counter, limit));
    header.Append(new IrCondBr(inRange, body, exit));

    var byteOffset = body.Append(new IrBinary(
      IrBinaryOp.Mul,
      counter,
      new IrConstantInt(IrType.I16, 2)));
    var sourceAddress = body.Append(new IrGep(source, byteOffset));
    var load = body.Append(new IrLoad(IrType.I16, sourceAddress));
    var destinationAddress = body.Append(new IrGep(destination, byteOffset));
    var store = body.Append(new IrStore(load, destinationAddress));
    body.Append(new IrBr(latch));

    var next = latch.Append(new IrBinary(
      IrBinaryOp.Add,
      counter,
      new IrConstantInt(IrType.I16, 1)));
    latch.Append(new IrBr(header));
    exit.Append(new IrRet());

    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    counter.AddIncoming(next, latch);
    return (fn, load, store);
  }

  private const string _ascending = """
    $ERROR BOUNDS ON
    SUB Work(BYVAL n%)
      DIM a%(0 TO 31), i%
      FOR i% = 0 TO n%
        a%(i%) = i%
      NEXT
    END SUB
    """;

  [Test]
  public void Loop_GivenRuntimeUpperBoundAndBoundsCheck_ThenItIsVersioned() {
    var fn = Lowered(_ascending);
    var originalBlocks = fn.Blocks.ToHashSet();

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(originalBlocks.All(fn.Blocks.Contains), Is.True,
      "the original checked loop is the fallback and must not be replaced");
    Assert.That(fn.Blocks.Any(block => block.Label.StartsWith("ver.fast.", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty, "versioning must leave valid SSA/CFG");
  }

  [Test]
  public void FastLoop_GivenTheGuardProvesItsRange_ThenBoundsBranchesAreSpecializedFalse() {
    var fn = Lowered(_ascending);
    Assume.That(LoopVersioning.Run(fn), Is.EqualTo(1));

    Assert.That(ErrorGuards(fn, 9, fast: true).Any(branch => branch.Condition is IrConstantInt { IsZero: true }), Is.True,
      "at least one cloned Error 9 branch must be disabled by the preheader proof");
    Assert.That(ErrorGuards(fn, 9, fast: false).Any(branch => branch.Condition is not IrConstantInt { IsZero: true }), Is.True,
      "the checked fallback must retain its generated bounds branch");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenDescendingUnitStep_ThenItIsVersioned() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n%)
        DIM a%(0 TO 31), i%
        FOR i% = n% TO 0 STEP -1
          a%(i%) = i%
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenEscapingCounter_ThenTheVersionsAreJoinedAtTheExit() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n%)
        DIM a%(0 TO 31), i%
        FOR i% = 0 TO n%
          a%(i%) = i%
        NEXT
        PRINT i%
      END SUB
      """);
    var phisBefore = fn.AllInstructions.OfType<IrPhi>().ToHashSet();

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty,
      "a counter read after the loop needs an exit join for the checked and fast definitions");
    Assert.That(fn.AllInstructions.OfType<IrPhi>().Any(phi =>
        !phisBefore.Contains(phi)
        && phi.Parent is { } parent
        && !parent.Label.StartsWith("ver.fast.", StringComparison.Ordinal)), Is.True,
      "versioning must create a non-clone phi that joins an escaping value from both loop versions");
  }

  [Test]
  public void Loop_GivenTheFastGuardIsCompileTimeFalse_ThenItIsNotVersioned() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work()
        DIM a%(0 TO 31), i%
        FOR i% = 0 TO 40
          a%(i%) = i%
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.Zero,
      "a statically unreachable fast path would only be cloned and removed again on every fixpoint sweep");
    Assert.That(fn.Blocks.Any(block => block.Label.StartsWith("ver.fast.", StringComparison.Ordinal)), Is.False);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenOneBoundsGuardWasAlreadyProvedSafe_ThenTheRemainingGuardCanStillBeVersioned() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n%)
        DIM a%(0 TO 31), i%
        FOR i% = 0 TO n%
          a%(i%) = i%
          a%(0) = 1
        NEXT
      END SUB
      """);

    Assert.That(RangeCheckElim.Run(fn), Is.GreaterThan(0),
      "the earlier range pass should decide at least the constant subscript guard");
    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1),
      "an already-false Error 9 guard is residue for CFG cleanup, not a reason to reject the loop");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenSourceError9Branch_ThenItIsNotTreatedAsACompilerBoundsCheck() {
    var fn = Lowered("""
      SUB Work(BYVAL n%)
        DIM i%
        FOR i% = 0 TO n%
          IF i% < 0 THEN ERROR 9
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.Zero,
      "source control flow carries IsSourceCondition and must not be reclassified as a generated guard");
  }

  [Test]
  public void Loop_GivenNonUnitStep_ThenEndpointProofVersionsIt() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n%)
        DIM a%(0 TO 31), i%
        FOR i% = 0 TO n% STEP 2
          a%(i%) = i%
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(ErrorGuards(fn, 9, fast: true).Any(branch => branch.Condition is IrConstantInt { IsZero: true }), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenRuntimeStep_ThenDirectionAndNoWrapJoinTheCombinedGuard() {
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n%, BYVAL stride%)
        DIM a%(0 TO 31), i%
        FOR i% = 0 TO n% STEP stride%
          a%(i%) = i%
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(ErrorGuards(fn, 9, fast: true).Any(branch => branch.Condition is IrConstantInt { IsZero: true }), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty,
      "runtime-step direction and no-wrap predicates must still leave the versioned CFG in valid SSA");
  }

  [Test]
  public void Loop_GivenAffineSubscript_ThenEndpointProofVersionsIt() {
    // LONG so the recovered integer tree is i32 throughout: the shape O0306 proves is
    // counter + invariant constant over one width.
    var fn = Lowered("""
      $ERROR BOUNDS ON
      SUB Work(BYVAL n&)
        DIM a%(0 TO 31), i&
        FOR i& = 0 TO n&
          a%(i& + 1) = 1
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(ErrorGuards(fn, 9, fast: true).Any(branch => branch.Condition is IrConstantInt { IsZero: true }), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FastLoop_GivenAffineOverflowCheck_ThenError6IsGuardedOnceAndSpecializedAway() {
    var fn = Lowered("""
      $ERROR OVERFLOW ON
      SUB Work(BYVAL n%, BYVAL bias%)
        DIM i%, value%
        FOR i% = 0 TO n%
          value% = i% + bias%
        NEXT
      END SUB
      """);

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));
    Assert.That(ErrorGuards(fn, 6, fast: true).Any(branch => branch.Condition is IrConstantInt { IsZero: true }), Is.True,
      "the fast clone may remove Error 6 only after its endpoint range guard succeeds");
    Assert.That(ErrorGuards(fn, 6, fast: false).Any(branch => branch.Condition is not IrConstantInt { IsZero: true }), Is.True,
      "the fallback retains the original per-iteration overflow trap");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FastMemoryAccesses_GivenAliasAndAlignmentGuard_ThenLoadsAndStoresReceiveFacts() {
    var (fn, originalLoad, originalStore) = RuntimeAliasingLoop();
    Assert.That(IrVerifier.Verify(fn), Is.Empty, "the hand-built loop must be valid before versioning");

    Assert.That(LoopVersioning.Run(fn), Is.EqualTo(1));

    var fastInstructions = fn.Blocks
      .Where(block => block.Label.StartsWith("ver.fast.", StringComparison.Ordinal))
      .SelectMany(block => block.Instructions)
      .ToList();
    var fastLoad = fastInstructions.OfType<IrLoad>().Single();
    var fastStore = fastInstructions.OfType<IrStore>().Single();

    Assert.Multiple(() => {
      Assert.That(LoopVersioningMemoryFacts.AlignmentOf(originalLoad), Is.EqualTo(1),
        "guard facts belong to the clone, never the fallback");
      Assert.That(LoopVersioningMemoryFacts.AlignmentOf(originalStore), Is.EqualTo(1));
      Assert.That(LoopVersioningMemoryFacts.NoAliasGroupOf(originalLoad), Is.Zero);
      Assert.That(LoopVersioningMemoryFacts.NoAliasGroupOf(originalStore), Is.Zero);

      Assert.That(LoopVersioningMemoryFacts.AlignmentOf(fastLoad), Is.EqualTo(2));
      Assert.That(LoopVersioningMemoryFacts.AlignmentOf(fastStore), Is.EqualTo(2),
        "void stores must participate in IrCloner's source-to-clone map so guarded facts reach them");
      Assert.That(LoopVersioningMemoryFacts.NoAliasGroupOf(fastLoad), Is.GreaterThan(0));
      Assert.That(LoopVersioningMemoryFacts.NoAliasGroupOf(fastStore), Is.GreaterThan(0));
      Assert.That(LoopVersioningMemoryFacts.NoAliasGroupOf(fastLoad),
        Is.Not.EqualTo(LoopVersioningMemoryFacts.NoAliasGroupOf(fastStore)),
        "the runtime disjoint-range check assigns different roots different no-alias groups");
    });
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Loop_GivenItWasAlreadyVersioned_ThenASecondRunDoesNothing() {
    var fn = Lowered(_ascending);
    Assume.That(LoopVersioning.Run(fn), Is.EqualTo(1));

    Assert.That(LoopVersioning.Run(fn), Is.Zero,
      "the chooser is now the preheader, so neither clone may recursively version itself");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
