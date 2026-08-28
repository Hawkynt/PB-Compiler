using System.Globalization;
using System.Text;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Emits strictly-valid textual LLVM IR (a <c>.ll</c> module) that the real LLVM
/// toolchain accepts (<c>llvm-as</c>, <c>opt -passes=verify</c>) and can lower to any
/// LLVM target (<c>llc</c>). This is what takes the compiler beyond 16-bit DOS: the
/// optimized IR is handed to LLVM's back end for x86-64/ARM/etc. Differs from
/// <see cref="IrPrinter"/> in using LLVM's spelling - <c>float</c>/<c>double</c>/<c>x86_fp80</c>
/// and the <c>getelementptr i8</c> form for byte-offset GEPs.
/// </summary>
public sealed class LlvmEmitter {

  private readonly Dictionary<IrValue, string> _names = new(ReferenceEqualityComparer.Instance);
  private int _slot;

  /// <summary>
  /// Emits a complete module, or returns null and reports WHICH construct this back end has no
  /// rendering for.
  ///
  /// <para>
  /// This is the entry a compilation driver uses, and the reason is that nothing catches a throw
  /// behind it: the x86-16 path can decline a function and let the direct emitter take it, but a
  /// <c>.ll</c> module has no second producer. A refusal that names the construct is the whole value
  /// this back end can offer for a program it cannot render - the same shape
  /// <see cref="IrLowering.TryLowerModule(Semantics.SemanticModel, out string?)"/> already has for
  /// the stage before it.
  /// </para>
  /// </summary>
  public static string? TryEmit(IrModule module, string? targetTriple, out string? declinedBecause) {
    declinedBecause = null;
    try {
      return Emit(module, targetTriple);
    } catch (EmitDeclinedException e) {
      declinedBecause = e.Message;
      return null;
    }
  }

  /// <summary>
  /// Emits a complete module, optionally with a target triple/datalayout header, raising
  /// <see cref="EmitDeclinedException"/> for a construct outside what this back end renders. Callers
  /// that cannot state in advance that a module is renderable want <see cref="TryEmit"/> instead.
  /// </summary>
  public static string Emit(IrModule module, string? targetTriple = null) {
    var sb = new StringBuilder();
    sb.Append("; ModuleID = '").Append(module.Name).Append("'\n");
    if (targetTriple is not null)
      sb.Append("target triple = \"").Append(targetTriple).Append("\"\n");
    if (sb.Length > 0)
      sb.Append('\n');
    foreach (var g in module.Globals)
      if (g.Bytes is { } bytes)
        sb.Append('@').Append(g.Name).Append(" = private constant [").Append(bytes.Length).Append(" x i8] c\"")
          .Append(EscapeBytes(bytes)).Append("\"\n");
      else
        sb.Append('@').Append(g.Name).Append(" = global ")
          .Append(g.Count > 1 ? $"[{g.Count} x {Ty(g.ValueType)}]" : Ty(g.ValueType))
          .Append(g.IsZeroInitialized ? " zeroinitializer" : "").Append('\n');
    if (module.Globals.Count > 0)
      sb.Append('\n');
    for (var i = 0; i < module.Functions.Count; ++i) {
      if (i > 0)
        sb.Append('\n');
      sb.Append(new LlvmEmitter().EmitFunction(module.Functions[i]));
    }
    return sb.ToString();
  }

  /// <summary>Emits a single function as a self-contained module fragment.</summary>
  public static string Emit(IrFunction function) => new LlvmEmitter().EmitFunction(function);

  private string EmitFunction(IrFunction fn) {
    this.AssignNames(fn);
    var sb = new StringBuilder();
    sb.Append(fn.IsDeclaration ? "declare " : "define ").Append(Ty(fn.ReturnType)).Append(" @").Append(fn.Name).Append('(');
    for (var i = 0; i < fn.Parameters.Count; ++i) {
      if (i > 0)
        sb.Append(", ");
      sb.Append(Ty(fn.Parameters[i].Type)).Append(' ').Append(this.Ref(fn.Parameters[i]));
    }
    if (fn.IsVarArgs)
      sb.Append(fn.Parameters.Count > 0 ? ", ..." : "...");
    sb.Append(')');
    if (fn.IsDeclaration)
      return sb.Append('\n').ToString();

    sb.Append(" {\n");
    foreach (var block in fn.Blocks) {
      sb.Append(block.Label).Append(":\n");
      foreach (var inst in block.Instructions)
        sb.Append("  ").Append(this.EmitInstruction(inst)).Append('\n');
    }
    sb.Append("}\n");
    return sb.ToString();
  }

