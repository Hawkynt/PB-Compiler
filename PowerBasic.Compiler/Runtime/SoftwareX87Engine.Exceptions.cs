using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  internal const ushort StatusInvalid = 0x0001;
  internal const ushort StatusDenormal = 0x0002;
  internal const ushort StatusZeroDivide = 0x0004;
  internal const ushort StatusOverflow = 0x0008;
  internal const ushort StatusUnderflow = 0x0010;
  internal const ushort StatusPrecision = 0x0020;
  internal const ushort StatusStackFault = 0x0040;
  internal const ushort StatusErrorSummary = 0x0080;

  /// <summary>
  /// Raises one of x87's six architectural arithmetic exceptions. The sticky status bit is always
  /// set. ES is set only when the corresponding control-word mask is clear, matching the x87 status
  /// image even though the 8086 software engine cannot deliver a hardware #MF exception.
  /// </summary>
  internal void RaiseException(ushort bit) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.Or(Reg.AX, bit);
    this._asm.Mov(Reg.DX, this.Control);
    this._asm.Test(Reg.DX, bit);
    var masked = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, masked);
    this._asm.Or(Reg.AX, StatusErrorSummary);
    this._asm.MarkLabel(masked);
    this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.AX);
  }

  internal void RaiseExceptions(ushort bits) {
    foreach (var bit in new[] { StatusInvalid, StatusDenormal, StatusZeroDivide, StatusOverflow, StatusUnderflow, StatusPrecision })
      if ((bits & bit) != 0)
        this.RaiseException(bit);
  }

  internal bool IsCanonicalSignalingNaN(int value, Label yes, Label no) {
    this._asm.Test(this.Scratch(value + Meta, OperandSize.Word), SignalingNaNMask);
    this._asm.J(Condition.NotEqual, yes);
    this._asm.Jmp(no);
    return true;
  }

  internal void QuietCanonicalNaN(int value) {
    this._asm.And(this.Scratch(value + Meta, OperandSize.Word), unchecked((ushort)~SignalingNaNMask));
    this._asm.Or(this.Scratch(value + Sig3, OperandSize.Word), 0x4000);
  }

  /// <summary>Writes comparison condition codes and clears C1, as all x87 compare instructions do.</summary>
  internal void SetCompareConditionCodes(bool c0, bool c2, bool c3) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.And(Reg.AX, 0xB8FF); // clear C0, C1, C2 and C3; keep TOP and exception state
    if (c0) this._asm.Or(Reg.AX, 0x0100);
    if (c2) this._asm.Or(Reg.AX, 0x0400);
    if (c3) this._asm.Or(Reg.AX, 0x4000);
    this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  internal void EmitIndefiniteNaN(int result) {
    this.ZeroCanonical(result);
    this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0xC000);
    this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), (int)(SignMask | ClassNaN));
  }

  internal void PropagateNaN(int source, int result) {
    this.CopyScratch(source, result);
    var signaling = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(result + Meta, OperandSize.Word), SignalingNaNMask);
    this._asm.J(Condition.NotEqual, signaling);
    this.QuietCanonicalNaN(result);
    this._asm.Jmp(done);
    this._asm.MarkLabel(signaling);
    this.RaiseException(StatusInvalid);
    this.QuietCanonicalNaN(result);
    this._asm.MarkLabel(done);
  }

  internal void EmitSignedClassResult(int a, int b, int result, ushort @class) {
    this.ZeroCanonical(result);
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, SignMask);
    this._asm.Or(Reg.AX, @class);
    this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    if (@class == ClassInfinity)
      this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0x8000);
  }
}
