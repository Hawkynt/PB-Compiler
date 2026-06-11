using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Emits the DOS runtime kernel into the program image. Register conventions
/// (callee preserves everything it does not return in):
///   PrintStr:     DS:SI = text, CX = length (writes to the current output
///                 handle rt_curout; in capture mode appends to rt_capbuf)
///   PrintInt16:   AX = signed value      (PB style: sign/space prefix, trailing space)
///   PrintInt32:   DX:AX = signed value   (same formatting)
///   PrintSingle:  value on ST(0), popped (7 significant digits)
///   PrintDouble:  value on ST(0), popped (15 significant digits)
///   PrintNewLine: - (resets the print column)
///   PrintZone:    advances to the next 14-column print zone
///   Pow:          ST(1)=base, ST(0)=exponent -> result ST(0)
///   LongMul/Div/Mod: left DX:AX, right CX:BX -> result DX:AX
///   Exit:         AL = exit code
/// String/file/array routine conventions are documented in the partials
/// (DosRuntime.Strings.cs, DosRuntime.Files.cs, DosRuntime.Arrays.cs).
/// Memory model: CS=DS=SS single segment, SP grows down from 0xFFFE; the far
/// string heap lives at CS+0x1000, the far array heap at CS+0x2000 (one full
/// 64 KiB segment each, reserved via MinExtraParagraphs).
/// </summary>
public sealed partial class DosRuntime {

  /// <summary>Paragraphs to reserve beyond the 64 KiB main segment: string heap + array heap.</summary>
  public const int ExtraHeapParagraphs = 0x2000;

  /// <summary>
  /// Dialect whose runtime behavior to replicate. Turbo Basic formats every
  /// float with 16 significant digits, zero-padded three-digit exponents
  /// ("1E+016") and switches to exponent notation below 0.1; its VAL wraps
  /// radix values to 16 bits. Label names are dialect-independent.
  /// </summary>
  public Syntax.Dialect Dialect { get; set; } = Syntax.Dialect.Pb35;

  public Label PrintStr { get; private set; } = null!;
  public Label PrintInt16 { get; private set; } = null!;
  public Label PrintInt32 { get; private set; } = null!;
  public Label PrintSingle { get; private set; } = null!;
  public Label PrintDouble { get; private set; } = null!;
  public Label PrintNewLine { get; private set; } = null!;
  public Label PrintZone { get; private set; } = null!;
  public Label Pow { get; private set; } = null!;
  public Label Floor { get; private set; } = null!;
  public Label Trunc { get; private set; } = null!;
  public Label LongMul { get; private set; } = null!;
  public Label LongDiv { get; private set; } = null!;
  public Label LongMod { get; private set; } = null!;
  public Label Exit { get; private set; } = null!;

  private Label _numBuffer = null!;
  private Label _scratch = null!;

  /// <summary>Emits the entry stub: segment setup, heap segment registers, FPU init, jump to user main.</summary>
  public void EmitEntry(Assembler asm, Label userMain) {
    // CS = DS = SS already arranged by the MZ header (SS=0 reloc, SP=0xFFFE);
    // DOS loads DS/ES = PSP, so re-point them at our single segment. The PSP
    // segment is kept for COMMAND$/ENVIRON$.
    asm.Push(Reg.DS);
    asm.Mov(Reg.AX, Reg.CS);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_pspseg")), Reg.AX);
    asm.Mov(Reg.AX, Reg.CS);
    asm.Mov(Mem.Word(asm.Lbl("rt_defseg")), Reg.AX);
    asm.Add(Reg.AX, 0x1000);
    asm.Mov(Mem.Word(asm.Lbl("rt_strseg")), Reg.AX);
    asm.Add(Reg.AX, 0x1000);
    asm.Mov(Mem.Word(asm.Lbl("rt_arrseg")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 1);
    this.EmitInternalsInit(asm);
    asm.Finit();
    asm.Jmp(userMain);
  }

  /// <summary>
  /// Code sections in canonical order. Each is self-contained (no fall-through
  /// across sections), so the pb36 runtime trimmer can emit any subset; the
  /// untrimmed emission iterates all of them in this exact order.
  /// </summary>
  internal (string Name, Action<Assembler> Emit)[] ProcedureSections() => [
    ("exit", this.EmitExit),
    ("errors", this.EmitErrors),
    ("print_str", this.EmitPrintStr),
    ("print_nl", this.EmitPrintNewLine),
    ("print_zone", this.EmitPrintZone),
    ("print_i16", this.EmitPrintInt16),
    ("print_i32", this.EmitPrintInt32),
    ("print_flt", this.EmitPrintFloat),
    ("pow", this.EmitPow),
    ("rounding", this.EmitRounding),
    ("long_helpers", this.EmitLongHelpers),
    ("strings", this.EmitStringProcedures),
    ("strings2", this.EmitString2Procedures),
    ("files", this.EmitFileProcedures),
    ("arrays", this.EmitArrayProcedures),
    ("lowlevel", this.EmitLowLevelProcedures),
    ("misc", this.EmitMiscProcedures),
    ("misc2", this.EmitMiscProcedures2),
    ("extras", this.EmitExtraProcedures),
    ("using_dyn", this.EmitUsingDyn),   // needs UseFmt (misc) and the string kernel
    ("quad", this.EmitQuadProcedures),
    ("ems", this.EmitEmsProcedures),
    ("fields", this.EmitFieldProcedures),
    ("chain", this.EmitChainProcedures),
  ];

  /// <summary>
  /// Emits the runtime procedures; call once between entry stub and user code.
  /// <paramref name="filter"/> selects sections (pb36 trimming; null = all),
  /// <paramref name="onSection"/> reports each emitted section's byte range.
  /// </summary>
  public void EmitProcedures(Assembler asm, Func<string, bool>? filter = null, Action<string, int, int>? onSection = null) {
    this._numBuffer = asm.Lbl("rt_numbuf");
    this._scratch = asm.Lbl("rt_scratch");
    this._asmStrTab = asm.Lbl("rt_strtab"); // sections referencing descriptors may be emitted without "strings"
    foreach (var (name, emit) in this.ProcedureSections()) {
      if (filter != null && !filter(name))
        continue;
      var start = asm.Position;
      emit(asm);
      onSection?.Invoke(name, start, asm.Position);
    }
  }

  /// <summary>Property name -> runtime label name, learned once from a throwaway emission (cannot drift).</summary>
  private static readonly Lazy<IReadOnlyDictionary<string, string>> _labelNames = new(() => {
    var probe = new Assembler();
    var reference = new DosRuntime();
    reference.EmitEntry(probe, probe.DefineLabel());
    reference.EmitProcedures(probe);
    reference.EmitConstants(probe);
    reference.EmitData(probe);

    var names = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var property in typeof(DosRuntime).GetProperties().Where(p => p.PropertyType == typeof(Label))) {
      var bound = (Label?)property.GetValue(reference)
        ?? throw new InvalidOperationException($"runtime label {property.Name} was never assigned");
      names[property.Name] = bound.Name ?? throw new InvalidOperationException($"runtime label {property.Name} is unnamed");
    }
    return names;
  });

