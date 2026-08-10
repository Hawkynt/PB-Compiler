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

    // "Rnd: -> ST0 = next SINGLE in [0,1)"
    ["rt_rnd"] = new("rt_rnd", [], _callerSaved, Answer: ResultKind.St0),

    // "RND(a, z): DX:AX=lower, CX:BX=upper -> DX:AX = lower + trunc(rnd * (upper-lower+1))"
    ["rt_rnd_range"] = new("rt_rndrange",
      [new(ArgKind.Pair, Reg.AX, Reg.DX), new(ArgKind.Pair, Reg.BX, Reg.CX)],
      _callerSaved, Result: Reg.AX),

    // LOF(n) and SEEK(n)/LOC(n): AX = the file number -> DX:AX
    ["rt_file_length"] = new("rt_lof", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),
    ["rt_file_pos"] = new("rt_fpos", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),

    // SEEK #n, p: AX = the file number, CX = the position
    ["rt_file_seek"] = new("rt_fseekstmt",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved),

    // PUT$ fh, s$: AX = the file number, DX = the handle. GET$ fh, n, s$: AX = file, CX = count -> AX
    ["rt_fput_str"] = new("rt_fputstr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.DX)], _callerSaved),
    ["rt_fget_str"] = new("rt_fgetstr",
      [new(ArgKind.Word, Reg.AX), new(ArgKind.Word, Reg.CX)], _callerSaved, Result: Reg.AX),

    // EOF(n): AX = the file number -> AX = PB's -1/0 truth
    ["rt_eof"] = new("rt_eof", [new(ArgKind.Word, Reg.AX)], _callerSaved, Result: Reg.AX),

    // CSRLIN -> AX = the 1-based cursor row; CONSIN / CONSOUT -> AX = -1 for a console, 0 redirected
    ["rt_csrlin"] = new("rt_csrlin", [], _callerSaved, Result: Reg.AX),
    ["rt_consin"] = new("rt_consin", [], _callerSaved, Result: Reg.AX),
    ["rt_consout"] = new("rt_consout", [], _callerSaved, Result: Reg.AX),
    // DEF SEG: the argument form stores the word, the bare form puts DS back
    ["rt_defseg_reset"] = new("rt_defsegreset", [], _callerSaved),

    // FREEFILE: no arguments -> AX = the lowest file number not in use
    ["rt_freefile"] = new("rt_freefile", [], _callerSaved, Result: Reg.AX),

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
