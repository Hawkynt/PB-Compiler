namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Deep-clones a connected set of basic blocks into a function, remapping every
/// internal value and block reference. External operands (values defined outside the
/// cloned region, e.g. arguments seeded in the value map, globals, constants) pass
/// through unchanged. Used by the inliner (and available for loop unrolling): it is the
/// reusable structural primitive for duplicating IR. Four passes handle SSA forward and
/// back references correctly - clone the blocks, clone the instructions (phis as empty
/// shells), re-resolve every cloned operand once the value map is complete, then fill phi
/// incomings.
///
/// <para>
/// The third pass is what makes the order of <c>source</c> irrelevant, and it is not
/// decoration. Block order is CREATION order, which is not dominance order: a block built
/// lazily - <c>IrLowering.EnsureGosubDispatch</c> is the one in production - is appended
/// after blocks it dominates, so an instruction cloned in pass 2 can name a value whose
/// clone does not exist yet. <see cref="Map"/> passes an unmapped value through unchanged,
/// which is right for a genuinely external operand and silently wrong for one inside the
/// region: the copy then reads the ORIGINAL's value, across a function boundary. Rewriting
/// every operand from its source once the map is complete cannot get that wrong, and costs
/// one pass over instructions that are already in hand.
/// </para>
/// </summary>
public sealed class IrCloner {

  private readonly Dictionary<IrValue, IrValue> _values;
  private readonly Dictionary<IrBasicBlock, IrBasicBlock> _blocks = new(ReferenceEqualityComparer.Instance);
  private readonly List<(IrInstruction Source, IrInstruction Clone)> _cloned = [];

  private IrCloner(Dictionary<IrValue, IrValue> seed) => this._values = seed;

  /// <summary>
  /// Clones <paramref name="source"/> blocks into <paramref name="into"/>, prefixing
  /// labels with <paramref name="labelPrefix"/>. <paramref name="seed"/> pre-maps values
  /// (e.g. callee params → call args). Returns the per-block mapping; the cloned entry is
  /// the clone of <paramref name="source"/>[0].
  /// </summary>
  public static IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> Clone(
      IrFunction into, IReadOnlyList<IrBasicBlock> source, Dictionary<IrValue, IrValue> seed, string labelPrefix)
    => Clone(into, source, seed, labelPrefix, out _);

  /// <summary>
  /// The same, also handing back the VALUE mapping.
  ///
  /// A caller that rewires anything outside the cloned region needs it: a phi in the loop's exit names
  /// a value defined in the block that was cloned, and after the original is removed that operand
  /// dominates nothing. The block map alone cannot answer "which value in the copy corresponds".
  /// </summary>
  public static IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> Clone(
      IrFunction into, IReadOnlyList<IrBasicBlock> source, Dictionary<IrValue, IrValue> seed, string labelPrefix,
      out IReadOnlyDictionary<IrValue, IrValue> values) {
    var cloner = new IrCloner(seed);
    values = cloner._values;
    foreach (var block in source)
      cloner._blocks[block] = into.CreateBlock(labelPrefix + block.Label);
    foreach (var block in source)
      cloner.CloneInstructions(block);
    cloner.ResolveOperands();
    foreach (var block in source)
      cloner.FillPhis(block);
    return cloner._blocks;
  }

  /// <summary>
  /// The copy of a value. A block ADDRESS is the one constant that has to be rewritten rather than
  /// passed through: it names a block, and the copy's blocks are not the original's. Naming one
  /// outside the cloned region is left alone, because a reference out of the region still means what
  /// it said.
  /// </summary>
  private IrValue Map(IrValue v) => v switch {
    IrBasicBlock => v,
    IrBlockAddress ba when this._blocks.TryGetValue(ba.Block, out var copy) => new IrBlockAddress(copy),
    _ => this._values.GetValueOrDefault(v, v),
  };

  private IrBasicBlock MapBlock(IrBasicBlock b) => this._blocks.GetValueOrDefault(b, b);