  /// <summary>
  /// pb36 deferred emission: assigns every public runtime label property its
  /// named (still unbound) label in <paramref name="asm"/> WITHOUT emitting
  /// anything - user code can then reference the runtime, and the trimmed
  /// <see cref="EmitProcedures"/>/<see cref="EmitData"/> later bind exactly
  /// the names in use.
  /// </summary>
  public void BindDeferred(Assembler asm) {
    ArgumentNullException.ThrowIfNull(asm);
    foreach (var property in typeof(DosRuntime).GetProperties().Where(p => p.PropertyType == typeof(Label)))
      property.SetValue(this, asm.Lbl(_labelNames.Value[property.Name]));
  }

  /// <summary>
  /// Unit mode (<c>$COMPILE UNIT</c>): instead of emitting the runtime, every
  /// public runtime label becomes an external symbol with the name the real
  /// runtime would bind it to - the linker resolves them against the main
  /// image's export table. The names are learned from a throwaway emission so
  /// they can never drift from the actual runtime.
  /// </summary>
  public void BindExternal(Assembler asm) {
    ArgumentNullException.ThrowIfNull(asm);
    var probe = new Assembler();
    var reference = new DosRuntime();
    reference.EmitEntry(probe, probe.DefineLabel());
    reference.EmitProcedures(probe);
    reference.EmitConstants(probe);
    reference.EmitData(probe);

    foreach (var property in typeof(DosRuntime).GetProperties().Where(p => p.PropertyType == typeof(Label))) {
      var bound = (Label?)property.GetValue(reference)
        ?? throw new InvalidOperationException($"runtime label {property.Name} was never assigned");
      property.SetValue(this, asm.External(bound.Name ?? throw new InvalidOperationException($"runtime label {property.Name} is unnamed")));
    }
  }

  /// <summary>Data sections in canonical order (see <see cref="ProcedureSections"/>).</summary>
  internal (string Name, Action<Assembler> Emit)[] DataSections() => [
    ("core_data", this.EmitCoreData),
    ("str_cells", this.EmitStringCells),
    ("str_tab", this.EmitStringTable),
    ("file_data", this.EmitFileData),
    ("arr_data", this.EmitArrayData),
    ("lowlevel_data", this.EmitLowLevelData),
    ("misc_data", this.EmitMiscData),
    ("internals_data", this.EmitInternalsData),
    ("quad_data", this.EmitQuadData),
    ("ems_data", this.EmitEmsData),
    ("field_data", this.EmitFieldData),
    ("chain_data", this.EmitChainData),
  ];

  /// <summary>
  /// Emits runtime data cells; call while laying out the data area.
  /// <paramref name="filter"/>/<paramref name="onSection"/> as in <see cref="EmitProcedures"/>.
  /// </summary>
  public void EmitData(Assembler asm, Func<string, bool>? filter = null, Action<string, int, int>? onSection = null) {
    foreach (var (name, emit) in this.DataSections()) {
      if (filter != null && !filter(name))
        continue;
      var start = asm.Position;
      emit(asm);
      onSection?.Invoke(name, start, asm.Position);
    }
  }

