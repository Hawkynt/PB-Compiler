using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The selection patterns that span more than one IR instruction: shapes the optimizer has already
/// reduced to arithmetic, which this target has a shorter spelling for.
///
/// <para>
/// They live here rather than in <c>Peephole</c> because they are questions about the IR - "is this
/// subtraction the tail of an absolute value" - and rather than in the IR passes because the ANSWER
/// is an encoding: <c>CWD</c> is the sign of <c>AX</c> smeared across <c>DX</c>, which no
/// target-independent form can say. Each is collected up front (<see cref="CollectIdioms"/>), so the
/// instructions a pattern absorbs can be skipped when the block loop reaches them.
/// </para>
/// </summary>
public sealed partial class InstructionSelector {

  /// <summary>
  /// IR instructions a multi-instruction pattern has taken over: the block loop passes them by, and the
  /// instruction that OWNS the pattern emits their work. Every member is single-use and pure by the
  /// test that put it here, so passing it by removes nothing observable.
  /// </summary>
  private readonly HashSet<IrInstruction> _consumed = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The predicate to emit for a comparison, where the min/max canonicalization gives a different
  /// answer from the comparison's own. The map is what carries the decision from the <c>select</c>
  /// that proves it to the <c>icmp</c> that emits it - two instructions the selector reaches at
  /// different times.
  /// </summary>
  private readonly Dictionary<IrCmp, IrCmpPred> _canonicalPredicate = new(ReferenceEqualityComparer.Instance);

