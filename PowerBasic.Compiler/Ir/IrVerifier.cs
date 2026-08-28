namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Checks the structural and SSA well-formedness of a function or module: exactly one
/// terminator per block, phi/predecessor agreement, operands that dominate their uses,
/// and per-instruction type consistency. Returns a list of human-readable problems
/// (empty means valid). Passes should leave the IR verifiable at every step.
/// </summary>
public sealed class IrVerifier {

  private readonly IrFunction _fn;
  private readonly IrDominators _dom;
  private readonly Dictionary<IrInstruction, int> _order = new(ReferenceEqualityComparer.Instance);
  private readonly List<string> _errors = [];

  private IrVerifier(IrFunction fn, IrDominators dom) {
    this._fn = fn;
    this._dom = dom;
    foreach (var block in fn.Blocks) {
      var i = 0;
      foreach (var inst in block.Instructions)
        this._order[inst] = i++;
    }
  }

  /// <summary>Verifies every function in the module; returns all problems found.</summary>
  public static IReadOnlyList<string> Verify(IrModule module) {
    var errors = new List<string>();
    foreach (var fn in module.Functions)
      errors.AddRange(Verify(fn));
    return errors;
  }

  /// <summary>Verifies a single function; returns all problems found (empty = valid).</summary>
  public static IReadOnlyList<string> Verify(IrFunction fn) {
    if (fn.IsDeclaration)
      return [];
    var dom = IrDominators.Build(fn)!;
    var v = new IrVerifier(fn, dom);
    v.Run();
    return v._errors;
  }

  private void Run() {
    if (this._fn.Entry!.Predecessors.Any())
      this.Error("entry block has predecessors (the entry must not be a branch target)");

    foreach (var block in this._fn.Blocks)
      this.VerifyBlock(block);
  }

  private void VerifyBlock(IrBasicBlock block) {
    if (block.Instructions.Count == 0) {
      this.Error($"block '{block.Label}' is empty");
      return;
    }

    // exactly one terminator, as the final instruction
    for (var i = 0; i < block.Instructions.Count; ++i) {
      var isLast = i == block.Instructions.Count - 1;
      var inst = block.Instructions[i];
      if (inst.IsTerminator && !isLast)
        this.Error($"block '{block.Label}' has a terminator '{inst.GetType().Name}' before its end");
      if (!inst.IsTerminator && isLast)
        this.Error($"block '{block.Label}' does not end in a terminator");
    }

    // phis lead the block and agree with the predecessor set
    var seenNonPhi = false;
    foreach (var inst in block.Instructions) {
      if (inst is IrPhi phi) {
        if (seenNonPhi)
          this.Error($"phi in block '{block.Label}' appears after a non-phi instruction");
        this.VerifyPhi(block, phi);
      } else {
        seenNonPhi = true;
      }
      this.VerifyTypes(inst);
      this.VerifyOperandDominance(inst);
    }
  }

  private void VerifyPhi(IrBasicBlock block, IrPhi phi) {
    var preds = block.Predecessors.Where(this._dom.IsReachable).ToHashSet(ReferenceEqualityComparer.Instance);
    var incoming = phi.IncomingBlocks.ToHashSet(ReferenceEqualityComparer.Instance);
    if (!preds.SetEquals(incoming))
      this.Error($"phi in block '{block.Label}' incoming blocks do not match its predecessors");

    for (var i = 0; i < phi.IncomingBlocks.Count; ++i)
      if (!phi.GetOperand(i).Type.SameStorage(phi.Type))
        this.Error($"phi in block '{block.Label}' incoming value #{i} has type {phi.GetOperand(i).Type}, expected {phi.Type}");
  }

