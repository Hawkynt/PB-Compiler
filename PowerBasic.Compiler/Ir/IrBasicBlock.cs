namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A maximal straight-line run of instructions ending in exactly one terminator.
/// Phis (if any) lead the block; the terminator closes it. Predecessors are derived
/// from the terminators of sibling blocks so CFG edges never drift out of sync.
/// </summary>
public sealed class IrBasicBlock : IrValue {

  private readonly List<IrInstruction> _instructions = [];

  public IrBasicBlock(string label) : base(IrType.Void) => this.Label = label;

  /// <summary>A label for printing / referencing the block.</summary>
  public string Label { get; set; }

  /// <summary>The function this block belongs to.</summary>
  public IrFunction? Parent { get; internal set; }

  /// <summary>The instructions of this block in execution order.</summary>
  public IReadOnlyList<IrInstruction> Instructions => this._instructions;

  /// <summary>The leading phi instructions of this block.</summary>
  public IEnumerable<IrPhi> Phis => this._instructions.TakeWhile(i => i is IrPhi).Cast<IrPhi>();

  /// <summary>The closing terminator, or null while the block is still being built.</summary>
  public IrInstruction? Terminator =>
    this._instructions.Count > 0 && this._instructions[^1].IsTerminator ? this._instructions[^1] : null;

  /// <summary>The successor blocks per the terminator (empty if not yet terminated).</summary>
  public IEnumerable<IrBasicBlock> Successors => this.Terminator?.Successors ?? [];

  /// <summary>The blocks that branch to this one (derived from sibling terminators).</summary>
  public IEnumerable<IrBasicBlock> Predecessors =>
    this.Parent is null
      ? []
      : this.Parent.Blocks.Where(b => b.Successors.Any(s => ReferenceEquals(s, this)));

  /// <summary>Appends an instruction, claiming ownership.</summary>
  public T Append<T>(T instruction) where T : IrInstruction {
    instruction.Parent = this;
    this._instructions.Add(instruction);
    return instruction;
  }

  /// <summary>Inserts an instruction immediately before an existing one.</summary>
  public T InsertBefore<T>(T instruction, IrInstruction before) where T : IrInstruction {
    var at = this._instructions.IndexOf(before);
    if (at < 0)
      throw new ArgumentException("anchor instruction is not in this block", nameof(before));
    instruction.Parent = this;
    this._instructions.Insert(at, instruction);
    return instruction;
  }

  /// <summary>Inserts a phi at the head of the block (phis must precede ordinary instructions).</summary>
  public IrPhi AppendPhi(IrPhi phi) {
    phi.Parent = this;
    var at = 0;
    while (at < this._instructions.Count && this._instructions[at] is IrPhi)
      ++at;
    this._instructions.Insert(at, phi);
    return phi;
  }

  /// <summary>Detaches an instruction from this block (does not drop its operand uses).</summary>
  public void Remove(IrInstruction instruction) {
    if (this._instructions.Remove(instruction))
      instruction.Parent = null;
  }

  public override string ToString() => this.Label;
}
