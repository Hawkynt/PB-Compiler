using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Selection of an <see cref="IrSwitch"/> into a dispatch that is not a compare per case: an unsigned
/// range test, a compile-time membership mask, a word jump table, its byte-indexed compression, or a
/// key-verified perfect hash, or a balanced sparse decision tree. This is the machine-level half of
/// what <c>Ir/Passes/SwitchFormation.cs</c> recovered - the pass says WHICH values go where, this file
/// says how an 8086 gets there.
///
/// <para>
/// It lives here rather than in a pass because every one of these shapes is a statement about the
/// instruction set and about the objective, and the IR knows neither. A jump table needs an addressing
/// mode with a base register and a displacement; the byte-index table trades one load for half the
/// bytes and is therefore only right under <c>$OPTIMIZE SIZE</c>; the membership mask needs a variable
/// shift, and a window wider than 15 needs a 32-bit one, which needs <c>$CPU 80386</c>. A pass choosing
/// between them would be choosing an encoding it cannot see.
/// </para>
///
/// <para>
/// <b>The shapes are tried cheapest-answer-first</b>, and the order is what makes each one's gate
/// simple: a contiguous run to one arm is a range and never anything else; a scattered set to one arm
/// is a mask; several arms over a small span are a table; a wide span whose values separate under a low
/// bit mask is a hash; a many-case set no constant-time shape can cover is a balanced tree. Anything
/// left falls through to the compare chain, which is what the selector already did and remains the
/// right answer for a few cases.
/// </para>
///
/// <para>
/// <b>Every shape holds the subject in AX and works in AX/BX/CX</b>, exactly as the direct emitter's
/// does, and for the same two reasons: <c>SI</c>/<c>DI</c> may hold a resident FOR counter that has to
/// survive the dispatch, and the indexed jump's base can only be <c>BX</c> on this target. Fixed
/// registers in the middle of an allocated function need saying twice - the instructions carry
/// <see cref="MInstr.Clobbers"/> so that a value live across the dispatch is denied those registers,
/// and the same list makes each one a scheduling barrier, so nothing independent can be moved into a
/// sequence whose registers are already spoken for.
/// </para>
///
/// <para>
/// <b>What is deliberately not here.</b> A sparse 32-bit subject still dispatches through the existing
/// high/low compare chain: the tree below compares a 16-bit value, while a LONG needs a sign-extension
/// guard before that is sound. Dense LONG sets do have a table path because subtracting the minimum
/// reduces their bounded window to a guarded word index.
/// </para>
/// </summary>
public sealed partial class InstructionSelector {

  /// <summary>
  /// Below this every shape here loses to the compare chain it would replace. Two equality compares are
  /// already the whole dispatch; three is where the mask - one shift and one test, whatever the count -
  /// starts to pay.
  /// </summary>
  private const int _MIN_DISPATCH_CASES = 3;

  /// <summary>The widest span a dense table covers: past it the table is mostly default entries.</summary>
  private const int _MAX_TABLE_SPAN = 256;

  /// <summary>
  /// How many table entries one case value may buy before the table stops being dense enough to pay.
  /// </summary>
  private const int _TABLE_DENSITY = 4;

  /// <summary>The smallest set the perfect hash will look at, and the widest mask it will try.</summary>
  private const int _MIN_HASH_CASES = 8;
  private const int _MAX_HASH_BITS = 8;

  /// <summary>The direct emitter's break-even point for a balanced sparse decision tree.</summary>
  private const int _MIN_TREE_CASES = 8;

  // the registers each shape works in; a value live across the dispatch must avoid exactly these
  private static readonly IReadOnlyList<Reg> _rangeRegisters = [Reg.AX];
  private static readonly IReadOnlyList<Reg> _maskRegisters = [Reg.AX, Reg.CX];
  private static readonly IReadOnlyList<Reg> _tableRegisters = [Reg.AX, Reg.BX];
  private static readonly IReadOnlyList<Reg> _wideTableRegisters = [Reg.AX, Reg.BX, Reg.DX];
  private static readonly IReadOnlyList<Reg> _hashRegisters = [Reg.AX, Reg.BX, Reg.CX];