  private void VerifyOperandDominance(IrInstruction inst) {
    if (inst is IrPhi phi) {
      for (var i = 0; i < phi.IncomingBlocks.Count; ++i) {
        var value = phi.GetOperand(i);
        var predBlock = phi.IncomingBlocks[i];
        if (!this.VerifyOperandIsOwned(value))
          continue;
        if (value is IrInstruction def && def.Parent is { } defBlock
            && !ReferenceEqualities(defBlock, predBlock) && !this._dom.Dominates(defBlock, predBlock))
          this.Error($"phi incoming value does not dominate predecessor '{predBlock.Label}'");
      }
      return;
    }

    var useBlock = inst.Parent!;
    foreach (var operand in inst.Operands) {
      if (!this.VerifyOperandIsOwned(operand))
        continue;
      if (operand is not IrInstruction def || def.Parent is not { } defBlock)
        continue;                                    // constants, args, globals impose no constraint
      if (ReferenceEqualities(defBlock, useBlock)) {
        if (this._order[def] >= this._order[inst])
          this.Error($"operand defined after its use in block '{useBlock.Label}'");
      } else if (!this._dom.Dominates(defBlock, useBlock)) {
        this.Error($"operand defined in '{defBlock.Label}' does not dominate use in '{useBlock.Label}'");
      }
    }
  }

  /// <summary>
  /// Whether an instruction operand is a definition this function still owns, reporting it when it is
  /// not. The dominance rules below can only speak about a definition that is IN the function, and the
  /// two ways one can fail to be are both real defects rather than theoretical ones: an instruction
  /// detached from its block (<see cref="IrInstruction.Parent"/> null) has no definition point at all,
  /// and one whose block belongs to ANOTHER function is a cross-function reference - which is what a
  /// clone that failed to remap an operand produces.
  ///
  /// <para>
  /// Both used to read as "constants, args, globals impose no constraint" and were skipped in silence.
  /// That is how an inlined body kept a reference to the CALLEE's phi through to the back ends: while
  /// the callee still existed the dominance check happened to flag it, and the moment
  /// <c>GlobalDce</c> removed the callee the operand's parent went null and the module verified
  /// clean.
  /// </para>
  /// </summary>
  private bool VerifyOperandIsOwned(IrValue operand) {
    if (operand is not IrInstruction def)
      return true;                                   // constants, args, globals and blocks define nothing here
    if (def.Parent is not { } defBlock) {
      this.Error($"operand '{def.GetType().Name}' is detached: it belongs to no block, so it has no definition point");
      return false;
    }
    if (!ReferenceEquals(defBlock.Parent, this._fn)) {
      this.Error($"operand '{def.GetType().Name}' is defined in block '{defBlock.Label}' of another function");
      return false;
    }
    return true;
  }

  private void VerifyTypes(IrInstruction inst) {
    switch (inst) {
      // operand agreement is a STORAGE question, so signedness may differ (the op carries it:
      // sdiv/udiv, slt/ult) but an MBF float never mixes with an IEEE one - the encodings differ
      case IrBinary b:
        if (!b.Lhs.Type.SameStorage(b.Rhs.Type) || !b.Type.SameStorage(b.Lhs.Type))
          this.Error($"binary '{b.Op}' operand/result types disagree ({b.Lhs.Type}, {b.Rhs.Type} -> {b.Type})");
        if (b.IsFloatOp && !b.Type.IsFloat)
          this.Error($"float op '{b.Op}' on non-float type {b.Type}");
        if (b.IsFloatOp && b.Type.IsMbf)
          this.Error($"float op '{b.Op}' on Microsoft Binary Format {b.Type} - MBF is storage only, convert with MbfToFP first");
        if (!b.IsFloatOp && !b.Type.IsInteger)
          this.Error($"integer op '{b.Op}' on non-integer type {b.Type}");
        break;
      case IrCmp c:
        if (!c.Lhs.Type.SameStorage(c.Rhs.Type))
          this.Error($"comparison operands disagree ({c.Lhs.Type} vs {c.Rhs.Type})");
        if (IsFloatPred(c.Pred) && !c.Lhs.Type.IsFloat)
          this.Error("float comparison on non-float operands");
        if (IsFloatPred(c.Pred) && c.Lhs.Type.IsMbf)
          this.Error("float comparison on Microsoft Binary Format operands - convert with MbfToFP first");
        if (!IsFloatPred(c.Pred) && c.Lhs.Type.IsFloat)
          this.Error("integer comparison on float operands");
        break;
      case IrCast cast:
        this.VerifyCast(cast);
        break;
      case IrLoad l when !l.Pointer.Type.IsPointer:
        this.Error("load from a non-pointer operand");
        break;
      case IrStore s when !s.Pointer.Type.IsPointer:
        this.Error("store to a non-pointer operand");
        break;
      case IrGep g when !g.BasePtr.Type.IsPointer || !g.ByteOffset.Type.IsInteger:
        this.Error("gep base must be a pointer and offset an integer");
        break;
      case IrRet r:
        var expected = this._fn.ReturnType;
        var actual = r.Value?.Type ?? IrType.Void;
        if (!actual.SameStorage(expected))
          this.Error($"ret type {actual} does not match function return type {expected}");
        break;
      case IrCondBr cb when !cb.Condition.Type.IsBool:
        this.Error($"condbr condition must be i1, got {cb.Condition.Type}");
        break;
      case IrSwitch sw:
        this.VerifySwitch(sw);
        break;
      case IrIndirectBr ib:
        if (!ib.Address.Type.IsPointer)
          this.Error($"indirectbr address must be a pointer, got {ib.Address.Type}");
        if (ib.Targets.Count == 0)
          this.Error("indirectbr with no possible target: the CFG would not show where it can go");
        break;
      case IrSelect sel:
        if (!sel.Condition.Type.IsBool)
          this.Error($"select condition must be i1, got {sel.Condition.Type}");
        if (!sel.IfTrue.Type.SameStorage(sel.IfFalse.Type) || !sel.Type.SameStorage(sel.IfTrue.Type))
          this.Error($"select arms/result types disagree ({sel.IfTrue.Type}, {sel.IfFalse.Type} -> {sel.Type})");
        break;
      case IrCall call when !call.Callee.Type.IsPointer:
        this.Error("call to a non-pointer callee");
        break;
    }
  }

