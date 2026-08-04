using System.Globalization;
using System.Text;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Emit;

/// <summary>Raised when the IR contains something this writer cannot render as PowerBASIC.</summary>
public sealed class IrBasicWriterException(string what) : Exception(what) {
  /// <summary>The construct that stopped it, already collapsed to its cause.</summary>
  public string What { get; } = what;
}

/// <summary>
/// Renders an <see cref="IrModule"/> back to PowerBASIC source - a back end that targets BASIC itself.
///
/// The existing <see cref="BasicWriter"/> renders the bound AST, which means it can only ever show a
/// program as it was written. This one renders the IR, so it shows a program as it will be
/// <b>compiled</b> - after lowering, and after whatever the optimizer did to it. That is the whole
/// point: an optimization that rewrites the program can be checked by compiling the BASIC this
/// produces and comparing what it prints against the original. A pass that changes behaviour stops
/// being an assertion about instruction counts and becomes a program that prints something else.
///
/// <para>
/// Control flow is emitted as labels and <c>GOTO</c>s rather than recovered into <c>IF</c>/<c>FOR</c>
/// blocks. A basic block IS a label and a branch IS a GOTO, so the translation is exact and needs no
/// structuring analysis that could be subtly wrong; the output is machine-readable rather than
/// pretty, which is the right trade for something whose job is verification.
/// </para>
/// <para>
/// SSA is destroyed the standard way: every value becomes a variable, and a phi becomes an assignment
/// on each incoming edge, placed before the branch that takes it.
/// </para>
/// <para>
/// Anything it cannot render exactly throws <see cref="IrBasicWriterException"/> naming the construct.
/// Emitting approximate BASIC would defeat the purpose - the output is meant to be compiled and
/// compared, so a construct rendered "close enough" would report a miscompile that is really a
/// mistranslation, or worse, hide a real one.
/// </para>
/// </summary>
public sealed class IrBasicWriter {

  private readonly StringBuilder _out = new();
  private readonly Dictionary<IrValue, string> _names = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrType, string> _declared = [];
  private readonly List<(string Name, IrType Type)> _locals = [];
  private int _seq;

  /// <summary>Renders a whole module: its globals, then each defined function.</summary>
  public static string Write(IrModule module) {
    var writer = new IrBasicWriter();
    writer.Module(module);
    return writer._out.ToString();
  }

  /// <summary>Renders one function on its own (the common case in tests).</summary>
  public static string Write(IrFunction function) {
    var writer = new IrBasicWriter();
    writer.Function(function);
    return writer._out.ToString();
  }

  private void Line(string text = "") => this._out.Append(text).Append('\n');

  private void Module(IrModule module) {
    foreach (var global in module.Globals)
      this.Global(global);
    // the module body has to come first: in BASIC the executable statements ARE the program, and a
    // SUB before them would be read as part of it
    var defined = module.Functions.Where(f => !f.IsDeclaration).ToList();
    foreach (var function in defined.Where(IsMain).Concat(defined.Where(f => !IsMain(f))))
      this.Function(function);
  }

  private static bool IsMain(IrFunction function) => function.Name.Equals("main", StringComparison.OrdinalIgnoreCase);

  private void Global(IrGlobalVariable global) {
    // A byte blob is the literal pool - a string constant, or the DATA blob. It needs no declaration
    // because every use writes the literal out inline; a use this writer cannot render inline (the
    // DATA cursor, say) declines at the use site, where the diagnostic is more useful anyway.
    if (global.Bytes is not null)
      return;
    var name = Sanitize(global.Name);
    this._names[global] = name;
    this.Line(global.Count > 1
      ? $"DIM {name}(0 TO {global.Count - 1}) AS {TypeName(global.ValueType)}"
      : $"DIM {name} AS {TypeName(global.ValueType)}");
  }

  // ---- functions --------------------------------------------------------------------------------

