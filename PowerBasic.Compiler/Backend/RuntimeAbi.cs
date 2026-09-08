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

  /// <summary>Where one IR argument goes: registers, the x87 stack, or a target address.</summary>
  internal enum ArgKind {

    /// <summary>A 16-bit value in <see cref="RuntimeArg.Register"/>.</summary>
    Word,

    /// <summary>A 32-bit value in <see cref="RuntimeArg.High"/>:<see cref="RuntimeArg.Register"/>.</summary>
    Pair,

    /// <summary>
    /// The LOW HALF of a 32-bit value in <see cref="RuntimeArg.Register"/>, the high half deliberately
    /// dropped.
    ///
    /// <para>
    /// The ordinary <see cref="Word"/> kind refuses a 32-bit value that is not provably narrow, and it
    /// is right to: dropping sixteen bits of an arbitrary value is a miscompile that reads as a
    /// plausible number. This kind is the same drop made ON PURPOSE, as a claim about a particular
    /// routine and a particular argument - and it is a claim, so it belongs at the row where someone
    /// can check it rather than inside the operand code where it would apply to everything.
    /// </para>
    /// <para>
    /// The array entries are what it exists for: <c>rt_arr_realloc</c> and <c>rt_arr_free</c> take the
    /// size of a block that ALREADY EXISTS, and a block that exists is under 64 KiB because
    /// <c>rt_arr_alloc</c> raises Error 7 on anything larger. Passing the pair instead costs a second
    /// physical register at the call, which is what put DIFF56's module body over the machine's six.
    /// </para>
    /// </summary>
    LowWord,

    /// <summary>The OFFSET of the global the pointer argument names (a string literal), as an immediate.</summary>
    Offset,

    /// <summary>
    /// A near offset in <see cref="RuntimeArg.Register"/> and its segment value in
    /// <see cref="RuntimeArg.High"/>. The selector derives DS for globals and SS for frame objects.
    ///
    /// <para>
    /// This kind WORKS - the entries below use it and the corpus pins them. What has repeatedly
    /// failed is composing a NEW multi-argument runtime routine around it, and the reproduction is
    /// worth keeping because six plausible explanations have already been eliminated.
    /// </para>
    /// <para>
    /// <b>The reproduction.</b> Add <c>rt_file_get</c> / <c>rt_file_put</c> mapping the IR's
    /// <c>(i32 file, i32 record, ptr buffer, i32 size)</c> onto a DOS routine that seeks with
    /// <c>rt_fsetpos</c>, resolves the handle with <c>rt_fhandle</c> and transfers with
    /// <c>rt_fwrite</c>. Then compile:
    /// </para>
    /// <code>
    /// TYPE R : a AS INTEGER : b AS INTEGER : END TYPE
    /// DIM r AS R
    /// OPEN "O.TXT" FOR BINARY AS #1
    /// r.a = 7 : PUT #1, , r
    /// </code>
    /// <para>
    /// The OPEN succeeds and the PUT raises ERR 57, with the optimizer OFF, while the direct emitter
    /// writes the record. Eliminated so far: the seek (the unnumbered form skips it and still fails);
    /// DI not being preserved across <c>rt_fsetpos</c> (staging it changes nothing); the staging
    /// cells colliding with a callee's (nothing between them touches rt_st0..3); the buffer's segment
    /// being SS rather than DS (a SHARED record, which lives in DGROUP, fails identically);
    /// argument-order clobbering (the emitted moves are AX, CX, BX, DI, SI, DX - all distinct); and
    /// the allocator ignoring <c>clobbers</c> (it excludes any register clobbered anywhere an
    /// interval is live).
    /// </para>
    /// <para>
    /// Whatever it is, it is not visible in the machine IR, which reads correctly instruction by
    /// instruction. It wants a single-step through the emulator rather than a seventh guess.
    /// </para>
    /// </summary>
    Pointer,

    /// <summary>The constant i1 volatility marker on an LLVM memory intrinsic; it has no runtime slot.</summary>
    VolatileFlag,

    /// <summary>A float pushed on the x87 stack, which the routine pops (the print entries take ST(0)).</summary>
    St0,

    /// <summary>
    /// A 32-bit UNSIGNED value staged as a zero-extended qword in the frame and FILDed onto the x87
    /// stack. There is no unsigned 32-bit printer: <c>rt_print_i32</c> would render 4294967295 as -1,
    /// so a DWORD goes through the 64-bit one, where the zeroed high half makes it positive. It is the
    /// four MOVs and the FILD the direct emitter writes for exactly this case.
    /// </summary>
    ZeroExtendedQwordSt0,

    /// <summary>
    /// A signed 64-bit integer staged verbatim in a qword frame cell and FILDed onto the x87 stack.
    /// PB keeps QUAD values integral on the x87 but formats PRINT through the 15-digit DOUBLE entry;
    /// preserving all four words before the FILD is what keeps values above 2^32 exact.
    /// </summary>
    SignedQwordSt0,

    /// <summary>
    /// A 16-bit value ZERO-extended into a register pair: the word goes in
    /// <see cref="RuntimeArg.Register"/> and <see cref="RuntimeArg.High"/> is cleared.
    ///
    /// This is how an unsigned WORD prints its full range. Sent through the 16-bit printer it would
    /// come out signed - 65535 as -1 - so the direct emitter writes <c>XOR DX,DX</c> and calls the
    /// 32-bit one instead, which is exactly this.
    /// </summary>
    ZeroPair,
  }

  internal sealed record RuntimeArg(ArgKind Kind, Reg Register, Reg High = default);

  /// <summary>How a routine hands its answer back, when the IR's result type is not simply the register.</summary>
  internal enum ResultKind {

    /// <summary>A 16-bit value in <see cref="Routine.Result"/> - a handle, a count, a code.</summary>
    Word,

    /// <summary>
    /// The routine answers a 16-bit value but the IR types the call 32-bit, so the word is
    /// SIGN-EXTENDED into the pair. <c>LEN</c> is the example: the runtime's <c>rt_len</c> gives a
    /// word, the IR declares <c>rt_str_len(ptr) -&gt; i32</c> because the same declaration also feeds
    /// the C back end, and the direct emitter writes exactly this <c>CWD</c> after the call.
    /// </summary>
    WidenedWord,

    /// <summary>The routine leaves its answer on the x87 stack (<c>VAL</c>), which is stored to the call's frame cell.</summary>
    St0,

    /// <summary>A 32-bit result in DX:AX, copied into the call's virtual register pair.</summary>
    Pair,

    /// <summary>A 16-bit integer bit pattern written to <c>rt_scratch</c>.</summary>
    ScratchI16,

    /// <summary>An unsigned byte in <c>rt_scratch</c>, zero-extended to the call's word result.</summary>
    ScratchU8ToWord,

    /// <summary>A 32-bit integer bit pattern written to <c>rt_scratch</c>.</summary>
    ScratchI32,

    /// <summary>An IEEE binary32 bit pattern written to <c>rt_scratch</c>.</summary>
    ScratchF32,

    /// <summary>An IEEE binary64 bit pattern written to <c>rt_scratch</c>.</summary>
    ScratchF64,

    /// <summary>
    /// An 8-bit answer in the low half of <see cref="Routine.Result"/>. A BYTE has no register pair
    /// and no word of its own here - <c>RegSize</c> makes it a byte register - so <see cref="Word"/>
    /// cannot describe it even though the routine really did compute a whole word.
    /// </summary>
    LowByte,

    /// <summary>
    /// An INTEGRAL answer left on the x87 stack for a call the IR types 64-bit, popped into the
    /// call's own qword frame cell. That cell is the only place this target holds a 64-bit integer
    /// (see <c>InstructionSelector.SelectQwordLoad</c>), and the x87 is the only thing that can carry
    /// one intact: a QUAD has as many mantissa bits as the register file it travels in.
    /// </summary>
    St0ToQword,
  }

  /// <summary>
  /// One runtime routine: the label the direct emitter calls, where its arguments go, what it
  /// destroys, and - for the routines that answer with a value - the register the result comes back in.
  /// <paramref name="Presets"/> are the register-to-register moves the convention requires beyond the
  /// arguments themselves, such as the <c>MOV DX, DS</c> that tells the string kernel which segment
  /// the literal bytes live in.
  /// </summary>
  internal sealed record Routine(string Label, RuntimeArg[] Args, IReadOnlyList<Reg> Clobbers,
    Reg? Result = null, (Reg Dest, Reg Source)[]? Presets = null, bool FileSelect = false,
    ResultKind Answer = ResultKind.Word, (Reg Dest, int Value)[]? Constants = null);

  // The conservative default is the full caller-saved file: a clobber claim one register too small
  // miscompiles a value that is never recomputed. A narrower set is used only where tests and runtime
  // inspection establish balanced saves for the excluded registers.
  private static readonly Reg[] _callerSaved = [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  // These three numeric-print entries have balanced SI/DI saves in their runtime bodies. Keeping the
  // arithmetic registers conservative while exposing that verified index-register preservation is
  // what lets an optimized 386 loop retain an ESI counter and EDI accumulator across PRINT.
  private static readonly Reg[] _numericPrintClobbers = [Reg.AX, Reg.BX, Reg.CX, Reg.DX];

  private static readonly Dictionary<string, Routine> _routines = new(StringComparer.Ordinal) {
    ["rt_arr_alloc"] = new("rt_arr_alloc", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_arr_alloc_nz"] = new("rt_arr_alloc_nz", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_arr_alloc_ptr"] = new("rt_arr_alloc_ptr", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_arr_realloc"] = new("rt_arr_realloc",
      [new(ArgKind.Word, Reg.BX), new(ArgKind.LowWord, Reg.CX), new(ArgKind.Pair, Reg.AX, Reg.DX)],
      _callerSaved, Result: Reg.AX),
    ["rt_arr_realloc_ptr"] = new("rt_arr_realloc_ptr",
      [new(ArgKind.Word, Reg.BX), new(ArgKind.LowWord, Reg.CX), new(ArgKind.Pair, Reg.AX, Reg.DX)],
      _callerSaved, Result: Reg.AX),
    ["rt_arr_free"] = new("rt_arr_free",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.LowWord, Reg.CX)], _callerSaved),
    ["rt_arr_free_ptr"] = new("rt_arr_free_ptr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.LowWord, Reg.CX)], _callerSaved),
  };

  /// <summary>The routine that routes console output at a file, and the cells the caller resets afterwards.</summary>
  internal const string FileSelectLabel = "rt_fselect";

  /// <summary>The convention for the named runtime declaration, or null when the bridge does not cover it.</summary>
  internal static Routine? For(string name) => _routines.GetValueOrDefault(name);

  /// <summary>
  /// Every DOS label a row here can make the back end CALL. Each is a claim that
  /// <see cref="Runtime.DosRuntime"/> defines a routine by that name, and a wrong claim used to be
  /// invisible until the linker met it - so it is checked against the runtime instead
  /// (<c>CodeGenerator.UnboundRuntimeCallees</c>).
  /// </summary>
  internal static IEnumerable<string> Labels =>
    _routines.Values.Select(r => r.Label).Append(FileSelectLabel).Distinct(StringComparer.OrdinalIgnoreCase);
}