  /// <summary>
  /// Selects <paramref name="sw"/> into a constant-time dispatch, or false to leave it to the compare
  /// chain. The cursor is restored to the block the switch arrived in, because that is the block
  /// control leaves the IR predecessor from and therefore where its phi copies belong.
  /// </summary>
  private bool TrySelectDispatch(IrSwitch sw) {
    if (!this._target.Optimize || sw.Condition.Type.Bits is not (16 or 32))
      return false;

    // first case naming a value wins, which is what SELECT CASE means and what IrSwitch already promises
    var arms = new List<(long Value, string Target)>();
    var seen = new HashSet<long>();
    foreach (var (value, target) in sw.Cases) {
      var normalized = sw.Condition.Type.Bits == 16 ? unchecked((short)value) : unchecked((int)value);
      if (seen.Add(normalized))
        arms.Add((normalized, target.Label));
    }
    if (arms.Count < _MIN_DISPATCH_CASES)
      return false;

    var dispatch = this._current;
    var fallback = sw.DefaultTarget.Label;
    long min = arms.Min(a => a.Value), max = arms.Max(a => a.Value);
    var span = max - min + 1;
    var targets = arms.Select(a => a.Target).Distinct(System.StringComparer.Ordinal).ToList();

    var selected = sw.Condition.Type.Bits == 32
      ? this.TryWideTableDispatch(sw.Condition, arms, min, max, span, fallback)
      : this.TryOperand(sw.Condition, out var operand) && operand is MOperand.Register subject && (
      this.TryRangeDispatch(subject, arms, targets, min, span, fallback)
      || this.TryMaskDispatch(subject, arms, targets, min, max, fallback)
      || this.TryTableDispatch(subject, arms, min, max, span, fallback)
      || this.TryHashDispatch(subject, arms, fallback)
      || this.TryTreeDispatch(subject, arms, fallback));
    if (!selected)
      return false;

    this._current = dispatch;
    return true;
  }

  /// <summary>
  /// A contiguous run of values all reaching the same arm is a RANGE, and an unsigned compare answers
  /// it in one test: <c>(subject - lo) &lt;=u (hi - lo)</c> is true exactly on <c>lo..hi</c>, because a
  /// subject below <c>lo</c> wraps to a large unsigned value. This is the direct emitter's O0032 fold
  /// arriving from the other side - the source wrote <c>CASE 0 TO 9</c>, the lowering turned it into two
  /// signed compares, and the pass turned those into ten cases, which is what makes them contiguous.
  /// </summary>
  private bool TryRangeDispatch(MOperand.Register subject, List<(long Value, string Target)> arms,
      List<string> targets, long min, long span, string fallback) {
    if (targets.Count != 1 || span != arms.Count)
      return false;

    this.EmitDispatchSubject(subject, min, _rangeRegisters);
    this.EmitDispatchCompare(span - 1, _rangeRegisters);
    this.EmitDispatchBranch(Condition.BelowOrEqual, targets[0], _rangeRegisters);
    this.EmitDispatchJump(fallback, _rangeRegisters);
    return true;
  }