  private void Function(IrFunction function) {
    this._names.Clear();
    this._locals.Clear();
    this._seq = 0;

    var isMain = function.Name.Equals("main", StringComparison.OrdinalIgnoreCase);
    var body = new IrBasicWriter { _seq = 0 };   // rendered first, so the DIMs it needs are known
    foreach (var (argument, index) in function.Parameters.Select((a, i) => (a, i)))
      body._names[argument] = argument.Name is { Length: > 0 } n ? Sanitize(n) : $"p{index}";
    body.Body(function);

    if (!isMain) {
      var parameters = string.Join(", ", function.Parameters.Select(a =>
        $"BYVAL {body._names[a]} AS {TypeName(a.Type)}"));
      this.Line(function.ReturnType.IsVoid
        ? $"SUB {Sanitize(function.Name)}({parameters})"
        : $"FUNCTION {Sanitize(function.Name)}({parameters}) AS {TypeName(function.ReturnType)}");
    }

    foreach (var (name, type) in body._locals)
      this.Line($"  DIM {name} AS {TypeName(type)}");
    this._out.Append(body._out);

    if (!isMain)
      this.Line(function.ReturnType.IsVoid ? "END SUB" : "END FUNCTION");
    else
      this.Line("END");
    this.Line();
  }

  private void Body(IrFunction function) {
    // Every phi is named BEFORE anything is emitted. A loop header reads its own phi in the header,
    // which is emitted long before the back edge that assigns it - naming them lazily on the first
    // incoming edge left that read with nothing to refer to.
    foreach (var phi in function.Blocks.SelectMany(b => b.Instructions).OfType<IrPhi>())
      this.Declare(phi);

    foreach (var block in function.Blocks) {
      this.Line($"{BlockLabel(block)}:");
      foreach (var instruction in block.Instructions)
        this.Instruction(instruction, function);
    }
  }

  /// <summary>A block's label, made unique and legal (PB labels are alphanumeric plus underscore).</summary>
  private static string BlockLabel(IrBasicBlock block) => "L_" + Sanitize(block.Label);

  private static string Sanitize(string name) {
    var text = new StringBuilder();
    foreach (var c in name)
      text.Append(char.IsLetterOrDigit(c) ? c : '_');
    return char.IsDigit(text.Length > 0 ? text[0] : 'x') ? "_" + text : text.ToString();
  }

  // ---- values -----------------------------------------------------------------------------------

  /// <summary>The variable an instruction's result lives in, minted on first use and DIMmed once.</summary>
  private string Define(IrInstruction instruction) => this.Declare(instruction);

  /// <summary>Mints (once) the variable a value lives in, and records the DIM it needs.</summary>
  private string Declare(IrValue value) {
    if (this._names.TryGetValue(value, out var existing))
      return existing;
    var name = $"v{this._seq++}";
    this._names[value] = name;
    this._locals.Add((name, value.Type));
    return name;
  }

  /// <summary>A value as a BASIC expression: a literal, or the variable holding it.</summary>
  private string Ref(IrValue value) => value switch {
    IrConstantInt c => Literal(c),
    IrConstantFloat f => f.Value.ToString("R", CultureInfo.InvariantCulture) is var s
      && !s.Contains('.') && !s.Contains('E') ? s + ".0" : f.Value.ToString("R", CultureInfo.InvariantCulture),
    IrUndef => "0",
    _ => this._names.TryGetValue(value, out var name)
      ? name
      : throw new IrBasicWriterException($"a value with no BASIC name ({value.GetType().Name})"),
  };

  /// <summary>An integer literal, with the suffix that gives it the right width.</summary>
  private static string Literal(IrConstantInt c) {
    var text = c.Value.ToString(CultureInfo.InvariantCulture);
    return c.Type.Bits switch {
      <= 16 => text,
      32 => text + "&",
      _ => text + "&&",
    };
  }

