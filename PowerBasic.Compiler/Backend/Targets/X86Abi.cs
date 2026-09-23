namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Concrete IA-32 and Intel 64 calling conventions used by machine targets.</summary>
public sealed record X86Abi(
    string Name,
    int PointerBits,
    int StackAlignment,
    int ShadowSpaceBytes,
    IReadOnlyList<MachineRegister> ArgumentRegisters,
    MachineRegister ReturnRegister,
    IReadOnlySet<MachineRegister> CalleeSavedRegisters,
    X86StackArgumentOrder ArgumentOrder = X86StackArgumentOrder.RightToLeft,
    X86StackCleanup StackCleanup = X86StackCleanup.Caller,
    X86CallDistance Distance = X86CallDistance.Near) : IMachineAbi {

  public static X86Abi For(Ir.IrCallConvention convention, X86Mode mode) {
    var bits = mode == X86Mode.Bit16 ? 16 : mode == X86Mode.Bit32 ? 32 : 64;
    var regs = mode == X86Mode.Bit16 ? X86RegisterFile.Gpr16 : mode == X86Mode.Bit32 ? X86RegisterFile.Gpr32 : X86RegisterFile.Gpr64;
    var ax = regs[0];
    var fast = mode == X86Mode.Bit64 ? new[] { regs[1], regs[2], regs[8], regs[9] } : new[] { regs[0], regs[2], regs[1] };
    var sysv64 = mode == X86Mode.Bit64 ? new[] { regs[7], regs[6], regs[2], regs[1], regs[8], regs[9] } : Array.Empty<MachineRegister>();
    var arguments = convention switch {
      Ir.IrCallConvention.Fastcall or Ir.IrCallConvention.Watcall => fast,
      Ir.IrCallConvention.Cdecl when mode == X86Mode.Bit64 => sysv64,
      _ => Array.Empty<MachineRegister>(),
    };
    return new($"x86-{bits}-{convention.ToString().ToLowerInvariant()}", bits,
      mode == X86Mode.Bit64 ? 16 : mode == X86Mode.Bit32 ? 4 : 2, 0, arguments, ax,
      mode == X86Mode.Bit64 ? new HashSet<MachineRegister>([regs[3], regs[5], regs[6], regs[7]])
        : new HashSet<MachineRegister>([regs[3], regs[5], regs[6], regs[7]]),
      convention is Ir.IrCallConvention.Basic or Ir.IrCallConvention.Pascal or Ir.IrCallConvention.Fastcall
        ? X86StackArgumentOrder.LeftToRight : X86StackArgumentOrder.RightToLeft,
      convention == Ir.IrCallConvention.Cdecl ? X86StackCleanup.Caller : X86StackCleanup.Callee,
      convention == Ir.IrCallConvention.BasicClosure ? X86CallDistance.Far : X86CallDistance.Near);
  }

  public IReadOnlyList<(int Argument, IReadOnlyList<MachineRegister> Registers, int StackOffset)> PlaceArguments(
      IReadOnlyList<Ir.IrType> types) {
    var result = new List<(int, IReadOnlyList<MachineRegister>, int)>();
    var stack = 0;
    var register = 0;
    foreach (var (type, index) in types.Select((type, index) => (type, index))) {
      var bits = Math.Max(type.Bits, type.IsPointer ? this.PointerBits : 8);
      var parts = Math.Max(1, (bits + this.PointerBits - 1) / this.PointerBits);
      var regs = register + parts <= this.ArgumentRegisters.Count
        ? this.ArgumentRegisters.Skip(register).Take(parts).ToArray()
        : Array.Empty<MachineRegister>();
      if (regs.Any()) register += parts;
      else { result.Add((index, regs, stack)); stack += parts * (this.PointerBits / 8); continue; }
      result.Add((index, regs, -1));
    }
    return result;
  }

  public static X86Abi I8086Cdecl { get; } = new(
    "i8086-cdecl", 16, 2, 0, [], X86RegisterFile.Gpr16[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr16[3], X86RegisterFile.Gpr16[5], X86RegisterFile.Gpr16[6], X86RegisterFile.Gpr16[7]]));

  public static X86Abi I386Cdecl { get; } = new(
    "i386-cdecl", 32, 4, 0, [], X86RegisterFile.Gpr32[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr32[3], X86RegisterFile.Gpr32[5], X86RegisterFile.Gpr32[6], X86RegisterFile.Gpr32[7]]));

  public static X86Abi SysV64 { get; } = new(
    "x86-64-sysv", 64, 16, 0,
    [X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[6], X86RegisterFile.Gpr64[2], X86RegisterFile.Gpr64[1],
      X86RegisterFile.Gpr64[8], X86RegisterFile.Gpr64[9]],
    X86RegisterFile.Gpr64[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr64[3], X86RegisterFile.Gpr64[5], X86RegisterFile.Gpr64[6],
      X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[12], X86RegisterFile.Gpr64[13], X86RegisterFile.Gpr64[14], X86RegisterFile.Gpr64[15]]));

  public static X86Abi Windows64 { get; } = new(
    "x86-64-windows", 64, 16, 32,
    [X86RegisterFile.Gpr64[1], X86RegisterFile.Gpr64[2], X86RegisterFile.Gpr64[8], X86RegisterFile.Gpr64[9]],
    X86RegisterFile.Gpr64[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr64[3], X86RegisterFile.Gpr64[5], X86RegisterFile.Gpr64[6],
      X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[12], X86RegisterFile.Gpr64[13], X86RegisterFile.Gpr64[14], X86RegisterFile.Gpr64[15]]));
}