  private void CloneInstructions(IrBasicBlock src) {
    var dst = this._blocks[src];
    foreach (var inst in src.Instructions) {
      if (inst is IrPhi phi) {
        var clone = dst.AppendPhi(new IrPhi(phi.Type) { FastMathFlags = phi.FastMathFlags }); // incomings filled in pass 4
        this._values[inst] = clone;
        continue;
      }
      var cloned = this.CloneInstruction(inst);
      cloned.FastMathFlags = inst.FastMathFlags;
      dst.Append(cloned);
      this._cloned.Add((inst, cloned));
      if (!inst.Type.IsVoid)
        this._values[inst] = cloned;
    }
  }

  /// <summary>
  /// Re-reads every cloned instruction's operands from its source, now that every value in the region
  /// has a clone. Only a forward reference actually moves - <see cref="IrInstruction.SetOperand"/> is a
  /// no-op when the value is already the one it maps to - so this is a repair of pass 2 rather than a
  /// second mapping of it. Block ADDRESSES are skipped: <see cref="Map"/> mints a fresh
  /// <see cref="IrBlockAddress"/> on every call, and pass 2 already had the complete block map.
  /// </summary>
  private void ResolveOperands() {
    foreach (var (source, clone) in this._cloned)
      for (var i = 0; i < source.Operands.Count; ++i) {
        var operand = source.GetOperand(i);
        if (operand is IrBlockAddress)
          continue;
        clone.SetOperand(i, this.Map(operand));
      }
  }

  private void FillPhis(IrBasicBlock src) {
    var dst = this._blocks[src];
    var srcPhis = src.Phis.ToList();
    var dstPhis = dst.Phis.ToList();
    for (var i = 0; i < srcPhis.Count; ++i)
      for (var j = 0; j < srcPhis[i].IncomingBlocks.Count; ++j)
        dstPhis[i].AddIncoming(this.Map(srcPhis[i].GetOperand(j)), this.MapBlock(srcPhis[i].IncomingBlocks[j]));
  }

  private IrInstruction CloneInstruction(IrInstruction inst) => inst switch {
    IrBinary b => new IrBinary(b.Op, this.Map(b.Lhs), this.Map(b.Rhs)),
    IrCmp c => new IrCmp(c.Pred, this.Map(c.Lhs), this.Map(c.Rhs)) { IsSourceCondition = c.IsSourceCondition },
    IrCast x => new IrCast(x.Op, this.Map(x.Value), x.Type),
    IrAlloca a => new IrAlloca(a.Allocated) { Count = a.Count, IsSourceVariable = a.IsSourceVariable },
    IrLoad l => new IrLoad(l.Type, this.Map(l.Pointer)),
    IrStore s => new IrStore(this.Map(s.Value), this.Map(s.Pointer)),
    IrGep g => g.ElementType is { } et ? new IrGep(this.Map(g.BasePtr), this.Map(g.ByteOffset), et) : new IrGep(this.Map(g.BasePtr), this.Map(g.ByteOffset)),
    IrFarPtr f => new IrFarPtr(this.Map(f.Segment), this.Map(f.Offset)),
    IrSelect sel => new IrSelect(this.Map(sel.Condition), this.Map(sel.IfTrue), this.Map(sel.IfFalse)),
    IrCall call => new IrCall(call.Type, this.Map(call.Callee),
      call.Args.Select(this.Map).ToList(), call.Convention),
    IrRet r => new IrRet(r.HasValue ? this.Map(r.Value!) : null),
    IrBr br => new IrBr(this.MapBlock(br.Target)),
    IrCondBr cb => new IrCondBr(this.Map(cb.Condition), this.MapBlock(cb.IfTrue), this.MapBlock(cb.IfFalse)),
    IrSwitch sw => this.CloneSwitch(sw),
    IrIndirectBr ib => new IrIndirectBr(this.Map(ib.Address), ib.Targets.Select(this.MapBlock)),
    IrUnreachable => new IrUnreachable(),
    _ => throw new InvalidOperationException($"cannot clone {inst.GetType().Name}"),
  };

  private IrSwitch CloneSwitch(IrSwitch sw) {
    var clone = new IrSwitch(this.Map(sw.Condition), this.MapBlock(sw.DefaultTarget));
    foreach (var (value, target) in sw.Cases)
      clone.AddCase(value, this.MapBlock(target));
    return clone;
  }
}
