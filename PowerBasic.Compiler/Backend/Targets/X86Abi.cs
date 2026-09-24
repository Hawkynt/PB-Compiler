namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Concrete x86 calling conventions used by the hosted machine targets.</summary>
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
    var (bits, regs) = mode switch {
      X86Mode.Bit16 => (16, X86RegisterFile.Gpr16),
      X86Mode.Bit32 => (32, X86RegisterFile.Gpr32),
      X86Mode.Bit64 => (64, X86RegisterFile.Gpr64),
      _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unsupported x86 mode"),
    };
    var ax = regs[0];
    // These are the conventions this compiler exposes, not a generic synonym table. In 16/32-bit
    // PB, FASTCALL is AX/DX/BX and WATCALL is AX/DX/BX/CX; WATCALL's fourth register and its
    // right-to-left overflow are distinct from FASTCALL. In 64-bit mode FASTCALL follows the
    // Microsoft x64 register set while CDECL follows SysV, matching the two concrete ABIs available
    // from this target layer.
    var fast = mode switch {
      X86Mode.Bit16 or X86Mode.Bit32 => new[] { regs[0], regs[2], regs[3] },
      X86Mode.Bit64 => new[] { regs[1], regs[2], regs[8], regs[9] },
      _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unsupported x86 mode"),
    };
    var wat = new[] { regs[0], regs[2], regs[3], regs[1] };
    var sysv64 = mode == X86Mode.Bit64
      ? new[] { regs[7], regs[6], regs[2], regs[1], regs[8], regs[9] }
      : Array.Empty<MachineRegister>();
    var arguments = convention switch {
      Ir.IrCallConvention.Fastcall => fast,
      Ir.IrCallConvention.Watcall => wat,
      Ir.IrCallConvention.Cdecl when mode == X86Mode.Bit64 => sysv64,
      Ir.IrCallConvention.Basic or Ir.IrCallConvention.Pascal or Ir.IrCallConvention.Cdecl
        or Ir.IrCallConvention.Stdcall
        or Ir.IrCallConvention.BasicClosure => Array.Empty<MachineRegister>(),
      _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "unsupported x86 calling convention"),
    };
    var argumentOrder = convention switch {
      Ir.IrCallConvention.Basic or Ir.IrCallConvention.Pascal or Ir.IrCallConvention.BasicClosure
        => X86StackArgumentOrder.LeftToRight,
      Ir.IrCallConvention.Fastcall when mode != X86Mode.Bit64 => X86StackArgumentOrder.LeftToRight,
      Ir.IrCallConvention.Fastcall => X86StackArgumentOrder.RightToLeft,
      Ir.IrCallConvention.Cdecl or Ir.IrCallConvention.Stdcall or Ir.IrCallConvention.Watcall
        => X86StackArgumentOrder.RightToLeft,
      _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "unsupported x86 calling convention"),
    };
    var stackCleanup = convention switch {
      Ir.IrCallConvention.Cdecl => X86StackCleanup.Caller,
      Ir.IrCallConvention.Fastcall when mode == X86Mode.Bit64 => X86StackCleanup.Caller,
      Ir.IrCallConvention.Basic or Ir.IrCallConvention.Pascal or Ir.IrCallConvention.Stdcall
        or Ir.IrCallConvention.Fastcall or Ir.IrCallConvention.Watcall or Ir.IrCallConvention.BasicClosure
        => X86StackCleanup.Callee,
      _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "unsupported x86 calling convention"),
    };
    var distance = convention switch {
      Ir.IrCallConvention.BasicClosure when mode == X86Mode.Bit16 => X86CallDistance.Far,
      Ir.IrCallConvention.BasicClosure => throw new NotSupportedException(
        $"{convention} requires a 16-bit segmented call target"),
      Ir.IrCallConvention.Basic or Ir.IrCallConvention.Pascal or Ir.IrCallConvention.Cdecl
        or Ir.IrCallConvention.Stdcall or Ir.IrCallConvention.Fastcall or Ir.IrCallConvention.Watcall
        => X86CallDistance.Near,
      _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "unsupported x86 calling convention"),
    };
    var shadowSpace = convention == Ir.IrCallConvention.Fastcall && mode == X86Mode.Bit64 ? 32 : 0;
    var calleeSaved = mode == X86Mode.Bit64
      ? convention == Ir.IrCallConvention.Fastcall
        ? new HashSet<MachineRegister>([regs[3], regs[5], regs[6], regs[7], regs[12], regs[13], regs[14], regs[15]])
        : new HashSet<MachineRegister>([regs[3], regs[5], regs[12], regs[13], regs[14], regs[15]])
      : new HashSet<MachineRegister>([regs[3], regs[5], regs[6], regs[7]]);
    if ((mode != X86Mode.Bit64 && convention is (Ir.IrCallConvention.Fastcall or Ir.IrCallConvention.Watcall))
        || (mode == X86Mode.Bit64 && convention == Ir.IrCallConvention.Watcall))
      calleeSaved.Remove(regs[3]);
    return new($"x86-{bits}-{convention.ToString().ToLowerInvariant()}", bits,
      mode == X86Mode.Bit64 ? 16 : mode == X86Mode.Bit32 ? 4 : 2, shadowSpace, arguments, ax,
      calleeSaved,
      argumentOrder, stackCleanup, distance);
  }

  /// <summary>
  /// Assigns arguments to leading ABI registers and the remaining stack area. Stack offsets are
  /// relative to the caller's stack pointer immediately before CALL. The offset includes reserved
  /// shadow space, so left-to-right conventions place the last argument after that area and
  /// right-to-left conventions place the first overflow argument after it.
  /// </summary>
  public IReadOnlyList<(int Argument, IReadOnlyList<MachineRegister> Registers, int StackOffset)> PlaceArguments(
      IReadOnlyList<Ir.IrType> types) {
    ArgumentNullException.ThrowIfNull(types);
    var result = new (int Argument, IReadOnlyList<MachineRegister> Registers, int StackOffset)[types.Count];
    var stackArguments = new List<(int Argument, int Bytes)>();
    var register = 0;
    foreach (var (type, index) in types.Select((type, index) => (type, index))) {
      if (type.IsVoid)
        throw new NotSupportedException($"{this.Name} cannot pass void argument #{index}");
      if (this.ArgumentRegisters.Count != 0 && type.IsIeeeFloat)
        throw new NotSupportedException(
          $"{this.Name} does not model floating-point argument registers (argument {index}: {type})");
      var bits = Math.Max((long)type.Bits, type.IsPointer ? this.PointerBits : 8);
      var parts = Math.Max(1L, (bits + this.PointerBits - 1) / this.PointerBits);
      if (this.ArgumentRegisters.Count != 0 && parts != 1)
        throw new NotSupportedException(
          $"{this.Name} register arguments must fit one {this.PointerBits}-bit register (argument {index}: {type})");
      var regs = register + parts <= this.ArgumentRegisters.Count
        ? this.ArgumentRegisters.Skip(register).Take((int)parts).ToArray()
        : Array.Empty<MachineRegister>();
      if (regs.Length != 0) {
        register += (int)parts;
        result[index] = (index, regs, -1);
      } else {
        var byteCount = parts * (this.PointerBits / 8L);
        if (byteCount > int.MaxValue)
          throw new NotSupportedException(
            $"{this.Name} cannot represent argument {index}'s {byteCount}-byte stack slot");
        stackArguments.Add((index, (int)byteCount));
      }
    }

    long stackOffset = this.ShadowSpaceBytes;
    var pushOrder = this.ArgumentOrder == X86StackArgumentOrder.RightToLeft
      ? stackArguments.OrderBy(argument => argument.Argument)
      : stackArguments.OrderByDescending(argument => argument.Argument);
    foreach (var (index, bytes) in pushOrder) {
      if (stackOffset > int.MaxValue)
        throw new NotSupportedException($"{this.Name} stack arguments exceed the supported offset range");
      result[index] = (index, [], (int)stackOffset);
      stackOffset += bytes;
      if (stackOffset > int.MaxValue)
        throw new NotSupportedException($"{this.Name} stack arguments exceed the supported offset range");
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