  private static string TypeName(IrType type) => type.Kind switch {
    IrTypeKind.Int => type.Bits switch {
      1 or 8 or 16 => type.Signed ? "INTEGER" : "WORD",
      32 => type.Signed ? "LONG" : "DWORD",
      64 => type.Signed ? "QUAD" : "QWORD",
      _ => throw new IrBasicWriterException($"an integer of {type.Bits} bits"),
    },
    IrTypeKind.Float when type.IsIeeeFloat => type.Bits == 32 ? "SINGLE" : "DOUBLE",
    _ => throw new IrBasicWriterException($"the type {type}"),
  };

  // ---- instructions -----------------------------------------------------------------------------

  private void Instruction(IrInstruction instruction, IrFunction function) {
    switch (instruction) {
      case IrBinary b: this.Line($"  {this.Define(b)} = {this.Binary(b)}"); return;
      case IrCmp c: this.Line($"  {this.Define(c)} = -({this.Ref(c.Lhs)} {Predicate(c.Pred)} {this.Ref(c.Rhs)})"); return;
      case IrCast cast: this.Cast(cast); return;
      case IrSelect s:
        // the ternary, spelled as the two-armed IF PB has had since forever
        this.Line($"  {this.Define(s)} = {this.Ref(s.IfFalse)}");
        this.Line($"  IF {this.Ref(s.Condition)} <> 0 THEN {this._names[s]} = {this.Ref(s.IfTrue)}");
        return;
      case IrBr br: this.PhiCopies(br.Target, br.Parent!); this.Line($"  GOTO {BlockLabel(br.Target)}"); return;
      case IrCondBr cond: this.CondBr(cond); return;
      case IrRet ret: this.Ret(ret, function); return;
      case IrPhi: return;                       // materialized on the incoming edges, not here
      case IrUnreachable: this.Line("  END"); return;
      case IrCall call: this.Call(call); return;

      // An alloca whose address never leaves it is a VARIABLE, not storage: BASIC has no way to name
      // a frame slot, but it does not need one - every load is a read of that variable and every
      // store a write. The moment the address is taken by anything else (a GEP into an array, a call
      // that receives it) this stops being true, so ScalarSlot refuses those.
      case IrAlloca alloca:
        this.ScalarSlot(alloca);
        return;
      case IrLoad load:
        this.Line($"  {this.Define(load)} = {this.Ref(this.ScalarSlot(load.Pointer))}");
        return;
      case IrStore store:
        this.Line($"  {this.Ref(this.ScalarSlot(store.Pointer))} = {this.Ref(store.Value)}");
        return;
      case IrSwitch sw: this.Switch(sw); return;
      default:
        throw new IrBasicWriterException($"the instruction {instruction.GetType().Name}");
    }
  }

  private string Binary(IrBinary b) {
    var (l, r) = (this.Ref(b.Lhs), this.Ref(b.Rhs));
    return b.Op switch {
      IrBinaryOp.Add or IrBinaryOp.FAdd => $"{l} + {r}",
      IrBinaryOp.Sub or IrBinaryOp.FSub => $"{l} - {r}",
      IrBinaryOp.Mul or IrBinaryOp.FMul => $"{l} * {r}",
      IrBinaryOp.FDiv => $"{l} / {r}",
      IrBinaryOp.SDiv => $"{l} \\ {r}",
      IrBinaryOp.SRem => $"{l} MOD {r}",
      IrBinaryOp.And => $"{l} AND {r}",
      IrBinaryOp.Or => $"{l} OR {r}",
      IrBinaryOp.Xor => $"{l} XOR {r}",
      // PB's SHIFT is a statement, so a shift is spelled as the multiply/divide it is - exact for a
      // logical shift of a non-negative value and for an arithmetic shift of any value
      IrBinaryOp.Shl => $"{l} * {Power2(b.Rhs)}",
      IrBinaryOp.AShr => $"{l} \\ {Power2(b.Rhs)}",
      _ => throw new IrBasicWriterException($"the operator {b.Op}"),
    };
  }