  private void EmitCoreData(Assembler asm) {
    asm.Align(2);
    this._numBuffer = asm.MarkLabel("rt_numbuf");
    asm.Db(new byte[36]);
    this._scratch = asm.MarkLabel("rt_scratch");
    asm.Db(new byte[16]);
  }

  private void EmitExit(Assembler asm) {
    this.Exit = asm.MarkLabel("rt_exit");
    asm.Mov(Reg.AH, 0x4C);
    asm.Int(0x21);
  }

  /// <summary>Fatal runtime errors: message to stdout, exit code 3.</summary>
  private void EmitErrors(Assembler asm) {
    void Emit(string label, string message) {
      asm.MarkLabel(label);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl(label + "_msg")));
      asm.Mov(Reg.CX, message.Length + 2);
      asm.Mov(Reg.BX, 1);
      asm.Mov(Reg.AH, 0x40);
      asm.Int(0x21);
      asm.Mov(Reg.AL, (Imm)3);
      asm.Jmp(this.Exit);
    }

    Emit("rt_err_oss", "OUT OF STRING SPACE");
    Emit("rt_err_arr", "OUT OF ARRAY SPACE");

    // I/O errors are trappable: ERR 57, dispatched through the ON ERROR machinery
    asm.MarkLabel("rt_err_io");
    asm.Mov(Reg.AX, 57);
    asm.Jmp(asm.Lbl("rt_raise"));
  }

  private void EmitErrorMessages(Assembler asm) {
    void Emit(string label, string message) {
      asm.MarkLabel(label + "_msg");
      asm.Db(message);
      asm.Db(0x0D, 0x0A);
    }

    Emit("rt_err_oss", "OUT OF STRING SPACE");
    Emit("rt_err_arr", "OUT OF ARRAY SPACE");
    Emit("rt_err_run", "RUNTIME ERROR");
  }

  private void EmitPrintStr(Assembler asm) {
    this.PrintStr = asm.MarkLabel("rt_print_str");
    var done = asm.DefineLabel("rt_print_str_done");
    var capture = asm.DefineLabel("rt_print_str_cap");
    asm.Jcxz(done);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Jne(capture);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.DX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_curout")));
    asm.Mov(Reg.AH, 0x40);
    asm.Int(0x21);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Add(Mem.Word(asm.Lbl("rt_col")), Reg.CX);
    asm.MarkLabel(done);
    asm.Ret();

    // capture mode (STR$): append the bytes to rt_capbuf instead of writing
    asm.MarkLabel(capture);
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Add(Mem.Word(asm.Lbl("rt_caplen")), Reg.CX);
    asm.Lea(Reg.DI, Mem.At(Reg.DI, asm.Lbl("rt_capbuf")));
    var copy = asm.DefineLabel();
    asm.MarkLabel(copy);
    asm.Lodsb();
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.DI);
    asm.Loop(copy);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitPrintNewLine(Assembler asm) {
    this.PrintNewLine = asm.MarkLabel("rt_print_nl");
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_crlf")));
    asm.Mov(Reg.CX, 2);
    asm.Call(this.PrintStr);
    asm.Mov(Mem.Word(asm.Lbl("rt_col")), (Imm)0);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  /// <summary>Advances to the next 14-column print zone (PRINT comma separator).</summary>
  private void EmitPrintZone(Assembler asm) {
    this.PrintZone = asm.MarkLabel("rt_print_zone");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_col")));
    asm.Xor(Reg.DX, Reg.DX);
    asm.Mov(Reg.BX, 14);
    asm.Div(Reg.BX);
    asm.Mov(Reg.CX, 14);
    asm.Sub(Reg.CX, Reg.DX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_spaces")));
    asm.Call(this.PrintStr);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>AX (signed) -> "[ |-]digits[ ]" on the current output.</summary>
  private void EmitPrintInt16(Assembler asm) {
    this.PrintInt16 = asm.MarkLabel("rt_print_i16");
    asm.Push(Reg.DX);
    asm.Cwd();                       // sign-extend into DX
    asm.Call(asm.Lbl("rt_print_i32"));
    asm.Pop(Reg.DX);
    asm.Ret();
  }

  /// <summary>DX:AX (signed) -> "[ |-]digits[ ]" on the current output.</summary>
  private void EmitPrintInt32(Assembler asm) {
    this.PrintInt32 = asm.MarkLabel("rt_print_i32");
    var convert = asm.DefineLabel();
    var digitLoop = asm.DefineLabel();
    var positive = asm.DefineLabel();

    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DI);
    asm.Push(Reg.AX);
    asm.Push(Reg.DX);

    // SI walks backwards from the end of the number buffer; trailing space first
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 31));
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    // remember sign, take absolute value
    asm.Xor(Reg.DI, Reg.DI);              // DI = sign flag
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(positive);
    asm.Mov(Reg.DI, 1);
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel(positive);

    asm.MarkLabel(convert);
    asm.Mov(Reg.CX, 10);
    asm.MarkLabel(digitLoop);
    // DX:AX / 10 -> quotient DX:AX, remainder BX (classic two-step division)
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Div(Reg.CX);                 // AX = high quotient
    asm.Xchg(Reg.AX, Reg.BX);        // BX = high quotient, AX = low dividend
    asm.Div(Reg.CX);                 // AX = low quotient, DX = remainder
    asm.Add(Reg.DX, '0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
    asm.Mov(Reg.DX, Reg.BX);         // quotient back into DX:AX
    asm.Mov(Reg.BX, Reg.AX);
    asm.Or(Reg.BX, Reg.DX);          // BX is scratch here - reloaded next round
    asm.Jnz(digitLoop);
    var noSign = asm.DefineLabel();
    asm.Dec(Reg.SI);
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(asm.Lbl("rt_print_i32_out"));
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    asm.MarkLabel("rt_print_i32_out");
    // CX = end - SI
    asm.Mov(Reg.CX, Imm.OffsetOf(this._numBuffer, 32));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);

    asm.Pop(Reg.DX);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  /// <summary>
  /// Prints ST(0) (popped). Entry points select the significant-digit count:
  /// 7 for SINGLE, 15 for DOUBLE/EXT. Fixed notation while the value fits,
  /// otherwise exponent notation. PB-style sign/space prefix and trailing space.
  /// </summary>
  private void EmitPrintFloat(Assembler asm) {
    var turbo = this.Dialect.IsTurboBasic();
    var microsoft = this.Dialect.Family() == DialectFamily.Microsoft;
    this.PrintSingle = asm.MarkLabel("rt_print_f32");
    asm.Mov(Reg.BX, turbo ? 16 : 7);
    asm.Jmp(asm.Lbl("rt_print_flt"));

    this.PrintDouble = asm.MarkLabel("rt_print_f64");
    asm.Mov(Reg.BX, turbo || microsoft ? 16 : 15);

    asm.MarkLabel("rt_print_flt");
    // The number is decomposed in C-helper style entirely on the FPU:
    //   exp10 = 0; while |x| >= 10^digits: x /= 10, exp10++; while x != 0 && |x| < 10^(digits-1): x *= 10, exp10--
    //   mantissa = round(x) as 64-bit integer; then decimal point sits at (digits + exp10).
    var zero = asm.DefineLabel();
    var scaleDown = asm.DefineLabel();
    var scaleDownTest = asm.DefineLabel();
    var scaleUp = asm.DefineLabel();
    var scaleUpTest = asm.DefineLabel();
    var emit = asm.DefineLabel();

    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);

    // zero is special-cased (scaling would loop forever)
    asm.Ftst();
    asm.FstswAx();
    asm.Sahf();
    asm.Jz(zero);

    // DI = sign flag; work on |x| (MOV keeps the SAHF flags alive for the JNC)
    asm.Mov(Reg.DI, (Imm)0);
    asm.Jnc(asm.Lbl("rt_print_flt_abs")); // C0 set means x < 0 after FTST
    asm.Mov(Reg.DI, 1);
    asm.MarkLabel("rt_print_flt_abs");
    asm.Fabs();

    // CX = decimal exponent counter
    asm.Xor(Reg.CX, Reg.CX);

    // upper bound = 10^digits, kept in ST(1) while scaling down
    this.EmitLoadPow10(asm, Reg.BX);          // ST0 = 10^digits, ST1 = x
    asm.MarkLabel(scaleDownTest);
    asm.Fcom();                                // compare 10^digits with ST1? (see operand order below)
    asm.FstswAx();
    asm.Sahf();
    asm.Ja(asm.Lbl("rt_print_flt_belowupper")); // 10^digits > x -> done scaling down
    asm.MarkLabel(scaleDown);
    asm.Fxch();
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));  // x /= 10
    asm.Fxch();
    asm.Inc(Reg.CX);
    asm.Jmp(scaleDownTest);
    asm.MarkLabel("rt_print_flt_belowupper");
    asm.Fstp(St.St0);                            // drop upper bound, ST0 = x

    // lower bound = 10^(digits-1)
    asm.Dec(Reg.BX);
    this.EmitLoadPow10(asm, Reg.BX);
    asm.Inc(Reg.BX);
    asm.MarkLabel(scaleUpTest);
    asm.Fcom();
    asm.FstswAx();
    asm.Sahf();
    asm.Jbe(asm.Lbl("rt_print_flt_scaled"));    // 10^(digits-1) <= x -> done
    asm.MarkLabel(scaleUp);
    asm.Fxch();
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));  // x *= 10
    asm.Fxch();
    asm.Dec(Reg.CX);
    asm.Jmp(scaleUpTest);
    asm.MarkLabel("rt_print_flt_scaled");
    asm.Fstp(St.St0);                             // ST0 = scaled x in [10^(digits-1), 10^digits)

    // mantissa -> 64-bit integer at rt_scratch
    asm.Frndint();
    // rounding can carry into an extra digit (9999999.93 -> 10000000, seen
    // with SINGLE 1E37); renormalize so the leading digit survives
    this.EmitLoadPow10(asm, Reg.BX);
    asm.Fcom();
    asm.FstswAx();
    asm.Sahf();
    asm.Ja(asm.Lbl("rt_print_flt_nocarry"));    // 10^digits > mantissa -> fine
    asm.Fxch();
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Frndint();
    asm.Fxch();
    asm.Inc(Reg.CX);
    asm.MarkLabel("rt_print_flt_nocarry");
    asm.Fstp(St.St0);
    asm.Fistp(Mem.Qword(asm.Lbl("rt_scratch")));
    asm.Jmp(emit);

    asm.MarkLabel(zero);
    asm.Fstp(St.St0);
    asm.Push(Reg.AX);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Cwd();
    asm.Call(this.PrintInt32);
    asm.Pop(Reg.AX);
    asm.Jmp(asm.Lbl("rt_print_flt_done"));

    asm.MarkLabel(emit);
    this.EmitFloatDigits(asm);

    asm.MarkLabel("rt_print_flt_done");
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  /// <summary>Loads 10^(value of <paramref name="countReg"/>) onto the FPU stack (clobbers nothing).</summary>
  private void EmitLoadPow10(Assembler asm, Reg countReg) {
    // FLD1; loop: FMUL ten - simple and good enough for digit counts <= 16
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, countReg);
    asm.Fld1();
    asm.Jcxz(done);
    asm.MarkLabel(loop);
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Loop(loop);
    asm.MarkLabel(done);
    asm.Pop(Reg.CX);
  }

  /// <summary>
  /// Renders the 64-bit mantissa at rt_scratch with decimal point position
  /// digits+CX, BX = digit count, DI = sign. Trailing zeros after the point are
  /// trimmed; the PB trailing space is appended.
  /// </summary>
  private void EmitFloatDigits(Assembler asm) {
    // Extract BX decimal digits from the 64-bit integer by repeated div 10
    // (long division across 4 words), filling the number buffer right-to-left.
    var digitLoop = asm.MarkLabel("rt_fd_digits");
    _ = digitLoop;

    // SI -> digit write cursor (right to left), start leaving room for exponent suffix
    asm.Push(Reg.DI);                                  // sign flag - DI doubles as word pointer below
    asm.Push(Reg.BX);                                  // original digit count for the layout phase
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 24));
    asm.Push(Reg.BX);                                  // working digit countdown

    asm.MarkLabel("rt_fd_next");
    // divide the 4-word value at rt_scratch by 10, remainder -> AL
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, 4);
    asm.Mov(Reg.DI, Imm.OffsetOf(this._scratch, 6));   // highest word
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel("rt_fd_divword");
    asm.Mov(Reg.AX, Mem.Word(Reg.DI));
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, 10);
    asm.Div(Reg.BX);                                   // DX:AX / 10
    asm.Pop(Reg.BX);
    asm.Mov(Mem.Word(Reg.DI), Reg.AX);
    asm.Sub(Reg.DI, 2);
    asm.Loop(asm.Lbl("rt_fd_divword"));
    asm.Pop(Reg.CX);
    asm.Add(Reg.DX, '0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
    asm.Pop(Reg.BX);
    asm.Dec(Reg.BX);
    asm.Push(Reg.BX);
    asm.Test(Reg.BX, Reg.BX);
    asm.Jnz(asm.Lbl("rt_fd_next"));
    asm.Pop(Reg.BX);                                   // drop the exhausted countdown
    asm.Pop(Reg.BX);                                   // original digit count back for layout
    asm.Pop(Reg.DI);                                   // restore sign flag for layout

    // Now: buffer holds the digit run; insert the decimal point.
    // Point position from the left = digits + CX (CX may be negative).
    // For the bring-up runtime only the fixed-notation common case is rendered
    // when 0 < pointpos <= digits; otherwise falls back to integer-style output
    // followed by E+CX (exponent notation).
    this.EmitFloatLayout(asm);
  }

  private void EmitFloatLayout(Assembler asm) {
    // flat helper to keep registers straight:
    //   SI = first digit, BX = digit count, CX = exp10, DI = sign
    asm.Call(asm.Lbl("rt_fd_layout"));
    asm.Jmp(asm.Lbl("rt_print_flt_done")); // back into the shared epilogue (registers still pushed)

    asm.MarkLabel("rt_fd_layout");
    var outBuf = this._numBuffer;                    // reuse front of buffer for output
    var write = Reg.DI;                              // sign consumed first, then DI = write cursor
    _ = write;

    var noSign = asm.DefineLabel();
    var fixedNotation = asm.DefineLabel();

    // DX = point position from left = BX + CX
    asm.Mov(Reg.DX, Reg.BX);
    asm.Add(Reg.DX, Reg.CX);

    // write cursor starts at buffer[0]
    asm.Push(Reg.SI);                                // first digit pointer
    asm.Mov(Reg.SI, Imm.OffsetOf(outBuf));

    // sign or leading space
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(asm.Lbl("rt_fd_signdone"));
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');
    asm.MarkLabel("rt_fd_signdone");
    asm.Inc(Reg.SI);
    asm.Pop(Reg.DI);                                 // DI = read cursor (first digit)

    // PB notation rules (verified against genuine PBC 3.50):
    //   1 <= pointpos <= digits      -> fixed             "123.45"
    //   -digits <= pointpos <= 0     -> fraction fixed    ".00545" (no leading 0)
    //   otherwise                    -> exponent          "1E+20"
    asm.Cmp(Reg.DX, 1);
    asm.Jl(asm.Lbl("rt_fd_fracmaybe"));
    asm.Cmp(Reg.DX, Reg.BX);
    asm.Jle(fixedNotation);
    asm.Jmp(asm.Lbl("rt_fd_exp"));

    asm.MarkLabel("rt_fd_fracmaybe");
    if (this.Dialect.IsTurboBasic()) {
      // TB shows fractions plainly only down to 0.1 (pointpos 0); below that
      // it always switches to exponent notation (0.01 prints as "1E-002")
      asm.Cmp(Reg.DX, (Imm)0);
      asm.Jl(asm.Lbl("rt_fd_exp"));
    } else {
      asm.Mov(Reg.AX, Reg.BX);
      asm.Neg(Reg.AX);
      asm.Cmp(Reg.DX, Reg.AX);
      asm.Jl(asm.Lbl("rt_fd_exp"));        // more leading zeros than digits -> exponent
    }

    // fraction fixed: '.', -pointpos zeros, all digits; shared trailing trim
    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Neg(Reg.CX);
    asm.Jcxz(asm.Lbl("rt_fd_fraczdone"));
    asm.MarkLabel("rt_fd_fraczero");
    asm.Mov(Mem.Byte(Reg.SI), (byte)'0');
    asm.Inc(Reg.SI);
    asm.Loop(asm.Lbl("rt_fd_fraczero"));
    asm.MarkLabel("rt_fd_fraczdone");
    asm.Mov(Reg.CX, Reg.BX);
    asm.MarkLabel("rt_fd_fraccpy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_fraccpy"));
    asm.Pop(Reg.CX);
    asm.Jmp(asm.Lbl("rt_fd_trim"));

    asm.MarkLabel(fixedNotation);
    // copy DX integer digits, then '.', then the rest; afterwards trim trailing zeros/point
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.MarkLabel("rt_fd_intcopy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_intcopy"));
    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Mov(Reg.CX, Reg.BX);
    asm.Sub(Reg.CX, Reg.DX);
    asm.Jcxz(asm.Lbl("rt_fd_fraccopied"));
    asm.MarkLabel("rt_fd_fraccopy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_fraccopy"));
    asm.MarkLabel("rt_fd_fraccopied");
    asm.Pop(Reg.CX);
    // trim trailing zeros, then a trailing point
    asm.MarkLabel("rt_fd_trim");
    asm.Dec(Reg.SI);
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'0');
    asm.Je(asm.Lbl("rt_fd_trim"));
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'.');
    asm.Je(asm.Lbl("rt_fd_pointtrimmed"));
    asm.Inc(Reg.SI);
    asm.MarkLabel("rt_fd_pointtrimmed");
    // trailing space, emit
    asm.Mov(Mem.Byte(Reg.SI), (byte)' ');
    asm.Inc(Reg.SI);
    asm.Jmp(asm.Lbl("rt_fd_flush"));

    // exponent notation: d.ddddddE+nn
    asm.MarkLabel("rt_fd_exp");
    // first digit, point, remaining digits (trimmed), 'E', sign, 2-digit exponent
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.BX);
    asm.Dec(Reg.CX);
    asm.Jcxz(asm.Lbl("rt_fd_expdigitsdone"));
    asm.MarkLabel("rt_fd_expdigits");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_expdigits"));
    asm.MarkLabel("rt_fd_expdigitsdone");
    asm.Pop(Reg.CX);
    // trim all trailing zeros; a then-bare point is dropped too (PB: "1E+20")
    asm.MarkLabel("rt_fd_exptrim");
    asm.Dec(Reg.SI);
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'0');
    asm.Je(asm.Lbl("rt_fd_exptrim"));
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'.');
    asm.Je(asm.Lbl("rt_fd_exptrimmed"));
    asm.Inc(Reg.SI);
    asm.MarkLabel("rt_fd_exptrimmed");
    if (this.Dialect.Family() == DialectFamily.Microsoft) {
      // QB renders SINGLE exponents with 'E' and DOUBLE ones with 'D'
      // (1E+08 vs 1D+16); BX still holds the entry's digit count
      asm.Mov(Reg.AL, 'E');
      asm.Cmp(Reg.BX, (Imm)7);
      asm.Je(asm.Lbl("rt_fd_expmark"));
      asm.Mov(Reg.AL, 'D');
      asm.MarkLabel("rt_fd_expmark");
      asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    } else {
      asm.Mov(Mem.Byte(Reg.SI), (byte)'E');
    }
    asm.Inc(Reg.SI);
    // exponent value = pointpos - 1
    asm.Dec(Reg.DX);
    asm.Mov(Mem.Byte(Reg.SI), (byte)'+');
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(asm.Lbl("rt_fd_exppos"));
    asm.Mov(Mem.Byte(Reg.SI), (byte)'-');
    asm.Neg(Reg.DX);
    asm.MarkLabel("rt_fd_exppos");
    asm.Inc(Reg.SI);
    // decimal exponent digits: PB without leading zeros ("1E+7", "1E+16"),
    // TB zero-padded to three digits ("1E+007", "1E+016")
    asm.Mov(Reg.AX, Reg.DX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Mov(Reg.BX, 10);
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel("rt_fd_expdiv");
    asm.Xor(Reg.DX, Reg.DX);
    asm.Div(Reg.BX);                       // AX = quotient, DX = digit
    asm.Push(Reg.DX);
    asm.Inc(Reg.CX);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(asm.Lbl("rt_fd_expdiv"));
    // TB zero-pads exponents to three digits ("1E+016"), QB to two ("1E+07")
    var padWidth = this.Dialect.IsTurboBasic() ? 3 : this.Dialect.Family() == DialectFamily.Microsoft ? 2 : 0;
    if (padWidth > 0) {
      asm.Xor(Reg.DX, Reg.DX);
      asm.MarkLabel("rt_fd_exppad");
      asm.Cmp(Reg.CX, (Imm)padWidth);
      asm.Jae(asm.Lbl("rt_fd_exppop"));
      asm.Push(Reg.DX);
      asm.Inc(Reg.CX);
      asm.Jmp(asm.Lbl("rt_fd_exppad"));
    }
    asm.MarkLabel("rt_fd_exppop");
    asm.Pop(Reg.AX);
    asm.Add(Reg.AL, (byte)'0');
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Loop(asm.Lbl("rt_fd_exppop"));
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Mov(Mem.Byte(Reg.SI), (byte)' ');
    asm.Inc(Reg.SI);

    asm.MarkLabel("rt_fd_flush");
    // emit outBuf .. SI
    asm.Mov(Reg.CX, Reg.SI);
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);
    asm.Ret();
  }

  /// <summary>ST(1)^ST(0) -> ST(0)  (x^y = 2^(y*log2 x); both operands popped, result pushed).</summary>
  private void EmitPow(Assembler asm) {
    this.Pow = asm.MarkLabel("rt_pow");
    // y * log2(x)
    asm.Fxch();              // ST0=x, ST1=y
    asm.Fld1();
    asm.Fxch();              // ST0=x, ST1=1, ST2=y
    asm.Fyl2x();             // ST0=log2(x), ST1=y
    asm.Fmulp();             // ST0=y*log2(x)
    // 2^z = 2^int(z) * 2^frac(z); callable directly as rt_pow2 (ST0=z -> ST0=2^z)
    asm.MarkLabel("rt_pow2");
    asm.Fld(St.St0);
    asm.Frndint();           // ST0=int(z) (rounding mode is fine for typical exponents)
    asm.Fxch();
    asm.Fsub(St.St0, St.St1);  // ST0=frac(z), ST1=int(z)
    asm.F2xm1();
    asm.Fld1();
    asm.Faddp();             // ST0=2^frac
    asm.Fscale();            // ST0=2^frac * 2^int(ST1)
    asm.Fstp(St.St1);         // drop int(z)
    asm.Ret();
  }

  /// <summary>INT (floor) and FIX (truncate): FRNDINT under a temporary rounding mode.</summary>
  private void EmitRounding(Assembler asm) {
    void Emit(string label, int rcBits) {
      asm.MarkLabel(label);
      asm.Fnstcw(Mem.Word(this._scratch, 12));
      asm.Mov(Reg.AX, Mem.Word(this._scratch, 12));
      asm.Or(Reg.AX, 0x0C00);
      if (rcBits != 0x0C00) {
        asm.And(Reg.AX, ~0x0C00 & 0xFFFF);
        asm.Or(Reg.AX, rcBits);
      }
      asm.Mov(Mem.Word(this._scratch, 14), Reg.AX);
      asm.Fldcw(Mem.Word(this._scratch, 14));
      asm.Frndint();
      asm.Fldcw(Mem.Word(this._scratch, 12));
      asm.Ret();
    }

    this.Floor = asm.Lbl("rt_floor");
    Emit("rt_floor", 0x0400);  // RC=01: toward -infinity
    this.Trunc = asm.Lbl("rt_trunc");
    Emit("rt_trunc", 0x0C00);  // RC=11: toward zero
  }

  private void EmitLongHelpers(Assembler asm) {
    // DX:AX * CX:BX -> DX:AX (signed/unsigned identical for low 32 bits)
    this.LongMul = asm.MarkLabel("rt_lmul");
    asm.Push(Reg.SI);
    asm.Mov(Reg.SI, Reg.AX);   // SI = a.lo
    asm.Mov(Reg.AX, Reg.DX);   // a.hi
    asm.Mul(Reg.BX);           // a.hi * b.lo -> AX (low part contributes to high word)
    asm.Xchg(Reg.AX, Reg.CX);  // CX = partial high, AX = b.hi
    asm.Mul(Reg.SI);           // b.hi * a.lo
    asm.Add(Reg.CX, Reg.AX);   // sum of cross products
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mul(Reg.BX);           // a.lo * b.lo -> DX:AX
    asm.Add(Reg.DX, Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();

    // signed DX:AX / CX:BX -> DX:AX quotient; remainder in CX:BX
    this.LongDiv = asm.MarkLabel("rt_ldiv");
    this.EmitLongDivide(asm, wantRemainder: false);

    // signed DX:AX MOD CX:BX -> DX:AX remainder
    this.LongMod = asm.MarkLabel("rt_lmod");
    this.EmitLongDivide(asm, wantRemainder: true);
  }

  /// <summary>Shift-subtract 32/32 signed division (KISS bring-up version; 32 iterations).</summary>
  private void EmitLongDivide(Assembler asm, bool wantRemainder) {
    var suffix = wantRemainder ? "m" : "d";
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.BP);

    // sign bookkeeping: SI bit0 = negate quotient, bit1 = negate remainder (sign of dividend)
    asm.Xor(Reg.SI, Reg.SI);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(asm.Lbl($"rt_l{suffix}_p1"));
    asm.Mov(Reg.SI, 3);             // dividend negative: flip quotient and remainder
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_p1");
    asm.Test(Reg.CX, Reg.CX);
    asm.Jns(asm.Lbl($"rt_l{suffix}_p2"));
    asm.Xor(Reg.SI, 1);             // divisor negative flips just the quotient sign
    asm.Not(Reg.CX);
    asm.Not(Reg.BX);
    asm.Add(Reg.BX, 1);
    asm.Adc(Reg.CX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_p2");

    // 32-bit restoring division: quotient builds in DX:AX, remainder in DI:BP
    asm.Xor(Reg.DI, Reg.DI);
    asm.Xor(Reg.BP, Reg.BP);
    asm.Push(Reg.CX);               // divisor saved on stack (CX reused as counter)
    asm.Push(Reg.BX);
    asm.Mov(Reg.CX, 32);
    asm.MarkLabel($"rt_l{suffix}_loop");
    // shift remainder:dividend left one bit
    asm.Shl(Reg.AX, 1);
    asm.Rcl(Reg.DX, 1);
    asm.Rcl(Reg.BP, 1);
    asm.Rcl(Reg.DI, 1);
    // compare remainder DI:BP with divisor [sp+2]:[sp]
    asm.Mov(Reg.BX, Reg.SP);
    asm.Cmp(Reg.DI, Mem.Word(Reg.BX, 2));
    asm.Jb(asm.Lbl($"rt_l{suffix}_next"));
    asm.Ja(asm.Lbl($"rt_l{suffix}_sub"));
    asm.Cmp(Reg.BP, Mem.Word(Reg.BX));
    asm.Jb(asm.Lbl($"rt_l{suffix}_next"));
    asm.MarkLabel($"rt_l{suffix}_sub");
    asm.Sub(Reg.BP, Mem.Word(Reg.BX));
    asm.Sbb(Reg.DI, Mem.Word(Reg.BX, 2));
    asm.Or(Reg.AX, 1);              // set quotient bit (low bit just shifted in is 0)
    asm.MarkLabel($"rt_l{suffix}_next");
    asm.Loop(asm.Lbl($"rt_l{suffix}_loop"));
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);

    if (wantRemainder) {
      asm.Mov(Reg.AX, Reg.BP);
      asm.Mov(Reg.DX, Reg.DI);
      asm.Test(Reg.SI, 2);
      asm.Jz(asm.Lbl($"rt_l{suffix}_done"));
    } else {
      asm.Test(Reg.SI, 1);
      asm.Jz(asm.Lbl($"rt_l{suffix}_done"));
    }
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_done");
    asm.Pop(Reg.BP);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  /// <summary>Constant pool; emitted with the data area.</summary>
  public void EmitConstants(Assembler asm) {
    asm.MarkLabel("rt_crlf");
    asm.Db(0x0D, 0x0A);
    asm.MarkLabel("rt_spaces");
    asm.Db(new string(' ', 16));
    this.EmitErrorMessages(asm);
    asm.Align(2);
    asm.MarkLabel("rt_const_ten_m64");
    asm.Dq(10.0);
    asm.MarkLabel("rt_const_65536");
    asm.Dq(65536.0);
    asm.MarkLabel("rt_const_2p31");
    asm.Dq(2147483648.0);
    asm.MarkLabel("rt_const_2p32");
    asm.Dq(4294967296.0);
    this.EmitMiscConstants(asm);
  }
}
