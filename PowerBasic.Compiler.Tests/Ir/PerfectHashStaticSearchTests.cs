using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0335 — byte static searches must reach the word-sized backend dispatch selector.</summary>
[TestFixture]
public sealed class PerfectHashStaticSearchTests {

  /// <summary>
  /// Deliberately UNORDERED. A strictly ascending table of this many keys is lowered as a binary
  /// search instead, which needs no switch subject at all; the dispatch these tests are about is the
  /// one an unordered table still requires.
  /// </summary>
  private static readonly byte[] _hashKeys = [50, 16, 135, 67, 33, 118, 84, 101];

  [Test]
  public void StaticSearch_GivenUnsignedByteKeys_ThenSwitchSubjectIsZeroExtendedToWord() {
    var module = new IrModule("test");
    var fn = BuildSearch(module, "unsigned", IrType.U8, IrType.I8, _hashKeys);

    Assert.That(StaticSearchRecognition.Run(module), Is.EqualTo(1));
    var dispatch = fn.AllInstructions.OfType<IrSwitch>().Single();
    var extension = dispatch.Condition as IrCast;

    Assert.Multiple(() => {
      Assert.That(extension, Is.Not.Null);
      Assert.That(extension!.Op, Is.EqualTo(IrCastOp.ZExt));
      Assert.That(extension.Type, Is.EqualTo(IrType.U16));
      Assert.That(extension.Value.Type, Is.EqualTo(IrType.I8),
        "the table's unsigned interpretation, not the key's declared signedness, defines the extension");
      Assert.That(dispatch.Cases.Select(@case => @case.Value), Is.EqualTo(_hashKeys.Select(value => (long)value)));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void StaticSearch_GivenSignedByteKeys_ThenSwitchSubjectIsSignExtendedToWord() {
    byte[] keys = [18, 0x80, 103, 35, 1, 86, 52, 69];
    var module = new IrModule("test");
    var fn = BuildSearch(module, "signed", IrType.I8, IrType.U8, keys);

    Assert.That(StaticSearchRecognition.Run(module), Is.EqualTo(1));
    var dispatch = fn.AllInstructions.OfType<IrSwitch>().Single();
    var extension = dispatch.Condition as IrCast;

    Assert.Multiple(() => {
      Assert.That(extension, Is.Not.Null);
      Assert.That(extension!.Op, Is.EqualTo(IrCastOp.SExt));
      Assert.That(extension.Type, Is.EqualTo(IrType.I16));
      Assert.That(extension.Value.Type, Is.EqualTo(IrType.U8),
        "equality searched raw byte patterns, so the table determines how those patterns become word keys");
      Assert.That(dispatch.Cases.Select(@case => @case.Value), Does.Contain(-128));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

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
}
