namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A function: a signature plus a list of basic blocks. The first block is the entry
/// (it must have no predecessors). A function with no blocks is a declaration (an
/// external symbol). The function is itself an <see cref="IrGlobalValue"/> so it can be
/// the callee operand of an <see cref="IrCall"/>.
/// </summary>
public sealed class IrFunction : IrGlobalValue {

  private readonly List<IrBasicBlock> _blocks = [];
  private readonly List<IrArgument> _parameters = [];

  public IrFunction(string name, IrType returnType, IEnumerable<IrArgument>? parameters = null) : base(name) {
    this.ReturnType = returnType;
    if (parameters is not null)
      foreach (var p in parameters)
        this.AddParameter(p);
  }

  /// <summary>The declared return type (<see cref="IrType.Void"/> for a SUB).</summary>
  public IrType ReturnType { get; }

  /// <summary>The formal parameters in signature order.</summary>
  public IReadOnlyList<IrArgument> Parameters => this._parameters;

  /// <summary>The basic blocks; the first is the entry.</summary>
  public IReadOnlyList<IrBasicBlock> Blocks => this._blocks;

  /// <summary>The entry block, or null for a declaration.</summary>
  public IrBasicBlock? Entry => this._blocks.Count > 0 ? this._blocks[0] : null;

  /// <summary>True when the function has no body (an external/imported symbol).</summary>
  public bool IsDeclaration => this._blocks.Count == 0;

  public IrArgument AddParameter(IrArgument argument) {
    argument.Parent = this;
    this._parameters.Add(argument);
    return argument;
  }

  /// <summary>Appends a block to the end of the function.</summary>
  public IrBasicBlock AddBlock(IrBasicBlock block) {
    block.Parent = this;
    this._blocks.Add(block);
    return block;
  }

  /// <summary>Creates, appends and returns a fresh block with the given label.</summary>
  public IrBasicBlock CreateBlock(string label) => this.AddBlock(new IrBasicBlock(label));

  /// <summary>Removes a block from the function.</summary>
  public void RemoveBlock(IrBasicBlock block) {
    if (this._blocks.Remove(block))
      block.Parent = null;
  }

  /// <summary>All instructions across all blocks, in block then program order.</summary>
  public IEnumerable<IrInstruction> AllInstructions => this._blocks.SelectMany(b => b.Instructions);

  /// <summary>Removes every block, turning the function back into a declaration (used when a body fails to lower).</summary>
  public void ClearBody() {
    foreach (var block in this._blocks)
      foreach (var inst in block.Instructions.ToList())
        inst.EraseFromParent();
    this._blocks.Clear();
  }
}
