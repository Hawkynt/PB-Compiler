using System.Globalization;
using System.Text;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Renders the IR to an LLVM-like textual form. The output is deterministic
/// (unnamed values get stable numeric slots) so it can be snapshot-tested and read
/// by humans while debugging the lowering and the middle-end.
/// </summary>
public sealed class IrPrinter {

  private readonly Dictionary<IrValue, string> _names = new(ReferenceEqualityComparer.Instance);
  private int _slot;

  /// <summary>Renders a whole module.</summary>
  public static string Print(IrModule module) {
    var sb = new StringBuilder();
    foreach (var g in module.Globals)
      sb.Append('@').Append(g.Name)
        .Append(g.Bytes is { } bytes ? $" = constant [{bytes.Length} x i8]" : $" = global {g.ValueType}{(g.IsZeroInitialized ? " zeroinitializer" : "")}")
        .Append('\n');
    if (module.Globals.Count > 0)
      sb.Append('\n');
    for (var i = 0; i < module.Functions.Count; ++i) {
      if (i > 0)
        sb.Append('\n');
      sb.Append(new IrPrinter().PrintFunction(module.Functions[i]));
    }
    return sb.ToString();
  }

  /// <summary>Renders a single function (with a fresh naming context).</summary>
  public static string Print(IrFunction function) => new IrPrinter().PrintFunction(function);

  private string PrintFunction(IrFunction fn) {
    this.AssignNames(fn);
    var sb = new StringBuilder();
    var keyword = fn.IsDeclaration ? "declare" : "define";
    sb.Append(keyword).Append(' ').Append(fn.ReturnType).Append(" @").Append(fn.Name).Append('(');
    for (var i = 0; i < fn.Parameters.Count; ++i) {
      if (i > 0)
        sb.Append(", ");
      sb.Append(fn.Parameters[i].Type).Append(' ').Append(this.Ref(fn.Parameters[i]));
    }
    sb.Append(')');
    if (fn.IsDeclaration)
      return sb.Append('\n').ToString();

    sb.Append(" {\n");
    foreach (var block in fn.Blocks) {
      sb.Append(block.Label).Append(":\n");
      foreach (var inst in block.Instructions)
        sb.Append("  ").Append(this.PrintInstruction(inst)).Append('\n');
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

  private string PrintInstruction(IrInstruction inst) {
    var lhs = inst.Type.IsVoid ? "" : this.Ref(inst) + " = ";
    return lhs + inst switch {
      IrBinary b => $"{Mnemonic(b.Op)} {b.Type} {this.Ref(b.Lhs)}, {this.Ref(b.Rhs)}",
      IrCmp c => $"{(IsFloatPred(c.Pred) ? "fcmp" : "icmp")} {Mnemonic(c.Pred)} {c.Lhs.Type} {this.Ref(c.Lhs)}, {this.Ref(c.Rhs)}",
      IrCast c => $"{Mnemonic(c.Op)} {c.Value.Type} {this.Ref(c.Value)} to {c.Type}",
      IrAlloca a => a.Count > 1 ? $"alloca {a.Allocated}, i32 {a.Count}" : $"alloca {a.Allocated}",
      IrLoad l => $"load {l.Type}, ptr {this.Ref(l.Pointer)}",
      IrStore s => $"store {s.Value.Type} {this.Ref(s.Value)}, ptr {this.Ref(s.Pointer)}",
      IrInlineAsm a => $"asm \"{a.Text.Trim()}\"",
      IrGep g => $"gep {g.ElementType?.ToString() ?? "i8"} {this.Ref(g.BasePtr)}, {g.ByteOffset.Type} {this.Ref(g.ByteOffset)}",
      IrFarPtr f => $"farptr {f.Segment.Type} {this.Ref(f.Segment)}:{f.Offset.Type} {this.Ref(f.Offset)}",
      IrPhi p => $"phi {p.Type} {this.PrintPhiInputs(p)}",
      IrSelect sel => $"select i1 {this.Ref(sel.Condition)}, {sel.Type} {this.Ref(sel.IfTrue)}, {sel.Type} {this.Ref(sel.IfFalse)}",
      IrCall call => $"call {call.Type} {this.Ref(call.Callee)}({this.PrintArgs(call)})",
      IrRet r => r.HasValue ? $"ret {r.Value!.Type} {this.Ref(r.Value)}" : "ret void",
      IrBr br => $"br label %{br.Target.Label}",
      IrCondBr cb => $"br i1 {this.Ref(cb.Condition)}, label %{cb.IfTrue.Label}, label %{cb.IfFalse.Label}",
      IrSwitch sw => this.PrintSwitch(sw),
      IrUnreachable => "unreachable",
      _ => inst.GetType().Name.ToLowerInvariant(),
    };
  }

  private string PrintPhiInputs(IrPhi phi) =>
    string.Join(", ", phi.IncomingBlocks.Select((blk, i) => $"[ {this.Ref(phi.GetOperand(i))}, %{blk.Label} ]"));

  private string PrintArgs(IrCall call) =>
    string.Join(", ", call.Args.Select(a => $"{a.Type} {this.Ref(a)}"));

  private string PrintSwitch(IrSwitch sw) {
    var cases = string.Join(" ", sw.Cases.Select(c => $"{sw.Condition.Type} {c.Value}, label %{c.Target.Label}"));
    return $"switch {sw.Condition.Type} {this.Ref(sw.Condition)}, label %{sw.DefaultTarget.Label} [ {cases} ]";
  }

  /// <summary>Renders a value as it appears in an operand position.</summary>
  private string Ref(IrValue value) => value switch {
    IrConstantInt ci => ci.Value.ToString(CultureInfo.InvariantCulture),
    IrConstantFloat cf => FormatFloat(cf.Value),
    IrNullPtr => "null",
    IrUndef => "undef",
    IrBlockAddress ba => $"blockaddress(%{ba.Block.Label})",
    IrGlobalValue gv => "@" + gv.Name,
    _ => this._names.TryGetValue(value, out var n) ? n : "%<?>",
  };

  private static string FormatFloat(double value) {
    var s = value.ToString("R", CultureInfo.InvariantCulture);
    return s.Contains('.') || s.Contains('E') || s.Contains("Inf") || s.Contains("NaN") ? s : s + ".0";
  }

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
    IrCastOp.FPToSIRound => "fptosi.round",
    IrCastOp.FPTrunc => "fptrunc", IrCastOp.FPExt => "fpext",
    IrCastOp.IntToPtr => "inttoptr", IrCastOp.PtrToInt => "ptrtoint", IrCastOp.BitCast => "bitcast",
    _ => op.ToString().ToLowerInvariant(),
  };
}
