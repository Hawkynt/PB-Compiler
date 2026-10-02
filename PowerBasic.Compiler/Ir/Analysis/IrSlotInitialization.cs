namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Which stack slots can be observed before the function itself has written them - the ones whose
/// zero start PowerBASIC's semantics still has to provide.
///
/// <para>
/// Every local starts at zero, and the frame used to be zeroed whole to guarantee it: a
/// <c>REP STOSW</c> over every slot, fourteen bytes of prologue in any function that had one. Most
/// slots never need it. A variable assigned before it is read, a temporary the lowering stores into
/// first, a spill slot - none of them can show the zero, so zeroing them is only cost.
/// </para>
///
/// <para>
/// The question is a forward MUST analysis: a slot is safe when, on every path from the entry, a store
/// of its whole type reaches it before anything reads it or lets its address go anywhere. A load is
/// a read. Every other use of the address - a GEP into it, a call it is passed to, a store of the
/// address itself - is an escape, and counts as a read at that point because the receiver may read.
/// A function with an error handler or inline assembly has control or memory edges the CFG does not
/// show, so all of its slots keep their zero start.
/// </para>
/// </summary>
public static class IrSlotInitialization {

  /// <summary>The allocas of <paramref name="function"/> that may be observed before being written.</summary>
  public static IReadOnlySet<IrAlloca> NeedingZeroStart(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var allocas = function.AllInstructions.OfType<IrAlloca>().ToList();
    if (function.HasErrorHandler || function.HasInlineAsm || function.Entry is null)
      return allocas.ToHashSet();

    var tracked = allocas.Select((alloca, index) => (alloca, index)).ToDictionary(p => p.alloca, p => p.index);
    var blocks = function.Blocks.ToList();
    // written-at-entry per block: the entry starts with nothing written; every other block starts
    // optimistic (everything written) and is narrowed by the meet over its predecessors
    var atEntry = blocks.ToDictionary(block => block, block => ReferenceEquals(block, function.Entry)
      ? new BitSet(tracked.Count)
      : BitSet.Full(tracked.Count));
    var needing = new HashSet<IrAlloca>();

    for (var changed = true; changed;) {
      changed = false;
      foreach (var block in blocks) {
        if (!ReferenceEquals(block, function.Entry)) {
          var meet = BitSet.Full(tracked.Count);
          var any = false;
          foreach (var predecessor in block.Predecessors) {
            meet.IntersectWith(Transfer(predecessor, atEntry[predecessor], tracked, needing: null));
            any = true;
          }
          if (!any)
            meet = new BitSet(tracked.Count);    // unreachable in the CFG: assume nothing was written
          if (!meet.SetEquals(atEntry[block])) {
            atEntry[block] = meet;
            changed = true;
          }
        }
      }
    }

    foreach (var block in blocks)
      Transfer(block, atEntry[block], tracked, needing);
    return needing;
  }

  /// <summary>
  /// The written set at the end of <paramref name="block"/>. With <paramref name="needing"/> given, a
  /// read or escape of an unwritten slot is recorded there.
  /// </summary>
  private static BitSet Transfer(IrBasicBlock block, BitSet entry, Dictionary<IrAlloca, int> tracked,
      HashSet<IrAlloca>? needing) {
    var written = entry.Clone();
    foreach (var instruction in block.Instructions) {
      // a store OF a slot's address lets it escape; a store TO a slot of its whole type writes it
      if (instruction is IrStore store) {
        if (store.Value is IrAlloca escaped && tracked.ContainsKey(escaped))
          Observe(escaped);
        if (store.Pointer is IrAlloca target && tracked.TryGetValue(target, out var index)
            && target.Count <= 1 && store.Value.Type.Equals(target.Allocated))
          written.Add(index);
        else if (store.Pointer is IrAlloca partial && tracked.ContainsKey(partial))
          Observe(partial);                      // a partial store leaves the rest unwritten
        continue;
      }
      foreach (var operand in instruction.Operands)
        if (operand is IrAlloca used && tracked.ContainsKey(used))
          Observe(used);
    }
    return written;

    void Observe(IrAlloca alloca) {
      if (!written.Contains(tracked[alloca]))
        needing?.Add(alloca);
    }
  }

  /// <summary>A fixed-width set of slot indices.</summary>
  private sealed class BitSet {
    private readonly bool[] _bits;

    public BitSet(int count) => this._bits = new bool[count];

    private BitSet(bool[] bits) => this._bits = bits;

    public static BitSet Full(int count) => new(Enumerable.Repeat(true, count).ToArray());

    public BitSet Clone() => new((bool[])this._bits.Clone());

    public bool Contains(int index) => this._bits[index];

    public void Add(int index) => this._bits[index] = true;

    public void IntersectWith(BitSet other) {
      for (var i = 0; i < this._bits.Length; ++i)
        this._bits[i] &= other._bits[i];
    }

    public bool SetEquals(BitSet other) => this._bits.AsSpan().SequenceEqual(other._bits);
  }
}
