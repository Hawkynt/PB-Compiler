using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// O0308 consumer for O0026. A checked element-wise INTEGER add/sub cannot enter the packed loop
  /// directly because PADDW/PSUBW keep the wrapped lane but do not raise Error 6. For a sufficiently
  /// long vectorizable loop, scan the two input streams first with the ordinary scalar ALU and branch
  /// on OF. If every pair is safe, the existing unchecked vector emitter is legal for the entire loop;
  /// if any pair overflows, execute the ordinary checked scalar FOR from the beginning instead.
  ///
  /// <para>
  /// The scan performs no stores and the accepted shape contains only static array reads, so it is
  /// unobservable. Falling back before the first store is what preserves the exact PB contract: the
  /// scalar loop still raises Error 6 at the first overflowing element, not at the preflight element.
  /// Multiplication deliberately stays scalar until O0308 has a product-range proof; bitwise operators
  /// need no overflow version at all.
  /// </para>
  /// </summary>
  private bool TryEmitOverflowVersionedVectorFor(ForStmt f, VariableSymbol counter, Mem counterCell, long step) {
    if (!this.Optimize || !this.OptimizeSpeed
        || !(this.HasMmx || this.HasSse2 || this.HasAvx2 || this.HasAvx512)
        || step != 1
        || this.CheckBounds || !this.CheckOverflow || this.CheckNumeric || this._trackResume
        || this._registerCounter is not null || this._registerAccumulator is not null
        || counter.Type is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false })
      return false;

    if (this.OptFolder.TryFold(f.From) is not { Integer: { } loRaw }
        || this.OptFolder.TryFold(f.To) is not { Integer: { } hiRaw })
      return false;
    // O0026 closes the vector loop by materializing hi+1. That is only equivalent to the real
    // signed-INTEGER FOR when both constants survive INTEGER coercion unchanged and the final
    // increment itself does not wrap. `... TO 32767 STEP 1` wraps to -32768 and keeps running when
    // $ERROR NUMERIC is off, so versioning that loop as a finite vector trip count would miscompile it.
    if (loRaw is < short.MinValue or > short.MaxValue
        || hiRaw is < short.MinValue or > short.MaxValue
        || hiRaw == short.MaxValue)
      return false;
    long lo = loRaw, hi = hiRaw, n = hi - lo + 1;
    if (n is <= 0 or > ushort.MaxValue)
      return false;

    if (f.Body is not [AssignStmt {
          Target: { } target,
          Value: BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract, Left: { } leftExpr, Right: { } rightExpr } bin,
        }])
      return false;
    if (model.TypeOf(target) is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false }
        || model.TypeOf(leftExpr) is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false }
        || model.TypeOf(rightExpr) is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false })
      return false;
    if (this.MatchCounterIndexedArray(target, counter) is null
        || this.MatchCounterIndexedArray(leftExpr, counter) is not { } a
        || this.MatchCounterIndexedArray(rightExpr, counter) is not { } b)
      return false;

    var lanes = this.HasAvx512 ? 32 : this.HasAvx2 ? 16 : this.HasSse2 ? 8 : 4;
    // A second O(n) walk only earns its code size when the SIMD phase has enough work to amortize it.
    // Keep at least 32 elements, and at least two full vectors on the widest selected target.
    if (n < Math.Max(32, lanes * 2L))
      return false;

    var asm = this._asm;
    void LoadBase(Reg reg, (VariableSymbol Sym, int Lbound) array) {
      var byteOffset = checked((int)((lo - array.Lbound) * 2));
      asm.Mov(reg, byteOffset);
      asm.Lea(reg, Mem.At(reg, this.SlotOf(array.Sym)));
    }

    var slow = asm.DefineLabel();
    var vector = asm.DefineLabel();
    var done = asm.DefineLabel();
    var scan = asm.DefineLabel();

    LoadBase(Reg.BX, a);
    LoadBase(Reg.SI, b);
    asm.Mov(Reg.CX, checked((int)n));
    this.AlignLoopTop();
    asm.MarkLabel(scan);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    if (bin.Op == BinaryOp.Add)
      asm.Add(Reg.AX, Mem.Word(Reg.SI));
    else
      asm.Sub(Reg.AX, Mem.Word(Reg.SI));
    asm.Jo(slow);
    asm.Add(Reg.BX, 2);
    asm.Add(Reg.SI, 2);
    asm.Dec(Reg.CX);
    asm.Jnz(scan);
    asm.Jmp(vector);

    // No store happened before this edge. Re-run from the original FOR header with checked emission,
    // which preserves both the first failing element and every observable counter/store before it.
    asm.MarkLabel(slow);
    this.EmitForInt16Fast(f, counterCell, step);
    asm.Jmp(done);

    asm.MarkLabel(vector);
    var savedOverflow = this.CheckOverflow;
    var emittedVector = false;
    this.CheckOverflow = false;
    try {
      emittedVector = this.TryEmitVectorizedFor(f, counter, counterCell, step);
    } finally {
      this.CheckOverflow = savedOverflow;
    }

    // Eligibility is intentionally mirrored rather than shared with O0026 so this file remains a
    // small consumer. If O0026 later gains another gate and declines here, correctness still wins:
    // execute the ordinary checked loop rather than turning an optimization miss into a compiler crash.
    if (!emittedVector)
      this.EmitForInt16Fast(f, counterCell, step);

    asm.MarkLabel(done);
    return true;
  }
}
