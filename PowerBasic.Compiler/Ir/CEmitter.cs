using System.Globalization;
using System.Text;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Emits portable C99 from the optimized IR - the second consumer of the middle end, alongside
/// <see cref="LlvmEmitter"/>, and the proof that the optimizer is not welded to one target: the
/// front end, the lowering and all eleven IR passes run unchanged, and only this file differs.
///
/// The translation is mechanical because the IR is already SSA over an explicit CFG:
/// <list type="bullet">
///   <item>every instruction with a value becomes one <c>static</c>-scoped C local, declared at
///     the top of the function and assigned exactly once - SSA is single-assignment C;</item>
///   <item>every basic block becomes a C label, every terminator a <c>goto</c> (or
///     <c>switch</c> / <c>return</c>);</item>
///   <item>a phi becomes an assignment to the phi's own local in each predecessor, staged through
///     a temporary so that a block whose phis feed each other (a swap) still copies in parallel;</item>
///   <item>the <c>rt_*</c> runtime ABI stays a set of extern declarations, satisfied by
///     <c>runtime/pbc_rt.c</c> - the same contract the LLVM path relies on.</item>
/// </list>
///
/// Integer arithmetic is emitted through the unsigned type of the same width and cast back, so
/// the wrap-around PB guarantees is defined behaviour in C rather than signed overflow (which is
/// undefined and would licence a C compiler to do anything). Division keeps C's truncate-toward-
/// zero semantics, which is exactly PB's <c>\</c> and <c>MOD</c>.
/// </summary>
public sealed class CEmitter {

  private readonly Dictionary<IrValue, string> _names = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, string> _labels = new(ReferenceEqualityComparer.Instance);
  private int _slot;
  private int _temp;

  /// <summary>The block emitted immediately after the current one - a terminator that jumps here falls through instead.</summary>
  private IrBasicBlock? _nextBlock;

  /// <summary>
  /// Comparisons whose only use is the branch immediately after them: rendered inline as the
  /// <c>if (...)</c> condition rather than a named <c>i1</c> temp, so a conditional reads
  /// <c>if (v0 &lt;= 10)</c>, not <c>v2 = (v0 &lt;= 10); if (v2)</c>. Requires adjacency, so the
  /// operands cannot be reassigned between the compare and the test.
  /// </summary>
  private readonly HashSet<IrCmp> _inlinedCmps = new(ReferenceEqualityComparer.Instance);

  /// <summary>The C name of PB's top-level code; <c>main</c> itself lives in the runtime shim.</summary>
  public const string MainName = "pb_main";

  /// <summary>Renders <paramref name="module"/> as a self-contained C99 translation unit.</summary>
  public static string Emit(IrModule module) {
    var sb = new StringBuilder();
    sb.Append("/* Generated from ").Append(module.Name).Append(" by pbc --emit-c. */\n");
    sb.Append("#include <stdint.h>\n#include <string.h>\n#include <math.h>\n#include \"pbc_rt.h\"\n\n");

    foreach (var g in module.Globals)
      if (g.Bytes is { } bytes)
        sb.Append("static const unsigned char ").Append(Sanitize(g.Name)).Append('[').Append(bytes.Length)
          .Append("] = {").Append(string.Join(",", bytes)).Append("};\n");
      else
        sb.Append("static ").Append(Ty(g.ValueType)).Append(' ').Append(Sanitize(g.Name))
          .Append(g.Count > 1 ? $"[{g.Count}] = {{0}}" : " = 0").Append(";\n");
    if (module.Globals.Count > 0)
      sb.Append('\n');

    foreach (var f in module.Functions)
      if (!IsCLibrary(f))
        sb.Append(Signature(f)).Append(";\n");
    sb.Append('\n');

    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        sb.Append(new CEmitter().EmitFunction(f)).Append('\n');
    return sb.ToString();
  }

  #region declarations

  private static string Signature(IrFunction f) {
    var sb = new StringBuilder();
    sb.Append(f.IsDeclaration ? "extern " : "").Append(Ty(f.ReturnType)).Append(' ').Append(FuncName(f)).Append('(');
    if (f.Parameters.Count == 0)
      sb.Append("void");
    else
      for (var i = 0; i < f.Parameters.Count; ++i)
        sb.Append(i > 0 ? ", " : "").Append(Ty(f.Parameters[i].Type)).Append(" p").Append(i);
    return sb.Append(')').ToString();
  }