  private void VerifySwitch(IrSwitch sw) {
    if (!sw.Condition.Type.IsInteger) {
      this.Error($"switch condition must be an integer, got {sw.Condition.Type}");
      return;
    }

    var patterns = new HashSet<ulong>();
    foreach (var (value, _) in sw.Cases) {
      if (!sw.IsCaseValueRepresentable(value)) {
        this.Error($"case {value} does not fit switch condition {sw.Condition.Type}");
        continue;
      }
      if (!patterns.Add(sw.PatternOf(value)))
        this.Error($"duplicate switch case bit pattern for {value}");
    }
  }

  private void VerifyCast(IrCast cast) {
    var from = cast.Value.Type;
    var to = cast.Type;
    var ok = cast.Op switch {
      IrCastOp.Trunc => from.IsInteger && to.IsInteger && from.Bits > to.Bits,
      IrCastOp.ZExt or IrCastOp.SExt => from.IsInteger && to.IsInteger && from.Bits < to.Bits,
      // the IEEE float ops never take MBF storage - it converts through MbfToFP/FPToMbf first
      IrCastOp.FPTrunc => from.IsIeeeFloat && to.IsIeeeFloat && from.Bits > to.Bits,
      IrCastOp.FPExt => from.IsIeeeFloat && to.IsIeeeFloat && from.Bits < to.Bits,
      IrCastOp.SIToFP or IrCastOp.UIToFP => from.IsInteger && to.IsIeeeFloat,
      IrCastOp.FPToSI or IrCastOp.FPToUI or IrCastOp.FPToSIRound or IrCastOp.FPToUIRound
        => from.IsIeeeFloat && to.IsInteger,
      IrCastOp.MbfToFP => from.IsMbf && to.IsIeeeFloat,
      IrCastOp.FPToMbf => from.IsIeeeFloat && to.IsMbf,
      IrCastOp.IntToPtr => from.IsInteger && to.IsPointer,
      IrCastOp.PtrToInt => from.IsPointer && to.IsInteger,
      IrCastOp.BitCast => true,
      _ => false,
    };
    if (!ok)
      this.Error($"invalid cast '{cast.Op}' from {from} to {to}");
  }

  private static bool ReferenceEqualities(IrBasicBlock a, IrBasicBlock b) => ReferenceEquals(a, b);
  private static bool IsFloatPred(IrCmpPred p) => p is >= IrCmpPred.Foeq;
  private void Error(string message) => this._errors.Add(message);
}