  /// <summary>Two to the power of a constant shift count; a runtime count has no closed BASIC form here.</summary>
  private static string Power2(IrValue count) => count is IrConstantInt { Value: >= 0 and < 31 } c
    ? (1L << (int)c.Value).ToString(CultureInfo.InvariantCulture) + (c.Value >= 15 ? "&" : "")
    : throw new IrBasicWriterException("a shift by a runtime amount");

  private static string Predicate(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq or IrCmpPred.Foeq => "=",
    IrCmpPred.Ne or IrCmpPred.Fone => "<>",
    IrCmpPred.Slt or IrCmpPred.Ult or IrCmpPred.Folt => "<",
    IrCmpPred.Sle or IrCmpPred.Ule or IrCmpPred.Fole => "<=",
    IrCmpPred.Sgt or IrCmpPred.Ugt or IrCmpPred.Fogt => ">",
    IrCmpPred.Sge or IrCmpPred.Uge or IrCmpPred.Foge => ">=",
    _ => throw new IrBasicWriterException($"the predicate {pred}"),
  };

  private void Cast(IrCast cast) {
    var source = this.Ref(cast.Value);
    var text = cast.Op switch {
      // a compare's i1 is 1/0 here (see IrCmp above), so widening it signed is a negation and
      // widening it unsigned is the identity - which is exactly BASIC's -1/0 truth value either way
      IrCastOp.SExt when cast.Value.Type.Bits == 1 => $"-{source}",
      IrCastOp.ZExt when cast.Value.Type.Bits == 1 => source,
      IrCastOp.SExt => source,                                  // PB widens a signed value on assignment
      IrCastOp.ZExt => $"{source} AND {Mask(cast.Value.Type.Bits)}",
      IrCastOp.SIToFP or IrCastOp.UIToFP => source,             // assignment to a real widens
      IrCastOp.FPToSIRound => $"CLNG({source})",                // BASIC rounds when a real meets an integer
      IrCastOp.FPToSI => $"FIX({source})",                      // FIX truncates toward zero
      IrCastOp.FPExt or IrCastOp.FPTrunc => source,
      _ => throw new IrBasicWriterException($"the cast {cast.Op}"),
    };
    this.Line($"  {this.Define(cast)} = {text}");
  }

  private static string Mask(int bits) => bits switch {
    8 => "&HFF&",
    16 => "&HFFFF&",
    _ => throw new IrBasicWriterException($"a zero-extension from {bits} bits"),
  };

  private void CondBr(IrCondBr cond) {
    // the copies for BOTH edges have to be placed before the test, and they cannot clash: an edge's
    // copies are only correct on that edge, so the false edge gets its own labelled run
    var taken = $"T{this._seq++}";
    this.Line($"  IF {this.Ref(cond.Condition)} = 0 THEN GOTO {taken}");
    this.PhiCopies(cond.IfTrue, cond.Parent!);
    this.Line($"  GOTO {BlockLabel(cond.IfTrue)}");
    this.Line($"{taken}:");
    this.PhiCopies(cond.IfFalse, cond.Parent!);
    this.Line($"  GOTO {BlockLabel(cond.IfFalse)}");
  }

  private void Switch(IrSwitch sw) {
    foreach (var (value, target) in sw.Cases) {
      var next = $"S{this._seq++}";
      this.Line($"  IF {this.Ref(sw.Condition)} <> {value} THEN GOTO {next}");
      this.PhiCopies(target, sw.Parent!);
      this.Line($"  GOTO {BlockLabel(target)}");
      this.Line($"{next}:");
    }
    this.PhiCopies(sw.DefaultTarget, sw.Parent!);
    this.Line($"  GOTO {BlockLabel(sw.DefaultTarget)}");
  }