  /// <summary>
  /// A scattered set of values all reaching the same arm is answered by a bit per value, built at
  /// compile time and brought down to bit 0 by the subject itself: normalize, guard the window, then
  /// <c>SHR mask, CL</c> and test bit 0. Constant time and no compare per value - the direct emitter's
  /// O0099, and the same shape whether the source spelled it <c>CASE 1, 8, 15</c>, <c>IF k = 1 OR ...</c>
  /// or the De Morgan complement <c>IF k &lt;&gt; 2 AND ...</c>, all three of which the pass reduced to
  /// this one set.
  ///
  /// <para>
  /// The WINDOW bounds it, not the count: <c>max - min</c> up to 15 fits a native 16-bit mask, 16 to 31
  /// needs a 32-bit one and therefore an 80386, and wider than that there is no register to hold the
  /// mask at all - so it declines and the chain stays. Gated on <c>$OPTIMIZE SPEED</c>, as the direct
  /// emitter's is.
  /// </para>
  /// </summary>
  private bool TryMaskDispatch(MOperand.Register subject, List<(long Value, string Target)> arms,
      List<string> targets, long min, long max, string fallback) {
    if (!this._target.OptimizeSpeed || targets.Count != 1)
      return false;
    var window = max - min;
    if (window > 31 || (window > 15 && !this._target.Cpu386))
      return false;

    var wide = window > 15;
    var mask = 0L;
    foreach (var (value, _) in arms)
      mask |= 1L << (int)(value - min);

    // the window guard comes first, and it is not an optimization: a subject outside the window
    // normalizes to a count the mask has no bit for, and what a shift by such a count leaves behind is
    // a property of the part (the 8086 does not mask the count, every later x86 masks it to five bits)
    // rather than an answer. Rejecting out-of-window subjects before the shift is what makes the bit
    // test mean membership.
    this.EmitDispatchSubject(subject, min, _maskRegisters);
    this.EmitDispatchCompare(window, _maskRegisters);
    this.EmitDispatchBranch(Condition.Above, fallback, _maskRegisters);

    this.ContinueDispatch("mask", _maskRegisters);
    var counter = Pinned(Reg.CX);
    var accumulator = Pinned(wide ? Reg.EAX : Reg.AX, wide ? MRegSize.Dword : MRegSize.Word);
    var subjectAx = Pinned(Reg.AX);
    this.EmitPinned(MOpcode.Mov, [counter, subjectAx], MovEffect(counter, subjectAx), _maskRegisters);
    var literal = new MOperand.Immediate(wide ? unchecked((int)mask) : unchecked((short)mask));
    this.EmitPinned(MOpcode.Mov, [accumulator, literal], MovEffect(accumulator, literal), _maskRegisters);
    var count = Pinned(Reg.CL, MRegSize.Byte);
    this.EmitPinned(MOpcode.Shr, [accumulator, count],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), _maskRegisters);
    this.EmitPinned(MOpcode.Test, [accumulator, new MOperand.Immediate(1)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), _maskRegisters);
    this.EmitDispatchBranch(Condition.NotEqual, targets[0], _maskRegisters);
    this.EmitDispatchJump(fallback, _maskRegisters);
    return true;
  }

  /// <summary>
  /// Several arms over a small span dispatch through a table of their addresses: normalize the subject
  /// to a 0-based index, one unsigned bounds check for everything outside, then the indexed indirect
  /// jump. O(1) whatever the arm count.
  ///
  /// <para>
  /// The table is dense - one entry per value in the span, defaults filled in - because a dense table
  /// needs no search. That is only affordable while the span stays close to the case count, which is
  /// what the density gate says. Under <c>$OPTIMIZE SIZE</c> the entries become byte SLOT numbers into a
  /// small address table whenever that is actually smaller (<c>span + 2*slots</c> against
  /// <c>2*span</c>), which is the shape a wide span with few distinct arms wants; under SPEED the plain
  /// word table stays, because one extra load per dispatch is not worth the bytes.
  /// </para>
  /// </summary>
  private bool TryTableDispatch(MOperand.Register subject, List<(long Value, string Target)> arms,
      long min, long max, long span, string fallback) {
    if (!IsDenseTable(arms, span))
      return false;

    this.EmitDispatchSubject(subject, min, _tableRegisters);
    this.EmitDispatchCompare(span, _tableRegisters);
    this.EmitDispatchBranch(Condition.AboveOrEqual, fallback, _tableRegisters);
    this.EmitIndexedJump(this.BuildDispatchTable(arms, min, max, span, fallback), fallback, _tableRegisters);
    return true;
  }

  /// <summary>
  /// A LONG table uses the same bounded low-word index as a word table, but first subtracts the
  /// minimum from the full DX:AX pair. A zero high word proves the normalized index fits in AX; the
  /// ordinary unsigned span check then distinguishes the table window from the rest of that word.
  /// </summary>
  private bool TryWideTableDispatch(IrValue condition, List<(long Value, string Target)> arms,
      long min, long max, long span, string fallback) {
    if (!IsDenseTable(arms, span) || !this.TryOperandPair(condition, out var low, out var high))
      return false;

    var ax = Pinned(Reg.AX);
    var dx = Pinned(Reg.DX);
    this.EmitPinned(MOpcode.Mov, [ax, low], DispatchMovEffect(low), _wideTableRegisters);
    this.EmitPinned(MOpcode.Mov, [dx, high], DispatchMovEffect(high), _wideTableRegisters);
    if (min != 0) {
      var bits = unchecked((uint)(int)min);
      this.EmitPinned(MOpcode.Sub, [ax, WordImmediate((ushort)bits)],
        new MInstrEffect([0], [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false), _wideTableRegisters);
      this.EmitPinned(MOpcode.Sbb, [dx, WordImmediate((ushort)(bits >> 16))],
        new MInstrEffect([0], [0], ReadsFlags: true, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false), _wideTableRegisters);
    }
    this.EmitPinned(MOpcode.Test, [dx, dx],
      new MInstrEffect([], [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), _wideTableRegisters);
    this.EmitDispatchBranch(Condition.NotEqual, fallback, _wideTableRegisters);
    this.ContinueDispatch("table.word", _wideTableRegisters);

    this.EmitDispatchCompare(span, _wideTableRegisters);
    this.EmitDispatchBranch(Condition.AboveOrEqual, fallback, _wideTableRegisters);
    this.EmitIndexedJump(this.BuildDispatchTable(arms, min, max, span, fallback), fallback, _wideTableRegisters);
    return true;
  }

  private static bool IsDenseTable(List<(long Value, string Target)> arms, long span)
    => arms.Count >= _TABLE_DENSITY && span <= _MAX_TABLE_SPAN && span <= _TABLE_DENSITY * (long)arms.Count;

  /// <summary>Builds the dense target vector, or SIZE's byte indirection over its distinct targets.</summary>
  private MOperand.BlockAddressTable BuildDispatchTable(List<(long Value, string Target)> arms,
      long min, long max, long span, string fallback) {
    var armOf = arms.ToDictionary(a => a.Value, a => a.Target);
    var slotOf = new Dictionary<string, int>(System.StringComparer.Ordinal);
    var slots = new List<string>();
    var index = new byte[span];
    for (var value = min; value <= max; ++value) {
      var target = armOf.TryGetValue(value, out var arm) ? arm : fallback;
      if (!slotOf.TryGetValue(target, out var slot)) {
        slot = slots.Count;
        slotOf[target] = slot;
        slots.Add(target);
      }
      index[value - min] = (byte)slot;
    }

    return this._target.OptimizeSize && slots.Count <= 256 && span > 2L * slots.Count
      ? new MOperand.BlockAddressTable(slots, index)
      : new MOperand.BlockAddressTable([.. index.Select(slot => slots[slot])]);
  }

  /// <summary>
  /// A set too wide for a dense table can still be constant time when its values separate under a low
  /// bit mask: <c>value AND (2^k - 1)</c> is then a perfect hash, and the dispatch is one AND plus one
  /// indexed jump. The hash is injective on the case values and on nothing else, so the value keyed at
  /// the slot is verified against the subject first - that verify is what makes the shape safe rather
  /// than merely fast. The direct emitter's O0100; gated on <c>$OPTIMIZE SPEED</c>, and on enough cases
  /// to be worth two tables.
  /// </summary>
  private bool TryHashDispatch(MOperand.Register subject, List<(long Value, string Target)> arms, string fallback) {
    if (!this._target.OptimizeSpeed || arms.Count < _MIN_HASH_CASES)
      return false;

    var bits = -1;
    for (var width = 3; width <= _MAX_HASH_BITS; ++width) {
      var mask = (1 << width) - 1;
      var used = new HashSet<long>();
      if (arms.All(a => used.Add(a.Value & mask))) {
        bits = width;
        break;
      }
    }
    if (bits < 0)
      return false;

    var keyMask = (1 << bits) - 1;
    var size = 1 << bits;
    var keys = new ushort[size];
    var slots = new string[size];
    for (var slot = 0; slot < size; ++slot)
      slots[slot] = fallback;                        // an empty slot answers the default whatever hashes there
    foreach (var (value, target) in arms) {
      keys[value & keyMask] = unchecked((ushort)value);
      slots[value & keyMask] = target;
    }

    this.EmitDispatchSubject(subject, 0, _hashRegisters);
    this.EmitIndexedJump(new MOperand.BlockAddressTable(slots, Keys: keys, KeyMask: keyMask), fallback, _hashRegisters);
    return true;
  }

  /// <summary>
  /// A large sparse set that cannot use a table or perfect hash is searched by signed comparisons
  /// against successive medians. Each source value is compared once somewhere in the tree, while a
  /// lookup takes logarithmic rather than linear comparisons. The equal and greater branches are
  /// consecutive terminators so they consume the same CMP flags and remain pinned by the scheduler.
  /// </summary>
  private bool TryTreeDispatch(MOperand.Register subject, List<(long Value, string Target)> arms,
      string fallback) {
    if (!this._target.OptimizeSpeed || arms.Count < _MIN_TREE_CASES)
      return false;

    var root = this._current;
    var points = arms.OrderBy(arm => arm.Value).ToList();

    MBlock NewNode() {
      var node = new MBlock($"{root.Label}.tree{this._splitCount++}");
      this._function.Blocks.Add(node);
      return node;
    }

    void Build(MBlock node, int low, int high) {
      this._current = node;
      var middle = (low + high) / 2;
      var point = points[middle];
      this.EmitCompare(subject, WordImmediate(unchecked((ushort)point.Value)));
      this.EmitBranch(Condition.Equal, point.Target);
      AddSuccessor(node, point.Target);

      var hasLeft = low < middle;
      var hasRight = middle < high;
      if (!hasLeft && !hasRight) {
        this.EmitJump(fallback);
        AddSuccessor(node, fallback);
        return;
      }

      var left = hasLeft ? NewNode() : null;
      var right = hasRight ? NewNode() : null;
      if (left is { } leftNode && right is { } rightNode) {
        this.EmitBranch(Condition.Greater, rightNode.Label);
        AddSuccessor(node, rightNode.Label);
        this.EmitJump(leftNode.Label);
        AddSuccessor(node, leftNode.Label);
      } else if (left is { } onlyLeft) {
        this.EmitBranch(Condition.Greater, fallback);
        AddSuccessor(node, fallback);
        this.EmitJump(onlyLeft.Label);
        AddSuccessor(node, onlyLeft.Label);
      } else {
        this.EmitBranch(Condition.Less, fallback);
        AddSuccessor(node, fallback);
        this.EmitJump(right!.Label);
        AddSuccessor(node, right.Label);
      }

      if (left is { })
        Build(left, low, middle - 1);
      if (right is { })
        Build(right, middle + 1, high);
    }

    Build(root, 0, points.Count - 1);
    this._current = root;
    return true;
  }

  // ---- the pieces every shape is built from --------------------------------

  private static MOperand.Register Pinned(Reg register, MRegSize size = MRegSize.Word)
    => new(MReg.Physical_(register, size));

  private static MInstrEffect DispatchMovEffect(MOperand source)
    => new(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
      ReadsFlags: false, WritesFlags: false,
      ReadsMemory: source is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell,
      WritesMemory: false);

  /// <summary>Appends one instruction of a dispatch, claiming the registers the whole sequence works in.</summary>
  private void EmitPinned(MOpcode opcode, IReadOnlyList<MOperand> operands, MInstrEffect effect,
      IReadOnlyList<Reg> clobbers, Condition? condition = null)
    => this._current.Instructions.Add(new MInstr(opcode, operands, effect, condition, clobbers));

  /// <summary>Brings the subject into AX and makes it a 0-based index into the case window.</summary>
  private void EmitDispatchSubject(MOperand.Register subject, long min, IReadOnlyList<Reg> clobbers) {
    var accumulator = Pinned(Reg.AX);
    this.EmitPinned(MOpcode.Mov, [accumulator, subject], MovEffect(accumulator, subject), clobbers);
    if (min != 0)
      this.EmitPinned(MOpcode.Sub, [accumulator, new MOperand.Immediate(min)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false), clobbers);
  }

  private void EmitDispatchCompare(long against, IReadOnlyList<Reg> clobbers)
    => this.EmitPinned(MOpcode.Cmp, [Pinned(Reg.AX), new MOperand.Immediate(against)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), clobbers);

  private void EmitDispatchBranch(Condition condition, string target, IReadOnlyList<Reg> clobbers) {
    this.EmitPinned(MOpcode.Jcc, [new MOperand.LabelRef(target)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      clobbers, condition);
    AddSuccessor(this._current, target);
  }

  private void EmitDispatchJump(string target, IReadOnlyList<Reg> clobbers) {
    this.EmitPinned(MOpcode.Jmp, [new MOperand.LabelRef(target)], MInstrEffect.None, clobbers);
    AddSuccessor(this._current, target);
  }

  /// <summary>
  /// The indexed indirect jump and its table, plus the CFG edge to every arm the table names - which is
  /// the machine-level reason a block a table names cannot be dropped: it is a successor, and liveness
  /// reads successors.
  /// </summary>
  private void EmitIndexedJump(MOperand.BlockAddressTable table, string fallback, IReadOnlyList<Reg> clobbers) {
    this.EmitPinned(MOpcode.JmpIndexed, [Pinned(Reg.AX), table, new MOperand.LabelRef(fallback)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false), clobbers);
    AddSuccessor(this._current, fallback);
    foreach (var block in table.Blocks)
      AddSuccessor(this._current, block);
  }

  /// <summary>
  /// Opens the next machine block of a dispatch and leaves the cursor there. A conditional branch has to
  /// be the last instruction of its block - the scheduler pins only TRAILING terminators, so work after
  /// one could be moved in front of it - so every shape that tests twice is two blocks.
  /// </summary>
  private void ContinueDispatch(string stem, IReadOnlyList<Reg> clobbers) {
    var next = new MBlock($"{this._current.Label}.{stem}{this._splitCount++}");
    this._function.Blocks.Add(next);
    this.EmitDispatchJump(next.Label, clobbers);
    this._current = next;
  }
}
