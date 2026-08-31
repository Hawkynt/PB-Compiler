using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private RuntimeTarget _target = RuntimeTarget.Baseline;

  /// <summary>The normalized architecture/feature surface available to every emitted runtime section.</summary>
  public RuntimeTarget Target {
    get => this._target;
    set => this._target = value;
  }

  /// <summary>
  /// Copies as many complete vectors as worthwhile from DS:SI to ES:DI, leaves the byte remainder in
  /// CX and preserves the vector register it borrows. The save lives on SS rather than in runtime BSS,
  /// so even the entry-time BSS zeroer can use the same machinery safely.
  /// </summary>
  private void EmitVectorCopyPrefix(Assembler asm) {
    var width = this.Target.MaxRuntimeBulkVectorWidthBytes;
    if (width < 16 || PreferredBulkVector(this.Target) is not { } vector)
      return;

    var scalarTail = asm.DefineLabel();
    var loop = asm.DefineLabel();
    asm.Cmp(Reg.CX, width * 2); // do not pay spill/restore for one vector
    asm.Jb(scalarTail);

    this.SpillVector(asm, vector, width);
    asm.MarkLabel(loop);
    LoadVector(asm, vector, Mem.At(Reg.SI));
    StoreVector(asm, Mem.At(Reg.DI).Es(), vector);
    asm.Add(Reg.SI, width);
    asm.Add(Reg.DI, width);
    asm.Sub(Reg.CX, width);
    asm.Cmp(Reg.CX, width);
    asm.Jae(loop);
    this.RestoreVector(asm, vector, width);

    asm.MarkLabel(scalarTail);
  }

  /// <summary>
  /// Zeroes as many complete vectors as worthwhile at ES:DI. CX counts units rather than bytes and
  /// is left holding the scalar tail. The borrowed vector register is restored exactly.
  /// </summary>
  private void EmitVectorZeroPrefix(Assembler asm, int unitBytes) {
    var width = this.Target.MaxRuntimeBulkVectorWidthBytes;
    if (width < 16 || PreferredBulkVector(this.Target) is not { } vector)
      return;

    var unitsPerVector = width / unitBytes;
    var scalarTail = asm.DefineLabel();
    var loop = asm.DefineLabel();
    asm.Cmp(Reg.CX, unitsPerVector * 2);
    asm.Jb(scalarTail);

    this.SpillVector(asm, vector, width);
    asm.VectorZeroTarget(vector);
    asm.MarkLabel(loop);
    StoreVector(asm, Mem.At(Reg.DI).Es(), vector);
    asm.Add(Reg.DI, width);
    asm.Sub(Reg.CX, unitsPerVector);
    asm.Cmp(Reg.CX, unitsPerVector);
    asm.Jae(loop);
    this.RestoreVector(asm, vector, width);

    asm.MarkLabel(scalarTail);
  }

  /// <summary>Zero-fills CX bytes at ES:DI, using vector, DWORD, then byte stores as available.</summary>
  private void EmitRepStosbZeroWidened(Assembler asm) {
    this.EmitVectorZeroPrefix(asm, unitBytes: 1);
    if (!this.Target.Has32BitGeneralPurpose) {
      asm.Xor(Reg.AL, Reg.AL);
      asm.Rep();
      asm.Stosb();
      return;
    }

    asm.Xor(Reg.EAX, Reg.EAX);
    asm.Push(Reg.CX);
    asm.Shr(Reg.CX, 2);
    asm.Rep();
    asm.Stosd();
    asm.Pop(Reg.CX);
    asm.And(Reg.CX, (Imm)3);
    asm.Rep();
    asm.Stosb();
  }

  private void SpillVector(Assembler asm, Reg vector, int width) {
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Sub(Reg.SP, width);
    StoreVector(asm, Mem.At(Reg.BP, -width).Ss(), vector);
  }

  private void RestoreVector(Assembler asm, Reg vector, int width) {
    LoadVector(asm, vector, Mem.At(Reg.BP, -width).Ss());
    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
  }

  private static Reg? PreferredBulkVector(RuntimeTarget target) => target.MaxRuntimeBulkVectorWidthBytes switch {
    >= 64 => Reg.ZMM0,
    >= 32 => Reg.YMM0,
    >= 16 => Reg.XMM0,
    _ => null,
  };

  private static void LoadVector(Assembler asm, Reg vector, Mem source) {
    if (vector.IsZmm())
      asm.Vmovdqu512Target(vector, source);
    else if (vector.IsYmm())
      asm.VmovdquTarget(vector, source);
    else
      asm.MovupsTarget(vector, source); // SSE1 is sufficient for an untyped 16-byte copy.
  }

  private static void StoreVector(Assembler asm, Mem destination, Reg vector) {
    if (vector.IsZmm())
      asm.Vmovdqu512TargetStore(destination, vector);
    else if (vector.IsYmm())
      asm.VmovdquTargetStore(destination, vector);
    else
      asm.MovupsTargetStore(destination, vector);
  }
}
