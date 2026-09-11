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
  private int _nextProfileBlockId;
  private IrCallConvention _convention;
  private bool _hasConvention;

  public IrFunction(string name, IrType returnType, IEnumerable<IrArgument>? parameters = null) : base(name) {
    this.ReturnType = returnType;
    if (parameters is not null)
      foreach (var p in parameters)
        this.AddParameter(p);
  }

  /// <summary>The module that owns this function, or null while it is standalone.</summary>
  public IrModule? Module { get; internal set; }

  /// <summary>The declared return type (<see cref="IrType.Void"/> for a SUB).</summary>
  public IrType ReturnType { get; }

  /// <summary>
  /// Calling-convention identity of this DEFINITION/declaration. A direct <see cref="IrCall"/> carries
  /// the same identity at the call site; keeping both sides is what lets cloning, hosted emission and
  /// target back ends preserve ABI rather than recovering it from a source symbol that a generated
  /// function does not have. BASIC is the default for hand-built IR whose definition ABI was never
  /// stated explicitly.
  /// </summary>
  public IrCallConvention Convention {
    get => this._convention;
    init {
      this._convention = value;
      this._hasConvention = true;
    }
  }

  /// <summary>Whether the definition ABI was stated rather than merely defaulted to BASIC.</summary>
  internal bool HasConvention => this._hasConvention;

  /// <summary>
  /// Records a direct call's ABI on a declaration/definition whose source signature has not already
  /// supplied one. Production lowering builds direct calls through <see cref="IrBuilder"/>, so this
  /// also covers declarations before their bodies are lowered. Once bound, the convention is immutable;
  /// <see cref="IrVerifier"/> reports a mismatching direct call instead of letting one silently win.
  /// </summary>
  internal void BindConvention(IrCallConvention convention) {
    if (this._hasConvention)
      return;
    this._convention = convention;
    this._hasConvention = true;
  }

  /// <summary>The formal parameters in signature order.</summary>
  public IReadOnlyList<IrArgument> Parameters => this._parameters;

  /// <summary>
  /// True when the declared parameters are only the FIXED prefix and a call may pass more.
  ///
  /// One runtime entry needs it and no BASIC procedure ever can: <c>rt_str_concat_n</c> takes an
  /// operand count and then that many string handles, so its call sites differ in arity by design.
  /// Everything else in the IR has a signature its calls must match, which is what the verifier and
  /// both text emitters assume - hence a flag rather than a convention.
  /// </summary>
  public bool IsVarArgs { get; init; }

  /// <summary>
  /// True when the source declared the procedure <c>NOINLINE</c>: the optimizer must keep every call
  /// to it a real call, so its emitted code stays inspectable.
  ///
  /// <para>
  /// This is a contract with the PROGRAMMER rather than a fact about the body, which is why it lives
  /// on the function instead of being rediscovered by <see cref="Passes.FunctionSummaries"/>. Without
  /// it a procedure whose only purpose is to be a barrier - the empty <c>SUB</c> that makes an operand
  /// opaque - is absorbed at its call sites, and the code the programmer wanted to look at stops
  /// existing. The direct emitter honours the same flag in <c>AnalyzeInlinableLeaf</c>.
  /// </para>
  /// </summary>
  public bool NoInline { get; init; }

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
    this.AttachProfileIdentity(block);
    block.Parent = this;
    this._blocks.Add(block);
    return block;
  }

  /// <summary>Creates, appends and returns a fresh block with the given label.</summary>
  public IrBasicBlock CreateBlock(string label) => this.AddBlock(new IrBasicBlock(label));

  /// <summary>
  /// Creates a fresh block and makes it the ENTRY, pushing the current one down. The old entry keeps
  /// every instruction it had and simply gains a predecessor, which is what turns it into a loop
  /// header - the shape tail-recursion elimination needs, since a phi has to name a block for the
  /// value that arrives the first time round and the entry of a function has none.
  /// </summary>
  public IrBasicBlock CreateEntryBlock(string label) {
    var block = new IrBasicBlock(label);
    this.AttachProfileIdentity(block);
    block.Parent = this;
    this._blocks.Insert(0, block);
    return block;
  }

  /// <summary>
  /// Replaces only the physical order of this function's blocks. CFG edges, instructions and block
  /// ownership are untouched; the original entry must remain first.
  /// </summary>
  /// <remarks>
  /// This deliberately is not public mutation of <see cref="Blocks"/>. Layout passes are allowed to
  /// rearrange an already-built body, but every caller still gets the invariant that index zero is
  /// the entry and that the list contains each owned block exactly once.
  /// </remarks>
  internal void ReorderBlocks(IReadOnlyList<IrBasicBlock> blocks) {
    ArgumentNullException.ThrowIfNull(blocks);
    if (blocks.Count != this._blocks.Count)
      throw new ArgumentException("block order must contain every function block exactly once", nameof(blocks));

    var entry = this.Entry;
    if (entry is not null && !ReferenceEquals(blocks[0], entry))
      throw new ArgumentException("block order must keep the function entry first", nameof(blocks));

    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    foreach (var block in blocks)
      if (!ReferenceEquals(block.Parent, this) || !seen.Add(block))
        throw new ArgumentException("block order contains a foreign or duplicate block", nameof(blocks));
    if (this._blocks.Any(block => !seen.Contains(block)))
      throw new ArgumentException("block order omits a function block", nameof(blocks));

    this._blocks.Clear();
    this._blocks.AddRange(blocks);
  }

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

  /// <summary>
  /// Every block whose ADDRESS this function takes - an <c>ON ERROR</c> handler, a statement boundary
  /// a <c>RESUME</c> can land on, a label a <c>CODEPTR32</c> names.
  ///
  /// <para>
  /// Such a block cannot be merged into its predecessor or dropped, however few edges reach it: the
  /// address still names it, and it is the one property of a block that no CFG rewrite can see, since
  /// it is carried in a VALUE rather than in an edge. Merging one away leaves an address pointing at
  /// a label the emitter never defines.
  /// </para>
  /// </summary>
  public HashSet<IrBasicBlock> AddressTakenBlocks() {
    var taken = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    foreach (var instruction in this.AllInstructions)
      foreach (var operand in instruction.Operands)
        if (operand is IrBlockAddress address)
          taken.Add(address.Block);
    return taken;
  }

  /// <summary>Removes every block, turning the function back into a declaration (used when a body fails to lower).</summary>
  public void ClearBody() {
    foreach (var block in this._blocks)
      foreach (var inst in block.Instructions.ToList())
        inst.EraseFromParent();
    this._blocks.Clear();
  }

  private void AttachProfileIdentity(IrBasicBlock block) {
    if (block.ProfileId is not { } id) {
      block.ProfileId = this._nextProfileBlockId++;
      return;
    }

    if (id < 0)
      throw new ArgumentOutOfRangeException(nameof(block), "profile block ids cannot be negative");
    if (id >= this._nextProfileBlockId)
      this._nextProfileBlockId = checked(id + 1);
  }
}
