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
    // rt_print_str(ptr text, i32 length) -> SI = OFFSET text, CX = length (DosRuntime.EmitPrintStr)
    ["rt_print_str"] = new("rt_print_str",
      [new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)], _callerSaved),
    // rt_print_i16(i16) -> AX (EmitPrintInt16: CWD then straight into the 32-bit printer)
    ["rt_print_i16"] = new("rt_print_i16", [new(ArgKind.Word, Reg.AX)], _numericPrintClobbers),
    // rt_print_i32(i32) -> DX:AX, the convention the direct emitter pushes into it
    ["rt_print_i32"] = new("rt_print_i32", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _numericPrintClobbers),
    // rt_print_nl() -> no arguments (EmitPrintNewLine)
    ["rt_print_nl"] = new("rt_print_nl", [], _numericPrintClobbers),
    // a comma separator advances to the next 14-column zone; the IR spells it after the source
    // syntax, the runtime after what it does
    ["rt_print_comma"] = new("rt_print_zone", [], _callerSaved),
    // rt_str_const(ptr text, i32 length) -> a string HANDLE. The runtime spells it rt_strmem and takes
    // the bytes as DS:SI with the length in CX, answering in AX (DosRuntime.EmitStrMem) - the same
    // three instructions the direct emitter writes before the call, MOV DX,DS included
    ["rt_str_const"] = new("rt_strmem",
      [new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Presets: [(Reg.DX, Reg.DS)]),
    // the SAME routine reading a fixed-width buffer that is not a literal: a STRING * n variable or
    // record field. It differs only in where the bytes are - a frame or data address rather than a
    // pooled literal - so the pointer arrives as an offset AND a segment instead of an immediate
    // offset with DS assumed
    ["rt_str_from_fixed"] = new("rt_strmem",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),

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

    // rt_error(code) -> AX = the error number; rt_raise dispatches it through ON ERROR and does not
    // return, which is why nothing after the call is reachable
    ["rt_error"] = new("rt_raise", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    // rt_locate(row, col) -> AX = row, CX = column, a zero meaning "keep the current one"
    ["rt_locate"] = new("rt_locate",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved),
    // rt_kill(handle) -> AX = filename handle, consumed
    ["rt_kill"] = new("rt_kill", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    // rt_str_concat(ptr,ptr) -> ptr is the runtime's StrCat: AX=left, DX=right -> AX, consuming both
    ["rt_str_concat"] = new("rt_strcat",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved, Result: Reg.AX),

    // "StrCatVar: AX=target handle, DX=source handle -> AX". It grows the TARGET in place when the
    // target is the topmost heap block and copies the source's bytes into it - so it consumes the
    // target and BORROWS the source, which is what makes Ir.Passes.StringAppendInPlace drop the copy
    // the lowering made of the source.
    ["rt_str_append_var"] = new("rt_strcatvar",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved, Result: Reg.AX),
    // "StrCatLit: AX=target handle, DS:SI=literal bytes, CX=length -> AX (consumes the target)". The
    // literal never becomes a handle at all, which is the whole win over rt_strmem + rt_strcat; the
    // routine pushes DS itself on its fallback path, so no segment preset is needed here.
    ["rt_str_append_lit"] = new("rt_strcatlit",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Offset, Reg.SI), new(ArgKind.Word, Reg.CX)],
      _callerSaved, Result: Reg.AX),
    // rt_str_dup(ptr) -> ptr is StrDup: "AX=handle -> AX=copy". The lowering puts one of these on
    // every read of a string variable or array element, which is what makes the consuming routines
    // above safe to call - see IrLowering.BorrowString
    ["rt_str_dup"] = new("rt_strdup", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    // "StrFree: AX=handle (0 ok)" - the zero case is why an assignment needs no first-time guard
    ["rt_str_free"] = new("rt_strfree", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    // rt_print_strvar(ptr handle) is the runtime's StrPrint: "AX=handle - writes to current output
    // (consumes)". PRINT of a string VARIABLE goes through this rather than through rt_print_str,
    // which takes literal bytes at DS:SI and has no handle to release. Consuming is what the IR wants
    // anyway: every string value in generated code is an owned temporary.
    ["rt_print_strvar"] = new("rt_str_print", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_fprint_strvar"] = new("rt_str_print",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.AX)], _callerSaved, FileSelect: true),

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

    // The string routines below are transcribed from the ABI block at the head of
    // DosRuntime.Strings.cs, which states each one's registers and whether it consumes its handles.
    // Consuming is what the IR wants: every string value in generated code is an owned temporary,
    // and the lowering puts an rt_str_dup on every read of a variable precisely so these are safe.

    // The MK wrappers reproduce the direct emitter's rt_scratch staging and return a new owned
    // handle in AX. Integers are copied as little-endian register bits; floats are popped from ST(0)
    // at their declared IEEE width.
    ["rt_str_mkbyt"] = new("rt_mkbyt", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_str_mki"] = new("rt_mki", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_str_mkl"] = new("rt_mkl", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_str_mkdwd"] = new("rt_mkdwd", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_str_mks"] = new("rt_mks", [new(ArgKind.St0, default)], _callerSaved, Result: Reg.AX),
    ["rt_str_mkd"] = new("rt_mkd", [new(ArgKind.St0, default)], _callerSaved, Result: Reg.AX),

    // rt_cv consumes the handle in AX and copies/pads CX bytes into rt_scratch. The result kind says
    // how the selector must load those exact bytes after the call.
    ["rt_str_cvi"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchI16, Constants: [(Reg.CX, 2)]),
    ["rt_str_cvbyt"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchU8ToWord, Constants: [(Reg.CX, 1)]),
    ["rt_str_cvwrd"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchI16, Constants: [(Reg.CX, 2)]),
    ["rt_str_cvl"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchI32, Constants: [(Reg.CX, 4)]),
    ["rt_str_cvdwd"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchI32, Constants: [(Reg.CX, 4)]),
    ["rt_str_cvs"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchF32, Constants: [(Reg.CX, 4)]),
    ["rt_str_cvd"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchF64, Constants: [(Reg.CX, 8)]),
    ["rt_str_cve"] = new("rt_cv", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Answer: ResultKind.ScratchF64, Constants: [(Reg.CX, 8)]),

    // Raw memcmp: DX:SI=left, BX:DI=right, CX=byte count -> AX=-1/0/1. The portable declaration
    // returns i32, so the signed word answer is widened exactly like LEN/ASC.
    ["rt_mem_compare"] = new("rt_memcmp",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Pointer, Reg.DI, Reg.BX),
       new(ArgKind.Word, Reg.CX)],
      _callerSaved, Result: Reg.AX, Answer: ResultKind.WidenedWord),
    ["llvm.memcpy.p0.p0.i32"] = new("rt_memcpy",
      [new(ArgKind.Pointer, Reg.DI, Reg.BX), new(ArgKind.Pointer, Reg.SI, Reg.DX),
       new(ArgKind.Word, Reg.CX), new(ArgKind.VolatileFlag, default)],
      _callerSaved),
    ["llvm.memset.p0.i32"] = new("rt_memset",
      [new(ArgKind.Pointer, Reg.DI, Reg.BX), new(ArgKind.Word, Reg.AX),
       new(ArgKind.Word, Reg.CX), new(ArgKind.VolatileFlag, default)],
      _callerSaved),

    // "Len: AX=handle -> AX=length (consumes)". The IR declares the result i32 - see
    // ResultKind.WidenedWord for why, and why the CWD is not optional
    ["rt_str_len"] = new("rt_len", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "Val: AX=handle -> ST0 (consumes)". The only runtime entry so far that answers on the x87 stack
    ["rt_str_val"] = new("rt_val", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0),

    // "StrLeft/StrRight: AX=handle, CX=count -> AX (consumes)"
    ["rt_str_left"] = new("rt_strleft",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),
    ["rt_str_right"] = new("rt_strright",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),

    // "StrMid: AX=handle, CX=start(1-based), DX=length -> AX (consumes; clamps)"
    ["rt_str_mid"] = new("rt_strmid",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.DX)],
      _callerSaved, Result: Reg.AX),

    // "StrMid: AX=handle, CX=start(1-based), DX=length -> AX (consumes; clamps)" - the two-argument
    // MID$(s$, i) is the same routine asked for everything from the start on. The runtime CLAMPS the
    // length, so the largest positive word says "to the end" without the caller computing a length
    // it would only have to be right about twice.
    ["rt_str_mid2"] = new("rt_strmid",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)],
      _callerSaved, Result: Reg.AX, Constants: [(Reg.DX, 0x7FFF)]),

    // "StrCmp: AX=left, DX=right -> AX=-1/0/1 bytewise (consumes both)". The IR declares the result
    // i32, so the word answer is sign-extended - the same reason rt_str_len carries WidenedWord.
    ["rt_str_compare"] = new("rt_strcmp",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "StrCmpEq: AX=left, DX=right -> AX=0 equal / 1 unequal (consumes both)" - the same call shape
    // as rt_strcmp with a different answer, which is why only a caller that tests it against zero may
    // be routed here (Ir.Passes.StringCompareEquality). The routine lives in its own trimmable runtime
    // section, so naming it here is also what keeps that section in the image.
    ["rt_str_compare_eq"] = new("rt_strcmpeq",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "MidSet: AX=target handle, CX=start, BX=length limit, DX=value handle (in-place replace;
    // consumes the value handle only)". The IR declares a pointer result and the routine returns
    // none - but it replaces IN PLACE and preserves AX, so the target handle it was given is still
    // there, which is exactly the value the IR wants back.
    ["rt_str_mid_assign"] = new("rt_midset",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX),
       new(ArgKind.Word, Reg.BX), new(ArgKind.Word, Reg.DX)],
      _callerSaved, Result: Reg.AX),

    // STR$ of a QUAD: a capture-mode wrapper around rt_print_i64, which takes ST0 integral and pops.
    ["rt_str_from_i64"] = new("rt_str_i64",
      [new(ArgKind.SignedQwordSt0, default)], _callerSaved, Result: Reg.AX),

    // "StrUpr/StrLwr: AX=handle -> AX (transforms in place)"
    ["rt_str_ucase"] = new("rt_strupr", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_str_lcase"] = new("rt_strlwr", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    // "LTrim/RTrim: AX=handle -> AX (consumes)" - and they clobber CX/DX beyond the usual, which the
    // full caller-saved set already covers
    ["rt_str_ltrim"] = new("rt_ltrim", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_str_rtrim"] = new("rt_rtrim", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),

    // "CharAt: AX=handle, CX=1-based index -> AX=that byte, or 0 past the end (consumes)". It clamps
    // the index below 1 exactly as rt_strmid does, which is what lets ASC(MID$(s$,i,1)) become one
    // call (Ir.Passes.StringByteRead). Its own trimmable section, referenced only from here.
    ["rt_str_char_at"] = new("rt_charat",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "Asc: AX=handle -> AX=first byte or 0 (consumes)"; the IR types the result i32
    ["rt_str_asc"] = new("rt_asc", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "Radix: DX:AX=value, CL=bits/digit (1/3/4), CH=min digits -> AX". HEX$ is four bits per digit
    // with a one-digit minimum, which is the CX the direct emitter loads: (digits << 8) | bits. The
    // IR carries no digit count - HEX$ WITH one is still a lowering decline - so the minimum is 1.
    ["rt_str_hex"] = new("rt_radix", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.CX, (1 << 8) | 4)]),

    // The same routine when the CALL carries the digit count: the lowering packs
    // (digits << 8) | bits into one word because that is the shape rt_radix reads, and a constant
    // count folds there rather than costing instructions here.
    ["rt_str_radix"] = new("rt_radix",
      [new(ArgKind.Pair, Reg.AX, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),

    // OCT$ and BIN$ are the same routine at three and one bits per digit - the direct emitter's own
    // `(digits << 8) | bits`, with the same one-digit minimum
    ["rt_str_oct"] = new("rt_radix", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.CX, (1 << 8) | 3)]),
    ["rt_str_bin"] = new("rt_radix", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.CX, (1 << 8) | 1)]),

    // "StrFill: CX=count, DL=char -> AX" - STRING$(n, code). The IR names (count, char) and the
    // runtime wants the char in DL, which is the low byte of the word this puts in DX
    ["rt_str_string"] = new("rt_strfill",
      [new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.DX)], _callerSaved, Result: Reg.AX),

    // "Chr: DL=char -> AX". The IR types the code point i32; only the low byte is read, so the word
    // narrowing that puts it in DX puts it in DL
    ["rt_str_chr"] = new("rt_chr", [new(ArgKind.Word, Reg.DX)], _callerSaved, Result: Reg.AX),

    // "StrFill: CX=count, DL=char -> AX" - SPACE$ is StrFill with a blank, which is exactly what the
    // direct emitter writes
    ["rt_str_space"] = new("rt_strfill", [new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.DX, ' ')]),

    // the three-argument INSTR names its start, so nothing is preset
    ["rt_str_instr_start"] = new("rt_instr",
      [new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)],
      _callerSaved, Result: Reg.AX, Answer: ResultKind.WidenedWord),

    // "Instr: AX=haystack, DX=needle, CX=start -> AX=position/0 (consumes both)". The IR's two-argument
    // form has no start, and the direct emitter loads CX=1 for it. The answer is a word the IR types
    // i32, so it widens - the CWD the emitter writes after the call
    ["rt_str_instr"] = new("rt_instr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord, Constants: [(Reg.CX, 1)]),

    // "ScanSet: AX=haystack, DX=set, CX=start(1-based), BL=0 find member / 1 find non-member ->
    // AX = position or 0 (consumes both)". INSTR ANY and VERIFY are the same routine under one flag,
    // which is a CONSTANT at every call site - so it is a preset here rather than an argument, and
    // the two spellings become two entries over one label. The answer is a word the IR types i32,
    // hence the CWD the direct emitter writes after the call.
    ["rt_str_scanset"] = new("rt_scanset",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord, Constants: [(Reg.BX, 0)]),
    ["rt_str_verify"] = new("rt_scanset",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord, Constants: [(Reg.BX, 1)]),

    // "Replace: AX=subject, DX=find, CX=repl -> AX = handle" - the direct emitter's own register
    // placement for REPLACE … WITH … IN, argument for argument
    ["rt_str_replace"] = new("rt_replace",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX),

    // "Extract: AX=main, DX=match, BL=0 substring / 1 any-set -> AX = handle (consumes both)"
    ["rt_str_extract"] = new("rt_extract",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.BX, 0)]),
    ["rt_str_extract_any"] = new("rt_extract",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Constants: [(Reg.BX, 1)]),

    // "Tally: AX=main, DX=match, BL flag as above -> AX = count (consumes both)"
    ["rt_str_tally"] = new("rt_tally",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord, Constants: [(Reg.BX, 0)]),
    ["rt_str_tally_any"] = new("rt_tally",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord, Constants: [(Reg.BX, 1)]),

    // "Repeat: AX=handle, CX=count -> AX (consumes)". The IR declares it (count, text), the runtime
    // wants the text in AX - hence the per-position table rather than a convention
    ["rt_str_repeat"] = new("rt_repeat",
      [new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),

    // STR$ of a number. "StrI16: AX=value (clobbers DX); StrI32: DX:AX=value; StrF64: ST0 (popped)"
    // - and rt_str_f32 is the SINGLE entry beside it, differing only in the digit count it sets,
    // which is the rendering the fidelity tests compare
    // "ASC(s$, n) = code - pokes one byte of a dynamic string in place": AX = handle, CX = position,
    // the code in DL. It returns nothing and preserves AX, so the handle it was given is still there
    // - which is the pointer the IR declares it answers with, the same arrangement as rt_midset.
    ["rt_str_asc_set"] = new("rt_ascset",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.DX)],
      _callerSaved, Result: Reg.AX),

    // "rt_linput: AX = the file number (0 = console) -> AX = the line as a handle". One routine
    // serves LINE INPUT and LINE INPUT #n, which is why the console form is the same entry with a
    // zero pinned into AX rather than a second row that could drift from this one.
    ["rt_finput_line"] = new("rt_linput", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_input_line"] = new("rt_linput", [], _callerSaved, Result: Reg.AX, Constants: [(Reg.AX, 0)]),
    // INPUT of a number: a token read, VAL'd and rounded. The console form presets the file number
    // to 0 exactly as the LINE INPUT pair above does.
    // ASCIIZ * n: a NUL-terminated fixed buffer. Reading one makes a handle of the bytes before the
    // NUL, writing one copies and terminates, and LEN counts to the NUL rather than to the capacity -
    // which is the whole difference from a blank-padded fixed string.
    //   "AsciizLoad:  DX=segment, SI=offset, CX=capacity -> AX=string handle"
    //   "AsciizStore: AX=handle (consumed), DX=segment, DI=offset, CX=capacity"
    //   "AsciizLen:   DX=segment, SI=offset, CX=capacity -> AX=length before NUL"
    ["rt_asciiz_load"] = new("rt_az_load",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),
    ["rt_asciiz_store"] = new("rt_az_store",
      [new(ArgKind.Pointer, Reg.DI, Reg.DX), new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_asciiz_len"] = new("rt_az_len",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.WidenedWord),
    // GET #n, r, v / PUT #n, r, v: a whole record between a file and a variable's storage. Four
    // arguments and six registers, which is every one this ABI has - the record number takes a pair,
    // the buffer takes an offset and a segment, and the file number and size take one each.
    ["rt_file_get"] = new("rt_frec_get",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.CX, Reg.BX),
       new(ArgKind.Pointer, Reg.DI, Reg.SI), new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_file_put"] = new("rt_frec_put",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.CX, Reg.BX),
       new(ArgKind.Pointer, Reg.DI, Reg.SI), new(ArgKind.Word, Reg.DX)], _callerSaved),
    // INPUT of a FLOAT: rt_val already leaves its answer on ST0, so there is nothing to convert.
    // All three widths share one entry - the runtime reads a number, and the DECLARED type picks the
    // formatter rather than a rounding step, which is the same rule PRINT follows.
    ["rt_finput_single"] = new("rt_inp_flt", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0),
    ["rt_input_single"] = new("rt_inp_flt", [], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0, Constants: [(Reg.AX, 0)]),
    ["rt_finput_double"] = new("rt_inp_flt", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0),
    ["rt_input_double"] = new("rt_inp_flt", [], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0, Constants: [(Reg.AX, 0)]),
    ["rt_finput_ext"] = new("rt_inp_flt", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0),
    ["rt_input_ext"] = new("rt_inp_flt", [], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.St0, Constants: [(Reg.AX, 0)]),
    // INPUT of a STRING item: one token, which rt_ftoken already answers with as a handle
    ["rt_finput_str"] = new("rt_ftoken", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_input_str"] = new("rt_ftoken", [], _callerSaved, Result: Reg.AX, Constants: [(Reg.AX, 0)]),
    // "StoreFixed: AX=handle, DX:DI=dest, CX=field length (copy + blank pad; consumes)" - the IR
    // names the destination first because it declares C-style, and the length arrives as an i32
    // whose low word is the field width, exactly as rt_print_str's length does
    ["rt_str_to_fixed"] = new("rt_store_fixed",
      [new(ArgKind.Pointer, Reg.DI, Reg.DX), new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.AX)],
      _callerSaved),
    // "StoreFixedR: AX=handle, DX:DI=dest, CX=field length (blank the field, then copy RIGHT-
    // justified; consumes)" - the same registers as rt_store_fixed, which is what RSET into a fixed
    // string is: the same store with the padding on the other end
    ["rt_str_to_fixed_r"] = new("rt_storefixed_r",
      [new(ArgKind.Pointer, Reg.DI, Reg.DX), new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.AX)],
      _callerSaved),
    ["rt_finput_i16"] = new("rt_inp_i16", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_input_i16"] = new("rt_inp_i16", [], _callerSaved, Result: Reg.AX, Constants: [(Reg.AX, 0)]),
    ["rt_finput_i32"] = new("rt_inp_i32", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.Pair),
    ["rt_input_i32"] = new("rt_inp_i32", [], _callerSaved, Result: Reg.AX,
      Answer: ResultKind.Pair, Constants: [(Reg.AX, 0)]),

    // "Rnd: -> ST0 = next SINGLE in [0,1)"
    ["rt_rnd"] = new("rt_rnd", [], _callerSaved, Answer: ResultKind.St0),

    // "RND(a, z): DX:AX=lower, CX:BX=upper -> DX:AX = lower + trunc(rnd * (upper-lower+1))"
    ["rt_rnd_range"] = new("rt_rndrange",
      [new(ArgKind.Pair, Reg.AX, Reg.DX), new(ArgKind.Pair, Reg.BX, Reg.CX)],
      _callerSaved, Result: Reg.AX, Answer: ResultKind.Pair),

    // LOF(n) and SEEK(n)/LOC(n): AX = the file number -> DX:AX
    ["rt_file_length"] = new("rt_lof", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.Pair),
    ["rt_file_pos"] = new("rt_fpos", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.Pair),

    // SEEK #n, p: AX = the file number, DX:CX = the position - a LONG, and the HIGH half matters.
    //
    // This row passed the position as a bare word and left DX holding whatever the caller had in it,
    // so rt_fseekstmt seeked to garbage:p. It went unseen because the only corpus program that seeks
    // did not route until the record routines arrived; SEEK #2, 5 then landed past the end of the
    // file and the GET$ after it read nothing, which printed as five NULs rather than as an error.
    ["rt_file_seek"] = new("rt_fseekstmt",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.CX, Reg.DX)], _callerSaved),

    // PUT$ fh, s$: AX = the file number, DX = the handle. GET$ fh, n, s$: AX = file, CX = count -> AX
    ["rt_fput_str"] = new("rt_fputstr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_fget_str"] = new("rt_fgetstr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),

    // EOF(n): AX = the file number -> AX = PB's -1/0 truth
    ["rt_eof"] = new("rt_eof", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    // SETEOF #n: truncate where the file stands (a DOS write of zero bytes)
    ["rt_file_seteof"] = new("rt_fseteof", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    // CSRLIN -> AX = the 1-based cursor row; CONSIN / CONSOUT -> AX = -1 for a console, 0 redirected
    ["rt_csrlin"] = new("rt_csrlin", [], _callerSaved, Result: Reg.AX),
    ["rt_consin"] = new("rt_consin", [], _callerSaved, Result: Reg.AX),
    ["rt_consout"] = new("rt_consout", [], _callerSaved, Result: Reg.AX),
    // DEF SEG: the argument form stores the word, the bare form puts DS back
    ["rt_defseg_reset"] = new("rt_defsegreset", [], _callerSaved),
    // PEEK(offset) -> AX = the byte, zero-extended; POKE offset, value -> AX = offset, DL = the byte.
    // Both go through DEF SEG's segment, the same rt_defseg cell the inline form reads.
    // VARSEG / CODESEG: the segment half of an address, which is a register the IR cannot name
    ["rt_varseg"] = new("rt_varseg", [], _callerSaved, Result: Reg.AX),
    ["rt_codeseg"] = new("rt_codeseg", [], _callerSaved, Result: Reg.AX),
    // $ERROR STACK ON: the procedure-entry headroom probe; raises Error 201 itself
    ["rt_stack_probe"] = new("rt_stackprobe", [], _callerSaved),
    // INTERRUPT n: AL = the vector, which the routine patches into its own INT instruction. It
    // loads every register from rt_regs, executes the INT, and stores them all back.
    ["rt_interrupt"] = new("rt_interrupt", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    // REG n / REG n, v: the register buffer INT and INTERRUPT load from, indexed by PB number
    ["rt_reg_get"] = new("rt_regget", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_reg_set"] = new("rt_regset", [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_peek"] = new("rt_peek", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_poke"] = new("rt_poke", [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_peeki"] = new("rt_peeki", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_peekl"] = new("rt_peekl", [new(ArgKind.Word, Reg.AX)], _callerSaved,
      Result: Reg.AX, Answer: ResultKind.Pair),

    // FREEFILE: no arguments -> AX = the lowest file number not in use
    ["rt_freefile"] = new("rt_freefile", [], _callerSaved, Result: Reg.AX),

    // ARRAY SORT / ARRAY SCAN. The engines take EVERY parameter from memory - the rt_arpb block and
    // the rt_num_* cells, which the lowering stores into directly because they are named runtime
    // cells and the IR can address one. Only the descriptor needs a routine, and only because its
    // first word is a segment; see DosRuntime.ArrayDesc for the whole argument.
    //
    //   "rt_arr_desc / rt_arr_tagdesc: DX:SI = the far address of the array's first element,
    //    BX = the lower bound, CX = the element byte size, DI = the element count -> AX = the
    //    DS offset of the filled descriptor". BX is pushed and restored; AX carries the answer.
    ["rt_arr_desc"] = new("rt_arr_desc",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Word, Reg.BX),
       new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.DI)], _callerSaved, Result: Reg.AX),
    ["rt_arr_tagdesc"] = new("rt_arr_tagdesc",
      [new(ArgKind.Pointer, Reg.SI, Reg.DX), new(ArgKind.Word, Reg.BX),
       new(ArgKind.Word, Reg.CX), new(ArgKind.Word, Reg.DI)], _callerSaved, Result: Reg.AX),
    // "SortNum / ScanNum take all parameters from memory; ScanNum returns the 1-based relative
    // position in AX (0 = no match)" - DosRuntime.ArrayNum's own ABI note, and rt_sortstr / rt_scanstr
    // say the same for the string array in DosRuntime.Strings2. Every one of the four saves and
    // restores each register it touches apart from the answer.
    // Dynamic array storage. The runtime's allocator is a bump allocator over the far array heap and
    // has always taken its size as a 32-bit BYTE COUNT in DX:AX - so the IR declares these in bytes
    // too, and does the count * elementSize multiply itself (IrLowering.ArrayBytes explains why that
    // is the right place for it, and why it must be a 32-bit multiply). The pair therefore maps
    // straight across with no arithmetic in the table, which is the only kind of mapping this table
    // can express.
    //
    // The _ptr entries are the exception, and deliberately so: their element is a TARGET pointer,
    // whose size the front end has no business knowing, so they take a COUNT and the runtime scales
    // it. On this target that scaling is a 32-bit doubling, which is the whole body of the shim.
    //
    //   "rt_arr_alloc:     DX:AX = byte count  -> AX = offset within rt_arrseg (zero-filled)"
    //   "rt_arr_alloc_ptr: DX:AX = element count -> the same, for a block of target pointers"
    ["rt_arr_alloc"] = new("rt_arr_alloc", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_arr_alloc_ptr"] = new("rt_arr_alloc_ptr", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),

    //   "rt_arr_realloc: BX = old block, CX = old byte count, DX:AX = new byte count -> AX = new block"
    //
    // The OLD count goes over as its low half alone (ArgKind.LowWord), because a block that was
    // allocated is under 64 KiB by construction - rt_arr_alloc refuses anything else. The NEW count's
    // high half is NOT droppable: it is the overflow the allocator turns into Error 7, so it has to
    // reach it, which is why that one is a real pair.
    ["rt_arr_realloc"] = new("rt_arr_realloc",
      [new(ArgKind.Word, Reg.BX), new(ArgKind.LowWord, Reg.CX), new(ArgKind.Pair, Reg.AX, Reg.DX)],
      _callerSaved, Result: Reg.AX),
    ["rt_arr_realloc_ptr"] = new("rt_arr_realloc_ptr",
      [new(ArgKind.Word, Reg.BX), new(ArgKind.LowWord, Reg.CX), new(ArgKind.Pair, Reg.AX, Reg.DX)],
      _callerSaved, Result: Reg.AX),

    //   "rt_arr_free: AX = block offset, CX = byte count (no-op unless topmost)"
    //
    // The size travels with the pointer because a bump allocator needs it: "is this block on top" is
    // offset + bytes == top, and nothing else in the runtime remembers how big a block was. The high
    // half is dropped for the reason above.
    ["rt_arr_free"] = new("rt_arr_free",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.LowWord, Reg.CX)], _callerSaved),
    ["rt_arr_free_ptr"] = new("rt_arr_free_ptr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.LowWord, Reg.CX)], _callerSaved),

    // The MEMORY-MODEL array classes, transcribed from the ABI block at the head of
    // DosRuntime.Ems.cs. HUGE takes conventional memory from DOS 48h and is addressed by stepping
    // the segment; VIRTUAL/EMS/XMS take EMS pages and are addressed through the page frame.
    //
    //   HugeAlloc: DX:AX = byte count -> AX = segment      HugeFree: AX = segment (0 ok)
    //   HugeZero:  AX = segment, CX:BX = byte count
    //   EmsAlloc:  DX:AX = byte count -> AX = handle       EmsFree:  DX = handle (0 ok)
    //   EmsFrame:  -> AX = page-frame segment              EmsZero:  DX = handle, CX:BX = byte count
    //   EmsMap2:   DX = handle, BX = logical page          EmsFre:   -> DX:AX = free EMS bytes
    //
    // The byte counts are real PAIRS rather than LowWord: a HUGE array is over 64 KiB by the time it
    // is worth declaring one, which is the whole point of the class.
    ["rt_huge_alloc"] = new("rt_hugealloc", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_huge_free"] = new("rt_hugefree", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_huge_zero"] = new("rt_hugezero",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.BX, Reg.CX)], _callerSaved),
    ["rt_ems_alloc"] = new("rt_emsalloc", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_ems_free"] = new("rt_emsfree", [new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_ems_frame"] = new("rt_emsframe", [], _callerSaved, Result: Reg.AX),
    ["rt_ems_zero"] = new("rt_emszero",
      [new(ArgKind.Word, Reg.DX), new(ArgKind.Pair, Reg.BX, Reg.CX)], _callerSaved),
    ["rt_ems_map2"] = new("rt_emsmap2",
      [new(ArgKind.Word, Reg.DX), new(ArgKind.Word, Reg.BX)], _callerSaved),
    // FRE(-11): the free EMS byte count, a LONG, so the answer is the DX:AX pair
    ["rt_ems_fre"] = new("rt_emsfre", [], _callerSaved, Result: Reg.AX, Answer: ResultKind.Pair),

    ["rt_array_sort_num"] = new("rt_sortnum", [], _callerSaved),
    ["rt_array_scan_num"] = new("rt_scannum", [], _callerSaved, Result: Reg.AX),
    ["rt_array_sort_str"] = new("rt_sortstr", [], _callerSaved),
    ["rt_array_scan_str"] = new("rt_scanstr", [], _callerSaved, Result: Reg.AX),
    // FIELD, transcribed from the ABI block at the head of DosRuntime.Fields.cs:
    //   FieldAdd: AX = PB file number, CX = width, BX = address of the string handle cell
    //   FieldGet / FieldPut: AX = PB file number - scatters / gathers one record through the fields
    //
    // The cell address is an ArgKind.Offset, and that is a correctness requirement rather than an
    // optimization. rt_fldadd KEEPS the address in a table and rt_fld_walk dereferences it later,
    // through DS, with nothing to say which segment it came from - so only a module-level cell may
    // ever be registered. An Offset refuses everything else; a Pointer would have handed over a
    // frame displacement read as though it were a data one.
    ["rt_field_add"] = new("rt_fldadd",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX), new(ArgKind.Offset, Reg.BX)], _callerSaved),
    ["rt_field_get"] = new("rt_fldget", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_field_put"] = new("rt_fldput", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    // "FSetPos: AX = PB file number, DX:CX = 1-based position (record number when the file's
    // reclen > 1, byte position otherwise)". This is the positioning a BARE GET/PUT does, and it is
    // NOT the one SEEK does - rt_fseekstmt applies PB's statement-level numbering on top. The high
    // half matters, so the position takes a register pair.
    ["rt_file_setpos"] = new("rt_fsetpos",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.CX, Reg.DX)], _callerSaved),

    // "LSET/RSET into a dynamic string: AX = target handle (mutated in place, NOT consumed),
    // DX = value handle (consumed), BL = 0 left / 1 right justified". The target keeps its handle
    // and its length, so the IR passes the cell's raw handle rather than a borrowed copy - a copy
    // would be justified into and thrown away, leaving the variable untouched.
    ["rt_str_justify"] = new("rt_justify",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX), new(ArgKind.Word, Reg.BX)], _callerSaved),

    // CHAIN / RUN, transcribed from the ABI block at the head of DosRuntime.Chain.cs. The handoff
    // file's own DOS handle lives in rt_chfh, so none of these carries it:
    //   ChainOpenWrite/ChainOpenRead: create/open PBCHAIN.$$$ (read: AX = 1 ok, 0 none)
    //   ChainWrite/ChainRead: DS:DX buffer, CX bytes
    //   ChainWriteStr: AX = string handle (KEPT); ChainReadStr: -> AX = a fresh handle
    //   ChainCloseDelete: AL = 1 to unlink after closing
    //   ChainExec: AX = target path handle (consumed) - EXECs and never returns
    //
    // The buffer is an ArgKind.Offset rather than a Pointer because these two routines assume DS
    // and take no segment: an Offset REFUSES anything but a module-level global, which is exactly
    // the storage that assumption holds for. A frame slot would have assembled a plausible DS-based
    // address out of an SS displacement and streamed the wrong bytes without a word of complaint.
    ["rt_chain_open_write"] = new("rt_chopenw", [], _callerSaved),
    ["rt_chain_open_read"] = new("rt_chopenr", [], _callerSaved, Result: Reg.AX),
    ["rt_chain_write"] = new("rt_chwrite",
      [new(ArgKind.Offset, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved),
    ["rt_chain_read"] = new("rt_chread",
      [new(ArgKind.Offset, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved),
    ["rt_chain_write_str"] = new("rt_chwstr", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_chain_read_str"] = new("rt_chrstr", [], _callerSaved, Result: Reg.AX),
    ["rt_chain_close"] = new("rt_chclose", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_chain_exec"] = new("rt_chainexec", [new(ArgKind.Word, Reg.AX)], _callerSaved),

    ["rt_str_from_i16"] = new("rt_str_i16", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    // PRINT of the remaining numeric widths, transcribed from CodeGenerator.Io.cs's EmitPrintValue -
    // the direct emitter's own dispatch, which is the only thing that says which formatter a PB type
    // is supposed to reach.
    //
    // EXTENDED goes through the DOUBLE formatter: the runtime's print entries share a body and
    // differ only in the significant digits they set, so the NAME picks the rendering while the value
    // arrives on ST(0) at the x87's own width either way. There is no rt_print_f80 and there should
    // not be one.
    ["rt_print_ext"] = new("rt_print_f64", [new(ArgKind.St0, default)], _callerSaved),
    ["rt_fprint_ext"] = new("rt_print_f64",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.St0, default)], _callerSaved, FileSelect: true),

    // a BYTE is 0..255, so the signed 16-bit printer renders it correctly - the emitter falls into
    // the same case for it
    ["rt_print_u8"] = new("rt_print_i16", [new(ArgKind.Word, Reg.AX)], _callerSaved),
    ["rt_fprint_u8"] = new("rt_print_i16",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.AX)], _callerSaved, FileSelect: true),

    // a WORD needs the zero-extension: "XOR DX,DX / CALL PrintInt32", or 65535 prints as -1
    ["rt_print_u16"] = new("rt_print_i32", [new(ArgKind.ZeroPair, Reg.AX, Reg.DX)], _callerSaved),
    ["rt_fprint_u16"] = new("rt_print_i32",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.ZeroPair, Reg.AX, Reg.DX)], _callerSaved, FileSelect: true),

    // "ST(1)^ST(0) -> ST(0) (both operands popped, result pushed)". Two St0 arguments arrive in
    // exactly that order - the base is pushed first, so it ends up in ST(1) with the exponent on top,
    // which is the convention verbatim. The routine touches no general register at all (it is twenty
    // x87 instructions and a RET), but the clobber set stays the full caller-saved file: over-claiming
    // costs the allocator a spill, under-claiming miscompiles.
    ["llvm.pow.f32"] = new("rt_pow", [new(ArgKind.St0, default), new(ArgKind.St0, default)],
      _callerSaved, Answer: ResultKind.St0),
    ["llvm.pow.f64"] = new("rt_pow", [new(ArgKind.St0, default), new(ArgKind.St0, default)],
      _callerSaved, Answer: ResultKind.St0),
    ["llvm.pow.f80"] = new("rt_pow", [new(ArgKind.St0, default), new(ArgKind.St0, default)],
      _callerSaved, Answer: ResultKind.St0),

    // FIX scaling, both directions, on ST(0) and answering there: rt_fixdn divides by ten to the
    // pbvFixDigits power (what reading a FIX cell means) and rt_fixup multiplies and rounds to the
    // nearest integer (what writing one means). The exponent is a RUNTIME cell, which is the whole
    // reason these are calls: a compile-time divide would be right only until pbvFixDigits changed.
    ["rt_fix_down"] = new("rt_fixdn", [new(ArgKind.St0, default)], _callerSaved, Answer: ResultKind.St0),
    ["rt_fix_up"] = new("rt_fixup", [new(ArgKind.St0, default)], _callerSaved, Answer: ResultKind.St0),

    // PRINT USING's numeric field. "DX:AX = scaled value, CH = field width (chars incl. point),
    // CL = decimals" - and bit 7 of CL is the thousands-grouping flag, which is why the lowering
    // hands the whole packed word rather than two numbers (Runtime.UsingFormat.Field.Spec). The
    // value arrives ALREADY SCALED by ten to the decimal count and already rounded to a 32-bit
    // integer, exactly as the direct emitter's FMUL/FISTP pair leaves it: rt_usefmt renders digits
    // and places a point, it does no arithmetic of its own.
    ["rt_using_field"] = new("rt_usefmt",
      [new(ArgKind.Pair, Reg.AX, Reg.DX), new(ArgKind.Word, Reg.CX)], _callerSaved),
    ["rt_fusing_field"] = new("rt_usefmt",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Pair, Reg.AX, Reg.DX), new(ArgKind.Word, Reg.CX)],
      _callerSaved, FileSelect: true),

    // USING$: the SAME field emission as PRINT USING, run with the print routines pointed at
    // rt_capbuf instead of at a file handle. Neither takes an argument; rt_capoff answers the
    // captured bytes as a string handle in AX, which is what makes USING$ a string expression rather
    // than a statement (DosRuntime.Capture.cs).
    ["rt_capture_begin"] = new("rt_capon", [], _callerSaved),
    ["rt_capture_end"] = new("rt_capoff", [], _callerSaved, Result: Reg.AX),

    // LPRINT: point the console routines at the printer for the length of one statement, and back
    // at the screen after it. Neither takes an argument nor touches a register - see
    // DosRuntime.Printer.cs for why they are routines at all rather than the four inline MOVs the
    // direct emitter writes.
    ["rt_lprint_on"] = new("rt_lpon", [], _callerSaved),
    ["rt_lprint_off"] = new("rt_lpoff", [], _callerSaved),

    // "rt_tab: CX = 1-based target column; spaces forward only", and rt_spc the same shape for a count
    ["rt_print_tab"] = new("rt_tab", [new(ArgKind.Word, Reg.CX)], _callerSaved),
    ["rt_fprint_tab"] = new("rt_tab",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, FileSelect: true),
    ["rt_print_spc"] = new("rt_spc", [new(ArgKind.Word, Reg.CX)], _callerSaved),
    ["rt_fprint_spc"] = new("rt_spc",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, FileSelect: true),

    // "PrintInt64: ST0 = integral value (popped)". A DWORD reaches it zero-extended through the frame,
    // which is how it prints its full unsigned range
    ["rt_print_u32"] = new("rt_print_i64", [new(ArgKind.ZeroExtendedQwordSt0, default)], _callerSaved),
    ["rt_fprint_u32"] = new("rt_print_i64",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.ZeroExtendedQwordSt0, default)], _callerSaved, FileSelect: true),

    // Genuine PBC 3.50 routes QUAD through the 15-digit DOUBLE formatter, so large values appear in
    // E notation. The IR keeps the value i64; the selector stages a constant bit-for-bit and FILDs it
    // so the formatter receives the same exact x87 integer as the direct emitter. Non-constant i64
    // values still decline until the machine IR has a general 64-bit representation.
    ["rt_print_i64"] = new("rt_print_f64", [new(ArgKind.SignedQwordSt0, default)], _callerSaved),
    ["rt_fprint_i64"] = new("rt_print_f64",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.SignedQwordSt0, default)], _callerSaved, FileSelect: true),

    // nothing else in the print family is listed

    // a BYTE is 0..255, so the signed 16-bit renderer answers correctly - the same reasoning
    // rt_print_u8 above rests on, and the same case the direct emitter falls into for it
    ["rt_str_from_u8"] = new("rt_str_i16", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    // A WORD does NOT go through rt_str_i16 - that entry opens with a CWD, so 65535 would render as
    // -1. It takes the 32-bit one with a zeroed high half, which is the same ZeroPair the print side
    // uses for rt_print_u16 and the same XOR DX,DX the direct emitter writes.
    ["rt_str_from_u16"] = new("rt_str_i32", [new(ArgKind.ZeroPair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    ["rt_str_from_i32"] = new("rt_str_i32", [new(ArgKind.Pair, Reg.AX, Reg.DX)], _callerSaved, Result: Reg.AX),
    // ...and for the same reason there is no rt_str_from_u32 through rt_str_i32: that entry renders
    // DX:AX SIGNED, so 4294967295 would come out as -1. A DWORD goes through the 64-bit one with a
    // zeroed high half, which is the trap ArgKind.ZeroExtendedQwordSt0 was introduced for on the
    // print side and the same four MOVs and FILD the direct emitter writes here.
    ["rt_str_from_u32"] = new("rt_str_i64",
      [new(ArgKind.ZeroExtendedQwordSt0, default)], _callerSaved, Result: Reg.AX),
    ["rt_str_from_single"] = new("rt_str_f32", [new(ArgKind.St0, default)], _callerSaved, Result: Reg.AX),
    ["rt_str_from_double"] = new("rt_str_f64", [new(ArgKind.St0, default)], _callerSaved, Result: Reg.AX),
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