  /// <summary>
  /// SSA destruction: on the edge into <paramref name="target"/>, every phi there takes the value its
  /// incoming entry names for this predecessor. Emitted before the branch, which is where the edge is.
  /// </summary>
  private void PhiCopies(IrBasicBlock target, IrBasicBlock from) {
    foreach (var phi in target.Instructions.OfType<IrPhi>()) {
      if (phi.IncomingFrom(from) is not { } incoming)
        throw new IrBasicWriterException("a phi with no entry for one of its predecessors");
      this.Line($"  {this.Declare(phi)} = {this.Ref(incoming)}");
    }
  }

  private void Ret(IrRet ret, IrFunction function) {
    if (ret.Value is { } value)
      this.Line($"  {Sanitize(function.Name)} = {this.Ref(value)}");
    this.Line(function.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
      ? "  END"
      : function.ReturnType.IsVoid ? "  EXIT SUB" : "  EXIT FUNCTION");
  }

  /// <summary>
  /// The variable an alloca stands for, or a refusal. It qualifies when it holds ONE value of a type
  /// BASIC can name and every use of its address is a plain load or store - which is to say, when it
  /// is a local variable that mem2reg simply did not get to (an escaping one, or one the pass left
  /// alone). Anything else is real storage and needs pointers this writer does not emit.
  /// </summary>
  private IrValue ScalarSlot(IrValue pointer) {
    if (pointer is not IrAlloca alloca)
      throw new IrBasicWriterException($"a load or store through {pointer.GetType().Name} rather than a local slot");
    if (alloca.Count > 1)
      throw new IrBasicWriterException("an alloca holding more than one element (an array)");
    if (alloca.Allocated.Kind is IrTypeKind.Ptr)
      throw new IrBasicWriterException("an alloca holding a pointer (a string handle or array descriptor)");
    foreach (var user in alloca.Users)
      if (user is not (IrLoad or IrStore))
        throw new IrBasicWriterException($"an alloca whose address escapes into {user.GetType().Name}");
    // a store's pointer operand is a use too, so only the VALUE position counts as an escape
    foreach (var user in alloca.Users)
      if (user is IrStore stored && ReferenceEquals(stored.Value, alloca))
        throw new IrBasicWriterException("an alloca whose address is itself stored");

    if (!this._names.TryGetValue(alloca, out _)) {
      var name = alloca.Name is { Length: > 0 } n ? Sanitize(n) + "_" + this._seq++ : $"v{this._seq++}";
      this._names[alloca] = name;
      this._locals.Add((name, alloca.Allocated));
    }
    return alloca;
  }

  /// <summary>
  /// The console-output routines, mapped back to the statement they were lowered FROM. The lowering
  /// emits one call per printed item plus a newline call, so each item becomes its own
  /// <c>PRINT ...;</c> and the newline becomes a bare <c>PRINT</c> - which prints identically, the
  /// trailing semicolon being exactly what suppresses the line break between items.
  /// </summary>
  private static readonly HashSet<string> _printItem = new(StringComparer.Ordinal) {
    "rt_print_i16", "rt_print_i32", "rt_print_u16", "rt_print_u32", "rt_print_u8", "rt_print_i8",
    "rt_print_single", "rt_print_double", "rt_print_strvar",
  };

  /// <summary>Runtime routines that PRODUCE a string; each renders as a BASIC expression, not a statement.</summary>
  private bool TryStringExpression(IrCall call, IrFunction callee) {
    switch (callee.Name) {
      // rt_str_const(bytes, length) IS a string literal - it is what one lowers to
      case "rt_str_const" when call.Args.FirstOrDefault() is IrGlobalVariable { Bytes: { } bytes }:
        this._names[call] = Quote(System.Text.Encoding.ASCII.GetString(bytes));
        return true;
      case "rt_str_concat":
        this._names[call] = $"({this.Ref(call.Args.ElementAt(0))} + {this.Ref(call.Args.ElementAt(1))})";
        return true;
      // a copy of a string value is that value: BASIC assigns strings by value, so the ownership
      // bookkeeping the handle model needs has no spelling here and no need of one
      case "rt_str_dup":
        this._names[call] = this.Ref(call.Args.First());
        return true;
      default:
        return false;
    }
  }