  private void AssignNames(IrFunction fn) {
    foreach (var arg in fn.Parameters)
      this._names[arg] = "%" + (arg.Name ?? (this._slot++).ToString(CultureInfo.InvariantCulture));
    foreach (var block in fn.Blocks)
      foreach (var inst in block.Instructions)
        if (!inst.Type.IsVoid)
          this._names[inst] = "%" + (inst.Name ?? (this._slot++).ToString(CultureInfo.InvariantCulture));
  }

  private string EmitInstruction(IrInstruction inst) {
    // BASIC's assignment conversion ROUNDS a real into an integer, and `fptosi`/`fptoui` truncate -
    // so the rounding has to be done first and explicitly. llvm.rint rounds by the current mode,
    // which is nearest-ties-to-even, the same rule the x87 default applies on the native path.
    //
    // Both signednesses, because PB rounds either way: its `b?? = 3.5` is 4 exactly as its
    // `i% = 3.5` is, oracle-verified against genuine PBC 3.5 for BYTE, WORD and DWORD.
    if (inst is IrCast { Op: IrCastOp.FPToSIRound or IrCastOp.FPToUIRound } round) {
      var floatType = Ty(round.Value.Type);
      var rounded = "%rint." + this._slot++.ToString(CultureInfo.InvariantCulture);
      var convert = round.Op == IrCastOp.FPToSIRound ? "fptosi" : "fptoui";
      return $"{rounded} = call {floatType} @llvm.rint.{Suffix(round.Value.Type)}({floatType} {this.Ref(round.Value)})"
        + "\n  " + $"{this.Ref(round)} = {convert} {floatType} {rounded} to {Ty(round.Type)}";
    }
    var lhs = inst.Type.IsVoid ? "" : this.Ref(inst) + " = ";
    return lhs + inst switch {
      IrBinary b => $"{Mnemonic(b.Op)} {Ty(b.Type)} {this.Ref(b.Lhs)}, {this.Ref(b.Rhs)}",
      IrCmp c => $"{(IsFloatPred(c.Pred) ? "fcmp" : "icmp")} {Mnemonic(c.Pred)} {Ty(c.Lhs.Type)} {this.Ref(c.Lhs)}, {this.Ref(c.Rhs)}",
      IrCast c => $"{Mnemonic(c.Op)} {Ty(c.Value.Type)} {this.Ref(c.Value)} to {Ty(c.Type)}",
      IrAlloca a => a.Count > 1 ? $"alloca {Ty(a.Allocated)}, i32 {a.Count}" : $"alloca {Ty(a.Allocated)}",
      IrLoad l => $"load {Ty(l.Type)}, ptr {this.Ref(l.Pointer)}",
      IrStore s => $"store {Ty(s.Value.Type)} {this.Ref(s.Value)}, ptr {this.Ref(s.Pointer)}",
      IrGep g => $"getelementptr {Ty(g.ElementType ?? IrType.I8)}, ptr {this.Ref(g.BasePtr)}, {Ty(g.ByteOffset.Type)} {this.Ref(g.ByteOffset)}",
      IrPhi p => $"phi {Ty(p.Type)} {this.PhiInputs(p)}",
      IrSelect sel => $"select i1 {this.Ref(sel.Condition)}, {Ty(sel.Type)} {this.Ref(sel.IfTrue)}, {Ty(sel.Type)} {this.Ref(sel.IfFalse)}",
      // a variadic callee needs its FUNCTION type spelled out at the call, which LLVM requires
      // wherever the argument list cannot be read off the declaration
      IrCall call => $"call {CalleeType(call)} {this.Ref(call.Callee)}({this.Args(call)})",
      IrRet r => r.HasValue ? $"ret {Ty(r.Value!.Type)} {this.Ref(r.Value)}" : "ret void",
      IrBr br => $"br label %{br.Target.Label}",
      IrCondBr cb => $"br i1 {this.Ref(cb.Condition)}, label %{cb.IfTrue.Label}, label %{cb.IfFalse.Label}",
      IrSwitch sw => this.EmitSwitch(sw),
      IrIndirectBr ib => $"indirectbr ptr {this.Ref(ib.Address)}, [ {string.Join(", ", ib.Targets.Select(t => "label %" + t.Label))} ]",
      IrUnreachable => "unreachable",
      _ => throw EmitDeclinedException.For("LLVM emission", inst),
    };
  }

