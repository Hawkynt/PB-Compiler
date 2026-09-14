using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// pb36 CAPTURING closures: a lambda that reads the locals of the procedure it was written inside.
///
/// <para>
/// The direct emitter reaches them through the enclosing FRAME - the environment pointer is that
/// frame, and each capture is read at the displacement the enclosing procedure's layout gave it.
/// That is unavailable here for the plainest of reasons: the routed back end lays out its own frames,
/// and the lambda is a separate function selected and allocated on its own, so there is no moment at
/// which one knows the other's displacements.
/// </para>
/// <para>
/// So the captures move into a RECORD instead - one per enclosing procedure, laid out over every
/// local any of its lambdas captures, at offsets both sides compute from the same symbols. The
/// enclosing procedure's captured locals LIVE in that record, which is what keeps a non-escaping
/// closure sharing them by reference: a lambda writing through the environment and the enclosing
/// procedure writing through its own name are writing to the same bytes.
/// </para>
/// <para>
/// An ESCAPING closure copies the record to the heap at creation, and the copy is a by-value snapshot
/// exactly as the direct emitter's is - the environment then outlives the frame. Both kinds read at
/// the same offsets, which is the point of laying the record out once: only WHERE the environment is
/// differs, never what is in it.
/// </para>
/// </summary>
public sealed partial class IrLowering {

  /// <summary>Byte offsets within the capture record, by the enclosing procedure's local.</summary>
  private Dictionary<VariableSymbol, int>? _captureOffsets;

  /// <summary>The record itself, in the ENCLOSING procedure's frame.</summary>
  private IrValue? _captureRecord;

  /// <summary>The two cells a lifted lambda's environment pointer arrives in - offset, then segment.</summary>
  private (IrValue Offset, IrValue Segment)? _closureEnv;

  /// <summary>
  /// The capture record's layout for <paramref name="enclosing"/>: every local any of its lambdas
  /// captures, each rounded up to an even size.
  ///
  /// <para>
  /// It is laid out over the PROCEDURE rather than over one lambda, because a local two lambdas
  /// capture has to be in one place - laid out per lambda it would be in two, and a write through one
  /// environment would not be seen through the other. The order is the emission order of the lambdas
  /// and then their own capture order, both of which are lists the binder built once, so the two
  /// sides of the boundary compute the identical layout without either telling the other.
  /// </para>
  /// </summary>
  private static (Dictionary<VariableSymbol, int> Offsets, int Size) CaptureRecord(
      SemanticModel model, ProcedureSymbol? enclosing) {
    var offsets = new Dictionary<VariableSymbol, int>(ReferenceEqualityComparer.Instance);
    var size = 0;
    foreach (var lifted in model.ProcedureList) {
      if (!model.LambdaEnclosing.TryGetValue(lifted, out var owner) || !ReferenceEquals(owner, enclosing))
        continue;
      foreach (var captured in lifted.Captures) {
        if (offsets.ContainsKey(captured))
          continue;
        offsets[captured] = size;
        size += Math.Max(2, (captured.Type.Size + 1) & ~1);
      }
    }
    return (offsets, size);
  }

