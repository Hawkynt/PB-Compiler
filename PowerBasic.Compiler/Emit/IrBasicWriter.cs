using System.Globalization;
using System.Text;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Syntax;

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

  /// <summary>
  /// Facts the rendered pb35 cannot carry, stated rather than silently dropped. Microsoft Binary
  /// Format is the one that matters: BASICA and GW-BASIC store SINGLE in it, pb35 stores IEEE, and
  /// the two disagree on exponent bias and layout. The rendered program computes the same VALUES to
  /// the precision IEEE gives, but it is not bit-for-bit the same storage, and a reader deserves to
  /// be told which of those they are holding.
  /// </summary>
  private readonly List<string> _warnings = [];
  private readonly List<(string Name, IrType Element, int Count)> _arrays = [];
  private readonly Dictionary<IrAlloca, string> _arrayNames = new(ReferenceEqualityComparer.Instance);

  /// <summary>One variable per (byte blob, field offset) - see <see cref="BlobField"/>.</summary>
  private readonly Dictionary<(IrAlloca Blob, long Offset), (string Name, IrType Type)> _blobFields = [];
  private int _seq;

  /// <summary>Renders a whole module: its globals, then each defined function.</summary>
  public static string Write(IrModule module) => Write(module, out _);

  /// <summary>
  /// Renders a module and reports what the pb35 spelling could not carry. The warnings are also
  /// written into the text as comments, so a rendered file is self-describing even when nobody kept
  /// the list - a translation that quietly loses a property is the thing worth avoiding.
  /// </summary>
  public static string Write(IrModule module, out IReadOnlyList<string> warnings) {
    var writer = new IrBasicWriter();
    writer.Module(module);
    warnings = writer._warnings;
    if (writer._warnings.Count == 0)
      return writer._out.ToString();
    var header = new StringBuilder();
    foreach (var warning in writer._warnings)
      header.Append("' WARNING: ").Append(warning).Append('\n');
    return header + writer._out.ToString();
  }

  /// <summary>Renders one function on its own (the common case in tests).</summary>
  public static string Write(IrFunction function) {
    var writer = new IrBasicWriter();
    writer.Function(function);
    return writer._out.ToString();
  }

  private void Line(string text = "") => this._out.Append(text).Append('\n');

  private void Module(IrModule module) {
    // The IR carries semantic choices, not just calculations. A pb35 recompile must use the source
    // dialect's runtime formatting, rounding, random-file and close semantics; $COMPAT is the
    // target language's lossless spelling for that requirement.
    if (module.EffectiveDialect != Dialect.Pb35) {
      this.Line($"$COMPAT {module.EffectiveDialect.CanonicalName()}");
      this.Line();
    }
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
    // SHARED, always: an IR global IS storage every function can see, and a module-level DIM without
    // it is visible only to the main body - a procedure reading one would not even compile. The
    // rendered program has procedures, so this is not a detail that could be left for later.
    this.Line(global.Count > 1
      ? $"DIM {name}(0 TO {global.Count - 1}) AS SHARED {this.DeclaredType(global.ValueType)}"
      : $"DIM {name} AS SHARED {this.DeclaredType(global.ValueType)}");
  }

  // ---- functions --------------------------------------------------------------------------------

  private void Function(IrFunction function) {
    this._names.Clear();
    this._locals.Clear();
    this._arrays.Clear();
    this._arrayNames.Clear();
    this._blobFields.Clear();
    this._seq = 0;

    var isMain = function.Name.Equals("main", StringComparison.OrdinalIgnoreCase);
    var body = new IrBasicWriter { _seq = 0 };   // rendered first, so the DIMs it needs are known
    foreach (var (argument, index) in function.Parameters.Select((a, i) => (a, i)))
      body._names[argument] = argument.Name is { Length: > 0 } n ? Sanitize(n) : $"p{index}";
    body.Body(function);

    if (!isMain) {
      var parameters = string.Join(", ", function.Parameters.Select(a =>
        $"BYVAL {body._names[a]} AS {body.DeclaredType(a.Type)}"));
      this.Line(function.ReturnType.IsVoid
        ? $"SUB {Sanitize(function.Name)}({parameters})"
        : $"FUNCTION {Sanitize(function.Name)}({parameters}) AS {this.DeclaredType(function.ReturnType)}");
    }

    // the body is rendered by a nested writer, so anything it had to say about the translation has to
    // come back with it - a warning recorded and then dropped is worse than none
    foreach (var warning in body._warnings)
      this.Note(warning);

    foreach (var (name, type) in body._locals)
      this.Line($"  DIM {name} AS {this.DeclaredType(type)}");
    foreach (var (name, element, count) in body._arrays)
      this.Line($"  DIM {name}(0 TO {count - 1}) AS {this.DeclaredType(element)}");
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

    foreach (var block in Ordered(function)) {
      this.Line($"{BlockLabel(block)}:");
      foreach (var instruction in block.Instructions)
        this.Instruction(instruction, function);
    }
  }

  /// <summary>
  /// The blocks in reverse post-order, so a value is named before it is used. Names are minted as
  /// definitions are reached, and the list order a function happens to hold is not dominance order -
  /// a pass that appends blocks (unrolling appends every copy it makes) leaves definitions sitting
  /// after the blocks that read them. Reverse post-order fixes that for everything except a phi's
  /// back-edge input, which is why phis are named up front instead.
  ///
  /// Unreachable blocks keep their original relative order at the end: they cannot be ordered by a
  /// walk that never reaches them, and dropping them would silently lose code.
  /// </summary>
  private static IEnumerable<IrBasicBlock> Ordered(IrFunction function) {
    var visited = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var post = new List<IrBasicBlock>();
    if (function.Entry is { } entry)
      Walk(entry);
    post.Reverse();
    return post.Concat(function.Blocks.Where(b => !visited.Contains(b)));

    void Walk(IrBasicBlock block) {
      if (!visited.Add(block))
        return;
      foreach (var successor in block.Terminator?.Successors ?? [])
        Walk(successor);
      post.Add(block);
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
    IrNullPtr => "\"\"",   // the null string handle IS the empty string
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

  /// <summary>The BASIC type for a DIM, noting anything the pb35 spelling cannot carry.</summary>
  private string DeclaredType(IrType type) {
    if (type.IsMbf)
      this.Note($"Microsoft Binary Format ({type}) is stored as IEEE {TypeName(type)}: pb35 has no MBF. "
        + "Values compute the same; the STORAGE layout does not survive.");
    return TypeName(type);
  }

  /// <summary>The value, with the note that its MBF storage did not survive the translation.</summary>
  private string MbfDropped(string source, IrType type) {
    this.Note($"Microsoft Binary Format is stored as IEEE {TypeName(type)}: pb35 has no MBF. "
      + "Values compute the same; the STORAGE layout does not survive.");
    return source;
  }

  /// <summary>Records a translation fact once, in order.</summary>
  private void Note(string warning) {
    if (!this._warnings.Contains(warning))
      this._warnings.Add(warning);
  }

  private static string TypeName(IrType type) => type.Kind switch {
    IrTypeKind.Int => type.Bits switch {
      1 or 8 or 16 => type.Signed ? "INTEGER" : "WORD",
      32 => type.Signed ? "LONG" : "DWORD",
      64 => type.Signed ? "QUAD" : "QWORD",
      _ => throw new IrBasicWriterException($"an integer of {type.Bits} bits"),
    },
    IrTypeKind.Float => type.Bits == 32 ? "SINGLE" : "DOUBLE",
    // every pointer this lowering produces for a VALUE is a string handle; storage pointers are
    // allocas and arrays, which are declared from what they hold rather than from the pointer
    IrTypeKind.Ptr => "STRING",
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
      case IrGep gep: this.ArrayElement(gep); return;   // a place, not a value: named for its uses
      case IrUnreachable: this.Line("  END"); return;
      case IrCall call: this.Call(call); return;

      // An alloca whose address never leaves it is a VARIABLE, not storage: BASIC has no way to name
      // a frame slot, but it does not need one - every load is a read of that variable and every
      // store a write. The moment the address is taken by anything else (a GEP into an array, a call
      // that receives it) this stops being true, so ScalarSlot refuses those.
      case IrAlloca alloca:
        // a multi-element slot is declared by the first subscript that names it (ArrayElement), so
        // the alloca itself contributes nothing here
        if (alloca.Count <= 1 && !this.IsByteBlob(alloca))
          this.ScalarSlot(alloca);
        return;
      case IrLoad load:
        this.Line($"  {this.Define(load)} = {this.Ref(this.ScalarSlot(load.Pointer, load.Type))}");
        return;
      case IrStore store:
        // A string slot is null-initialised at entry so the handle it replaces is readable. In BASIC
        // a string variable already starts empty, so the initialiser has nothing to say - and
        // rendering it would need a spelling for the null pointer, which the language has not got.
        if (store.Value is IrNullPtr && store.Pointer.Type.Kind == IrTypeKind.Ptr)
          return;
        this.Line($"  {this.Ref(this.ScalarSlot(store.Pointer, store.Value.Type))} = {this.Ref(store.Value)}");
        return;
      // inline asm renders as the "!" statement it came from - the one construct whose faithful
      // rendering is its own text, since the writer's target IS PowerBASIC
      case IrInlineAsm asm:
        this.Line($"  ! {asm.Text.Trim()}");
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

  /// <summary>A fresh variable of the given type, for an intermediate the BASIC form needs.</summary>
  private string Temp(IrType type) {
    var name = $"t{this._seq++}";
    this._locals.Add((name, type));
    return name;
  }

  /// <summary>
  /// Narrowing to <paramref name="bits"/>, spelled exactly. BASIC has no truncating conversion - an
  /// out-of-range assignment is an error, not a wrap - so the two's-complement result is computed:
  /// mask to the width, then fold the values above the signed maximum back down by 2^bits. The
  /// intermediate has to be wider than the target, which is why it needs a temporary of its own.
  /// </summary>
  private void Truncate(IrCast cast, string source, int bits) {
    var wide = cast.Value.Type;
    var temp = this.Temp(wide);
    var mask = (1L << bits) - 1;
    var signBit = 1L << (bits - 1);
    var suffix = wide.Bits > 16 ? "&" : "";
    this.Line($"  {temp} = {source} AND {mask}{suffix}");
    this.Line($"  IF {temp} > {signBit - 1}{suffix} THEN {temp} = {temp} - {mask + 1}{suffix}");
    this.Line($"  {this.Define(cast)} = {temp}");
  }

  private void Cast(IrCast cast) {
    var source = this.Ref(cast.Value);
    if (cast.Op == IrCastOp.Trunc) {
      if (cast.Type.Bits is not (8 or 16) || cast.Value.Type.Bits <= cast.Type.Bits)
        throw new IrBasicWriterException($"a truncation to {cast.Type.Bits} bits");
      this.Truncate(cast, source, cast.Type.Bits);
      return;
    }
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
      // Widening loses nothing, so it really is the identity. NARROWING is not: it is the rounding PB
      // performs when a value meets a narrower declared type, and dropping it here renders a program
      // that keeps precision the original discards. DIFF35 accumulates `s! = s! + x!`, where the sum
      // is computed at the x87's width and rounded to SINGLE on every store - rendered without the
      // CSNG, the total came out different.
      IrCastOp.FPExt => source,
      IrCastOp.FPTrunc => cast.Type.Bits switch {
        32 => $"CSNG({source})",
        64 => $"CDBL({source})",
        _ => throw new IrBasicWriterException($"a narrowing to {cast.Type.Bits}-bit float"),
      },
      // pb35 has no Microsoft Binary Format, so the conversions to and from it are the identity here
      // and the STORAGE simply becomes IEEE. That is a real loss of fidelity, so it is stated rather
      // than performed quietly - see DeclaredType.
      IrCastOp.MbfToFP or IrCastOp.FPToMbf => this.MbfDropped(source, cast.Type),
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
  private IrValue ScalarSlot(IrValue pointer, IrType? width = null) {
    // a GEP into an array slot is a subscript, and the element it names is what is read or written
    if (pointer is IrGep gep) {
      if (gep.BasePtr is IrAlloca gepBlob && this.IsByteBlob(gepBlob))
        return gep.ByteOffset is IrConstantInt at
          ? this.BlobField(gepBlob, at.Value, width ?? IrType.I16, gep)
          : throw new IrBasicWriterException("a TYPE field at a computed offset");
      return this.ArrayElement(gep);
    }
    // A module-level variable is a name, and reading or writing it through its address is just using
    // that name. The DIM belongs to the module render; a function rendered on its own therefore
    // refers to a variable it does not declare, which is correct - a single function was never a
    // whole program.
    if (pointer is IrGlobalVariable { Bytes: null, Count: 1 } global) {
      if (!this._names.ContainsKey(global))
        this._names[global] = Sanitize(global.Name);
      return global;
    }
    if (pointer is IrAlloca blob && this.IsByteBlob(blob))
      return this.BlobField(blob, 0, width ?? blob.Allocated, blob);
    if (pointer is not IrAlloca alloca)
      throw new IrBasicWriterException($"a load or store through {pointer.GetType().Name} rather than a local slot");
    // a zero byte offset folds away, so a load or store through the array itself is element zero -
    // which is what a(lo) compiles to, the subscript having been made relative to the lower bound
    if (alloca.Count > 1) {
      this._names[alloca] = $"{this.ArrayName(alloca, alloca.Allocated)}(0)";
      return alloca;
    }
    if (alloca.Allocated.Kind is IrTypeKind.Ptr)
      throw new IrBasicWriterException("an alloca holding a pointer (a string handle or array descriptor)");
    // a GEP is not an escape - it names an element, and the array and blob paths above render it.
    // Anything else taking the address (a call, a phi, a store OF the pointer) is.
    foreach (var user in alloca.Users)
      if (user is not (IrLoad or IrStore or IrGep))
        throw new IrBasicWriterException($"an alloca whose address escapes into {user.GetType().Name}");
    if (alloca.Count > 1)
      throw new IrBasicWriterException("an alloca holding more than one element used without a subscript");
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
    "rt_print_single", "rt_print_double", "rt_print_ext", "rt_print_i64", "rt_print_u64",
    "rt_print_strvar",
  };

  /// <summary>
  /// The runtime routines that ARE a BASIC intrinsic, mapped back to the spelling they were lowered
  /// from. Each is a pure expression, so it renders in place at every use rather than as a statement.
  /// The names are the intrinsic's own - LEN, VAL, LEFT$ - which is what makes the rendered program
  /// readable next to the original instead of a transcript of the runtime.
  /// </summary>
  private static readonly Dictionary<string, string> _intrinsics = new(StringComparer.Ordinal) {
    ["rt_str_len"] = "LEN", ["rt_str_val"] = "VAL", ["rt_str_asc"] = "ASC",
    ["rt_str_left"] = "LEFT$", ["rt_str_right"] = "RIGHT$",
    ["rt_str_mid"] = "MID$", ["rt_str_mid2"] = "MID$",
    ["rt_str_chr"] = "CHR$", ["rt_str_space"] = "SPACE$",
    ["rt_str_string"] = "STRING$", ["rt_str_string_s"] = "STRING$",
    ["rt_str_hex"] = "HEX$", ["rt_str_oct"] = "OCT$", ["rt_str_bin"] = "BIN$",
    ["rt_str_ucase"] = "UCASE$", ["rt_str_lcase"] = "LCASE$",
    ["rt_str_ltrim"] = "LTRIM$", ["rt_str_rtrim"] = "RTRIM$",
    ["rt_str_instr"] = "INSTR", ["rt_str_instr_start"] = "INSTR",
    ["rt_str_mkbyt"] = "MKBYT$", ["rt_str_mki"] = "MKI$", ["rt_str_mkl"] = "MKL$",
    ["rt_str_mks"] = "MKS$",
    ["rt_str_mkd"] = "MKD$", ["rt_str_mkdwd"] = "MKDWD$",
    ["rt_str_cvi"] = "CVI", ["rt_str_cvbyt"] = "CVBYT", ["rt_str_cvwrd"] = "CVWRD",
    ["rt_str_cvl"] = "CVL", ["rt_str_cvdwd"] = "CVDWD", ["rt_str_cvs"] = "CVS",
    ["rt_str_cvd"] = "CVD", ["rt_str_cve"] = "CVE",
    // STR$ of every width lowers to its own routine; they all came from the one intrinsic
    ["rt_str_from_i8"] = "STR$", ["rt_str_from_i16"] = "STR$", ["rt_str_from_i32"] = "STR$",
    ["rt_str_from_i64"] = "STR$", ["rt_str_from_u8"] = "STR$", ["rt_str_from_u16"] = "STR$",
    ["rt_str_from_u32"] = "STR$", ["rt_str_from_u64"] = "STR$",
    ["rt_str_from_single"] = "STR$", ["rt_str_from_double"] = "STR$",
  };

  /// <summary>Runtime routines that PRODUCE a string; each renders as a BASIC expression, not a statement.</summary>
  /// <summary>
  /// The LLVM math intrinsics, back to the BASIC functions they came from. The lowering appends the
  /// result width to the name (llvm.sqrt.f32, llvm.sqrt.f64, llvm.sqrt.f80 are one intrinsic at three
  /// precisions), so the width is stripped before the lookup rather than tripled in the table.
  /// </summary>
  private static readonly Dictionary<string, string> _mathIntrinsics = new(StringComparer.Ordinal) {
    ["sqrt"] = "SQR", ["sin"] = "SIN", ["cos"] = "COS", ["tan"] = "TAN",
    ["log"] = "LOG", ["exp"] = "EXP", ["fabs"] = "ABS", ["atan"] = "ATN",
  };

  /// <summary>The intrinsic behind an <c>llvm.NAME.fN</c> declaration, or null.</summary>
  private static string? MathName(string runtime) {
    if (!runtime.StartsWith("llvm.", StringComparison.Ordinal))
      return null;
    var parts = runtime.Split('.');
    return parts.Length == 3 ? parts[1] : null;
  }

  private bool TryStringExpression(IrCall call, IrFunction callee) {
    if (MathName(callee.Name) is { } math) {
      // exponentiation is an operator in BASIC, not a function - it is what '^' lowered to
      if (math == "pow" && call.Args.Count() == 2) {
        this._names[call] = $"({this.Ref(call.Args.ElementAt(0))} ^ {this.Ref(call.Args.ElementAt(1))})";
        return true;
      }
      if (_mathIntrinsics.TryGetValue(math, out var fn) && call.Args.Count() == 1) {
        this._names[call] = $"{fn}({this.Ref(call.Args.First())})";
        return true;
      }
    }
    if (_intrinsics.TryGetValue(callee.Name, out var intrinsic)) {
      this._names[call] = $"{intrinsic}({string.Join(", ", call.Args.Select(this.Ref))})";
      return true;
    }
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
      // Rounding half AWAY from zero, which is what QuickBASIC 1.0 to 3.0 do and pb35 does not.
      // pb35's own CINT rounds half to EVEN, so reproducing the source dialect means writing the
      // arithmetic out: move away from zero by a half and truncate. That is the point of carrying the
      // rounding as its own call - the renderer can emit the extra code rather than silently adopt
      // the target's rule.
      case "rt_round_half_away": {
        var x = this.Ref(call.Args.First());
        this.Note("rounding half away from zero (QuickBASIC 1.0-3.0) is written out as "
          + "SGN(x) * INT(ABS(x) + 0.5): pb35's own CINT rounds half to even.");
        this._names[call] = $"(SGN({x}) * INT(ABS({x}) + 0.5))";
        return true;
      }

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
      case "rt_fprint_i16" or "rt_fprint_i32" or "rt_fprint_i64" or "rt_fprint_u8" or "rt_fprint_u16"
        or "rt_fprint_u32" or "rt_fprint_u64" or "rt_fprint_single" or "rt_fprint_double"
        or "rt_fprint_ext" or "rt_fprint_strvar"
        when args.Count == 2:
        this.Line($"  PRINT #{this.Ref(args[0])}, {this.Ref(args[1])};");
        return true;
      default:
        return false;
    }
  }

  /// <summary>A BASIC string literal, with the doubled quotes BASIC escapes with.</summary>
  private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

  /// <summary>
  /// The BASIC array element a GEP names. The lowering builds a byte offset as
  /// <c>index * sizeof(element)</c>, so the index is recovered by undoing that multiply - which the
  /// optimizer may have turned into a shift, since element sizes are powers of two. Recovering it is
  /// what lets the access be written as <c>a(i)</c>; emitting the byte arithmetic instead would need
  /// pointers, and would render as something no reader could check against the original.
  /// </summary>
  /// <summary>
  /// A byte blob - what a <c>TYPE</c> variable or a fixed string lowers to: one alloca of N bytes,
  /// written and read at constant offsets with the WIDTH of whatever field lives there. It is not an
  /// array of anything, so rendering it as one is wrong twice over: the element type is a byte while
  /// the accesses are words and longs, and a GEP's displacement is in BYTES while a BASIC subscript
  /// counts elements.
  ///
  /// It is rendered by giving each field its own variable - scalar replacement, which is exact as
  /// long as the fields do not overlap and the blob's address never leaves it. That drops the TYPE
  /// declaration, which no reader of the rendered program needs: what they need is a program that
  /// computes the same thing.
  /// </summary>
  private bool IsByteBlob(IrAlloca alloca) => alloca.Count > 1 && alloca.Allocated.Bits == 8;

  /// <summary>The variable standing for the field at a byte offset, or a refusal.</summary>
  private IrValue BlobField(IrAlloca blob, long offset, IrType type, IrValue place) {
    foreach (var user in blob.Users)
      if (user is not (IrLoad or IrStore or IrGep))
        throw new IrBasicWriterException($"a TYPE variable whose address escapes into {user.GetType().Name}");

    var key = (blob, offset);
    if (!this._blobFields.TryGetValue(key, out var field)) {
      // a second access at the same offset must agree on the width, or the two are aliasing bytes
      // and separate variables would silently stop tracking each other
      field = ($"{(blob.Name is { Length: > 0 } n ? Sanitize(n) : "rec")}_{offset}_{this._seq++}", type);
      this._blobFields[key] = field;
      this._locals.Add(field);
    }
    if (field.Type != type)
      throw new IrBasicWriterException(
        $"a TYPE field read at two widths ({field.Type} and {type}) - overlapping fields cannot be split");
    this._names[place] = field.Name;
    return place;
  }

  private IrValue ArrayElement(IrGep gep) {
    // a module-level array is declared by the module render and named by its own identifier
    if (gep.BasePtr is IrGlobalVariable { Bytes: null } global) {
      var globalElement = gep.ElementType ?? global.ValueType;
      var globalStride = gep.ElementType is not null ? 1 : SizeOf(globalElement);
      var globalIndex = globalStride == 1 ? gep.ByteOffset : Undo(gep.ByteOffset, globalStride);
      if (!this._names.ContainsKey(global))
        this._names[global] = Sanitize(global.Name);
      this._names[gep] = $"{this._names[global]}({this.Ref(globalIndex)})";
      return gep;
    }
    if (gep.BasePtr is not IrAlloca array)
      throw new IrBasicWriterException($"a subscript of {gep.BasePtr.GetType().Name} rather than a local array");
    foreach (var user in array.Users)
      if (user is not (IrGep or IrLoad or IrStore))
        throw new IrBasicWriterException($"an array whose address escapes into {user.GetType().Name}");

    var element = gep.ElementType ?? array.Allocated;
    var stride = gep.ElementType is not null ? 1 : SizeOf(element);
    var index = stride == 1 ? gep.ByteOffset : Undo(gep.ByteOffset, stride);

    // an element is not a value of its own - it is a PLACE, named by its subscript expression
    this._names[gep] = $"{this.ArrayName(array, element)}({this.Ref(index)})";
    return gep;
  }

  /// <summary>The identifier an array slot is declared under, minted (and DIMmed) on first use.</summary>
  private string ArrayName(IrAlloca array, IrType element) {
    if (this._arrayNames.TryGetValue(array, out var existing))
      return existing;
    var name = array.Name is { Length: > 0 } n ? Sanitize(n) + "_" + this._seq++ : $"a{this._seq++}";
    this._arrayNames[array] = name;
    this._arrays.Add((name, element, array.Count));
    return name;
  }

  /// <summary>The index behind a byte offset of <paramref name="stride"/>, or a refusal.</summary>
  private static IrValue Undo(IrValue byteOffset, int stride) => byteOffset switch {
    IrBinary { Op: IrBinaryOp.Mul } m when m.Rhs is IrConstantInt c && c.Value == stride => m.Lhs,
    IrBinary { Op: IrBinaryOp.Mul } m when m.Lhs is IrConstantInt c && c.Value == stride => m.Rhs,
    IrBinary { Op: IrBinaryOp.Shl } sh when sh.Rhs is IrConstantInt s && 1L << (int)s.Value == stride => sh.Lhs,
    IrConstantInt k when k.Value % stride == 0 => new IrConstantInt(k.Type, k.Value / stride),
    _ => throw new IrBasicWriterException($"a subscript whose byte offset is not a multiple of {stride}"),
  };

  private static int SizeOf(IrType type) => type.Kind switch {
    IrTypeKind.Int or IrTypeKind.Float => Math.Max(1, type.Bits / 8),
    _ => throw new IrBasicWriterException($"the element type {type}"),
  };

  private void Call(IrCall call) {
    if (call.Callee is not IrFunction callee)
      throw new IrBasicWriterException("an indirect call");
    if (callee.IsDeclaration) {
      // Releasing the handle an assignment replaced is the same kind of bookkeeping rt_str_dup is,
      // and has the same spelling here: none. BASIC assigns strings by value and says nothing about
      // when the old one goes.
      if (callee.Name == "rt_str_free")
        return;
      if (this.TryStringExpression(call, callee))
        return;
      // ERROR n: the statement that raises, spelled as itself
      if (callee.Name == "rt_error") {
        this.Line($"  ERROR {this.Ref(call.Args.First())}");
        return;
      }
      if (callee.Name == "rt_locate" && call.Args.Count() == 2) {
        this.Line($"  LOCATE {this.Ref(call.Args.ElementAt(0))}, {this.Ref(call.Args.ElementAt(1))}");
        return;
      }
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