  private string PhiInputs(IrPhi phi) =>
    string.Join(", ", phi.IncomingBlocks.Select((blk, i) => $"[ {this.Ref(phi.GetOperand(i))}, %{blk.Label} ]"));

  private string Args(IrCall call) =>
    string.Join(", ", call.Args.Select(a => $"{Ty(a.Type)} {this.Ref(a)}"));

  /// <summary>
  /// What a call names between <c>call</c> and the callee: ordinarily the RESULT type, but for a
  /// variadic callee the whole function type - which is what tells LLVM where the declared
  /// parameters stop and the variadic ones begin.
  /// </summary>
  private static string CalleeType(IrCall call) {
    if (call.Callee is not IrFunction { IsVarArgs: true } callee)
      return Ty(call.Type);
    var declared = string.Join(", ", callee.Parameters.Select(p => Ty(p.Type)));
    return $"{Ty(callee.ReturnType)} ({(declared.Length > 0 ? declared + ", ..." : "...")})";
  }

  private string EmitSwitch(IrSwitch sw) {
    var cases = string.Join(" ", sw.Cases.Select(c => $"{Ty(sw.Condition.Type)} {c.Value}, label %{c.Target.Label}"));
    return $"switch {Ty(sw.Condition.Type)} {this.Ref(sw.Condition)}, label %{sw.DefaultTarget.Label} [ {cases} ]";
  }