  /// <summary>PB's <c>main</c> is the program body, not C's entry point - the shim calls it.</summary>
  private static string FuncName(IrFunction f) =>
    f.Name == "main" ? MainName : CLibraryName(f.Name) ?? Sanitize(f.Name);

  /// <summary>
  /// The C standard-library spelling of an LLVM intrinsic, or null when it is not one. The IR
  /// lowers PB's math to <c>llvm.sqrt.f64</c> and friends because that is what an LLVM back end
  /// optimizes natively; C has the same functions under their &lt;math.h&gt; names, with the
  /// <c>f</c>/<c>l</c> suffix carrying the width. <c>llvm.memcpy</c> and <c>llvm.memset</c> map to
  /// <c>memcpy</c> and <c>memset</c>, whose trailing is-volatile argument the call site drops.
  /// </summary>
  private static string? CLibraryName(string name) {
    if (!name.StartsWith("llvm.", StringComparison.Ordinal))
      return null;
    if (name.StartsWith("llvm.memcpy", StringComparison.Ordinal))
      return "memcpy";
    if (name.StartsWith("llvm.memset", StringComparison.Ordinal))
      return "memset";
    var parts = name.Split('.');
    if (parts.Length < 3)
      return null;
    var suffix = parts[^1] switch { "f32" => "f", "f80" => "l", _ => "" };
    return parts[1] + suffix;
  }

  /// <summary>True for a function the C library already provides, so no prototype is emitted for it.</summary>
  private static bool IsCLibrary(IrFunction f) => CLibraryName(f.Name) is not null;