  /// <summary>The min/max selects whose arms go with that relabelled predicate the other way round.</summary>
  private readonly HashSet<IrSelect> _swappedArms = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Finds the multi-instruction patterns before selection walks the blocks, because a pattern is
  /// owned by its LAST instruction and the ones in front of it have to be recognised as absorbed when
  /// the loop reaches them - which is earlier.
  ///
  /// <para>
  /// <b>The min/max canonicalization.</b> A <c>select</c> whose two arms ARE its comparison's two
  /// operands is a minimum or a maximum, and BASIC has four spellings of each that PB compiles to one
  /// answer: <c>IF a &gt; b THEN m = a ELSE m = b</c> reaches the IR as <c>select(sgt(a,b), a, b)</c>,
  /// <c>MAX%(a, b)</c> as <c>select(sge(a,b), a, b)</c>, the one-armed clamp
  /// <c>IF x &gt; hi THEN x = hi</c> as <c>select(sgt(x,hi), hi, x)</c> and <c>MIN%(x, hi)</c> as
  /// <c>select(sle(x,hi), x, hi)</c>. All four are two shapes, and without this they differ by the one
  /// byte that is <c>JG</c> rather than <c>JGE</c>.
  /// </para>
  ///
  /// <para>
  /// Two rewrites bring them together, and each is an identity on its own. Reversing the arms is the
  /// same choice made through the NEGATED predicate - <c>select(p, b, a)</c> is
  /// <c>select(!p, a, b)</c> - so the arms are put in the comparison's own order. And a strict
  /// ordering may then be relaxed to its or-equal twin, because the two differ only where the operands
  /// are EQUAL and there both arms answer the same value. What comes out is one shape per function:
  /// the operands in the comparison's order, tested with <c>&lt;=</c> or <c>&gt;=</c>.
  /// </para>
  ///
  /// <para>
  /// The compare must have exactly ONE user. A comparison the program also branches on is not free to
  /// change its answer at equality or to invert, and CSE is perfectly capable of handing the same
  /// <c>icmp</c> to a select and a branch.
  /// </para>
  /// </summary>
  private void CollectIdioms(IrFunction function) {
    if (!this._target.Optimize)
      return;
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions) {
        switch (instr) {
          case IrSelect select when MinMaxForm(select) is var (cmp, pred, swap):
            this._canonicalPredicate[cmp] = pred;
            if (swap)
              this._swappedArms.Add(select);
            break;
          case IrBinary binary when AbsShape(binary) is { } shape:
            this._consumed.Add(shape.Mask);
            this._consumed.Add(shape.Xor);
            break;
          case IrBinary binary when SgnShape(binary) is { } sign:
            foreach (var absorbed in sign.Absorbed)
              this._consumed.Add(absorbed);
            break;
          case IrCast { Op: IrCastOp.SIToFP } cast when ReadableAsIntegerCell(cast):
            this._integerOperands[cast] = null;
            break;
        }
      }
  }

  /// <summary>The predicate to emit for a comparison - its own, unless the min/max rule relabelled it.</summary>
  private IrCmpPred PredicateOf(IrCmp cmp)
    => this._canonicalPredicate.TryGetValue(cmp, out var canonical) ? canonical : cmp.Pred;

  /// <summary>
  /// Whether a <c>select</c> is a minimum or a maximum over its comparison's own operands, and how to
  /// spell it canonically: the comparison's predicate as it should be EMITTED, plus whether the
  /// select's arms have to change places to go with it. Null when the select is not one, or is already
  /// in the canonical shape.
  /// </summary>
  private static (IrCmp Cmp, IrCmpPred Pred, bool Swap)? MinMaxForm(IrSelect select) {
    if (select.Condition is not IrCmp { Users.Count: 1 } cmp || !cmp.Lhs.Type.IsInteger)
      return null;
    bool swap;
    if (SameValue(cmp.Lhs, select.IfTrue) && SameValue(cmp.Rhs, select.IfFalse))
      swap = false;
    else if (SameValue(cmp.Lhs, select.IfFalse) && SameValue(cmp.Rhs, select.IfTrue))
      swap = true;
    else
      return null;
    if (Negated(cmp.Pred) is null)
      return null;                                     // an equality select is not an ordering
    var effective = swap ? Negated(cmp.Pred)!.Value : cmp.Pred;
    if (OrEqual(effective) is not { } pred)
      return null;
    return !swap && pred == cmp.Pred ? null : (cmp, pred, swap);
  }

  /// <summary>Whether this select's arms are read the other way round (see <see cref="MinMaxForm"/>).</summary>
  private bool HasSwappedArms(IrSelect select) => this._swappedArms.Contains(select);

  /// <summary>
  /// The or-equal spelling of an ordering predicate - itself when it already is one, null for
  /// equality and for anything unordered.
  /// </summary>
  private static IrCmpPred? OrEqual(IrCmpPred pred) => pred switch {
    IrCmpPred.Slt or IrCmpPred.Sle => IrCmpPred.Sle,
    IrCmpPred.Sgt or IrCmpPred.Sge => IrCmpPred.Sge,
    IrCmpPred.Ult or IrCmpPred.Ule => IrCmpPred.Ule,
    IrCmpPred.Ugt or IrCmpPred.Uge => IrCmpPred.Uge,
    _ => null,
  };

  /// <summary>The predicate that answers the opposite of this one; null for the float predicates.</summary>
  private static IrCmpPred? Negated(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq => IrCmpPred.Ne,
    IrCmpPred.Ne => IrCmpPred.Eq,
    IrCmpPred.Slt => IrCmpPred.Sge,
    IrCmpPred.Sle => IrCmpPred.Sgt,
    IrCmpPred.Sgt => IrCmpPred.Sle,
    IrCmpPred.Sge => IrCmpPred.Slt,
    IrCmpPred.Ult => IrCmpPred.Uge,
    IrCmpPred.Ule => IrCmpPred.Ugt,
    IrCmpPred.Ugt => IrCmpPred.Ule,
    IrCmpPred.Uge => IrCmpPred.Ult,
    _ => null,
  };

  /// <summary>
  /// Whether two operands denote the same value. Reference identity for anything the program computed;
  /// an integer literal is compared by value and width, because the lowering mints a fresh constant per
  /// mention and the <c>7</c> in a compare is not the same object as the <c>7</c> in the arm beside it.
  /// </summary>
  private static bool SameValue(IrValue left, IrValue right)
    => ReferenceEquals(left, right)
       || (left is IrConstantInt a && right is IrConstantInt b
           && a.Value == b.Value && a.Type.Bits == b.Type.Bits);

  /// <summary>
  /// The branchless absolute value, as the optimizer leaves it:
  /// <c>m = x &gt;&gt;a 15; (x XOR m) - m</c>. Answers the three instructions when this subtraction is
  /// its tail, and null otherwise.
  ///
  /// <para>
  /// The mask must have exactly two users (the XOR and this subtraction) and the XOR exactly one, so
  /// nothing else can observe either intermediate - which is what makes replacing all three with a
  /// four-instruction accumulator sequence an equivalence rather than a duplication. Both must also sit
  /// in THIS block: they dominate the subtraction wherever they are, but moving work downwards across a
  /// branch could move it into a loop.
  /// </para>
  /// </summary>
  private static (IrValue Source, IrBinary Mask, IrBinary Xor)? AbsShape(IrBinary sub) {
    if (sub.Op != IrBinaryOp.Sub || !sub.Type.IsInteger || sub.Type.Bits != 16)
      return null;
    if (sub.Rhs is not IrBinary { Op: IrBinaryOp.AShr, Rhs: IrConstantInt { Value: 15 } } mask)
      return null;
    if (sub.Lhs is not IrBinary { Op: IrBinaryOp.Xor } xor)
      return null;
    var source = mask.Lhs;
    var pairs = (ReferenceEquals(xor.Lhs, source) && ReferenceEquals(xor.Rhs, mask))
                || (ReferenceEquals(xor.Rhs, source) && ReferenceEquals(xor.Lhs, mask));
    if (!pairs || xor.Users.Count != 1 || mask.Users.Count != 2)
      return null;
    if (!ReferenceEquals(mask.Parent, sub.Parent) || !ReferenceEquals(xor.Parent, sub.Parent))
      return null;
    return (source, mask, xor);
  }

  // ---- x87 memory operands ------------------------------------------------------------------------

  /// <summary>
  /// The four popping x87 operations and the memory form of each - <c>ST(0) op= [cell]</c>.
  /// </summary>
  private static readonly Dictionary<IrBinaryOp, (MOpcode Real, MOpcode Integer)> _floatMemoryOps = new() {
    [IrBinaryOp.FAdd] = (MOpcode.Fadd, MOpcode.Fiadd),
    [IrBinaryOp.FSub] = (MOpcode.Fsub, MOpcode.Fisub),
    [IrBinaryOp.FMul] = (MOpcode.Fmul, MOpcode.Fimul),
    [IrBinaryOp.FDiv] = (MOpcode.Fdiv, MOpcode.Fidiv),
  };

  /// <summary>
  /// The integer widenings that are never converted at all: their cell is staged where the conversion
  /// stood and every consumer reads the INTEGER out of it. The value is the cell, filled in on first
  /// use - null until the selection reaches the widening.
  /// </summary>
  private readonly Dictionary<IrCast, MOperand?> _integerOperands = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Whether an integer widening can be left as the integer it is - <c>x! + i%</c> reaches the IR as
  /// <c>fadd x, (sitofp i)</c>, and the x87 reads a word or dword integer cell directly
  /// (<c>FIADD</c>), so converting first spends a <c>FILD</c>, an 80-bit temporary and a store on a
  /// value the operation could have read itself.
  ///
  /// <para>
  /// EVERY consumer has to be one of those operations and has to take the widening as its RIGHT
  /// operand, because that is the only side the memory form reads. One consumer that wants the
  /// converted value wants the whole conversion, and there would then be two spellings of it.
  /// </para>
  ///
  /// <para>
  /// Only a SIGNED source, and only 16 or 32 bits: those are the two widths the integer memory forms
  /// have, and both read their cell as signed. An unsigned source has to be widened by hand before
  /// even <c>FILD</c> can read it (<see cref="SelectIntToFloat"/> explains why), so its cell is not one
  /// the program already has.
  /// </para>
  /// </summary>
  private static bool ReadableAsIntegerCell(IrCast cast)
    => cast.Type.IsIeeeFloat
       && cast.Value.Type is { IsInteger: true, Signed: true, Bits: 16 or 32 }
       && cast.Users.Count > 0
       && cast.Users.All(user => user is IrBinary { IsFloatOp: true, Type.IsMbf: false } binary
            && _floatMemoryOps.ContainsKey(binary.Op)
            && ReferenceEquals(binary.Rhs, cast) && !ReferenceEquals(binary.Lhs, cast));

  /// <summary>
  /// Whether this widening is one of those, and - the first time selection reaches it - stages its
  /// integer into the cell every consumer will read. Answering false leaves the ordinary conversion
  /// path to it, which is also what happens when the integer has no machine operand.
  /// </summary>
  private bool StagesAsInteger(IrCast cast) {
    if (!this._integerOperands.TryGetValue(cast, out var cell))
      return false;
    if (cell is not null)
      return true;
    if (this.StageInteger(cast.Value) is not { } staged) {
      this._integerOperands.Remove(cast);
      return false;
    }
    this._integerOperands[cast] = staged;
    return true;
  }

  /// <summary>
  /// A floating operation whose right operand the x87 can read out of memory:
  /// <c>FLD lhs; F&lt;op&gt; [rhs]</c> instead of <c>FLD lhs; FLD rhs; F&lt;op&gt;P</c>.
  ///
  /// <para>
  /// Two operands qualify and no others. A LITERAL is already a qword in the code generator's float
  /// pool, which is the very cell the direct emitter multiplies by. And an INTEGER value widened for
  /// this operation is staged into a word or dword cell the integer form reads directly - the staging
  /// store is the same one <see cref="SelectIntToFloat"/> would have written, minus the <c>FILD</c>,
  /// the 80-bit temporary and the store into it.
  /// </para>
  ///
  /// <para>
  /// An ordinary intermediate does NOT qualify, and that is a property of the machine rather than a
  /// missed case: this back end parks every float temporary at the x87's own 80-bit width (see
  /// <see cref="FloatCell"/>, where the reason is fidelity), and <c>FADD</c> has no tbyte form. Only
  /// <c>FLD</c> reaches ten bytes.
  /// </para>
  /// </summary>
  private bool TrySelectFloatMemoryBinary(IrBinary bin) {
    if (!this._target.Optimize || bin.Type.IsMbf || !_floatMemoryOps.TryGetValue(bin.Op, out var forms))
      return false;
    if (!this.TryFloatOperand(bin.Lhs, out var lhs))
      return false;

    MOperand cell;
    MOpcode opcode;
    if (bin.Rhs is IrConstantFloat constant) {
      cell = new MOperand.DataCell(FloatConstantName(constant.Value), 0, MRegSize.Qword);
      opcode = forms.Real;
    } else if (bin.Rhs is IrCast widening && this._integerOperands.TryGetValue(widening, out var staged)
               && staged is not null) {
      cell = staged;
      opcode = forms.Integer;
    } else {
      return false;
    }

    this.EmitX87(MOpcode.Fld, lhs, reads: true);
    this.EmitX87(opcode, cell, reads: true);
    this.EmitX87(MOpcode.Fstp, this.FloatCell(bin), reads: false);
    return true;
  }

  /// <summary>
  /// A float comparison against a literal, read out of the constant pool instead of pushed:
  /// <c>FLD a; FCOMP [k]</c>. <c>FCOMP</c> pops the one value it compared, so the stack is empty
  /// afterwards exactly as it is after the <c>FLD/FLD/FXCH/FCOMPP</c> sequence it replaces, and the
  /// status word it leaves is the same comparison of the same two values in the same order.
  /// </summary>
  private bool TrySelectFloatMemoryCompare(IrCmp cmp, Condition cc) {
    if (!this._target.Optimize || cmp.Rhs is not IrConstantFloat constant)
      return false;
    if (!this.TryFloatOperand(cmp.Lhs, out var lhs))
      return false;

    var ax = new MOperand.Register(MReg.Physical_(Reg.AX));
    this.EmitX87(MOpcode.Fld, lhs, reads: true);
    this.EmitX87(MOpcode.Fcomp,
      new MOperand.DataCell(FloatConstantName(constant.Value), 0, MRegSize.Qword), reads: true);
    this._current.Instructions.Add(new MInstr(MOpcode.FstswAx, [ax],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false), clobbers: [Reg.AX]));
    this._current.Instructions.Add(new MInstr(MOpcode.Sahf, [ax],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    return this.MaterializeCondition(cmp, cc);
  }

  /// <summary>
  /// Puts a 16- or 32-bit signed integer into a frame cell the x87's integer forms can read, and
  /// answers that cell. Null when the value has no machine operand, which declines the fold rather
  /// than the function - the ordinary conversion path is still there.
  /// </summary>
  private MOperand? StageInteger(IrValue value) {
    var wide = IsWide(value.Type);
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(wide ? 4 : 2);
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);
    if (wide) {
      if (!this.TryOperandPair(value, out var lo, out var hi))
        return null;
      this.StoreWord(cell, lo);
      this.StoreWord(Shifted(cell, 2), hi);
      return new MOperand.StackSlot(slot, MRegSize.Dword);
    }
    if (!this.TryOperand(value, out var word))
      return null;
    this.StoreWord(cell, word);
    return cell;
  }

  /// <summary>
  /// <c>ABS</c> over a 16-bit value, branchless and in the accumulator:
  /// <code>
  ///   MOV AX, x
  ///   CWD              ; DX = 0 or -1, the sign of AX smeared
  ///   XOR AX, DX       ; ones-complement when negative, unchanged when not
  ///   SUB AX, DX       ; ...and the +1 that makes it two's complement
  /// </code>
  /// which is exactly what the direct emitter writes (O0249), and five bytes shorter than the
  /// shift/xor/subtract chain over three virtual registers that selecting the three IR instructions
  /// separately produces - <c>SAR r,15</c> alone is fifteen <c>SAR r,1</c>s on an 8086.
  ///
  /// <para>
  /// <c>CWD</c> is why this cannot be done over virtual registers: it names <c>DX</c> and reads
  /// <c>AX</c>, and there is no other instruction on this part that produces a sign mask in one step.
  /// Both are declared clobbers on every instruction of the sequence, the same way
  /// <see cref="SelectAccumulatorMultiply"/> and <see cref="SelectDivide"/> hold the pair.
  /// </para>
  /// </summary>
  private bool TrySelectBranchlessAbs(IrBinary bin) {
    if (!this._target.Optimize || AbsShape(bin) is not { } shape)
      return false;
    if (!this.TryOperand(shape.Source, out var source))
      return false;

    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    var dx = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
    Reg[] pinned = [Reg.AX, Reg.DX];
    MInstrEffect Accumulate() => new(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false,
      WritesFlags: true, ReadsMemory: false, WritesMemory: false);

    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, source], MovEffect(ax, source),
      condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Cwd, [],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Xor, [ax, dx], Accumulate(), condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Sub, [ax, dx], Accumulate(), condition: null, clobbers: pinned));

    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ax], MovEffect(destOp, ax)));
    return true;
  }

  /// <summary>
  /// The three-way sign, as the lowering spells it: <c>(x &gt; 0) - (x &lt; 0)</c>, two comparisons
  /// widened to integers and subtracted. Answers the source and the four instructions this subtraction
  /// is the tail of, or null.
  ///
  /// <para>
  /// Each of the four has exactly one user - the next link of the chain - so nothing else can observe
  /// a partial result, and all four sit in this block. Selected separately they are not four
  /// instructions but two materialized truth values, which on this target is two compares, four moves
  /// and a diamond apiece.
  /// </para>
  /// </summary>
  private static (IrValue Source, IrInstruction[] Absorbed)? SgnShape(IrBinary sub) {
    if (sub.Op != IrBinaryOp.Sub || !sub.Type.IsInteger || sub.Type.Bits != 16)
      return null;
    if (WidenedComparisonAgainstZero(sub.Lhs, IrCmpPred.Sgt) is not var (positive, greater)
        || WidenedComparisonAgainstZero(sub.Rhs, IrCmpPred.Slt) is not var (negative, less))
      return null;
    if (!ReferenceEquals(greater.Lhs, less.Lhs) || greater.Lhs.Type is not { IsInteger: true, Signed: true, Bits: 16 })
      return null;
    IrInstruction[] absorbed = [positive, negative, greater, less];
    return absorbed.All(instr => ReferenceEquals(instr.Parent, sub.Parent))
      ? (greater.Lhs, absorbed) : null;
  }

  /// <summary>One arm of the sign: <c>zext (icmp pred x, 0)</c>, used by nothing but the subtraction.</summary>
  private static (IrCast Widening, IrCmp Compare)? WidenedComparisonAgainstZero(IrValue value, IrCmpPred pred)
    => value is IrCast { Op: IrCastOp.ZExt, Users.Count: 1 } widening
       && widening.Value is IrCmp { Users.Count: 1, Rhs: IrConstantInt { Value: 0 } } compare
       && compare.Pred == pred
      ? (widening, compare) : null;

  /// <summary>
  /// <c>SGN</c> over a 16-bit value, branchless and off the x87 (O0249):
  /// <code>
  ///   MOV AX, x
  ///   CWD              ; DX = 0 or -1, the sign smeared
  ///   NEG AX           ; ...and CF = (x &lt;&gt; 0)
  ///   ADC DX, DX       ; 2*sign + (x &lt;&gt; 0): -1, 0 or 1
  /// </code>
  /// which is the direct emitter's sequence, instruction for instruction. Reading the three cases off
  /// it is the whole proof: a positive <c>x</c> gives <c>DX = 0</c> and <c>CF = 1</c>, so <c>1</c>; a
  /// negative one gives <c>DX = -1</c> and <c>CF = 1</c>, so <c>-2 + 1 = -1</c>; and zero gives
  /// <c>DX = 0</c> with <c>CF = 0</c>, because <c>NEG 0</c> is the one case that clears the carry.
  /// </summary>
  private bool TrySelectBranchlessSgn(IrBinary bin) {
    if (!this._target.Optimize || SgnShape(bin) is not { } shape)
      return false;
    if (!this.TryOperand(shape.Source, out var source))
      return false;

    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    var dx = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
    Reg[] pinned = [Reg.AX, Reg.DX];

    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, source], MovEffect(ax, source),
      condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Cwd, [],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Neg, [ax],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Adc, [dx, dx],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: true, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), condition: null, clobbers: pinned));

    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, dx], MovEffect(destOp, dx)));
    return true;
  }
}