  private string Ref(IrValue value) => value switch {
    IrConstantInt ci => ci.Value.ToString(CultureInfo.InvariantCulture),
    IrConstantFloat cf => cf.Type.Bits == 80 ? FormatFp80(cf.Value) : FormatFloat(cf.Value),
    IrNullPtr => "null",
    IrUndef => "undef",
    // an ON ERROR handler's address; the enclosing function is never optimized, so LLVM's
    // requirement that a blockaddress-taken block stay intact is met by construction
    IrBlockAddress ba => $"blockaddress(@{ba.Block.Parent?.Name ?? throw new Backend.BackendInvariantException(
      "LLVM back end", "LlvmEmitter.Ref",
      "a block whose address is taken belongs to a function (IrFunction.AddBlock sets Parent)")}, %{ba.Block.Label})",
    IrGlobalValue gv => "@" + gv.Name,
    // Not "%undef": AssignNames names every parameter and every non-void instruction of the function
    // before a line of it is emitted, so a value with no name here is one from ANOTHER function, or a
    // void instruction read as an operand - both IR the verifier rejects. Rendering it as undef
    // produced a module that assembles and computes something else, which is the one outcome worse
    // than a stack trace.
    _ => this._names.TryGetValue(value, out var n) ? n
      : throw new Backend.BackendInvariantException("LLVM back end", "LlvmEmitter.Ref",
          $"every operand is a constant, a global, or a value named by AssignNames ({value.GetType().Name} is none)"),
  };

  /// <summary>
  /// LLVM type spelling. LLVM integers are signless, so an unsigned IR type renders as the same
  /// <c>iN</c> - the signedness travels on the instruction (<c>udiv</c>, <c>ult</c>, <c>zext</c>),
  /// which is exactly how it reaches this emitter. Microsoft Binary Format has no LLVM spelling at
  /// all and must be converted to IEEE before it gets here (<see cref="IrCastOp.MbfToFP"/>).
  /// </summary>
  /// <summary>The type suffix an LLVM intrinsic name carries (<c>llvm.rint.f64</c>).</summary>
  private static string Suffix(IrType t) => "f" + t.Bits;

  private static string Ty(IrType t) => t.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Int => "i" + t.Bits,
    IrTypeKind.Float when t.IsMbf => throw new EmitDeclinedException(
      $"LLVM emission: a value in Microsoft Binary Format ({t}), which has no LLVM type "
      + "- it has to be converted to IEEE with MbfToFP before emission"),
    IrTypeKind.Float => t.Bits switch { 32 => "float", 64 => "double", 80 => "x86_fp80", _ => "fp" + t.Bits },
    IrTypeKind.Ptr => "ptr",
    _ => "void",
  };

  /// <summary>Escapes bytes for an LLVM c"..." string constant (\XX hex for non-printables).</summary>
  private static string EscapeBytes(byte[] bytes) {
    var sb = new StringBuilder(bytes.Length);
    foreach (var b in bytes)
      if (b is >= 0x20 and < 0x7F && b != (byte)'"' && b != (byte)'\\')
        sb.Append((char)b);
      else
        sb.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
    return sb.ToString();
  }

  /// <summary>Renders an x86_fp80 constant in LLVM's 0xK form (16-bit sign+exponent, 64-bit significand).</summary>
  private static string FormatFp80(double value) {
    if (value == 0.0)
      return "0xK00000000000000000000";
    var bits = BitConverter.DoubleToInt64Bits(value);
    var sign = (int)((bits >> 63) & 1);
    var exp = (int)((bits >> 52) & 0x7FF);
    var mant = bits & 0xFFFFFFFFFFFFF;
    if (exp is 0 or 0x7FF)
      return "0xK00000000000000000000";          // denormal/inf/nan: rare in folded constants, emit zero
    var ext = (ushort)((sign << 15) | ((exp - 1023 + 16383) & 0x7FFF));
    var sig = (1UL << 63) | ((ulong)mant << 11);  // explicit integer bit + left-aligned mantissa
    return $"0xK{ext:X4}{sig:X16}";
  }

  /// <summary>LLVM accepts hex float literals; emit doubles as 0x-bit-pattern to round-trip exactly.</summary>
  private static string FormatFloat(double value) =>
    "0x" + BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

  private static bool IsFloatPred(IrCmpPred p) => p is >= IrCmpPred.Foeq;

  private static string Mnemonic(IrBinaryOp op) => op switch {
    IrBinaryOp.Add => "add", IrBinaryOp.Sub => "sub", IrBinaryOp.Mul => "mul",
    IrBinaryOp.SDiv => "sdiv", IrBinaryOp.UDiv => "udiv", IrBinaryOp.SRem => "srem", IrBinaryOp.URem => "urem",
    IrBinaryOp.And => "and", IrBinaryOp.Or => "or", IrBinaryOp.Xor => "xor",
    IrBinaryOp.Shl => "shl", IrBinaryOp.LShr => "lshr", IrBinaryOp.AShr => "ashr",
    IrBinaryOp.FAdd => "fadd", IrBinaryOp.FSub => "fsub", IrBinaryOp.FMul => "fmul", IrBinaryOp.FDiv => "fdiv",
    _ => op.ToString().ToLowerInvariant(),
  };

  private static string Mnemonic(IrCmpPred p) => p switch {
    IrCmpPred.Eq => "eq", IrCmpPred.Ne => "ne",
    IrCmpPred.Slt => "slt", IrCmpPred.Sle => "sle", IrCmpPred.Sgt => "sgt", IrCmpPred.Sge => "sge",
    IrCmpPred.Ult => "ult", IrCmpPred.Ule => "ule", IrCmpPred.Ugt => "ugt", IrCmpPred.Uge => "uge",
    IrCmpPred.Foeq => "oeq", IrCmpPred.Fone => "one", IrCmpPred.Folt => "olt",
    IrCmpPred.Fole => "ole", IrCmpPred.Fogt => "ogt", IrCmpPred.Foge => "oge",
    _ => p.ToString().ToLowerInvariant(),
  };

  private static string Mnemonic(IrCastOp op) => op switch {
    IrCastOp.Trunc => "trunc", IrCastOp.ZExt => "zext", IrCastOp.SExt => "sext",
    IrCastOp.FPToSI => "fptosi", IrCastOp.FPToUI => "fptoui", IrCastOp.SIToFP => "sitofp", IrCastOp.UIToFP => "uitofp",
    IrCastOp.FPTrunc => "fptrunc", IrCastOp.FPExt => "fpext",
    IrCastOp.IntToPtr => "inttoptr", IrCastOp.PtrToInt => "ptrtoint", IrCastOp.BitCast => "bitcast",
    _ => op.ToString().ToLowerInvariant(),
  };
}
