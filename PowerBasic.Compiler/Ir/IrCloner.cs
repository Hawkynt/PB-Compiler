namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Deep-clones a connected set of basic blocks into a function, remapping every
/// internal value and block reference. External operands (values defined outside the
/// cloned region, e.g. arguments seeded in the value map, globals, constants) pass
/// through unchanged. Used by the inliner (and available for loop unrolling): it is the
/// reusable structural primitive for duplicating IR. Three passes handle SSA back-edges
/// correctly - clone the blocks, clone the instructions (phis as empty shells), then
/// fill phi incomings once every value exists.
/// </summary>
public sealed class IrCloner {

  private readonly Dictionary<IrValue, IrValue> _values;
  private readonly Dictionary<IrBasicBlock, IrBasicBlock> _blocks = new(ReferenceEqualityComparer.Instance);

  private IrCloner(Dictionary<IrValue, IrValue> seed) => this._values = seed;

  /// <summary>
  /// Clones <paramref name="source"/> blocks into <paramref name="into"/>, prefixing
  /// labels with <paramref name="labelPrefix"/>. <paramref name="seed"/> pre-maps values
  /// (e.g. callee params → call args). Returns the per-block mapping; the cloned entry is
  /// the clone of <paramref name="source"/>[0].
  /// </summary>
  public static IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> Clone(
      IrFunction into, IReadOnlyList<IrBasicBlock> source, Dictionary<IrValue, IrValue> seed, string labelPrefix) {
    var cloner = new IrCloner(seed);
    foreach (var block in source)
      cloner._blocks[block] = into.CreateBlock(labelPrefix + block.Label);
    foreach (var block in source)
      cloner.CloneInstructions(block);
    foreach (var block in source)
      cloner.FillPhis(block);
    return cloner._blocks;
  }

  private IrValue Map(IrValue v) =>
    v is IrBasicBlock ? v : this._values.GetValueOrDefault(v, v);

  private IrBasicBlock MapBlock(IrBasicBlock b) => this._blocks.GetValueOrDefault(b, b);

  private void CloneInstructions(IrBasicBlock src) {
    var dst = this._blocks[src];
    foreach (var inst in src.Instructions) {
      if (inst is IrPhi phi) {
        var clone = dst.AppendPhi(new IrPhi(phi.Type));   // incomings filled in the third pass
        this._values[inst] = clone;
        continue;
      }
      var cloned = this.CloneInstruction(inst);
      dst.Append(cloned);
      if (!inst.Type.IsVoid)
        this._values[inst] = cloned;
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
    IrCmp c => new IrCmp(c.Pred, this.Map(c.Lhs), this.Map(c.Rhs)),
    IrCast x => new IrCast(x.Op, this.Map(x.Value), x.Type),
    IrAlloca a => new IrAlloca(a.Allocated) { Count = a.Count },
    IrLoad l => new IrLoad(l.Type, this.Map(l.Pointer)),
    IrStore s => new IrStore(this.Map(s.Value), this.Map(s.Pointer)),
    IrGep g => new IrGep(this.Map(g.BasePtr), this.Map(g.ByteOffset)),
    IrSelect sel => new IrSelect(this.Map(sel.Condition), this.Map(sel.IfTrue), this.Map(sel.IfFalse)),
    IrCall call => new IrCall(call.Type, this.Map(call.Callee), call.Args.Select(this.Map).ToList()),
    IrRet r => new IrRet(r.HasValue ? this.Map(r.Value!) : null),
    IrBr br => new IrBr(this.MapBlock(br.Target)),
    IrCondBr cb => new IrCondBr(this.Map(cb.Condition), this.MapBlock(cb.IfTrue), this.MapBlock(cb.IfFalse)),
    IrSwitch sw => this.CloneSwitch(sw),
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
