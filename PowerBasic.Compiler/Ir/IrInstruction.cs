namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The base of every IR instruction. An instruction is itself a value (its result),
/// so its operands are other <see cref="IrValue"/>s and it appears in their use-lists.
/// Operand mutation goes through <see cref="SetOperand"/> so the use-lists stay exact.
/// </summary>
public abstract class IrInstruction : IrValue {

  private readonly List<IrValue> _operands = [];

  protected IrInstruction(IrType type) : base(type) { }

  /// <summary>The basic block currently holding this instruction, or null if detached.</summary>
  public IrBasicBlock? Parent { get; internal set; }

  /// <summary>This instruction's operands, in fixed positional order.</summary>
  public IReadOnlyList<IrValue> Operands => this._operands;

  /// <summary>
  /// Floating-point semantic freedoms attached to this operation. Strict is the default; the SPEED
  /// objective grants flags only to operations for which the corresponding relaxed rewrite is legal.
  /// Non-floating instructions leave this at <see cref="IrFastMathFlags.None"/>.
  /// </summary>
  public IrFastMathFlags FastMathFlags { get; set; }

  /// <summary>True for control-flow terminators (the required last instruction of a block).</summary>
  public virtual bool IsTerminator => false;

  /// <summary>The basic blocks this instruction may transfer control to (empty for non-terminators).</summary>
  public virtual IEnumerable<IrBasicBlock> Successors => [];

  /// <summary>Appends an operand and registers this instruction as a user of it.</summary>
  protected void AddOperand(IrValue operand) {
    this._operands.Add(operand);
    operand.AddUser(this);
  }

  /// <summary>Returns the operand at the given position.</summary>
  public IrValue GetOperand(int index) => this._operands[index];

  /// <summary>Replaces the operand at <paramref name="index"/>, keeping both use-lists consistent.</summary>
  public void SetOperand(int index, IrValue value) {
    var old = this._operands[index];
    if (ReferenceEquals(old, value))
      return;

    old.RemoveUser(this);
    this._operands[index] = value;
    value.AddUser(this);
  }

  /// <summary>Repoints every operand equal to <paramref name="from"/> at <paramref name="to"/>.</summary>
  internal void ReplaceOperand(IrValue from, IrValue to) {
    for (var i = 0; i < this._operands.Count; ++i)
      if (ReferenceEquals(this._operands[i], from))
        this.SetOperand(i, to);
  }

  /// <summary>Removes the operand at <paramref name="index"/>, updating the use-list.</summary>
  protected void RemoveOperandAt(int index) {
    this._operands[index].RemoveUser(this);
    this._operands.RemoveAt(index);
  }

  /// <summary>
  /// Detaches this instruction from every operand's use-list (call before discarding
  /// it). The instruction must already be unused and removed from its block.
  /// </summary>
  public void DropOperandUses() {
    foreach (var operand in this._operands)
      operand.RemoveUser(this);
    this._operands.Clear();
  }

  /// <summary>Removes this instruction from its parent block and drops its operand uses.</summary>
  public void EraseFromParent() {
    this.Parent?.Remove(this);
    this.DropOperandUses();
  }
}
