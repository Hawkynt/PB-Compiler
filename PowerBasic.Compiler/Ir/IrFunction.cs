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

  /// <summary>
  /// True when the body arms a PB error handler (<c>ON ERROR</c> / <c>RESUME</c>), which the
  /// optimizer must not touch. A raise transfers control from an arbitrary point - including from
  /// inside a runtime routine - to a block the CFG shows no edge to, so every CFG-based conclusion
  /// about this function is unsound: the handler looks unreachable, values look like they can only
  /// arrive along the visible predecessors, and a store the handler reads looks dead.
  ///
  /// <see cref="Passes.IrPassManager"/> skips such a function outright rather than each pass carrying
  /// its own guard - one place to be right instead of a dozen. It is the same trade the direct
  /// emitter makes, where <c>_trackResume</c> disables the optimizations wholesale.
  /// </summary>
  public bool HasErrorHandler { get; set; }

  /// <summary>
  /// True when the body contains an <see cref="Passes.IrPassManager"/>-opaque block of inline assembly.
  ///
  /// Inline asm reaches BASIC variables by name, jumps to BASIC labels and may touch any register, so
  /// every fact a pass would derive about this function - which values are live, which stores are
  /// dead, which slots can be promoted - is a fact about the part of the function the IR can see.
  /// The optimizer skips it whole rather than each pass carrying a guard, which is the same trade
  /// made for <see cref="HasErrorHandler"/> and the one the direct emitter makes.
  /// </summary>
  public bool HasInlineAsm { get; set; }

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

  /// <summary>
  /// Removes a block from the function, dropping its instructions' uses of their operands.
  ///
  /// <para>
  /// Dropping the uses is what makes the removal complete, and leaving them was costing optimizations
  /// silently. An instruction whose block has gone stays registered in its operands' use-lists, and
  /// it still names that block as its parent - so every pass that asks "is this value read outside
  /// the loop" or "does this have exactly one user" is answered by a reader that can never run. The
  /// result is not a wrong transform but a DECLINED one, which leaves no trace to find it by: a
  /// nested loop whose inner half had been unrolled kept the outer half forever, because the dead
  /// float shadow of the accumulator still counted as a use.
  /// </para>
  /// <para>
  /// Callers that move the instructions out first are unaffected - there is nothing left to erase.
  /// </para>
  /// </summary>
  public void RemoveBlock(IrBasicBlock block) {
    if (!this._blocks.Remove(block))
      return;
    foreach (var instruction in block.Instructions.ToList())
      instruction.EraseFromParent();
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