  /// <summary>
  /// Maps an IR type to its C spelling; a pointer is opaque, as in the IR. Integers declare in their
  /// signed C form and each operation casts to <see cref="UTy"/> where it needs wrap-around or an
  /// unsigned reading, so an unsigned IR type needs no separate declaration spelling. Microsoft
  /// Binary Format is a DOS storage encoding with no C equivalent - it must be converted to IEEE
  /// before emission (<see cref="IrCastOp.MbfToFP"/>).
  /// </summary>
  private static string Ty(IrType t) => t.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Ptr => "void *",
    IrTypeKind.Float when t.IsMbf => throw new NotSupportedException(
      $"Microsoft Binary Format ({t}) has no C type - convert to IEEE with MbfToFP before emission"),
    IrTypeKind.Float => t.Bits switch { 32 => "float", 64 => "double", _ => "long double" },
    _ => t.Bits switch { 1 => "int8_t", 8 => "int8_t", 16 => "int16_t", 32 => "int32_t", _ => "int64_t" },
  };

  /// <summary>The unsigned C type of the same width - arithmetic runs here so wrap-around is defined.</summary>
  private static string UTy(IrType t) => t.Bits switch { 1 or 8 => "uint8_t", 16 => "uint16_t", 32 => "uint32_t", _ => "uint64_t" };

  private static string Sanitize(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
    return char.IsAsciiDigit(sb[0]) ? "_" + sb : sb.ToString();
  }

  #endregion

  #region function body

  private string EmitFunction(IrFunction f) {
    for (var i = 0; i < f.Parameters.Count; ++i)
      this._names[f.Parameters[i]] = "p" + i;
    // block labels are numbered, not named: IR block names are optional and repeat freely, and a
    // C label must be unique inside its function
    for (var i = 0; i < f.Blocks.Count; ++i)
      this._labels[f.Blocks[i]] = f.Blocks[i].Name is { Length: > 0 } n ? $"L{i}_{Sanitize(n)}" : $"L{i}";

    // a block needs its C label only if some terminator actually emits a `goto` to it - an edge
    // that becomes fall-through emits nothing, so its target is not a label site. Mirror the exact
    // fall-through rule the terminator emission uses (a jump to the next block falls through), so
    // no dead label survives; the result reads the way hand-written code does.
    var targets = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < f.Blocks.Count; ++i) {
      var next = i + 1 < f.Blocks.Count ? f.Blocks[i + 1] : null;
      void Goto(IrBasicBlock t) { if (!ReferenceEquals(t, next)) targets.Add(t); }
      switch (f.Blocks[i].Terminator) {
        case IrBr br: Goto(br.Target); break;
        case IrCondBr cb when ReferenceEquals(cb.IfFalse, next): Goto(cb.IfTrue); break;
        case IrCondBr cb when ReferenceEquals(cb.IfTrue, next): Goto(cb.IfFalse); break;
        case IrCondBr cb: targets.Add(cb.IfTrue); targets.Add(cb.IfFalse); break;
        case IrSwitch sw:
          targets.Add(sw.DefaultTarget);
          foreach (var (_, t) in sw.Cases) targets.Add(t);
          break;
      }
    }

    // a compare used only by the branch right after it folds into the branch's condition
    this._inlinedCmps.Clear();
    foreach (var block in f.Blocks) {
      var ins = block.Instructions;
      if (ins.Count >= 2 && ins[^1] is IrCondBr cb && cb.Condition is IrCmp c
          && ReferenceEquals(ins[^2], c) && c.Users.Count == 1)
        this._inlinedCmps.Add(c);
    }

    var body = new StringBuilder();
    for (var i = 0; i < f.Blocks.Count; ++i) {
      var block = f.Blocks[i];
      this._nextBlock = i + 1 < f.Blocks.Count ? f.Blocks[i + 1] : null;
      if (targets.Contains(block))
        body.Append(this.Label(block)).Append(":;\n");
      foreach (var inst in block.Instructions)
        if (inst is not IrPhi && !(inst is IrCmp ic && this._inlinedCmps.Contains(ic)))
          this.EmitInstruction(body, inst, block);   // an inlined compare emits no statement of its own
    }
    this._nextBlock = null;

    // declarations first: C99 allows mixed declarations, but one block up front keeps the output
    // readable and works with the strictest -std=c89 -pedantic consumers too
    var decls = new StringBuilder();
    foreach (var block in f.Blocks)
      foreach (var inst in block.Instructions) {
        if (inst is IrAlloca a)   // the frame storage an alloca hands out the address of
          decls.Append("  ").Append(Ty(a.Allocated)).Append(" alloca_").Append(this.Name(a))
            .Append('[').Append(Math.Max(a.Count, 1)).Append("];\n");
        if (inst.Type.Kind != IrTypeKind.Void && !(inst is IrCmp ic && this._inlinedCmps.Contains(ic)))
          decls.Append("  ").Append(Ty(inst.Type)).Append(' ').Append(this.Name(inst)).Append(inst is IrPhi ? " = 0" : "").Append(";\n");
      }
    for (var i = 0; i < this._temp; ++i)
      decls.Append("  int64_t t").Append(i).Append(";\n");

    return Signature(f) + " {\n" + decls + body + "}\n";
  }

  private string Label(IrBasicBlock b) => this._labels[b];

  private void EmitInstruction(StringBuilder sb, IrInstruction inst, IrBasicBlock block) {
    var lhs = inst.Type.Kind == IrTypeKind.Void ? "" : this.Name(inst) + " = ";
    switch (inst) {
      case IrBinary b:
        sb.Append("  ").Append(lhs).Append(this.Binary(b)).Append(";\n");
        break;

      case IrCmp c:
        sb.Append("  ").Append(lhs).Append(this.Compare(c)).Append(";\n");
        break;

      case IrCast c:
        sb.Append("  ").Append(lhs).Append(this.Cast(c)).Append(";\n");
        break;

      case IrAlloca a:
        // the frame storage is declared with the other locals; the value is its address
        sb.Append("  ").Append(lhs).Append("(void *)alloca_").Append(this.Name(a)).Append(";\n");
        break;

      case IrLoad l:
        sb.Append("  ").Append(lhs).Append("(*(").Append(Ty(l.Type)).Append(" *)").Append(this.Ref(l.Pointer)).Append(");\n");
        break;

      case IrStore s:
        sb.Append("  *(").Append(Ty(s.Value.Type)).Append(" *)").Append(this.Ref(s.Pointer))
          .Append(" = ").Append(this.Ref(s.Value)).Append(";\n");
        break;

      case IrGep g: {
        var scale = g.ElementType is { } e ? $" * (int64_t)sizeof({Ty(e)})" : "";
        sb.Append("  ").Append(lhs).Append("(void *)((unsigned char *)").Append(this.Ref(g.BasePtr))
          .Append(" + (int64_t)").Append(this.Ref(g.ByteOffset)).Append(scale).Append(");\n");
        break;
      }

      case IrSelect s:
        sb.Append("  ").Append(lhs).Append(this.Ref(s.Condition)).Append(" ? ").Append(this.Ref(s.IfTrue))
          .Append(" : ").Append(this.Ref(s.IfFalse)).Append(";\n");
        break;

      case IrCall c: {
        var callee = c.Callee is IrFunction target ? FuncName(target) : "(*(void (*)())" + this.Ref(c.Callee) + ")";
        var args = c.Args.Select(this.Ref).ToList();
        if (callee is "memcpy" or "memset" && args.Count == 4)
          args.RemoveAt(3);                          // LLVM's trailing is-volatile flag
        sb.Append("  ").Append(lhs).Append(callee).Append('(').Append(string.Join(", ", args)).Append(");\n");
        break;
      }

      case IrRet r:
        this.EmitPhiCopies(sb, block, null);
        sb.Append("  return").Append(r.Value is { } v ? " " + this.Ref(v) : "").Append(";\n");
        break;

      case IrBr b:
        this.EmitPhiCopies(sb, block, b.Target);
        if (!ReferenceEquals(b.Target, this._nextBlock))   // a jump to the very next block is fall-through
          sb.Append("  goto ").Append(this.Label(b.Target)).Append(";\n");
        break;

      case IrCondBr b when ReferenceEquals(b.IfFalse, this._nextBlock):
        // the false arm falls through: `if (c) { ...; goto true; }` then the false path continues
        sb.Append("  if (").Append(this.Cond(b.Condition)).Append(") {\n");
        this.EmitPhiCopies(sb, block, b.IfTrue, "  ");
        sb.Append("    goto ").Append(this.Label(b.IfTrue)).Append(";\n  }\n");
        this.EmitPhiCopies(sb, block, b.IfFalse);
        break;

      case IrCondBr b when ReferenceEquals(b.IfTrue, this._nextBlock):
        // the true arm falls through: test the negation and jump only on the false arm
        sb.Append("  if (!(").Append(this.Cond(b.Condition)).Append(")) {\n");
        this.EmitPhiCopies(sb, block, b.IfFalse, "  ");
        sb.Append("    goto ").Append(this.Label(b.IfFalse)).Append(";\n  }\n");
        this.EmitPhiCopies(sb, block, b.IfTrue);
        break;

      case IrCondBr b:
        sb.Append("  if (").Append(this.Cond(b.Condition)).Append(") {\n");
        this.EmitPhiCopies(sb, block, b.IfTrue, "  ");
        sb.Append("    goto ").Append(this.Label(b.IfTrue)).Append(";\n  } else {\n");
        this.EmitPhiCopies(sb, block, b.IfFalse, "  ");
        sb.Append("    goto ").Append(this.Label(b.IfFalse)).Append(";\n  }\n");
        break;

      case IrSwitch s:
        sb.Append("  switch (").Append(this.Ref(s.Condition)).Append(") {\n");
        foreach (var (value, target) in s.Cases) {
          sb.Append("  case ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(":\n");
          this.EmitPhiCopies(sb, block, target, "  ");
          sb.Append("    goto ").Append(this.Label(target)).Append(";\n");
        }
        sb.Append("  default:\n");
        this.EmitPhiCopies(sb, block, s.DefaultTarget, "  ");
        sb.Append("    goto ").Append(this.Label(s.DefaultTarget)).Append(";\n  }\n");
        break;

      case IrUnreachable:
        sb.Append("  rt_unreachable();\n");
        break;

      default:
        throw new NotSupportedException($"C emission: {inst.GetType().Name}");
    }
  }

  /// <summary>
  /// Writes the phi assignments for the edge <paramref name="from"/> -&gt; <paramref name="to"/>.
  /// All incoming values are read into temporaries first and only then written to the phi locals,
  /// so mutually referencing phis (the classic loop-carried swap) still behave like the parallel
  /// copy an SSA phi node is.
  /// </summary>
  private void EmitPhiCopies(StringBuilder sb, IrBasicBlock from, IrBasicBlock? to, string indent = "") {
    if (to is null)
      return;
    var phis = to.Instructions.OfType<IrPhi>().Where(p => p.IncomingFrom(from) is not null).ToList();
    if (phis.Count == 0)
      return;
    // A phi's copy is a parallel assignment. When the copies do not form a cycle - the ordinary
    // loop-carried value, whose incoming operand is a freshly computed value, not another phi in
    // this same set - they sequentialize into plain direct copies: `counter = counter_next;`. Only
    // a genuine swap (phi A <- phi B and phi B <- phi A) needs the staged temps, and only that case
    // pays for them. Direct copies are what a person writes and what the C compiler wants to see.
    var copies = phis.Select(p => (Phi: p, Src: p.IncomingFrom(from)!)).ToList();
    var order = new List<(IrPhi Phi, IrValue Src)>();
    var pending = copies.ToList();
    while (pending.Count > 0) {
      // a copy may be emitted (its destination overwritten) only once nothing still pending reads it
      var ready = pending.Where(c => !pending.Any(o => !ReferenceEquals(o.Phi, c.Phi) && ReferenceEquals(o.Src, c.Phi))).ToList();
      if (ready.Count == 0) {
        this.EmitStagedPhiCopies(sb, copies, indent);   // a cycle - fall back to the temp-staged parallel copy
        return;
      }
      foreach (var c in ready) {
        order.Add(c);
        pending.Remove(c);
      }
    }
    foreach (var (phi, src) in order) {
      var value = this.Ref(src);
      var cast = phi.Type.Equals(src.Type) ? value : "(" + Ty(phi.Type) + ")" + value;
      sb.Append(indent).Append("  ").Append(this.Name(phi)).Append(" = ").Append(cast).Append(";\n");
    }
  }

  /// <summary>The safe parallel copy for a phi cycle (a loop-carried swap): read every incoming value into a temp, then write the phi locals - so mutually-referencing phis still copy simultaneously. The temps are pre-declared (int64 holds any integer width the cycle carries).</summary>
  private void EmitStagedPhiCopies(StringBuilder sb, List<(IrPhi Phi, IrValue Src)> copies, string indent) {
    var staged = new List<(string Temp, IrPhi Phi)>();
    foreach (var (phi, src) in copies) {
      var temp = "t" + this._temp++;
      staged.Add((temp, phi));
      sb.Append(indent).Append("  ").Append(temp).Append(" = (int64_t)").Append(this.Ref(src)).Append(";\n");
    }
    foreach (var (temp, phi) in staged)
      sb.Append(indent).Append("  ").Append(this.Name(phi)).Append(" = (").Append(Ty(phi.Type)).Append(')').Append(temp).Append(";\n");
  }

  #endregion

  #region operations

  private string Binary(IrBinary b) {
    var (l, r) = (this.Ref(b.Lhs), this.Ref(b.Rhs));
    if (b.IsFloatOp) {
      var op = b.Op switch { IrBinaryOp.FAdd => "+", IrBinaryOp.FSub => "-", IrBinaryOp.FMul => "*", _ => "/" };
      return $"{l} {op} {r}";
    }
    var t = b.Type;
    // wrap-around arithmetic goes through the unsigned type: signed overflow is undefined in C,
    // and PB defines it (the value wraps), so the unsigned round-trip is the only faithful form
    string Wrap(string op) => $"({Ty(t)})(({UTy(t)}){l} {op} ({UTy(t)}){r})";
    return b.Op switch {
      IrBinaryOp.Add => Wrap("+"),
      IrBinaryOp.Sub => Wrap("-"),
      IrBinaryOp.Mul => Wrap("*"),
      IrBinaryOp.And => Wrap("&"),
      IrBinaryOp.Or => Wrap("|"),
      IrBinaryOp.Xor => Wrap("^"),
      IrBinaryOp.Shl => Wrap("<<"),
      IrBinaryOp.SDiv => $"({Ty(t)})({l} / {r})",              // C truncates toward zero = PB's \
      IrBinaryOp.SRem => $"({Ty(t)})({l} % {r})",              // remainder takes the dividend's sign = PB's MOD
      IrBinaryOp.UDiv => $"({Ty(t)})(({UTy(t)}){l} / ({UTy(t)}){r})",
      IrBinaryOp.URem => $"({Ty(t)})(({UTy(t)}){l} % ({UTy(t)}){r})",
      IrBinaryOp.LShr => $"({Ty(t)})(({UTy(t)}){l} >> {r})",
      _ => $"({Ty(t)})({l} >> {r})",                            // AShr: arithmetic on a signed type
    };
  }

  /// <summary>A branch condition: the inlined compare expression where one was folded in, else the value's name.</summary>
  private string Cond(IrValue condition) =>
    condition is IrCmp c && this._inlinedCmps.Contains(c) ? this.Compare(c) : this.Ref(condition);

  private string Compare(IrCmp c) {
    var (l, r) = (this.Ref(c.Lhs), this.Ref(c.Rhs));
    var t = c.Lhs.Type;
    string Unsigned(string op) => $"(({UTy(t)}){l} {op} ({UTy(t)}){r})";
    return c.Pred switch {
      IrCmpPred.Eq or IrCmpPred.Foeq => $"({l} == {r})",
      IrCmpPred.Ne or IrCmpPred.Fone => $"({l} != {r})",
      IrCmpPred.Slt or IrCmpPred.Folt => $"({l} < {r})",
      IrCmpPred.Sle or IrCmpPred.Fole => $"({l} <= {r})",
      IrCmpPred.Sgt or IrCmpPred.Fogt => $"({l} > {r})",
      IrCmpPred.Sge or IrCmpPred.Foge => $"({l} >= {r})",
      IrCmpPred.Ult => Unsigned("<"),
      IrCmpPred.Ule => Unsigned("<="),
      IrCmpPred.Ugt => Unsigned(">"),
      _ => Unsigned(">="),
    };
  }

  private string Cast(IrCast c) {
    var v = this.Ref(c.Value);
    var from = c.Value.Type;
    return c.Op switch {
      // truncation and sign extension are plain casts once the source is read at its own width
      IrCastOp.Trunc or IrCastOp.SExt => $"({Ty(c.Type)}){v}",
      IrCastOp.ZExt => $"({Ty(c.Type)})({UTy(from)}){v}",
      IrCastOp.SIToFP or IrCastOp.FPTrunc or IrCastOp.FPExt or IrCastOp.FPToSI => $"({Ty(c.Type)}){v}",
      // a C cast truncates, so the rounding conversion has to say so: llrint rounds to nearest with
      // ties to even under the default rounding mode, which is the one BASIC assignment uses
      IrCastOp.FPToSIRound => $"({Ty(c.Type)})llrint({v})",
      IrCastOp.UIToFP => $"({Ty(c.Type)})({UTy(from)}){v}",
      IrCastOp.FPToUI => $"({Ty(c.Type)})({UTy(c.Type)}){v}",
      IrCastOp.IntToPtr => $"(void *)(intptr_t){v}",
      IrCastOp.PtrToInt => $"({Ty(c.Type)})(intptr_t){v}",
      _ => $"({Ty(c.Type)}){v}",                                 // BitCast between same-width ints
    };
  }

  #endregion

  #region operands

  /// <summary>The C expression naming a value: a constant literal, a symbol, or an SSA local.</summary>
  private string Ref(IrValue v) => v switch {
    IrConstantInt i when i.Type.Bits >= 64 => i.Value.ToString(CultureInfo.InvariantCulture) + "LL",
    IrConstantInt i => "(" + Ty(i.Type) + ")" + i.Value.ToString(CultureInfo.InvariantCulture),
    IrConstantFloat f => FloatLiteral(f),
    IrNullPtr => "((void *)0)",
    IrUndef u => u.Type.Kind == IrTypeKind.Ptr ? "((void *)0)" : "(" + Ty(u.Type) + ")0",
    // The address of a basic block - what ON ERROR arms its handler with. Standard C has no such
    // value (GCC's '&&label' is an extension, and even with it the non-local jump ON ERROR performs
    // needs setjmp/longjmp rather than a computed goto), so this declines instead of naming the
    // block and emitting something that compiles but does not handle errors.
    IrBlockAddress => throw new NotSupportedException(
      "C emission: the address of a basic block (ON ERROR arms a handler with one). "
      + "A non-local jump from an arbitrary fault point needs setjmp/longjmp, which this emitter does not model yet."),
    // a global's IR value is its ADDRESS: a byte blob is an array (which decays), a scalar
    // global is a plain object whose address has to be taken
    // a global's IR value is its ADDRESS: an array (byte blob or element array) decays, a
    // single object needs its address taken
    IrGlobalVariable g => (g.Bytes is null && g.Count <= 1 ? "(void *)&" : "(void *)") + Sanitize(g.Name),
    IrFunction f => FuncName(f),
    _ => this.Name(v),
  };

  /// <summary>Exact round-tripping literal ("G17"), so no precision is lost through the text form.</summary>
  private static string FloatLiteral(IrConstantFloat f) {
    var text = f.Value.ToString("G17", CultureInfo.InvariantCulture);
    if (!text.Contains('.') && !text.Contains('E') && !text.Contains("Inf") && !text.Contains("NaN"))
      text += ".0";
    return f.Type.Bits == 32 ? text + "f" : text;
  }

  private string Name(IrValue v) {
    if (this._names.TryGetValue(v, out var name))
      return name;
    name = "v" + this._slot++;
    this._names[v] = name;
    return name;
  }

  #endregion
}
