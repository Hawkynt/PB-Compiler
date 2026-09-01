using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0334/O0335/O0336 — constant-set search and byte-classification dispatch recovery.</summary>
[TestFixture]
public sealed class StaticDispatchOptimizationTests {

  [Test]
  public void SortedConstantSearch_GivenEightUniqueKeys_ThenItBecomesABalancedDecisionTree() {
    var module = new IrModule("test");
    var fn = BuildSearch(module, "sorted", [1, 3, 5, 7, 9, 11, 13, 15]);

    Assert.That(StaticSearchRecognition.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(fn.Blocks.Any(block => block.Label.StartsWith("bsearch.", StringComparison.Ordinal)), Is.True);
      Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(HasCycle(fn), Is.False, "the search loop should have become an acyclic decision tree");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void SortedConstantSearch_GivenASignedTableAndUnsignedCompatibleKey_ThenOrderingStaysSigned() {
    var module = new IrModule("test");
    var fn = BuildSearch(module, "signed", IrType.I8, IrType.U8, [0x80, 0xff, 0, 1, 2, 3, 4, 5]);

    Assert.That(StaticSearchRecognition.Run(module), Is.EqualTo(1));
    var ordering = fn.Blocks
      .Where(block => block.Label.StartsWith("bsearch.order.", StringComparison.Ordinal))
      .SelectMany(block => block.Instructions.OfType<IrCmp>())
      .ToList();
    Assert.Multiple(() => {
      Assert.That(ordering, Is.Not.Empty);
      Assert.That(ordering, Has.All.Matches<IrCmp>(comparison => comparison.Pred == IrCmpPred.Slt),
        "the table's signed ordering defines the binary-search relation even when the key type is unsigned");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void UnsortedConstantSearch_GivenAUniqueStaticSet_ThenItBecomesVerifiedSwitchDispatch() {
    var module = new IrModule("test");
    var fn = BuildSearch(module, "set", [17, 2, 91, 4, 33]);

    Assert.That(StaticSearchRecognition.Run(module), Is.EqualTo(1));
    var dispatch = fn.AllInstructions.OfType<IrSwitch>().Single();
    Assert.Multiple(() => {
      Assert.That(dispatch.Cases.Count, Is.EqualTo(5));
      Assert.That(dispatch.DefaultTarget.Terminator, Is.InstanceOf<IrRet>());
      Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void StaticSearch_GivenDuplicateKeys_ThenFirstMatchSemanticsPreventRewriting() {
    var module = new IrModule("test");
    var fn = BuildSearch(module, "duplicates", [1, 4, 4, 7, 9, 12, 20, 30]);

    Assert.That(StaticSearchRecognition.Run(module), Is.Zero);
    Assert.That(HasCycle(fn), Is.True, "the declined linear search must retain its counted loop");
  }

  [Test]
  public void ByteClassificationChain_GivenThreeConstantClasses_ThenSwitchFormationRecoversTheFsmDispatch() {
    var c = new IrArgument(IrType.U8, 0, "c");
    var fn = new IrFunction("classify", IrType.I16, [c]);
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var second = fn.AddBlock(new IrBasicBlock("second"));
    var third = fn.AddBlock(new IrBasicBlock("third"));
    var digit = fn.AddBlock(new IrBasicBlock("digit"));
    var space = fn.AddBlock(new IrBasicBlock("space"));
    var comma = fn.AddBlock(new IrBasicBlock("comma"));
    var other = fn.AddBlock(new IrBasicBlock("other"));

    var isDigit = head.Append(new IrCmp(IrCmpPred.Eq, c, new IrConstantInt(IrType.U8, (byte)'0')));
    head.Append(new IrCondBr(isDigit, digit, second));
    var isSpace = second.Append(new IrCmp(IrCmpPred.Eq, c, new IrConstantInt(IrType.U8, (byte)' ')));
    second.Append(new IrCondBr(isSpace, space, third));
    var isComma = third.Append(new IrCmp(IrCmpPred.Eq, c, new IrConstantInt(IrType.U8, (byte)',')));
    third.Append(new IrCondBr(isComma, comma, other));
    digit.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    space.Append(new IrRet(new IrConstantInt(IrType.I16, 2)));
    comma.Append(new IrRet(new IrConstantInt(IrType.I16, 3)));
    other.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(SwitchFormation.Run(fn), Is.EqualTo(1));
    Assert.That(head.Terminator, Is.InstanceOf<IrSwitch>());
    Assert.That(((IrSwitch)head.Terminator!).Cases.Count, Is.EqualTo(3));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private static IrFunction BuildSearch(IrModule module, string name, byte[] keys)
    => BuildSearch(module, name, IrType.U8, IrType.U8, keys);

  private static IrFunction BuildSearch(IrModule module, string name, IrType tableType, IrType keyType, byte[] keys) {
    var table = module.AddGlobal(new IrGlobalVariable($"{name}.keys", tableType) {
      Bytes = keys,
      Count = keys.Length,
      IsZeroInitialized = false,
    });
    var key = new IrArgument(keyType, 0, "key");
    var fn = module.AddFunction(new IrFunction(name, IrType.I16, [key]));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var header = fn.AddBlock(new IrBasicBlock("header"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var found = fn.AddBlock(new IrBasicBlock("found"));
    var latch = fn.AddBlock(new IrBasicBlock("latch"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));

    pre.Append(new IrBr(header));
    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    var inRange = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, keys.Length)));
    header.Append(new IrCondBr(inRange, body, exit));
    var at = body.Append(new IrGep(table, counter, tableType));
    var current = body.Append(new IrLoad(tableType, at));
    var equal = body.Append(new IrCmp(IrCmpPred.Eq, current, key));
    body.Append(new IrCondBr(equal, found, latch));
    found.Append(new IrRet(counter));
    var next = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    latch.Append(new IrBr(header));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), pre);
    counter.AddIncoming(next, latch);
    exit.Append(new IrRet(new IrConstantInt(IrType.I16, -1)));
    return fn;
  }

  private static bool HasCycle(IrFunction fn) {
    var visited = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var active = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    foreach (var block in fn.Blocks)
      if (Visit(block))
        return true;
    return false;

    bool Visit(IrBasicBlock block) {
      if (visited.Contains(block))
        return false;
      if (!active.Add(block))
        return true;
      foreach (var successor in block.Successors)
        if (Visit(successor))
          return true;
      active.Remove(block);
      visited.Add(block);
      return false;
    }
  }
}
