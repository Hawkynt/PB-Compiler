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
    ["rt_array_sort_num"] = new("rt_sortnum", [], _callerSaved),
    ["rt_array_scan_num"] = new("rt_scannum", [], _callerSaved, Result: Reg.AX),
    ["rt_array_sort_str"] = new("rt_sortstr", [], _callerSaved),
    ["rt_array_scan_str"] = new("rt_scanstr", [], _callerSaved, Result: Reg.AX),

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

    // deliberately NO rt_str_from_u16 entry: rt_str_i16 opens with a CWD, so routing an unsigned
    // WORD through it would render 65535 as -1
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
}
