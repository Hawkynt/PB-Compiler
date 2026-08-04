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

    /// <summary>A float pushed on the x87 stack, which the routine pops (the print entries take ST(0)).</summary>
    St0,
  }

  internal sealed record RuntimeArg(ArgKind Kind, Reg Register, Reg High = default);

  /// <summary>
  /// One runtime routine: the label the direct emitter calls, where its arguments go, what it
  /// destroys, and - for the routines that answer with a value - the register the result comes back in.
  /// <paramref name="Presets"/> are the register-to-register moves the convention requires beyond the
  /// arguments themselves, such as the <c>MOV DX, DS</c> that tells the string kernel which segment
  /// the literal bytes live in.
  /// </summary>
  internal sealed record Routine(string Label, RuntimeArg[] Args, IReadOnlyList<Reg> Clobbers,
    Reg? Result = null, (Reg Dest, Reg Source)[]? Presets = null, bool FileSelect = false);

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
    // rt_str_const(ptr text, i32 length) -> a string HANDLE. The runtime spells it rt_strmem and takes
    // the bytes as DS:SI with the length in CX, answering in AX (DosRuntime.EmitStrMem) - the same
    // three instructions the direct emitter writes before the call, MOV DX,DS included
    ["rt_str_const"] = new("rt_strmem",
      [new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Presets: [(Reg.DX, Reg.DS)]),

    // "PrintSingle/PrintDouble: value on ST(0), popped". SINGLE and DOUBLE have SEPARATE entries even
    // though both share the body: rt_print_f32 and rt_print_f64 differ only in the significant-digit
    // count they set (7 against 15/16, and the dialect moves it), which is exactly the rendering the
    // fidelity tests compare - so the source type must pick the entry rather than the format on the
    // stack, which is one and the same by then
    ["rt_print_single"] = new("rt_print_f32", [new(ArgKind.St0, default)], _callerSaved),
    ["rt_print_double"] = new("rt_print_f64", [new(ArgKind.St0, default)], _callerSaved),
    ["rt_fprint_single"] = new("rt_print_f32",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.St0, default)], _callerSaved, FileSelect: true),
    ["rt_fprint_double"] = new("rt_print_f64",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.St0, default)], _callerSaved, FileSelect: true),

    // rt_locate(row, col) -> AX = row, CX = column, a zero meaning "keep the current one"
    ["rt_locate"] = new("rt_locate",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved),
    // rt_kill(handle) -> AX = filename handle, consumed
    ["rt_kill"] = new("rt_kill", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    // rt_str_concat(ptr,ptr) -> ptr is the runtime's StrCat: AX=left, DX=right -> AX, consuming both
    ["rt_str_concat"] = new("rt_strcat",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved, Result: Reg.AX),

    // files. The runtime documents these conventions at the head of DosRuntime.Files.cs:
    // FOpen AX=filename handle, BX=PB file number, CX=mode, SI=reclen; FClose AX=file number.
    // The IR names the file number first, the runtime puts it in BX - hence the per-position table
    ["rt_file_open"] = new("rt_fopen", [
      new(ArgKind.Word, Reg.BX),      // PB file number
      new(ArgKind.Word, Reg.AX),      // filename string handle (consumed)
      new(ArgKind.Word, Reg.CX),      // mode: 0 INPUT, 1 OUTPUT, 2 APPEND, 3 RANDOM, 4 BINARY
      new(ArgKind.Word, Reg.SI),      // record length (RANDOM)
    ], _callerSaved),
    ["rt_file_close"] = new("rt_fclose", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_file_close_all"] = new("rt_fcloseall", [], _callerSaved),

    // PRINT #n. The runtime has no per-file print entries: FSelect routes the console routines at a
    // file (rt_curout plus that file's own print column), and the caller resets it to stdout after.
    // The IR models one call per printed item, so the select/restore pair wraps each one - the same
    // instructions the direct emitter writes once per statement, and the same observable column
    // accounting, since nothing else runs between the items
    ["rt_fprint_str"] = new("rt_print_str",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)],
      _callerSaved, FileSelect: true),
    ["rt_fprint_i16"] = new("rt_print_i16",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.AX)], _callerSaved, FileSelect: true),
    ["rt_fprint_i32"] = new("rt_print_i32",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, FileSelect: true),
    ["rt_fprint_nl"] = new("rt_print_nl", [new(ArgKind.Word, Reg.AX)], _callerSaved, FileSelect: true),
    ["rt_fprint_comma"] = new("rt_print_zone", [new(ArgKind.Word, Reg.AX)], _callerSaved, FileSelect: true),
  };

  /// <summary>The routine that routes console output at a file, and the cells the caller resets afterwards.</summary>
  internal const string FileSelectLabel = "rt_fselect";

  /// <summary>The convention for the named runtime declaration, or null when the bridge does not cover it.</summary>
  internal static Routine? For(string name) => _routines.GetValueOrDefault(name);
}
