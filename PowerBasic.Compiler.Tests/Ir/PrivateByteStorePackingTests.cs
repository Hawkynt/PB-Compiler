using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class PrivateByteStorePackingTests {
  private static (IrFunction Function, IrBuilder Builder, IrAlloca Buffer) Create(int count = 8) {
    var function = new IrFunction("f", IrType.Void);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var buffer = builder.Block!.Append(new IrAlloca(IrType.I8) { Count = count });
    buffer.IsSourceVariable = true;
    return (function, builder, buffer);
  }

  private static IrStore Write(IrBuilder builder, IrAlloca buffer, int index, byte value)
    => builder.Store(new IrConstantInt(IrType.I8, value),
      builder.Gep(buffer, new IrConstantInt(IrType.I32, index)));

  private static IrStore[] Writes(IrFunction function) => function.AllInstructions.OfType<IrStore>().ToArray();

  [Test]
  public void Pack_GivenSequentialArrayBytesOn8086_ThenBuildsTwoLittleEndianWords() {
    var (fn, builder, buffer) = Create();
    Write(builder, buffer, 0, 1);
    Write(builder, buffer, 1, 2);
    Write(builder, buffer, 2, 3);
    Write(builder, buffer, 3, 4);
    builder.Ret();

    var changed = PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I8086, CostObjective.Balanced));

    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(2));
      Assert.That(Writes(fn).Select(store => store.Value.Type.Bits), Is.EqualTo(new[] { 16, 16 }));
      Assert.That(Writes(fn).Select(store => ((IrConstantInt)store.Value).ZeroExtended),
        Is.EqualTo(new ulong[] { 0x0201, 0x0403 }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Pack_GivenReversedByteWritesAndAProvenDisjointRead_ThenReordersIntoOne386Dword() {
    var (fn, builder, buffer) = Create();
    var independent = builder.Alloca(IrType.I8);
    builder.Store(new IrConstantInt(IrType.I8, 99), independent);
    Write(builder, buffer, 2, 3);
    var read = builder.Load(IrType.I8, independent);
    builder.Store(read, independent); // Keep the disjoint read observably used.
    Write(builder, buffer, 0, 1);
    Write(builder, buffer, 3, 4);
    Write(builder, buffer, 1, 2);
    builder.Ret();

    var changed = PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I80386, CostObjective.Balanced));
    var packet = Writes(fn).Single(store => store.IsPackedPrivateWrite);

    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(1));
      Assert.That(packet.Value.Type.Bits, Is.EqualTo(32));
      Assert.That(((IrConstantInt)packet.Value).ZeroExtended, Is.EqualTo(0x04030201u));
      Assert.That(read.Parent, Is.Not.Null, "the proven-disjoint observation must remain");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Select_GivenPackedPrivateArrayOnBalanced386_ThenEmitsOneDwordStore() {
    var (fn, builder, buffer) = Create();
    Write(builder, buffer, 3, 4);
    Write(builder, buffer, 1, 2);
    Write(builder, buffer, 0, 1);
    Write(builder, buffer, 2, 3);
    builder.Ret();
    Assert.That(PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.EqualTo(1));
    var machine = InstructionSelector.TrySelect(fn, out var decline,
      new SelectionTarget(Optimize: true, CpuLevel: 386));
    Assert.That(machine, Is.Not.Null, decline);
    Assert.That(machine!.AllInstructions.Count(instr => instr.Opcode == MOpcode.Mov
      && instr.Operands is [
        MOperand.Memory { Size: MRegSize.Dword },
        MOperand.Immediate { Value: 0x04030201 }
      ]), Is.EqualTo(1), "the 386 selector must preserve the optimized packet width");
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenOutOfOrderPb35ByteArrayWrites_ThenPreservesAllValues(bool optimize) {
    const string source = """
      DIM data(0 TO 3) AS BYTE
      data(2) = 3
      data(0) = 1
      data(3) = 4
      data(1) = 2
      PRINT data(0); data(1); data(2); data(3)
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "ARRAY.BAS", Dialect.Pb35),
      "ARRAY.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var compiler = new CodeGenerator(model) { Optimize = optimize };
    var image = compiler.EmitExecutable();
    Assert.That(compiler.Errors, Is.Empty, string.Join("; ", compiler.Errors));
    Assert.That(compiler.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).Output, Does.Contain("1  2  3  4"));
  }

  [Test]
  public void Pack_GivenIntermediateReadOfTheNextByte_ThenDoesNotMoveTheFutureWrite() {
    var (fn, builder, buffer) = Create();
    Write(builder, buffer, 0, 1);
    var observed = builder.Load(IrType.I8,
      builder.Gep(buffer, new IrConstantInt(IrType.I32, 1)));
    Write(builder, buffer, 1, 2);
    builder.Ret();

    Assert.Multiple(() => {
      Assert.That(PrivateByteStorePacking.Run(fn,
        new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.Zero);
      Assert.That(Writes(fn), Has.Length.EqualTo(2));
      Assert.That(observed.Parent, Is.Not.Null);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Pack_GivenUnknownIntermediateReadOffset_ThenLeavesWritesSeparate() {
    var fn = new IrFunction("f", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var buffer = builder.Block!.Append(new IrAlloca(IrType.I8) { Count = 8 });
    Write(builder, buffer, 0, 1);
    builder.Load(IrType.I8, builder.Gep(buffer, fn.Parameters[0]));
    Write(builder, buffer, 1, 2);
    builder.Ret();

    Assert.That(PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Pack_GivenCallOrEscapedBuffer_ThenLeavesStoresSeparate() {
    var (fn, builder, buffer) = Create();
    var opaque = new IrFunction("opaque", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);
    Write(builder, buffer, 0, 1);
    builder.Call(IrType.Void, opaque, buffer);
    Write(builder, buffer, 1, 2);
    builder.Ret();

    Assert.That(PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.Zero);
    Assert.That(Writes(fn), Has.Length.EqualTo(2));
  }

  [Test]
  public void Pack_GivenAbsoluteSegmentArray_ThenNeverAssumesItIsPrivateRam() {
    var fn = new IrFunction("f", IrType.Void);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var pinned = builder.FarPtr(new IrConstantInt(IrType.I16, unchecked((short)0xB800)),
      new IrConstantInt(IrType.I16, 0));
    builder.Store(new IrConstantInt(IrType.I8, 1), pinned);
    var next = builder.FarPtr(new IrConstantInt(IrType.I16, unchecked((short)0xB800)),
      new IrConstantInt(IrType.I16, 1));
    builder.Store(new IrConstantInt(IrType.I8, 2), next);
    builder.Ret();

    Assert.Multiple(() => {
      Assert.That(PrivateByteStorePacking.Run(fn,
        new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.Zero);
      Assert.That(Writes(fn).Select(store => store.Value.Type.Bits), Is.EqualTo(new[] { 8, 8 }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Pack_GivenPartialObjectBoundary_ThenDoesNotWriteOutsideTheBuffer() {
    var (fn, builder, buffer) = Create(3);
    Write(builder, buffer, 1, 0xAA);
    Write(builder, buffer, 2, 0xBB);
    builder.Ret();

    Assert.That(PrivateByteStorePacking.Run(fn,
      new TargetCost(CpuTier.I80386, CostObjective.Balanced)), Is.EqualTo(1));
    Assert.That(Writes(fn).Single().Value.Type.Bits, Is.EqualTo(16));
    Assert.That(((IrConstantInt)Writes(fn).Single().Value).ZeroExtended, Is.EqualTo(0xBBAAu));
  }
}
