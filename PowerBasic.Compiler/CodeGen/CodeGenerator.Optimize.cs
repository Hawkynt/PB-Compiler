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
/// <see cref="Optimize"/> and change nothing when it is false).
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>
  /// Master optimizer gate. The optimizer is dialect-agnostic - it reads the
  /// bound model's per-dialect types and semantics, so it preserves observable
  /// behavior for every dialect (verified by the differential harness across
  /// QB/PDS/TB/PB). It defaults on for pb36 (the "optimizing PB"); any other
  /// dialect can opt in by setting this (the <c>--optimize</c> CLI flag).
  /// Never changes observable behavior, only time and size.
  /// </summary>
  public bool Optimize { get; set; } = model.Dialect == Dialect.Pb36;

  private ConstantFolder? _pb36Folder;
  private ConstantFolder OptFolder => this._pb36Folder ??= new(model.Equates, model.EnumMembers);

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
    if (!this.Optimize)
      return false;

    // O9: literal string concatenation folds into one pooled literal
    if (model.TypeOf(e) is StringType) {
      if (this.OptFolder.TryFold(e) is not { Text: { } text })
        return false;
      this.EmitStringLiteral(text);
      return true;
    }

    if (model.TypeOf(e) is not ScalarType { IsFloat: false } type)
      return false;
    if (this.OptFolder.TryFold(e) is not { Integer: { } raw })
      return false;

    this.EmitIntegralConstant(WrapToType(raw, type), KindOf(type));
    return true;
  }

  /// <summary>
  /// pb36 O17 (SCCP): when SSA + sparse conditional constant propagation proved
  /// the variable reads inside <paramref name="e"/> constant, substitute them and
  /// fold - cross-block constant propagation the local folder cannot do. Only
  /// fires when at least one read was actually proven (so expressions with no
  /// tracked constant emit exactly as before), and the result is wrapped to the
  /// expression's type, so it equals the value the program would compute.
  /// </summary>
  private bool TryEmitProvenConstant(Expression e) {
    if (this._provenReads is not { Count: > 0 } proven)
      return false;
    // under $ERROR OVERFLOW/NUMERIC a folded constant would skip a runtime trap
    // the real arithmetic must still raise (error 6), so do not fold there
    if (this.CheckOverflow || this.CheckNumeric)
      return false;
    if (model.TypeOf(e) is not ScalarType { IsFloat: false } type)
      return false;
    if (IsUnsignedDwordCompare(e))
      return false; // a DWORD ordered comparison must run unsigned; the type-less folder does it signed
    var substituted = SubstituteProven(e, proven, out var changed);
    if (!changed)
      return false; // no proven read here - leave emission untouched
    if (this.OptFolder.TryFold(substituted) is not { Integer: { } raw })
      return false; // an untracked read / call / float kept it non-constant
    this.EmitIntegralConstant(WrapToType(raw, type), KindOf(type));
    return true;
  }

  /// <summary>An ordered (&lt;/&gt;/&lt;=/&gt;=) comparison with a DWORD operand: the type-less folder would compare signed, so it must stay a runtime unsigned compare.</summary>
  private bool IsUnsignedDwordCompare(Expression e) =>
    e is BinaryExpr { Op: BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual } cmp
      && (model.TypeOf(cmp.Left) is ScalarType { IsFloat: false, Signed: false, ByteSize: 4 }
          || model.TypeOf(cmp.Right) is ScalarType { IsFloat: false, Signed: false, ByteSize: 4 });

  /// <summary>
  /// pb36 O17 in the modular int16 path: when the whole tree folds via SCCP-proven
  /// reads, emit the low 16 bits straight into AX instead of the per-node ALU
  /// sequence. The modular path runs only unchecked, so no overflow trap is lost.
  /// </summary>
  private bool TryEmitModularProvenConstant(Expression e) {
    if (this._provenReads is not { Count: > 0 } proven || this.CheckOverflow || this.CheckNumeric)
      return false;
    var substituted = SubstituteProven(e, proven, out var changed);
    if (!changed)
      return false;
    if (this.OptFolder.TryFold(substituted) is not { Integer: { } raw })
      return false;
    var value = (short)(raw & 0xFFFF);
    if (value == 0)
      this._asm.Xor(Reg.AX, Reg.AX);
    else
      this._asm.Mov(Reg.AX, (int)value);
    return true;
  }

  /// <summary>Clones <paramref name="e"/> replacing each proven-constant read with its literal; <paramref name="changed"/> reports whether any substitution happened.</summary>
  private static Expression SubstituteProven(Expression e, Dictionary<NameExpr, long> proven, out bool changed) {
    switch (e) {
      case NameExpr n when proven.TryGetValue(n, out var value):
        changed = true;
        return new IntegerLiteralExpr(n.Position, value, TypeSuffix.None);
      case UnaryExpr u: {
        var operand = SubstituteProven(u.Operand, proven, out changed);
        return changed ? u with { Operand = operand } : u;
      }
      case BinaryExpr b: {
        var left = SubstituteProven(b.Left, proven, out var leftChanged);
        var right = SubstituteProven(b.Right, proven, out var rightChanged);
        changed = leftChanged || rightChanged;
        return changed ? b with { Left = left, Right = right } : b;
      }
      default:
        changed = false;
        return e;
    }
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
        if (this.Optimize && (value & 0xFFFF) == 0)
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
        if (this.Optimize && low == 0)
          asm.Xor(Reg.AX, Reg.AX);
        else
          asm.Mov(Reg.AX, low);
        if (this.Optimize && high == 0)
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

  /// <summary>True when $CPU 80486 selected - C2 alignment and 486-only opcodes (BSWAP) become legal.</summary>
  private bool Cpu486 => model.MetaStatements.Any(m =>
    m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase)
    && m.Arguments is [{ } level, ..]
    && level.Text is "80486" or "486");

  /// <summary>
  /// REP-copies CX-free <paramref name="byteCount"/> bytes DS:SI -> ES:DI.
  /// pb35 keeps the byte-wide copy; pb36 widens to words (8086-safe) and to
  /// DWORDs under $CPU 80386, with the odd tail copied byte-wise - pure copies
  /// are width-agnostic, so behavior is identical.
  /// </summary>
  private void EmitBlockMove(int byteCount) {
    var asm = this._asm;
    if (!this.Optimize || byteCount < 4) {
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
    if (!this.Optimize || !this.OptimizeSpeed || !Equals(counter.Type, PbType.Integer))
      return false;
    if (this.OptFolder.TryFold(f.From) is not { Integer: { } fromRaw }
        || this.OptFolder.TryFold(f.To) is not { Integer: { } toRaw })
      return false;
    var stepRaw = 1L;
    if (f.Step != null) {
      if (this.OptFolder.TryFold(f.Step) is not { Integer: { } s })
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
    if (!this.Optimize) {
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
  /// Disabled under <c>$ERROR OVERFLOW ON</c>: a shift chain cannot reproduce
  /// the genuine <c>IMUL</c>'s signed-overflow trap (error 6), so the checked
  /// multiply must keep the real instruction and its <c>JNO</c> guard.
  /// </summary>
  private bool TryEmitStrengthReducedMultiply(BinaryExpr b, PbType opType) {
    if (!this.Optimize || b.Op != BinaryOp.Multiply || this.CheckOverflow)
      return false;
    if (opType is not ScalarType { IsFloat: false, ByteSize: 2 or 4 } scalar)
      return false;

    Expression variable;
    long constant;
    if (this.OptFolder.TryFold(b.Right) is { Integer: { } right }) {
      variable = b.Left;
      constant = right;
    } else if (this.OptFolder.TryFold(b.Left) is { Integer: { } left }) {
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

  /// <summary>
  /// pb36 O4: <c>x \ 2^n</c> and <c>x MOD 2^n</c> as shift/mask sequences.
  /// PB truncates toward zero and the MOD result carries the dividend's sign
  /// (IDIV semantics), so signed forms bias by <c>2^n - 1</c> before shifting:
  /// <c>x \ 2^n = (x + b) &gt;&gt; n</c> and <c>x MOD 2^n = ((x + b) AND mask) - b</c>
  /// with <c>b = mask</c> for negative x, else 0. A positive constant divisor
  /// can neither divide by zero nor overflow the quotient, so the sequences
  /// are legal under every $ERROR mode. 8086-safe: shift counts above four go
  /// through CL, never the 186+ immediate form.
  /// </summary>
  private bool TryEmitStrengthReducedDivMod(BinaryExpr b, PbType opType) {
    if (!this.Optimize || b.Op is not (BinaryOp.IntegerDivide or BinaryOp.Modulo))
      return false;
    if (opType is not ScalarType { IsFloat: false, ByteSize: 2 or 4 } scalar)
      return false;
    if (this.OptFolder.TryFold(b.Right) is not { Integer: { } divisor })
      return false;
    if (divisor <= 0)
      return false;
    var wide = scalar.ByteSize == 4;

    // pb36 O4: a non-power-of-two constant divisor lowers to a verified
    // reciprocal multiply (16-bit signed, $OPTIMIZE SPEED) - a MUL+shift instead
    // of the ~25-40-cycle IDIV. The magic is brute-force-checked at compile time
    // against every int16, so it is exact; otherwise IDIV stays.
    if (!BitOperations.IsPow2((ulong)divisor)) {
      if (this.OptimizeSpeed && !wide && scalar.Signed && divisor <= short.MaxValue
          && TryMagicSigned16((int)divisor, out var magic, out var magShift, out var addX))
        return this.EmitReciprocalDivMod16(b, (int)divisor, magic, magShift, addX);
      return false;
    }

    var shift = BitOperations.TrailingZeroCount((ulong)divisor);
    if (shift > (wide ? 8 : 15))
      return false; // pair shifts beyond this lose to the runtime call

    var asm = this._asm;
    this.EmitExpression(b.Left);
    this.Coerce(model.TypeOf(b.Left), opType, b.Left);

    if (shift == 0) { // \ 1 is the identity, MOD 1 is zero (operand effects kept)
      if (b.Op == BinaryOp.Modulo) {
        asm.Xor(Reg.AX, Reg.AX);
        if (wide)
          asm.Xor(Reg.DX, Reg.DX);
      }
      return true;
    }

    var mask = (int)(divisor - 1);
    if (!wide) {
      // AX = x, signed INTEGER (unsigned 16-bit never reaches: WORD promotes to LONG)
      asm.Cwd();
      asm.And(Reg.DX, (Imm)mask);                  // b = x < 0 ? mask : 0
      asm.Add(Reg.AX, Reg.DX);
      if (b.Op == BinaryOp.IntegerDivide) {
        this.EmitShiftRight(Reg.AX, shift, arithmetic: true);
      } else {
        asm.And(Reg.AX, (Imm)mask);
        asm.Sub(Reg.AX, Reg.DX);
      }
      return true;
    }

    // DX:AX = x (LONG signed or DWORD unsigned)
    if (scalar.Signed) {
      var nonNegative = asm.DefineLabel();
      asm.Or(Reg.DX, Reg.DX);
      asm.Jns(nonNegative);
      asm.Add(Reg.AX, (Imm)mask);
      asm.Adc(Reg.DX, (Imm)0);
      if (b.Op == BinaryOp.Modulo) {
        asm.And(Reg.AX, (Imm)mask);
        asm.Xor(Reg.DX, Reg.DX);
        asm.Sub(Reg.AX, (Imm)mask);
        asm.Sbb(Reg.DX, (Imm)0);
        var done = asm.DefineLabel();
        asm.Jmp(done);
        asm.MarkLabel(nonNegative);
        asm.And(Reg.AX, (Imm)mask);
        asm.Xor(Reg.DX, Reg.DX);
        asm.MarkLabel(done);
        return true;
      }
      asm.MarkLabel(nonNegative);
      for (var i = 0; i < shift; ++i) {
        asm.Sar(Reg.DX, 1);
        asm.Rcr(Reg.AX, 1);
      }
      return true;
    }

    if (b.Op == BinaryOp.Modulo) {
      asm.And(Reg.AX, (Imm)mask);
      asm.Xor(Reg.DX, Reg.DX);
      return true;
    }
    for (var i = 0; i < shift; ++i) {
      asm.Shr(Reg.DX, 1);
      asm.Rcr(Reg.AX, 1);
    }
    return true;
  }

  /// <summary>
  /// pb36 O4: derives a signed 16-bit division magic (Hacker's Delight) for a
  /// positive non-power-of-two divisor, then brute-force-verifies the exact
  /// formula the emitter will produce against every int16 - so the reciprocal
  /// multiply is provably bit-identical to IDIV, or it is rejected.
  /// </summary>
  private static bool TryMagicSigned16(int d, out int magic, out int shift, out bool addX) {
    magic = 0;
    shift = 0;
    addX = false;
    if (d < 2 || d > short.MaxValue)
      return false;
    const int W = 16;
    long two15 = 1L << (W - 1);
    long ad = d;
    long anc = two15 - 1 - two15 % ad;        // |nc| for d > 0
    var p = W - 1;
    long q1 = two15 / anc, r1 = two15 - q1 * anc;
    long q2 = two15 / ad, r2 = two15 - q2 * ad;
    long delta;
    do {
      ++p;
      q1 *= 2; r1 *= 2;
      if (r1 >= anc) { ++q1; r1 -= anc; }
      q2 *= 2; r2 *= 2;
      if (r2 >= ad) { ++q2; r2 -= ad; }
      delta = ad - r2;
    } while (q1 < delta || (q1 == delta && r1 == 0));
    magic = (int)((q2 + 1) & 0xFFFF);
    shift = p - W;
    addX = (magic & 0x8000) != 0;
    if (shift is < 0 or > 15)
      return false;
    for (var xi = (int)short.MinValue; xi <= short.MaxValue; ++xi) {
      var mulhi = (int)((long)xi * (short)magic >> 16);
      var q0 = mulhi + (addX ? xi : 0);
      var q = (q0 >> shift) - (xi >> 15);
      if (q != xi / d)
        return false;
    }
    return true;
  }

  /// <summary>
  /// pb36 O4: signed 16-bit <c>x \ d</c> / <c>x MOD d</c> as the verified
  /// reciprocal multiply <c>q = (mulhi(x, magic) [+x]) &gt;&gt; shift - (x &gt;&gt; 15)</c>,
  /// then <c>r = x - q*d</c> for MOD. x is parked in a temp because the 8086-safe
  /// arithmetic shifts need CL.
  /// </summary>
  private bool EmitReciprocalDivMod16(BinaryExpr b, int d, int magic, int shift, bool addX) {
    var asm = this._asm;
    this.EmitExpression(b.Left);
    this.Coerce(model.TypeOf(b.Left), PbType.Integer, b.Left);   // AX = x
    var xCell = this.AllocTemp(2).WithSize(OperandSize.Word);
    asm.Mov(xCell, Reg.AX);
    asm.Mov(Reg.BX, (Imm)(short)magic);
    asm.Imul(Reg.BX);                          // DX:AX = x * magic; DX = mulhi
    if (addX)
      asm.Add(Reg.DX, xCell);                  // q0 = mulhi + x
    this.EmitShiftRight(Reg.DX, shift, arithmetic: true);
    asm.Mov(Reg.AX, xCell);
    this.EmitShiftRight(Reg.AX, 15, arithmetic: true);   // AX = (x < 0) ? -1 : 0
    asm.Sub(Reg.DX, Reg.AX);                   // DX = quotient
    if (b.Op == BinaryOp.IntegerDivide) {
      asm.Mov(Reg.AX, Reg.DX);
    } else {
      asm.Mov(Reg.AX, Reg.DX);
      asm.Mov(Reg.BX, (Imm)d);
      asm.Imul(Reg.BX);                        // AX = (q * d) low 16
      asm.Mov(Reg.BX, xCell);
      asm.Sub(Reg.BX, Reg.AX);                 // x - q*d
      asm.Mov(Reg.AX, Reg.BX);
    }
    this.ReleaseTemp(2);
    return true;
  }

  /// <summary>Shifts <paramref name="register"/> left by <paramref name="count"/> 8086-safely (1-shifts up to four, CL beyond - never the 186+ immediate form).</summary>
  private void EmitShiftLeft(Reg register, int count) {
    var asm = this._asm;
    if (count <= 0)
      return;
    if (count > 4) {
      asm.Mov(Reg.CL, (Imm)count);
      asm.Shl(register, Reg.CL);
      return;
    }
    for (var i = 0; i < count; ++i)
      asm.Shl(register, 1);
  }

  /// <summary>Shifts <paramref name="register"/> right by <paramref name="count"/> 8086-safely (1-shifts up to four, CL beyond).</summary>
  private void EmitShiftRight(Reg register, int count, bool arithmetic) {
    var asm = this._asm;
    if (count > 4) {
      asm.Mov(Reg.CL, (Imm)count);
      if (arithmetic)
        asm.Sar(register, Reg.CL);
      else
        asm.Shr(register, Reg.CL);
      return;
    }
    for (var i = 0; i < count; ++i)
      if (arithmetic)
        asm.Sar(register, 1);
      else
        asm.Shr(register, 1);
  }

  #endregion

  #region O4 - modular 16-bit multiply by a constant (shift + add/sub, $OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O4 (modular int16 context): <c>v * c</c> for a compile-time integral
  /// <c>c</c> lowers to a short shift/add/sub chain instead of the ~128-cycle
  /// 8086 <c>IMUL BX</c>. The modular path only runs when neither overflow nor
  /// numeric checking is active (its entry gate in <see cref="EmitModularInt16"/>),
  /// so the result is purely modular 16-bit and every chain below reproduces the
  /// product's low 16 bits exactly. SPEED-gated: the chains trade a few bytes
  /// for the cycles, so SIZE/default keep the compact IMUL.
  ///
  /// Decompositions, with <c>v</c> in AX: powers of two are one shift; a
  /// two-bit multiplier <c>2^a+2^b</c> is <c>(v + v&lt;&lt;(a-b))&lt;&lt;b</c>; a
  /// contiguous run of ones <c>2^a-2^b</c> is <c>(v&lt;&lt;(a-b) - v)&lt;&lt;b</c>;
  /// negative multipliers compute the magnitude then <c>NEG</c> (modular). Other
  /// shapes fall back to IMUL.
  /// </summary>
  private bool TryEmitModularConstMul(BinaryExpr b) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return false;

    Expression variable;
    long raw;
    if (this.TryModularFoldConst(b.Right, out raw))
      variable = b.Left;
    else if (this.TryModularFoldConst(b.Left, out raw))
      variable = b.Right;
    else
      return false;

    var asm = this._asm;
    var m = (int)(short)(raw & 0xFFFF); // the multiplier reduced to its 16-bit modular value

    if (m == 0) {                       // v * 0 - evaluate v for its side effects, result 0
      this.EmitModularInt16(variable);
      asm.Xor(Reg.AX, Reg.AX);
      return true;
    }
    if (m == 1) {                       // identity
      this.EmitModularInt16(variable);
      return true;
    }
    if (m == -1) {                      // negation
      this.EmitModularInt16(variable);
      asm.Neg(Reg.AX);
      return true;
    }

    var neg = m < 0;
    var mag = (uint)Math.Abs(m);        // |m| <= 32768, fits a power-of-two probe

    if (BitOperations.IsPow2(mag)) {
      this.EmitModularInt16(variable);
      this.EmitShiftLeft(Reg.AX, BitOperations.TrailingZeroCount(mag));
      if (neg)
        asm.Neg(Reg.AX);
      return true;
    }

    var lo = BitOperations.TrailingZeroCount(mag);
    if (BitOperations.PopCount(mag) == 2) {                 // 2^a + 2^b
      var hi = 31 - BitOperations.LeadingZeroCount(mag);
      this.EmitModularInt16(variable);
      asm.Mov(Reg.BX, Reg.AX);
      this.EmitShiftLeft(Reg.BX, hi - lo);
      asm.Add(Reg.AX, Reg.BX);
      this.EmitShiftLeft(Reg.AX, lo);
      if (neg)
        asm.Neg(Reg.AX);
      return true;
    }

    var run = mag >> lo;                                    // 2^a - 2^b -> a run of ones
    if (BitOperations.IsPow2(run + 1)) {
      var width = BitOperations.TrailingZeroCount(run + 1); // a - b
      this.EmitModularInt16(variable);
      asm.Mov(Reg.BX, Reg.AX);
      this.EmitShiftLeft(Reg.AX, width);
      asm.Sub(Reg.AX, Reg.BX);
      this.EmitShiftLeft(Reg.AX, lo);
      if (neg)
        asm.Neg(Reg.AX);
      return true;
    }

    return false; // three-or-more-term multipliers keep the compact IMUL
  }

  /// <summary>
  /// pb36 O8 (modular int16 context): <c>v +/- c</c> for a compile-time integral
  /// <c>c</c> becomes one immediate ALU op instead of loading the constant into a
  /// register and pushing/popping the other operand - smaller and faster, so it
  /// is unconditional (no SPEED gate). <c>v - c</c> adds <c>-c</c>; <c>c - v</c>
  /// negates then adds <c>c</c>. All arithmetic is modular 16-bit, matching the
  /// generic <c>ADD/SUB AX,BX</c> path bit-for-bit.
  /// </summary>
  private bool TryEmitModularConstAddSub(BinaryExpr b) {
    if (!this.Optimize)
      return false;
    var asm = this._asm;

    if (b.Op == BinaryOp.Add) {
      if (this.TryModularFoldConst(b.Right, out var rc))
        this.EmitModularInt16(b.Left);
      else if (this.TryModularFoldConst(b.Left, out rc))
        this.EmitModularInt16(b.Right);
      else
        return false;
      this.EmitModularAddImm(rc);
      return true;
    }

    // subtract
    if (this.TryModularFoldConst(b.Right, out var sc)) {        // v - c
      this.EmitModularInt16(b.Left);
      this.EmitModularAddImm(-sc);
      return true;
    }
    if (this.TryModularFoldConst(b.Left, out var lc)) {         // c - v = -(v) + c
      this.EmitModularInt16(b.Right);
      asm.Neg(Reg.AX);
      this.EmitModularAddImm(lc);
      return true;
    }
    return false;
  }

  /// <summary>Adds the 16-bit modular value of <paramref name="constant"/> to AX (nothing when it is zero mod 2^16).</summary>
  private void EmitModularAddImm(long constant) => this.EmitAddImm16((short)(constant & 0xFFFF));

  /// <summary>
  /// AX += <paramref name="imm"/> (16-bit signed). O8 peephole: +/-1 uses
  /// <c>INC</c>/<c>DEC</c> (one byte). INC/DEC still set OF, so a following
  /// <c>JNO</c> overflow trap stays correct; they leave CF alone, which the
  /// add/sub paths never read.
  /// </summary>
  private void EmitAddImm16(int imm) {
    var asm = this._asm;
    switch (imm) {
      case 0: break;
      case 1: asm.Inc(Reg.AX); break;
      case -1: asm.Dec(Reg.AX); break;
      default: asm.Add(Reg.AX, (Imm)imm); break;
    }
  }

  /// <summary>AX -= <paramref name="imm"/> (16-bit signed), with the +/-1 INC/DEC peephole.</summary>
  private void EmitSubImm16(int imm) {
    var asm = this._asm;
    switch (imm) {
      case 0: break;
      case 1: asm.Dec(Reg.AX); break;
      case -1: asm.Inc(Reg.AX); break;
      default: asm.Sub(Reg.AX, (Imm)imm); break;
    }
  }

  /// <summary>
  /// True when <paramref name="e"/> is a pure compile-time integral constant
  /// safe to fold away in the modular path: it must not carry a CSE mark (whose
  /// define another node may reload), so skipping its evaluation is observable
  /// to no one.
  /// </summary>
  private bool TryModularFoldConst(Expression e, out long value) {
    value = 0;
    if (this._cseMarks?.ContainsKey(e) == true)
      return false;
    if (this.OptFolder.TryFold(e) is not { Integer: { } v })
      return false;
    value = v;
    return true;
  }

  #endregion

  #region O5 - FOR counter register residency ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O5: a signed-INTEGER FOR counter whose loop body never touches SI
  /// (only scalar-integer assignments / INCR over scalar locals - no arrays,
  /// strings, calls, control flow or counter writes) lives in SI for the whole
  /// loop. The compare and increment run register-to-register and every counter
  /// read inside the body reads SI, eliminating the per-iteration cell load,
  /// store and reload. The cell is written once on exit so post-loop reads see
  /// the genuine increment-then-test end value. Overflow-checked counters
  /// (`$ERROR NUMERIC`) stay on the memory path that carries the JO check.
  /// </summary>
  private bool TryEmitForCounterInRegister(ForStmt f, VariableSymbol counter, Mem cell, long step) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return false;
    if (counter.Type is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false })
      return false;
    if (this.CheckNumeric || this.CheckOverflow || this._registerCounter != null)
      return false;
    if (this._trackResume)
      return false; // an error mid-loop would let a handler read the stale cell
    if (!this.BodyIsSiClean(f.Body, counter))
      return false;

    var asm = this._asm;
    var limit = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), PbType.Integer, f.To);
    asm.Mov(limit, Reg.AX);
    this.EmitExpression(f.From);                 // From may read the accumulator's cell - keep DI free until now
    this.Coerce(model.TypeOf(f.From), PbType.Integer, f.From);
    asm.Mov(Reg.SI, Reg.AX);

    this._registerCounter = (counter, Reg.SI);

    // pb36 O5: keep one hot 2-byte INTEGER accumulator in DI for the loop too
    var accumulator = this.FindAccumulator(f.Body, counter);
    var accCell = accumulator != null ? this.TryDirectCell(accumulator) : null;
    if (accumulator != null && accCell is { } accSlot) {
      asm.Mov(Reg.DI, Adjust(accSlot, 0, OperandSize.Word)); // load its pre-loop value
      this._registerAccumulator = (accumulator, Reg.DI);
    }

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var cont = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(cont);
    this._iterateAny.Push(cont);

    asm.MarkLabel(top);
    asm.Cmp(Reg.SI, limit);
    if (step >= 0)
      asm.Jg(done);
    else
      asm.Jl(done);
    foreach (var statement in f.Body)
      this.EmitStatement(statement);
    asm.MarkLabel(cont);
    if (step >= 0)
      asm.Add(Reg.SI, (Imm)(int)step);
    else
      asm.Sub(Reg.SI, (Imm)(int)Math.Abs(step));
    asm.Jmp(top);
    asm.MarkLabel(done);

    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    asm.Mov(cell, Reg.SI);      // post-loop reads use the cell again
    if (this._registerAccumulator is { } resident && accCell is { } slot)
      asm.Mov(Adjust(slot, 0, OperandSize.Word), resident.Reg); // flush the accumulator
    this._registerCounter = null;
    this._registerAccumulator = null;
    this.ReleaseTemp(2);
    return true;
  }

  /// <summary>
  /// The first 2-byte signed-INTEGER scalar local written in an SI-clean loop
  /// body (assignment or INCR target, not the counter, not STATIC, with a direct
  /// frame cell) - a candidate to live in DI for the loop. Its reads/writes all
  /// route through the residency paths, so keeping it in a register is invisible.
  /// </summary>
  private VariableSymbol? FindAccumulator(IReadOnlyList<Statement> body, VariableSymbol counter) {
    foreach (var statement in body) {
      var target = statement switch {
        AssignStmt { Target: NameExpr t } => t,
        IncrDecrStmt { Target: NameExpr t } => t,
        _ => (NameExpr?)null,
      };
      if (target == null || !model.VariableBindings.TryGetValue(target, out var symbol))
        continue;
      if (ReferenceEquals(symbol, counter))
        continue;
      if (symbol.Type is ScalarType { ByteSize: 2, Signed: true, IsFloat: false }
          && symbol.Storage is not VariableStorage.Static
          && this.TryDirectCell(symbol) != null)
        return symbol;
    }
    return null;
  }

  /// <summary>True when every body statement is a scalar-integer assignment / INCR (over scalar locals, no counter write) whose emission provably leaves SI untouched.</summary>
  private bool BodyIsSiClean(IReadOnlyList<Statement> body, VariableSymbol counter) {
    foreach (var statement in body)
      switch (statement) {
        case AssignStmt { Target: NameExpr } a
            when this.ScalarIntTarget(a.Target) is { } target && !ReferenceEquals(target, counter)
              && SiCleanExpression(a.Value, model):
          continue;
        case IncrDecrStmt { Target: NameExpr } id
            when this.ScalarIntTarget(id.Target) is { } target && !ReferenceEquals(target, counter)
              && (id.Amount == null || SiCleanExpression(id.Amount, model)):
          continue;
        default:
          return false;
      }
    return true;
  }

  private VariableSymbol? ScalarIntTarget(Expression e)
    => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
      && s.Type is ScalarType { IsFloat: false, ByteSize: <= 4 } && s.Storage is not VariableStorage.Static
      ? s : null;

  /// <summary>Pure scalar-integer arithmetic over scalar reads and literals - its emission uses only AX/BX/CX/DX (+ the x87), never SI/DI.</summary>
  private static bool SiCleanExpression(Expression e, SemanticModel model) => e switch {
    IntegerLiteralExpr => true,
    NamedConstantExpr c => !(model.Equates.TryGetValue(c.Name, out var v) && v.Text != null),
    NameExpr n => !model.IntrinsicBindings.ContainsKey(n)
      && model.VariableBindings.TryGetValue(n, out var s)
      && s.Type is ScalarType { IsFloat: false },
    UnaryExpr { Op: UnaryOp.Negate or UnaryOp.Not } u => SiCleanExpression(u.Operand, model),
    BinaryExpr b => b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
        or BinaryOp.IntegerDivide or BinaryOp.Modulo
        or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp
        or BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual
      && SiCleanExpression(b.Left, model) && SiCleanExpression(b.Right, model),
    _ => false,
  };

  #endregion

  #region O6b - induction-variable array store ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O6b: a FOR loop whose single body statement is an INTEGER-array store
  /// <c>a%(i%) = expr</c>, where the counter <c>i%</c> is the sole index and
  /// <c>expr</c> is barrier-free (no calls, no intrinsics, no reads from <c>a%</c>),
  /// steps a DS-relative byte pointer by the element stride (2) instead of
  /// recomputing <c>(i - lbound)*2 + base</c> with IMUL each iteration.
  ///
  /// The stored value is computed into AX via the normal expression emitter
  /// (with BX saved/restored across the evaluation since integer multiply uses
  /// BX), then written through the stepped pointer as <c>MOV [BX], AX</c>.
  ///
  /// Gate: $OPTIMIZE SPEED; no $ERROR BOUNDS/OVERFLOW/NUMERIC/ON ERROR RESUME;
  /// signed INTEGER counter with a compile-time-constant nonzero step; static,
  /// non-HUGE/VIRTUAL/ABSOLUTE, rank-1, INTEGER-element array; exactly one body
  /// statement of the form <c>a%(i%) = expr</c>; expr is barrier-free and
  /// references no element of <c>a%</c> (checked conservatively: if the array
  /// symbol appears anywhere in the expression tree we decline).
  /// </summary>
  private bool TryEmitForArrayStore(ForStmt f, VariableSymbol counter, Mem counterCell, long step) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return false;
    if (counter.Type is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false })
      return false;
    if (this.CheckBounds || this.CheckOverflow || this.CheckNumeric)
      return false;
    if (this._trackResume)
      return false;

    // body must be exactly one array-store statement a%(i%) = expr
    if (f.Body is not [AssignStmt { Target: CallOrIndexExpr storeTarget } storeAssign])
      return false;
    if (!model.VariableBindings.TryGetValue(storeTarget, out var array))
      return false;
    if (storeTarget.Arguments is not [NameExpr idxExpr])
      return false;
    if (!model.VariableBindings.TryGetValue(idxExpr, out var idxSym) || !ReferenceEquals(idxSym, counter))
      return false;

    // array must be static, rank-1, INTEGER- or signed-LONG-element, not special
    if (array.Type is not ArrayType { Element: ScalarType { IsFloat: false } element, IsDynamic: false, Rank: 1 } arrayType)
      return false;
    if (element.ByteSize is not (2 or 4) || (element.ByteSize == 4 && !element.Signed))
      return false;
    var elementSize = element.ByteSize;
    if (arrayType.StaticBounds is not { } bounds)
      return false;
    if (array.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute)
      return false;

    // expr must be barrier-free and must not reference the array symbol
    var expr = storeAssign.Value;
    if (!SiCleanExpression(expr, model))
      return false;
    if (ExpressionReferencesArray(expr, array, model))
      return false;

    // set up the loop
    var asm = this._asm;
    var arraySlot = this.SlotOf(array);
    var lbound = bounds[0].Lower;

    // counter = from; compute initial pointer in BX = &a%[from]
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), PbType.Integer, f.From);
    asm.Mov(counterCell, Reg.AX);          // counter cell = from value
    asm.Sub(Reg.AX, lbound);               // AX = from - lbound
    asm.Shl(Reg.AX, 1);                    // AX = (from-lbound)*2  (byte offset; shift-by-1, 8086-safe)
    if (elementSize == 4)
      asm.Shl(Reg.AX, 1);                  // *4 for a LONG element (a second 8086-safe shift-by-1)
    asm.Mov(Reg.BX, Reg.AX);
    asm.Lea(Reg.BX, Mem.At(Reg.BX, arraySlot)); // BX = DS-relative address of first element

    var limit = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), PbType.Integer, f.To);
    asm.Mov(limit, Reg.AX);

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var cont = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(cont);
    this._iterateAny.Push(cont);

    asm.MarkLabel(top);
    asm.Mov(Reg.AX, counterCell);
    asm.Cmp(Reg.AX, limit);
    if (step >= 0)
      asm.Jg(done);
    else
      asm.Jl(done);

    // save BX across the expression evaluation (integer ops may use BX internally)
    asm.Push(Reg.BX);
    this.EmitExpression(expr);
    this.Coerce(model.TypeOf(expr), elementSize == 4 ? PbType.Long : PbType.Integer, expr);
    asm.Pop(Reg.BX);

    // store into the stepped element address, then advance the pointer
    asm.Mov(Mem.Word(Reg.BX), Reg.AX);     // low word (INTEGER) or low half (LONG)
    if (elementSize == 4)
      asm.Mov(Mem.Word(Reg.BX, 2), Reg.DX); // high half of the LONG (DX:AX)
    var stepBytes = (int)step * elementSize;
    if (stepBytes >= 0)
      asm.Add(Reg.BX, stepBytes);
    else
      asm.Sub(Reg.BX, -stepBytes);

    asm.MarkLabel(cont);
    // increment counter
    var mag = (int)Math.Abs(step);
    asm.Mov(Reg.AX, counterCell);
    if (step >= 0)
      asm.Add(Reg.AX, mag);
    else
      asm.Sub(Reg.AX, mag);
    asm.Mov(counterCell, Reg.AX);
    asm.Jmp(top);

    asm.MarkLabel(done);
    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    this.ReleaseTemp(2);
    return true;
  }

  /// <summary>
  /// True when <paramref name="expr"/> contains any reference to <paramref name="array"/>
  /// (as an array element read or as a bare array name) - conservative aliasing check
  /// for O6b: if the expression reads <c>a%</c> through any subscript, the stepped
  /// write pointer and a fresh address computation would produce the same bytes, but
  /// we decline anyway to stay strictly safe.
  /// </summary>
  private static bool ExpressionReferencesArray(Expression expr, VariableSymbol array, SemanticModel model) {
    switch (expr) {
      case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
        return false;

      case NameExpr n:
        return model.VariableBindings.TryGetValue(n, out var s) && ReferenceEquals(s, array);

      case CallOrIndexExpr call:
        if (model.VariableBindings.TryGetValue(call, out var cs) && ReferenceEquals(cs, array))
          return true;
        return call.Arguments.Any(a => ExpressionReferencesArray(a, array, model));

      case UnaryExpr u:
        return ExpressionReferencesArray(u.Operand, array, model);

      case BinaryExpr b:
        return ExpressionReferencesArray(b.Left, array, model)
          || ExpressionReferencesArray(b.Right, array, model);

      default:
        return true; // unknown shape - be conservative
    }
  }

  #endregion

  #region O13 - silent fixed-point FOR counters ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O13: a SINGLE/DOUBLE FOR counter whose constant bounds and step all
  /// sit on a common power-of-two grid (step = n*2^-k) runs as a scaled 16-bit
  /// integer - the per-iteration x87 compare (FCOM/FSTSW/SAHF) becomes a plain
  /// CMP. Bit-exact because every iterate value i*2^-k is exactly representable
  /// (|i| &lt; 2^15 here, far inside the 2^24 SINGLE window) and equals the
  /// genuine FADD chain a + n*step, while FILD + FMUL by the power-of-two 2^-k
  /// introduces no rounding. The counter cell ends on the first failing value,
  /// matching the genuine FOR.
  /// </summary>
  private bool TryEmitFixedPointFor(ForStmt f, VariableSymbol counter, Mem cell) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return false;
    if (counter.Type is not ScalarType { IsFloat: true } counterType)
      return false;
    if (!TryFoldDouble(f.From, out var from) || !TryFoldDouble(f.To, out var to))
      return false;
    var step = 1.0;
    if (f.Step != null && !TryFoldDouble(f.Step, out step))
      return false;
    if (step == 0)
      return false;

    // smallest k <= 16 putting from/to/step on the 2^-k grid exactly
    var k = -1;
    for (var t = 0; t <= 16; ++t)
      if (IsExactMultiple(from, t) && IsExactMultiple(to, t) && IsExactMultiple(step, t)) {
        k = t;
        break;
      }
    if (k < 0)
      return false;
    var scale = 1L << k;
    var iFrom = (long)Math.Round(from * scale);
    var iTo = (long)Math.Round(to * scale);
    var iStep = (long)Math.Round(step * scale);

    long count = iStep > 0
      ? iFrom > iTo ? 0 : (iTo - iFrom) / iStep + 1
      : iFrom < iTo ? 0 : (iFrom - iTo) / -iStep + 1;
    var iFinal = iFrom + count * iStep;

    // a scaled 16-bit counter keeps the compare/increment trivial; the float
    // exactness window is far wider (2^24 SINGLE) so 16 bits is the binding limit
    if (Math.Abs(iFrom) > short.MaxValue || Math.Abs(iTo) > short.MaxValue
        || Math.Abs(iFinal) > short.MaxValue || Math.Abs(iStep) > short.MaxValue)
      return false;
    if (CountUnrollableStatements(f.Body, model, counter) is null)
      return false;

    var asm = this._asm;
    var floatCell = counterType.ByteSize == 8 ? Adjust(cell, 0, OperandSize.Qword) : Adjust(cell, 0, OperandSize.Dword);
    var i16 = this.AllocTemp(2);
    var invScale = this.FloatConstOf(1.0 / scale);

    asm.Mov(i16, (Imm)(int)(short)iFrom);
    var top = asm.DefineLabel();
    var end = asm.DefineLabel();
    asm.MarkLabel(top);
    asm.Mov(Reg.AX, i16);
    asm.Cmp(Reg.AX, (Imm)(int)(short)iTo);
    if (iStep > 0)
      asm.Jg(end);
    else
      asm.Jl(end);

    // materialize x = i * 2^-k into the counter cell (exact), then the body
    asm.Fild(i16);
    asm.Fmul(Mem.Qword(invScale));
    asm.Fstp(floatCell);
    foreach (var statement in f.Body)
      this.EmitStatement(statement);

    asm.Add(i16, (Imm)(int)(short)iStep);
    asm.Jmp(top);
    asm.MarkLabel(end);

    // counter cell ends on the first failing value (a + count*step)
    asm.Fld(Mem.Qword(this.FloatConstOf(iFinal / (double)scale)));
    asm.Fstp(floatCell);
    return true;
  }

  /// <summary>True when x*2^k is an integer that reconstructs x exactly (x is on the 2^-k dyadic grid).</summary>
  private static bool IsExactMultiple(double x, int k) {
    var scaled = x * (1L << k);
    return scaled == Math.Floor(scaled) && Math.Abs(scaled) < 9.0e15;
  }

  private static bool TryFoldDouble(Expression e, out double value) {
    switch (e) {
      case FloatLiteralExpr f:
        value = f.Value;
        return true;
      case IntegerLiteralExpr i:
        value = i.Value;
        return true;
      case UnaryExpr { Op: UnaryOp.Negate } u when TryFoldDouble(u.Operand, out var inner):
        value = -inner;
        return true;
      default:
        value = 0;
        return false;
    }
  }

  #endregion

  #region O20 - algorithmic idiom replacement ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O20: replaces whole constant-trip INTEGER FOR loops with better
  /// algorithms when the result is provably bit-identical:
  /// empty bodies collapse to the closed-form counter end value, constant
  /// element fills with the bare counter as subscript become one REP STOSW,
  /// and arithmetic-series accumulations add the folded series total once.
  /// SPEED-gated like O7/O10 - DOS-era code uses such loops for timing.
  /// </summary>
  private bool TryEmitForIdiom(ForStmt f, VariableSymbol counter, Mem slot) {
    if (!this.Optimize || !this.OptimizeSpeed || !Equals(counter.Type, PbType.Integer))
      return false;
    if (this.OptFolder.TryFold(f.From) is not { Integer: { } fromRaw }
        || this.OptFolder.TryFold(f.To) is not { Integer: { } toRaw })
      return false;
    var stepRaw = 1L;
    if (f.Step != null) {
      if (this.OptFolder.TryFold(f.Step) is not { Integer: { } s })
        return false;
      stepRaw = s;
    }
    var from = (short)fromRaw;
    var to = (short)toRaw;
    var step = (short)stepRaw;
    if (step == 0)
      return false;

    // simulate the iterates exactly like the generic engine (signed compare,
    // 16-bit wrap on increment); bail out on wrap-around marathons
    var values = new List<short>();
    var current = from;
    for (; values.Count <= 32767; current = unchecked((short)(current + step))) {
      var continues = step > 0 ? current <= to : current >= to;
      if (!continues)
        break;
      values.Add(current);
    }
    if (values.Count > 32767)
      return false;

    var asm = this._asm;

    // ---- empty body: the loop IS its counter end value ----------------------
    if (f.Body.Count == 0) {
      asm.Mov(slot, (Imm)(int)current);
      return true;
    }

    if (values.Count == 0)
      return false; // zero-trip loops with bodies keep the generic engine (cheap anyway)

    // ---- constant fill: a(i%) = const over the whole range -> REP STOSW -----
    if (!this.CheckBounds
        && f.Body is [AssignStmt { Target: CallOrIndexExpr fill } fillAssign]
        && step == 1
        && fill.Arguments is [NameExpr fillIndex]
        && model.VariableBindings.TryGetValue(fillIndex, out var fillCounter)
        && ReferenceEquals(fillCounter, counter)
        && model.VariableBindings.TryGetValue(fill, out var array)
        && array.Type is ArrayType { Element: ScalarType { ByteSize: 2 } }
        && this.OptFolder.TryFold(fillAssign.Value) is { Integer: { } fillValue }) {
      asm.Mov(slot, (Imm)from);                 // counter = first index for the address computation
      if (this.EmitPlace(fill) is { } firstElement) {
        asm.Push(Reg.ES);
        if (firstElement.Far) {
          asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arrseg")));
          asm.Mov(Reg.ES, Reg.AX);
        } else {
          asm.Push(Reg.DS);
          asm.Pop(Reg.ES);
        }
        asm.Lea(Reg.DI, firstElement.Cell);
        // pb36 C1 ($CPU 80386): broadcast the 16-bit fill value into both halves of EAX
        // and store two elements per REP STOSD, with a trailing STOSW for an odd count -
        // same words written, about twice as fast.
        if (this.Optimize && this.Cpu386 && values.Count >= 4) {
          var word = (ushort)(short)fillValue;
          asm.Mov(Reg.EAX, (Imm)(((int)word << 16) | word));
          asm.Mov(Reg.CX, (Imm)(values.Count / 2));
          asm.Cld();
          asm.Rep();
          asm.Stosd();
          if (values.Count % 2 != 0)
            asm.Stosw();
        } else {
          asm.Mov(Reg.CX, values.Count);
          asm.Mov(Reg.AX, (int)(short)fillValue);
          asm.Cld();
          asm.Rep();
          asm.Stosw();
        }
        asm.Pop(Reg.ES);
        asm.Mov(slot, (Imm)(int)current);       // closed-form counter end (wrap included)
        return true;
      }
      return false;
    }

    // ---- array copy: dst(i%) = src(i%) over the whole range -> REP MOVSW ----
    if (!this.CheckBounds
        && f.Body is [AssignStmt { Target: CallOrIndexExpr copyDst, Value: CallOrIndexExpr copySrc }]
        && step == 1
        && copyDst.Arguments is [NameExpr dstIndex]
        && copySrc.Arguments is [NameExpr srcIndex]
        && model.VariableBindings.TryGetValue(dstIndex, out var dstCounter) && ReferenceEquals(dstCounter, counter)
        && model.VariableBindings.TryGetValue(srcIndex, out var srcCounter) && ReferenceEquals(srcCounter, counter)
        && model.VariableBindings.TryGetValue(copyDst, out var dstArray) && dstArray.Type is ArrayType { Element: ScalarType { ByteSize: 2 } }
        && model.VariableBindings.TryGetValue(copySrc, out var srcArray) && srcArray.Type is ArrayType { Element: ScalarType { ByteSize: 2 } }
        && !ReferenceEquals(dstArray, srcArray)) {
      asm.Mov(slot, (Imm)from);
      if (this.EmitPlace(copySrc) is { } srcElement) {
        asm.Lea(Reg.SI, srcElement.Cell);
        asm.Mov(Reg.DX, srcElement.Far ? Reg.ES : Reg.DS);  // remember source segment
        asm.Push(Reg.DX);
        asm.Push(Reg.SI);
        if (this.EmitPlace(copyDst) is { } dstElement) {
          asm.Lea(Reg.DI, dstElement.Cell);
          if (!dstElement.Far) {
            asm.Push(Reg.DS);
            asm.Pop(Reg.ES);
          }
          asm.Pop(Reg.SI);
          asm.Pop(Reg.DX);
          asm.Push(Reg.DS);
          asm.Mov(Reg.DS, Reg.DX);               // DS:SI = source, ES:DI = dest
          asm.Mov(Reg.CX, values.Count);
          asm.Cld();
          asm.Rep();
          asm.Movsw();
          asm.Pop(Reg.DS);
          asm.Mov(slot, (Imm)(int)current);
          return true;
        }
        asm.Pop(Reg.SI);
        asm.Pop(Reg.DX);
      }
      return false;
    }

    // ---- arithmetic series: acc = acc + i% (or i% + acc) -> one folded add --
    if (f.Body is [AssignStmt { Target: NameExpr accName, Value: BinaryExpr { Op: BinaryOp.Add } sum }]
        && model.VariableBindings.TryGetValue(accName, out var acc)
        && !ReferenceEquals(acc, counter)
        && acc.Storage != VariableStorage.Parameter
        && this.TryDirectCell(acc) is { } accCell) {
      var leftIsCounter = sum.Left is NameExpr ln && model.VariableBindings.TryGetValue(ln, out var lsym) && ReferenceEquals(lsym, counter);
      var rightIsCounter = sum.Right is NameExpr rn && model.VariableBindings.TryGetValue(rn, out var rsym) && ReferenceEquals(rsym, counter);
      var other = leftIsCounter ? sum.Right : sum.Left;
      var otherIsAcc = other is NameExpr on && model.VariableBindings.TryGetValue(on, out var osym) && ReferenceEquals(osym, acc);
      if ((leftIsCounter ^ rightIsCounter) && otherIsAcc) {
        long total = 0;
        long partialMax = 0;
        foreach (var v in values) {
          total += v;
          partialMax = Math.Max(partialMax, Math.Abs(total));
        }
        switch (acc.Type) {
          case ScalarType { IsFloat: false, ByteSize: 2 }: {
            // 16-bit accumulation is modular: adding the folded total wraps
            // exactly like the per-iteration adds
            asm.Mov(Reg.AX, (int)(short)total);
            asm.Add(accCell.WithSize(OperandSize.Word), Reg.AX);
            asm.Mov(slot, (Imm)(int)current);
            return true;
          }
          case ScalarType { IsFloat: false, ByteSize: 4 } when partialMax < int.MaxValue: {
            // partial sums of a 16-bit counter stay far below 2^31, so the
            // per-iteration exact-store chain equals one 32-bit pair add
            asm.Mov(Reg.AX, (int)(total & 0xFFFF));
            asm.Add(accCell.WithSize(OperandSize.Word), Reg.AX);
            asm.Mov(Reg.AX, (int)((total >> 16) & 0xFFFF));
            asm.Adc(Adjust(accCell, 2, OperandSize.Word), Reg.AX);
            asm.Mov(slot, (Imm)(int)current);
            return true;
          }
          case ScalarType { Kind: ScalarKind.Double }: {
            // DOUBLE holds every partial of a 16-bit series exactly
            asm.Fld(Adjust(accCell, 0, OperandSize.Qword));
            asm.Fadd(Mem.Qword(this.FloatConstOf(total)));
            asm.Fstp(Adjust(accCell, 0, OperandSize.Qword));
            asm.Mov(slot, (Imm)(int)current);
            return true;
          }
          case ScalarType { Kind: ScalarKind.Single } when partialMax <= 1L << 24: {
            // every partial sum is exact in SINGLE, so one exact add is
            // bit-identical to the chain of per-iteration adds
            asm.Fld(Adjust(accCell, 0, OperandSize.Dword));
            asm.Fadd(Mem.Qword(this.FloatConstOf(total)));
            asm.Fstp(Adjust(accCell, 0, OperandSize.Dword));
            asm.Mov(slot, (Imm)(int)current);
            return true;
          }
        }
      }
    }

    return false;
  }

  #endregion

  #region O6b - induction-variable strength reduction for array element addressing ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 O6b ($OPTIMIZE SPEED): a FOR loop whose single-statement body reads a
  /// static rank-1 INTEGER array element indexed by exactly the loop counter
  /// (<c>x% = a%(i%)</c>) replaces the per-iteration address recomputation
  /// (IMUL + label offset) with a pre-computed stepped pointer stored in a frame
  /// slot. The initial element address is computed once before the loop; at the
  /// end of each iteration the slot advances by <c>elementSize * step</c>. Inside
  /// the loop body the element is accessed as <c>MOV BX,[addrSlot] / MOV AX,[BX]</c>,
  /// eliminating the IMUL that scales the subscript.
  ///
  /// Sound because:
  /// - $ERROR BOUNDS / NUMERIC / OVERFLOW / on-error-resume are all off, so no
  ///   runtime check is skipped.
  /// - The array is static (no REDIM possible), non-HUGE/VIRTUAL (near DS element).
  /// - The body is a single read-only array access assigned to a scalar - the
  ///   counter is not written and no call, label, or control flow appears.
  /// - The step is a compile-time constant, so the stride and address arithmetic
  ///   are exact.
  /// </summary>
  private bool TryEmitForArrayIvsr(ForStmt f, VariableSymbol counter, Mem counterCell, long step) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return false;
    if (counter.Type is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false })
      return false;
    if (this.CheckBounds || this.CheckNumeric || this.CheckOverflow)
      return false;
    if (this._trackResume)
      return false; // stale cell observable to an error handler
    if (this._registerCounter != null || this._registerAccumulator != null)
      return false; // nested register-residency loops - keep it simple

    // Body must be exactly one assignment: x% = a%(i%)
    if (f.Body.Count != 1 || f.Body[0] is not AssignStmt assign)
      return false;

    // Value must be a CallOrIndexExpr bound to a variable (array read), indexed
    // by exactly the FOR counter, with no other arguments or nested expressions
    if (assign.Value is not CallOrIndexExpr { Arguments: [NameExpr readIdx] } readCall)
      return false;
    if (!model.VariableBindings.TryGetValue(readIdx, out var idxSym) || !ReferenceEquals(idxSym, counter))
      return false;
    if (!model.VariableBindings.TryGetValue(readCall, out var readArr))
      return false;
    // Static rank-1 array with 2-byte integer or signed-4-byte (LONG) element,
    // not HUGE/VIRTUAL/ABSOLUTE
    if (readArr.Type is not ArrayType { StaticBounds: { } readBounds } readArrType
        || readBounds.Count != 1
        || readArr.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute
        || readArrType.Element is not ScalarType { IsFloat: false } readElem
        || readElem.ByteSize is not (2 or 4)
        || (readElem.ByteSize == 4 && !readElem.Signed))
      return false;
    // Target must be a writable scalar of the element's width (not counter, not BYREF param)
    if (assign.Target is not NameExpr tgtExpr)
      return false;
    if (!model.VariableBindings.TryGetValue(tgtExpr, out var tgtSym))
      return false;
    if (ReferenceEquals(tgtSym, counter))
      return false; // writing counter through x% = a%(i%) is pathological - skip
    if (tgtSym.Type is not ScalarType { IsFloat: false } tgtScalar || tgtScalar.ByteSize != readElem.ByteSize)
      return false;
    if (tgtSym.Storage == VariableStorage.Parameter)
      return false; // BYREF parameter pointer: aliasing risk
    if (this.TryDirectCell(tgtSym) is not { } tgtCell)
      return false;

    var asm = this._asm;
    var arrayLabel = this.SlotOf(readArr);
    var lbound = readBounds[0].Lower;
    var elementSize = Math.Max(readArrType.Element.Size, 1); // always 2 for INTEGER gate above

    // Initialize counter cell to From
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), PbType.Integer, f.From);
    asm.Mov(counterCell.WithSize(OperandSize.Word), Reg.AX);

    // Evaluate limit once into a temp slot
    var limitSlot = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), PbType.Integer, f.To);
    asm.Mov(limitSlot, Reg.AX);

    // Compute initial element pointer: OFFSET(array) + (from - lbound) * elementSize.
    // Done directly (no EmitPlace) so no IMUL appears — for elementSize=2, use ADD AX,AX.
    var addrSlot = this.AllocTemp(2);
    if (this.OptFolder.TryFold(f.From) is { Integer: { } fromConst }) {
      // Compile-time FROM: initial offset is a pure assembler-time constant.
      var byteOffset = checked((int)((fromConst - lbound) * elementSize));
      asm.Mov(addrSlot, Imm.OffsetOf(arrayLabel, byteOffset));
    } else {
      // Runtime FROM: counter cell already holds the from value (stored just above).
      asm.Mov(Reg.AX, counterCell.WithSize(OperandSize.Word)); // AX = from
      if (lbound != 0)
        asm.Sub(Reg.AX, (Imm)lbound);                          // AX = from - lbound
      // Multiply by elementSize using shifts (no IMUL).
      // Gate above ensures elementSize == 2; one ADD AX,AX doubles it.
      for (var s = elementSize; s > 1; s >>= 1)
        asm.Add(Reg.AX, Reg.AX);                               // AX *= 2 per bit
      asm.Add(Reg.AX, Imm.OffsetOf(arrayLabel));               // AX += base label offset
      asm.Mov(addrSlot, Reg.AX);
    }

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var cont = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(cont);
    this._iterateAny.Push(cont);

    asm.MarkLabel(top);
    asm.Mov(Reg.AX, counterCell.WithSize(OperandSize.Word));
    asm.Cmp(Reg.AX, limitSlot);
    if (step >= 0) asm.Jg(done); else asm.Jl(done);

    // Body: x% = a%(i%) via the stepped address slot (no per-iteration IMUL)
    asm.Mov(Reg.BX, addrSlot);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    asm.Mov(tgtCell.WithSize(OperandSize.Word), Reg.AX);
    if (readElem.ByteSize == 4) {
      asm.Mov(Reg.DX, Mem.Word(Reg.BX, 2));                  // high half of the LONG element
      asm.Mov(Adjust(tgtCell, 2, OperandSize.Word), Reg.DX);
    }

    asm.MarkLabel(cont);
    // Advance stepped address and counter
    var addrStride = (Imm)(int)(Math.Max(readArrType.Element.Size, 1) * Math.Abs(step));
    if (step >= 0) asm.Add(addrSlot, addrStride); else asm.Sub(addrSlot, addrStride);
    asm.Mov(Reg.AX, counterCell.WithSize(OperandSize.Word));
    var counterStride = (Imm)(int)Math.Abs(step);
    if (step >= 0) asm.Add(Reg.AX, counterStride); else asm.Sub(Reg.AX, counterStride);
    asm.Mov(counterCell.WithSize(OperandSize.Word), Reg.AX);

    asm.Jmp(top);
    asm.MarkLabel(done);

    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    this.ReleaseTemp(2); // addrSlot (released last-allocated first)
    this.ReleaseTemp(2); // limitSlot
    return true;
  }
  #endregion
  #region LICM - loop-invariant code motion ($OPTIMIZE SPEED)

  /// <summary>
  /// pb36 LICM: hoists pure integer subexpressions from the body of
  /// <paramref name="f"/> into the preheader when their operands are all
  /// loop-invariant (not written in the body and not the loop counter).
  ///
  /// Each hoistable expression is computed once into a dedicated CSE frame slot
  /// before the loop; in-body occurrences reload the slot via the existing CSE
  /// DEFINE/reload mechanism. Returns the number of new slots allocated (0 when
  /// no invariants are found or the gate rejects the loop).
  ///
  /// Gate:
  /// <list type="bullet">
  ///   <item><see cref="OptimizeSpeed"/> must be on.</item>
  ///   <item>No checked arithmetic (<see cref="CheckNumeric"/> /
  ///   <see cref="CheckOverflow"/>): hoisted expressions must never trap in a
  ///   zero-trip loop where the body does not execute.</item>
  ///   <item>No error-handler scope (<c>_trackResume</c>): a handler could
  ///   observe the preheader computation in a loop that would otherwise be
  ///   skipped.</item>
  /// </list>
  /// </summary>
  private int EmitLicmPreheader(ForStmt f, VariableSymbol counter) {
    if (!this.Optimize || !this.OptimizeSpeed)
      return 0;
    if (this.CheckNumeric || this.CheckOverflow)
      return 0;
    if (this._trackResume)
      return 0;

    // checkedArithmetic mirrors the gate in OptCommonSubexpr.Analyze
    var checkedArithmetic = model.MetaStatements.Any(m =>
      m.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
      && m.Arguments.Count >= 2
      && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "ALL"
      && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase));

    var firstSlot = this._cseBytes / 4; // next available slot index
    var licm = OptCommonSubexpr.AnalyzeLicm(f.Body, counter, firstSlot, checkedArithmetic, model);
    if (licm.SlotCount == 0)
      return 0;

    // allocate the new slots in the frame (EndFrame will see the updated _cseBytes)
    this._cseBytes += licm.SlotCount * 4;

    // merge LICM marks into the frame-wide CSE mark dictionary
    this._cseMarks ??= new(ReferenceEqualityComparer.Instance);
    foreach (var (node, mark) in licm.Marks)
      this._cseMarks[node] = mark;

    // emit the preheader: each DEFINE is emitted here (once, before the loop);
    // EmitExpression sees the DEFINE mark, evaluates the tree and stashes the
    // result to the slot - identical to the in-body DEFINE path for block-local CSE.
    // After the preheader emit we downgrade the DEFINE mark on the same node to a
    // USE, so that the body occurrence (same AST node instance) reloads the slot
    // rather than recomputing and re-stashing it.
    foreach (var defineNode in licm.Preheader) {
      // integer nodes: DEFINE fires inside EmitExpression (checks _cseMarks for IsFloat:false nodes)
      // modular nodes: DEFINE fires inside EmitModularInt16 (typed Single/Double by the binder but
      //   computed on the 16-bit ALU); EmitExpression would bypass the CSE mark for float-typed nodes
      if (licm.ModularPreheader.Contains(defineNode))
        this.EmitModularInt16(defineNode); // DEFINE: compute on 16-bit ALU + stash to slot
      else
        this.EmitExpression(defineNode);   // DEFINE: compute + stash to slot
      // downgrade to USE so the body occurrence (same AST node) reloads the slot
      if (this._cseMarks!.TryGetValue(defineNode, out var defMark))
        this._cseMarks[defineNode] = new OptCommonSubexpr.CseMark(defMark.Slot, IsDefine: false);
    }

    return licm.SlotCount;
  }

  #endregion
}

