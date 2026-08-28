using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Taking SSA apart on one CFG edge is a PARALLEL copy: every phi on the edge reads the values the
/// predecessor ENDS with, so writing the copies out one after another is only correct in an order
/// where no copy overwrites a register a later one still has to read.
///
/// <para>
/// Two shapes need saying separately. An acyclic edge always HAS such an order - <c>a &lt;- b</c>
/// beside <c>b &lt;- c</c> is fine written the other way round - and used to decline anyway, because
/// the test asked whether a source was any destination rather than whether the copies could be
/// ordered. A CYCLE has no such order at all, and needs one value held outside it while the rest move
/// over it; that is the loop-carried swap, and it is what <c>DIFF39</c> and <c>DIFF49</c> write with
/// the optimizer off.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendPhiSwapTests {

  /// <summary>
  /// <c>loop: x = phi[a, y]; y = phi[b, x]; br loop</c> - the two-value swap, the smallest cycle
  /// there is. Both phis are ordinary word values, so both are registers and neither can be written
  /// before the other is read.
  /// </summary>
  private static IrFunction SwapAcrossTheBackEdge() {
    var first = new IrArgument(IrType.I16, 0);
    var second = new IrArgument(IrType.I16, 1);
    var fn = new IrFunction("Swap", IrType.I16, [first, second]);
    var entry = fn.CreateBlock("entry");
    var loop = fn.CreateBlock("loop");
    var x = loop.AppendPhi(new IrPhi(IrType.I16));
    var y = loop.AppendPhi(new IrPhi(IrType.I16));
    entry.Append(new IrBr(loop));
    x.AddIncoming(first, entry);
    x.AddIncoming(y, loop);
    y.AddIncoming(second, entry);
    y.AddIncoming(x, loop);
    loop.Append(new IrCondBr(new IrCmp(IrCmpPred.Ne, x, new IrConstantInt(IrType.I16, 0)), loop, entry));
    return fn;
  }

  /// <summary>
  /// The same edge without a cycle: <c>x</c> takes <c>y</c>'s value and <c>y</c> takes a constant.
  /// One copy's source is the other's destination, so the old test declined it; ordering settles it
  /// without a temporary, and this pins that no scratch is minted for a case that does not need one.
  /// </summary>
  private static IrFunction ChainAcrossTheBackEdge() {
    var first = new IrArgument(IrType.I16, 0);
    var second = new IrArgument(IrType.I16, 1);
    var fn = new IrFunction("Chain", IrType.I16, [first, second]);
    var entry = fn.CreateBlock("entry");
    var loop = fn.CreateBlock("loop");
    var x = loop.AppendPhi(new IrPhi(IrType.I16));
    var y = loop.AppendPhi(new IrPhi(IrType.I16));
    entry.Append(new IrBr(loop));
    x.AddIncoming(first, entry);
    x.AddIncoming(y, loop);
    y.AddIncoming(second, entry);
    y.AddIncoming(new IrConstantInt(IrType.I16, 7), loop);
    loop.Append(new IrCondBr(new IrCmp(IrCmpPred.Ne, x, new IrConstantInt(IrType.I16, 0)), loop, entry));
    return fn;
  }

  /// <summary>Every register-to-register MOV the loop block ends with - the edge's copies.</summary>
  private static List<MInstr> EdgeCopies(MFunction machine)
    => [.. machine.Blocks.First(b => b.Label == "loop").Instructions
      .Where(i => i.Opcode == MOpcode.Mov
        && i.Operands is [MOperand.Register, MOperand.Register])];

  [Test]
  public void TrySelect_GivenTwoPhisThatSwapOnOneEdge_ThenAScratchRegisterBreaksTheCycle() {
    var machine = InstructionSelector.TrySelect(SwapAcrossTheBackEdge(), out var reason);

    Assert.That(machine, Is.Not.Null, $"declined: {reason}");
    var copies = EdgeCopies(machine!);
    // three, not two: one value is saved into a register outside the cycle before the swap runs
    Assert.That(copies, Has.Count.EqualTo(3), "a two-value swap costs one extra move and no more");

    // ...and the sequence really performs the exchange. Run it symbolically: each register starts
    // holding its own id, and the two the phis name must end holding each other's. This is the claim
    // an unsequenced parallel copy fails - it leaves both registers holding one of the two values -
    // and it is not the same as "no copy reads what an earlier one wrote", which the SAVE does on
    // purpose.
    var holds = new Dictionary<int, int>();
    int Initially(int register) => holds.TryGetValue(register, out var value) ? value : register;
    foreach (var copy in copies)
      holds[((MOperand.Register)copy.Operands[0]).Reg.VirtualId] =
        Initially(((MOperand.Register)copy.Operands[1]).Reg.VirtualId);

    var exchanged = holds.Where(pair => pair.Value != pair.Key
      && holds.TryGetValue(pair.Value, out var back) && back == pair.Key).ToList();
    Assert.That(exchanged, Has.Count.EqualTo(2),
      "the copies on the edge do not end up exchanging the two values the phis name");
  }

  [Test]
  public void TrySelect_GivenAnAcyclicEdge_ThenOrderingAloneSettlesItWithNoScratch() {
    var machine = InstructionSelector.TrySelect(ChainAcrossTheBackEdge(), out var reason);

    Assert.That(machine, Is.Not.Null, $"declined: {reason}");
    // x <- y is written first and y <- 7 second; the constant copy is not register-to-register, so
    // exactly one register-to-register move remains and no temporary was needed
    Assert.That(EdgeCopies(machine!), Has.Count.EqualTo(1),
      "an edge that only needed ordering must not mint a scratch register");
  }

  /// <summary>
  /// And the program the shape comes from. <c>DIFF39</c> and <c>DIFF49</c> are the corpus instances;
  /// this is the smallest one, and it is run rather than inspected because a swap that selects and
  /// then exchanges the wrong pair is exactly what an unsequenced parallel copy produces.
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenALoopCarriedSwap_WhenRouted_ThenItAgreesWithTheDirectBuild(bool optimize) {
    // the values come out of DATA, so nothing folds the loop away and prints the answer
    const string source = """
      DIM a AS INTEGER, b AS INTEGER, t AS INTEGER, i AS INTEGER
      READ a
      READ b
      FOR i = 1 TO 5
        t = a
        a = b
        b = t
      NEXT i
      PRINT a; b
      DATA 3, 8
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the module body did not route");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
      // five swaps of a pair is one swap
      Assert.That(string.Join(" ", routedCpu.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
        Is.EqualTo("8 3"));
    });
  }

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }
}