  /// <summary>
  /// Prepares whichever side of a closure this body is: the record, for a procedure whose lambdas
  /// capture its locals, and the environment cells, for a lambda that reads someone else's.
  /// </summary>
  private void SetUpClosures(ProcedureSymbol? proc) {
    if (proc?.ClosureEnvPtr is not null) {
      this.SetUpClosureEntry();
      return;
    }
    var (offsets, size) = CaptureRecord(this._model, proc);
    if (size == 0)
      return;
    this._captureOffsets = offsets;
    this._captureRecord = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I8) { Count = size, Name = (proc?.Name ?? "main") + ".env" });
  }

  /// <summary>
  /// The two cells a capturing lambda's environment pointer lands in. Nothing in the body writes
  /// them - the PROLOGUE does, out of the <c>BX:CX</c> the closure carried - which is what the role
  /// mark on each alloca says; see <see cref="IrAlloca.EnvRole"/>.
  /// </summary>
  private void SetUpClosureEntry() {
    var offset = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I16) { Name = "env.off", EnvRole = ClosureEnvRole.Offset });
    var segment = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I16) { Name = "env.seg", EnvRole = ClosureEnvRole.Segment });
    this._closureEnv = (offset, segment);
  }

  /// <summary>
  /// A CAPTURED name, as the far address it is: the environment pointer plus the capture's offset in
  /// the record. Far because the environment may be the enclosing frame (<c>SS</c>) or a heap block,
  /// and the lambda cannot tell which - nor does it need to, which is the whole point of the pair.
  /// </summary>
  private IrValue CapturedAddress(VariableSymbol captured) {
    if (this._closureEnv is not { } env)
      throw new IrLoweringException($"captured name {captured.Name} outside a capturing lambda");
    if (this._proc is not { } lambda || lambda.Captures.Count <= captured.Offset)
      throw new IrLoweringException($"captured name {captured.Name} has no environment slot");
    // The lambda's own Captured symbol carries the INDEX into Captures, and the record was laid out
    // over the enclosing procedure - so the offset is looked up by the symbol Captures names, not by
    // the local one standing in for it here.
    var (offsets, _) = CaptureRecord(this._model, this._model.LambdaEnclosing.GetValueOrDefault(lambda));
    if (!offsets.TryGetValue(lambda.Captures[captured.Offset], out var within))
      throw new IrLoweringException($"captured name {captured.Name} is not in its environment record");
    return this._b.FarPtr(this._b.Load(IrType.I16, env.Segment),
      this._b.Add(this._b.Load(IrType.I16, env.Offset), new IrConstantInt(IrType.I16, (short)within)));
  }

  /// <summary>
  /// Writes the environment half of a lambda's closure value: the record's address and the segment it
  /// is in.
  ///
  /// <para>
  /// A NON-capturing lambda has none, and a null pointer is what says so - the lifted procedure never
  /// reads it. A non-escaping capturing one points straight at the enclosing frame's record, which is
  /// what makes its captures shared BY REFERENCE. An escaping one cannot: the frame dies before the
  /// closure is called, so the record is copied to the heap and the copy is a by-value snapshot taken
  /// at creation - the same bargain the direct emitter strikes, and the same one docs/PB36.md
  /// documents.
  /// </para>
  /// </summary>
  private void StoreClosureEnvironment(ProcedureSymbol lifted, IrValue closure) {
    if (lifted.Captures.Count == 0) {
      this.StoreNullEnvironment(closure);
      return;
    }
    if (this._captureRecord is not { } record)
      throw new IrLoweringException($"lambda {lifted.Name} captures locals this body has no record for");

    if (!lifted.IsEscapingClosure) {
      this._b.Store(this._b.Cast(IrCastOp.PtrToInt, record, IrType.U16), this.ClosureWord(closure, ClosureEnvWord));
      this._b.Store(this._b.Call(IrType.I16, this.RuntimeFn("rt_stack_seg", IrType.I16)),
        this.ClosureWord(closure, ClosureEnvWord + 1));
      return;
    }

    var (_, size) = CaptureRecord(this._model, this._model.LambdaEnclosing.GetValueOrDefault(lifted));
    var block = this._b.Call(IrType.FarPtr, this.RuntimeFn("rt_arr_alloc", IrType.FarPtr, IrType.I32),
      new IrConstantInt(IrType.I32, size));
    this._b.Call(IrType.Void,
      this.RuntimeFn("llvm.memcpy.p0.p0.i32", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1),
      block, record, new IrConstantInt(IrType.I32, size), IrBuilder.ConstBool(false));
    this._b.Store(this._b.Cast(IrCastOp.PtrToInt, block, IrType.U16), this.ClosureWord(closure, ClosureEnvWord));
    this._b.Store(this._b.Load(IrType.I16, this.RuntimeCell("rt_arrseg", IrType.I16)),
      this.ClosureWord(closure, ClosureEnvWord + 1));
  }
}
