namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// A segment and an offset as one flat address, for a target with no segments.
///
/// <para>
/// <c>DEF SEG</c> with <c>PEEK</c> and <c>POKE</c>, and a <c>DIM ... AT segment</c> array, name memory
/// by real-mode segment and offset: the byte at <c>segment * 16 + offset</c> of the first megabyte.
/// The 6502 takes that address as its own, wrapped to its 64 KB - so <c>POKE 53280, 0</c> after the
/// default <c>DEF SEG</c> of 0 is the C64's border colour, and a screen array is <c>DIM AT &amp;H40</c>.
/// A hosted program cannot reach absolute memory at all, so it gets a megabyte of its own standing in
/// for DOS's: whatever one route writes there, every other route to the same segment and offset reads
/// back, which is all a program can observe of it.
/// </para>
/// </summary>
public static class FarPointerFlattening {

  /// <summary>The hosted stand-in for the first megabyte.</summary>
  public const string ConventionalMemory = "rt.conventional";

  public static void Run(IrModule module, bool conventionalMemory) {
    IrGlobalVariable? memory = null;
    foreach (var function in module.Functions.Where(f => !f.IsDeclaration))
      foreach (var block in function.Blocks)
        foreach (var far in block.Instructions.OfType<IrFarPtr>().ToList()) {
          T Before<T>(T instruction) where T : IrInstruction => block.InsertBefore(instruction, far);
          var segment = Before(new IrCast(IrCastOp.ZExt, far.Segment, IrType.I32));
          var offset = Before(new IrCast(IrCastOp.ZExt, far.Offset, IrType.I32));
          var paragraphs = Before(new IrBinary(IrBinaryOp.Mul, segment, new IrConstantInt(IrType.I32, 16)));
          var linear = Before(new IrBinary(IrBinaryOp.Add, paragraphs, offset));
          IrValue address;
          if (conventionalMemory) {
            memory ??= module.FindGlobal(ConventionalMemory)
              ?? module.AddGlobal(new IrGlobalVariable(ConventionalMemory, IrType.I8) { Count = 1 << 20, IsZeroInitialized = true });
            // the 8086's twenty address lines: FFFF:0010 is byte 0 again
            var wrapped = Before(new IrBinary(IrBinaryOp.And, linear, new IrConstantInt(IrType.I32, (1 << 20) - 1)));
            address = Before(new IrGep(memory, wrapped));
          } else {
            var word = Before(new IrCast(IrCastOp.Trunc, linear, IrType.U16));
            address = Before(new IrCast(IrCastOp.IntToPtr, word, IrType.Ptr));
          }
          far.ReplaceAllUsesWith(address);
          far.DropOperandUses();
          block.Remove(far);
        }
  }
}