  /// <summary>
  /// The file routines, mapped back to the statements they came from. Every one of these takes the PB
  /// file number as its first argument, which is the <c>#n</c> the statement names.
  /// </summary>
  private bool TryFileStatement(IrCall call, IrFunction callee) {
    var args = call.Args.ToList();
    switch (callee.Name) {
      // rt_file_open(number, name, mode, reclen) - the mode is the lowering's own encoding
      case "rt_file_open" when args.Count == 4: {
        var mode = args[2] is IrConstantInt m
          ? m.Value switch {
            0 => "INPUT", 1 => "OUTPUT", 2 => "APPEND", 3 => "RANDOM", 4 => "BINARY",
            _ => throw new IrBasicWriterException($"OPEN with the unknown mode {m.Value}"),
          }
          : throw new IrBasicWriterException("OPEN with a mode computed at run time");
        var length = args[3] is IrConstantInt { Value: > 0 } len ? $" LEN = {len.Value}" : "";
        this.Line($"  OPEN {this.Ref(args[1])} FOR {mode} AS #{this.Ref(args[0])}{length}");
        return true;
      }
      case "rt_file_close" when args.Count == 1:
        this.Line($"  CLOSE #{this.Ref(args[0])}");
        return true;
      case "rt_file_close_all" when args.Count == 0:
        this.Line("  CLOSE");
        return true;
      // the newline on a file: PB's PRINT # wants the separating comma even with nothing after it,
      // so the empty string stands in - it writes no characters and then the line break, which is
      // exactly what the bare newline call does
      case "rt_fprint_nl" when args.Count == 1:
        this.Line($"  PRINT #{this.Ref(args[0])}, \"\"");
        return true;
      case "rt_fprint_str" when args is [{ } number, IrGlobalVariable { Bytes: { } bytes }, _]:
        this.Line($"  PRINT #{this.Ref(number)}, {Quote(System.Text.Encoding.ASCII.GetString(bytes))};");
        return true;
      case "rt_fprint_i16" or "rt_fprint_i32" or "rt_fprint_u16" or "rt_fprint_u32"
        or "rt_fprint_u8" or "rt_fprint_single" or "rt_fprint_double" or "rt_fprint_strvar"
        when args.Count == 2:
        this.Line($"  PRINT #{this.Ref(args[0])}, {this.Ref(args[1])};");
        return true;
      default:
        return false;
    }
  }

  /// <summary>A BASIC string literal, with the doubled quotes BASIC escapes with.</summary>
  private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

  private void Call(IrCall call) {
    if (call.Callee is not IrFunction callee)
      throw new IrBasicWriterException("an indirect call");
    if (callee.IsDeclaration) {
      if (this.TryStringExpression(call, callee))
        return;
      if (callee.Name == "rt_print_nl") {
        this.Line("  PRINT");
        return;
      }
      // PRINT of a literal: the lowering passes the bytes and their length, and the length is
      // implied by the literal itself once it is written back out
      if (callee.Name == "rt_print_str" && call.Args.FirstOrDefault() is IrGlobalVariable { Bytes: { } bytes }) {
        this.Line($"  PRINT {Quote(System.Text.Encoding.ASCII.GetString(bytes))};");
        return;
      }
      if (_printItem.Contains(callee.Name)) {
        this.Line($"  PRINT {this.Ref(call.Args.First())};");
        return;
      }
      if (this.TryFileStatement(call, callee))
        return;
      throw new IrBasicWriterException($"a call to the runtime routine {callee.Name}");
    }
    var args = string.Join(", ", call.Args.Select(this.Ref));
    this.Line(call.Type.IsVoid
      ? $"  CALL {Sanitize(callee.Name)}({args})"
      : $"  {this.Define(call)} = {Sanitize(callee.Name)}({args})");
  }
}
