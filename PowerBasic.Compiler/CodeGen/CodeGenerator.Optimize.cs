using System.Numerics;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 optimizations (docs/PB36.md). Every transformation here must preserve
/// observable behavior exactly - the differential harness re-runs all pb35
/// batteries under <c>--dialect pb36</c> against genuine PBC 3.50, and the
/// default pb35 code paths stay bit-identical (the pb36 checks read
/// <see cref="OptimizePb36"/> and change nothing when it is false).
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>True when pb36 optimizations may alter the emitted code (never its observable behavior).</summary>
  private bool OptimizePb36 => model.Dialect == Dialect.Pb36;

  private ConstantFolder? _pb36Folder;
  private ConstantFolder Pb36Folder => this._pb36Folder ??= new(model.Equates);

  /// <summary>
  /// Wraps a compile-time value to the silent-wrap storage semantics of
  /// <paramref name="type"/> - folded arithmetic must land on exactly the bits
  /// the runtime ALU would have produced (QUIRKS: PB wraps without $ERROR NUMERIC).
  /// </summary>
  public static long WrapToType(long value, ScalarType type) => type switch {
    { ByteSize: 1 } => (byte)value,
    { ByteSize: 2, Signed: true } => (short)value,
    { ByteSize: 2 } => (ushort)value,
    { ByteSize: 4, Signed: true } => (int)value,
    { ByteSize: 4 } => (uint)value,
    _ => value,
  };

  #region O1 - constant folding (integral, wrap-correct)

  /// <summary>
  /// pb36 O1: emits a constant integral expression as one folded literal load.
  /// Only pure integral expressions fold (the folder knows literals, equates
  /// and operators - never calls), and the result is wrapped to the bound
  /// type, so the bits match the unfolded runtime arithmetic exactly.
  /// </summary>
  private bool TryEmitFolded(Expression e) {
    if (!this.OptimizePb36)
      return false;

    // O9: literal string concatenation folds into one pooled literal
    if (model.TypeOf(e) is StringType) {
      if (this.Pb36Folder.TryFold(e) is not { Text: { } text })
        return false;
      this.EmitStringLiteral(text);
      return true;
    }

    if (model.TypeOf(e) is not ScalarType { IsFloat: false } type)
      return false;
    if (this.Pb36Folder.TryFold(e) is not { Integer: { } raw })
      return false;

    this.EmitIntegralConstant(WrapToType(raw, type), KindOf(type));
    return true;
  }

  /// <summary>
  /// Loads an integral constant into the evaluation registers. Under pb36 the
  /// zero idiom (O8) applies: <c>XOR r,r</c> instead of <c>MOV r,0</c> - safe
  /// here because expression results never carry live flags across statements.
  /// </summary>
  private void EmitIntegralConstant(long value, ValueKind kind) {
    var asm = this._asm;
    switch (kind) {
      case ValueKind.Int16:
        if (this.OptimizePb36 && (value & 0xFFFF) == 0)
          asm.Xor(Reg.AX, Reg.AX);
        else
          asm.Mov(Reg.AX, (int)value);
        break;

      case ValueKind.Int64:
        asm.Fild(Mem.Qword(this.QuadConstOf(value)));
        break;

      default: {
        var low = (int)(value & 0xFFFF);
        var high = (int)((value >> 16) & 0xFFFF);
        if (this.OptimizePb36 && low == 0)
          asm.Xor(Reg.AX, Reg.AX);
        else
          asm.Mov(Reg.AX, low);
        if (this.OptimizePb36 && high == 0)
          asm.Xor(Reg.DX, Reg.DX);
        else
          asm.Mov(Reg.DX, high);
        break;
      }
    }
  }

  #endregion

  #region C1/R3 - block-move widening

  /// <summary>True when $CPU 80386 (or higher) is selected - 32-bit string ops are legal.</summary>
  private bool Cpu386 => model.MetaStatements.Any(m =>
    m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase)
    && m.Arguments is [{ } level, ..]
    && level.Text is "80386" or "80486" or "386" or "486");

  /// <summary>
  /// REP-copies CX-free <paramref name="byteCount"/> bytes DS:SI -> ES:DI.
  /// pb35 keeps the byte-wide copy; pb36 widens to words (8086-safe) and to
  /// DWORDs under $CPU 80386, with the odd tail copied byte-wise - pure copies
  /// are width-agnostic, so behavior is identical.
  /// </summary>
  private void EmitBlockMove(int byteCount) {
    var asm = this._asm;
    if (!this.OptimizePb36 || byteCount < 4) {
      asm.Mov(Reg.CX, byteCount);
      asm.Rep();
      asm.Movsb();
      return;
    }

    if (this.Cpu386 && byteCount >= 8) {
      asm.Mov(Reg.CX, byteCount / 4);
      asm.Rep();
      asm.Movsd();
      if ((byteCount & 2) != 0)
        asm.Movsw();
    } else {
      asm.Mov(Reg.CX, byteCount / 2);
      asm.Rep();
      asm.Movsw();
    }
    if ((byteCount & 1) != 0)
      asm.Movsb();
  }

  #endregion

  #region O7 - small-trip loop unrolling ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O7: a constant-trip INTEGER FOR loop with at most 4 iterations and a
  /// small straight-line body unrolls completely - no compare, no jump, the
  /// counter slot is set per iteration and ends on the increment-then-test
  /// final value (QUIRK 2.28). SPEED-gated because unrolling trades size.
  /// Only signed INTEGER counters qualify (their compare semantics equal the
  /// simulated shorts); WORD/BYTE counters keep the generic loop.
  /// </summary>
  private bool TryEmitUnrolledFor(ForStmt f, VariableSymbol counter, Mem slot) {
    if (!this.OptimizePb36 || !this.OptimizeSpeed || !Equals(counter.Type, PbType.Integer))
      return false;
    if (this.Pb36Folder.TryFold(f.From) is not { Integer: { } fromRaw }
        || this.Pb36Folder.TryFold(f.To) is not { Integer: { } toRaw })
      return false;
    var stepRaw = 1L;
    if (f.Step != null) {
      if (this.Pb36Folder.TryFold(f.Step) is not { Integer: { } s })
        return false;
      stepRaw = s;
    }
    var from = (short)fromRaw;
    var to = (short)toRaw;
    var step = (short)stepRaw;
    if (step == 0)
      return false;

    // simulate the loop exactly as the generic engine runs it (signed compares,
    // silent 16-bit wrap on the increment)
    var values = new List<short>();
    var current = from;
    for (; values.Count <= 4; current = unchecked((short)(current + step))) {
      var continues = step > 0 ? current <= to : current >= to;
      if (!continues)
        break;
      values.Add(current);
    }
    if (values.Count > 4)
      return false; // too many iterations (or a wrapping endless loop)

    if (CountUnrollableStatements(f.Body, model, counter) is not { } bodySize || bodySize > 8)
      return false;

    var asm = this._asm;
    if (values.Count == 0) {
      asm.Mov(slot, (Imm)from); // zero-trip: FOR still assigns the start value
      return true;
    }

    foreach (var value in values) {
      asm.Mov(slot, (Imm)value);
      foreach (var statement in f.Body)
        this.EmitStatement(statement);
    }
    asm.Mov(slot, (Imm)(int)current); // first failing value, wrap included
    return true;
  }

  /// <summary>
  /// Counts the statements of an unrollable body; null when anything inside
  /// forbids duplication (control transfer, nested loops, error handling, or
  /// a write to the counter).
  /// </summary>
  private static int? CountUnrollableStatements(IReadOnlyList<Statement> body, SemanticModel model, VariableSymbol counter) {
    var count = 0;
    foreach (var statement in body) {
      ++count;
      switch (statement) {
        case AssignStmt a:
          if (WritesCounter(a.Target, model, counter))
            return null;
          continue;

        case IncrDecrStmt id:
          if (WritesCounter(id.Target, model, counter))
            return null;
          continue;

        case SwapStmt sw:
          if (WritesCounter(sw.Left, model, counter) || WritesCounter(sw.Right, model, counter))
            return null;
          continue;

        case PrintStmt or CallStmt or CommandStmt or MidAssignStmt or LsetRsetStmt
          or EraseStmt or DefSegStmt or SeekStmt or CloseStmt or OpenStmt:
          continue;

        case InputStmt input:
          if (input.Targets.Any(t => WritesCounter(t, model, counter)))
            return null;
          continue;

        case ReadStmt read:
          if (read.Targets.Any(t => WritesCounter(t, model, counter)))
            return null;
          continue;

        case IfStmt i: {
          var branches = new List<IReadOnlyList<Statement>> { i.Then };
          branches.AddRange(i.ElseIfs.Select(e => e.Body));
          if (i.Else != null)
            branches.Add(i.Else);
          foreach (var branch in branches) {
            if (CountUnrollableStatements(branch, model, counter) is not { } inner)
              return null;
            count += inner;
          }
          continue;
        }

        default:
          return null; // jumps, loops, SELECT, error handling, labels, declarations, ...
      }
    }
    return count;
  }

  private static bool WritesCounter(Expression target, SemanticModel model, VariableSymbol counter)
    => model.VariableBindings.TryGetValue(target, out var symbol) && ReferenceEquals(symbol, counter);

  #endregion

  #region O11 - literal pool packing (containment + overlap)

  /// <summary>
  /// Emits the string-literal pool. pb35 keeps one labeled blob per literal;
  /// pb36 packs them into a greedy superstring - contained literals share the
  /// host's bytes, and suffix/prefix overlaps merge. Sound because generated
  /// code only ever READS literals (copies via StrMem / prints via PrintStr);
  /// nothing can write through a literal reference.
  /// </summary>
  private void EmitLiteralPool(Assembler asm) {
    if (!this.OptimizePb36) {
      foreach (var (text, label) in this._stringLiterals) {
        asm.MarkLabel(label);
        asm.Db(text);
      }
      return;
    }

    // longest first so shorter literals can land inside earlier ones
    var ordered = this._stringLiterals
      .OrderByDescending(p => p.Key.Length)
      .ThenBy(p => p.Key, StringComparer.Ordinal)
      .ToList();

    var pool = new System.Text.StringBuilder();
    var offsets = new List<(Asm.Label Label, int Offset)>();
    foreach (var (text, label) in ordered) {
      var index = IndexOf(pool, text);
      if (index < 0) {
        var overlap = SuffixOverlap(pool, text);
        index = pool.Length - overlap;
        pool.Append(text, overlap, text.Length - overlap);
      }
      offsets.Add((label, index));
    }

    var poolStart = asm.Position;
    asm.Db(pool.ToString());
    foreach (var (label, offset) in offsets)
      label.Position = poolStart + offset;
  }

  private static int IndexOf(System.Text.StringBuilder pool, string text)
    => pool.ToString().IndexOf(text, StringComparison.Ordinal);

  /// <summary>Length of the longest pool suffix that is also a prefix of <paramref name="text"/>.</summary>
  private static int SuffixOverlap(System.Text.StringBuilder pool, string text) {
    var max = Math.Min(pool.Length, text.Length - 1);
    for (var k = max; k > 0; --k) {
      var matches = true;
      for (var i = 0; i < k && matches; ++i)
        matches = pool[pool.Length - k + i] == text[i];
      if (matches)
        return k;
    }
    return 0;
  }

  #endregion

  #region O19 - definite-assignment frame-zero elision

  /// <summary>
  /// pb36 O19: true when every non-dynamic-string stack local of
  /// <paramref name="body"/> is provably assigned before any use, so the
  /// whole-frame zero fill is unobservable. The proof is a conservative
  /// straight-line prefix scan: it accepts whole-variable assignments whose
  /// right side reads only already-assigned (or non-local) storage and a
  /// leading FOR header (the counter is written before the body runs); any
  /// control flow, call or other statement ends the prefix. Dynamic STRING/
  /// FLEX slots are excluded - assignment itself frees the previous handle,
  /// so their slots must start at 0 (the caller zeroes them individually).
  /// Locals whose type embeds a string handle never qualify.
  /// </summary>
  public static bool CanElideFrameZeroing(SemanticModel model, IReadOnlyList<Statement> body, IReadOnlyList<VariableSymbol> stackLocals) {
    var pending = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var local in stackLocals) {
      if (local.Type is StringType or FlexType)
        continue; // zeroed individually by the caller
      if (EmbedsStringHandle(local.Type))
        return false; // a garbage embedded handle would corrupt the string heap
      pending.Add(local);
    }
    if (pending.Count == 0)
      return true;

    foreach (var statement in body)
      switch (statement) {
        case MetaStmt or EquateStmt or DefTypeStmt or DataStmt or LabelStmt:
          continue; // inert (a label alone cannot be jumped to from an accepted prefix)

        case DimStmt dim: {
          foreach (var v in dim.Variables)
            foreach (var (lower, upper) in v.ArrayBounds ?? []) {
              if (lower != null && ReadsPending(model, lower, pending))
                return false;
              if (ReadsPending(model, upper, pending))
                return false;
            }
          continue;
        }

        case AssignStmt { Target: NameExpr target } assign: {
          if (ReadsPending(model, assign.Value, pending))
            return false;
          if (model.VariableBindings.TryGetValue(target, out var symbol))
            pending.Remove(symbol);
          if (pending.Count == 0)
            return true;
          continue;
        }

        case ForStmt { Variable: NameExpr counter } loop: {
          if (ReadsPending(model, loop.From, pending) || ReadsPending(model, loop.To, pending)
              || (loop.Step != null && ReadsPending(model, loop.Step, pending)))
            return false;
          if (model.VariableBindings.TryGetValue(counter, out var symbol))
            pending.Remove(symbol); // the FOR header writes the counter unconditionally
          return pending.Count == 0; // the body may run zero times - prefix ends here
        }

        default:
          return pending.Count == 0; // prefix ends at the first complex statement
      }

    return pending.Count == 0;
  }

  /// <summary>True when <paramref name="type"/> stores a dynamic string handle anywhere inside.</summary>
  private static bool EmbedsStringHandle(PbType type) => type switch {
    StringType or FlexType => true,
    UdtType udt => udt.Fields.Any(f => EmbedsStringHandle(f.Type)),
    ArrayType array => EmbedsStringHandle(array.Element),
    _ => false,
  };

  /// <summary>
  /// True when evaluating <paramref name="e"/> could read a still-unassigned
  /// local (or do anything the prefix proof cannot see through, e.g. call a
  /// user procedure that might receive a pending local BYREF).
  /// </summary>
  private static bool ReadsPending(SemanticModel model, Expression e, HashSet<VariableSymbol> pending) {
    switch (e) {
      case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
        return false;

      case NameExpr name:
        if (model.CallBindings.ContainsKey(name))
          return true; // parameterless user FUNCTION - opaque
        return model.VariableBindings.TryGetValue(name, out var symbol) && pending.Contains(symbol);

      case CallOrIndexExpr call: {
        if (model.CallBindings.ContainsKey(call))
          return true; // user FUNCTION call - opaque side effects
        if (model.VariableBindings.TryGetValue(call, out var array) && pending.Contains(array))
          return true;
        return call.Arguments.Any(a => ReadsPending(model, a, pending));
      }

      case MemberExpr member:
        return ReadsPending(model, member.Target, pending);

      case IndexExpr index:
        return ReadsPending(model, index.Target, pending) || index.Arguments.Any(a => ReadsPending(model, a, pending));

      case PtrDerefExpr deref:
        return ReadsPending(model, deref.Pointer, pending)
          || (deref.Index != null && ReadsPending(model, deref.Index, pending));

      case ByValArgExpr byVal:
        return ReadsPending(model, byVal.Value, pending);

      case UnaryExpr unary:
        return ReadsPending(model, unary.Operand, pending);

      case BinaryExpr binary:
        return ReadsPending(model, binary.Left, pending) || ReadsPending(model, binary.Right, pending);

      case FileNumberExpr file:
        return ReadsPending(model, file.Number, pending);

      default:
        return true; // unknown expression shapes are opaque
    }
  }

  #endregion

  #region O4 - multiply strength reduction

  /// <summary>
  /// pb36 O4: <c>x * 2^n</c> (and <c>* 0</c> / <c>* 1</c>) as 8086-safe shifts.
  /// The non-constant operand is still evaluated (it may call FUNCTIONs), and
  /// shifting matches the low bits of the product exactly, so wrap semantics
  /// are preserved. Constants come from the pure folder only.
  /// </summary>
  private bool TryEmitStrengthReducedMultiply(BinaryExpr b, PbType opType) {
    if (!this.OptimizePb36 || b.Op != BinaryOp.Multiply)
      return false;
    if (opType is not ScalarType { IsFloat: false, ByteSize: 2 or 4 } scalar)
      return false;

    Expression variable;
    long constant;
    if (this.Pb36Folder.TryFold(b.Right) is { Integer: { } right }) {
      variable = b.Left;
      constant = right;
    } else if (this.Pb36Folder.TryFold(b.Left) is { Integer: { } left }) {
      variable = b.Right;
      constant = left;
    } else
      return false;

    var maxShift = scalar.ByteSize == 4 ? 4 : 8; // beyond this the generic path is cheaper
    int shift;
    if (constant == 0)
      shift = -1;
    else if (constant > 0 && BitOperations.IsPow2((ulong)constant) && BitOperations.TrailingZeroCount((ulong)constant) <= maxShift)
      shift = BitOperations.TrailingZeroCount((ulong)constant);
    else
      return false;

    var asm = this._asm;
    this.EmitExpression(variable);
    this.Coerce(model.TypeOf(variable), opType, variable);

    if (shift < 0) { // * 0: operand evaluated for its effects, result zero
      asm.Xor(Reg.AX, Reg.AX);
      if (scalar.ByteSize == 4)
        asm.Xor(Reg.DX, Reg.DX);
      return true;
    }

    for (var i = 0; i < shift; ++i)
      if (scalar.ByteSize == 4) {
        asm.Shl(Reg.AX, 1);
        asm.Rcl(Reg.DX, 1);
      } else if (shift > 4 && i == 0) {
        asm.Mov(Reg.CL, (Imm)shift);
        asm.Shl(Reg.AX, Reg.CL);
        break;
      } else
        asm.Shl(Reg.AX, 1);

    return true;
  }

  #endregion
}
