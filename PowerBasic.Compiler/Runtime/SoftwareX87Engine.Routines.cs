using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private readonly Dictionary<(byte Opcode, int Operand, Mem Memory), Label> _routines = [];
  private readonly Dictionary<(int Operation, int Left, int Right, int Result), Label> _kernels = [];

  private bool EmitRoutine((byte Opcode, int Operand, Mem Memory) key, Func<bool>? emit) {
    if (emit is null)
      return false;
    this.EnsureState();
    // A helper CALL changes SP. Explicit SP/ESP-relative operands must retain inline addressing.
    if (key.Memory.Base is Reg.SP or Reg.ESP || key.Memory.Index is Reg.SP or Reg.ESP)
      return emit();
    // x87 instructions do not alter integer FLAGS, even if their integer implementation does.
    return this.EmitSharedRoutine(this._routines, key, () => {
      this._asm.Pushf();
      var emitted = emit();
      this._asm.Popf();
      return emitted;
    });
  }

  private void EmitKernel(int operation, int left, int right, int result, Action emit) =>
    this.EmitSharedRoutine(this._kernels, (operation, left, right, result), () => { emit(); return true; });

  private bool EmitSharedRoutine<TKey>(Dictionary<TKey, Label> routines, TKey key, Func<bool> emit) where TKey : notnull {
    if (!this._shareRoutines)
      return emit();
    if (routines.TryGetValue(key, out var routine)) {
      this._asm.Call(routine);
      return true;
    }
    var over = this._asm.DefineLabel();
    routine = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.MarkLabel(routine);
    if (!emit())
      throw new InvalidOperationException("A recognized software x87 operation could not emit its routine.");
    this._asm.Ret();
    this._asm.MarkLabel(over);
    routines.Add(key, routine);
    this._asm.Call(routine);
    return true;
  }
}
