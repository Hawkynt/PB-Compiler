using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The bridge between the IR's runtime declarations and the DOS runtime the direct code generator
/// calls (docs/X86-BACKEND.md).
///
/// The two sides describe the same routines in different languages. The IR lowering declares them
/// C-style - <c>rt_print_str(ptr, i32)</c> - because the same IR also feeds the C and LLVM back ends,
/// where a runtime call really is a C call. <see cref="Runtime.DosRuntime"/>, which the direct emitter
/// calls, is register-based and vintage-shaped: the string entry wants its address in <c>SI</c> and its
/// length in <c>CX</c>, and nothing is pushed at all. This table is the mapping, one entry per routine:
/// the label to call, where each IR argument goes, and what the routine destroys.
///
/// It is deliberately a short, explicit table rather than a convention. Each entry is a claim about a
/// specific hand-written assembly routine, and a wrong claim miscompiles silently - so a routine is
/// listed only after its emitter in <c>DosRuntime</c> has been read, and everything unlisted declines.
/// </summary>
internal static class RuntimeAbi {

  /// <summary>Where one IR argument goes: a register, a register pair (32-bit), or the address of the data object a pointer names.</summary>
  internal enum ArgKind {

    /// <summary>A 16-bit value in <see cref="RuntimeArg.Register"/>.</summary>
    Word,

    /// <summary>A 32-bit value in <see cref="RuntimeArg.High"/>:<see cref="RuntimeArg.Register"/>.</summary>
    Pair,

    /// <summary>The OFFSET of the global the pointer argument names (a string literal), as an immediate.</summary>
    Offset,
  }

  internal sealed record RuntimeArg(ArgKind Kind, Reg Register, Reg High = default);

  /// <summary>One runtime routine: the label the direct emitter calls, its argument registers, and the registers it destroys.</summary>
  internal sealed record Routine(string Label, RuntimeArg[] Args, IReadOnlyList<Reg> Clobbers);

  // The print routines all save and restore every register they touch, so they are in fact
  // register-transparent - but "in fact" is not the same as "provably", and a clobber claim that is
  // one register too small miscompiles a value that is never recomputed. The set is therefore the
  // full caller-saved file, which is always sound: the allocator then simply refuses to keep any
  // value in a register across the call. Narrowing it is a real optimization, and it needs a
  // mechanical check of the routine's push/pop discipline standing behind it, not a reading.
  private static readonly Reg[] _callerSaved = [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  private static readonly Dictionary<string, Routine> _routines = new(StringComparer.Ordinal) {
    // rt_print_str(ptr text, i32 length) -> SI = OFFSET text, CX = length (DosRuntime.EmitPrintStr)
    ["rt_print_str"] = new("rt_print_str",
      [new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)], _callerSaved),
    // rt_print_i16(i16) -> AX (EmitPrintInt16: CWD then straight into the 32-bit printer)
    ["rt_print_i16"] = new("rt_print_i16", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    // rt_print_i32(i32) -> DX:AX, the convention the direct emitter pushes into it
    ["rt_print_i32"] = new("rt_print_i32", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved),
    // rt_print_nl() -> no arguments (EmitPrintNewLine)
    ["rt_print_nl"] = new("rt_print_nl", [], _callerSaved),
    // a comma separator advances to the next 14-column zone; the IR spells it after the source
    // syntax, the runtime after what it does
    ["rt_print_comma"] = new("rt_print_zone", [], _callerSaved),
  };

  /// <summary>The convention for the named runtime declaration, or null when the bridge does not cover it.</summary>
  internal static Routine? For(string name) => _routines.GetValueOrDefault(name);
}
