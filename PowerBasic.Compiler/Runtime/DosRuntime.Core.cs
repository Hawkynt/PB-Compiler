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

  /// <summary>
  /// <c>$COMPAT &lt;dialect&gt;</c> override: the dialect whose numeric PRINT formatting (significant
  /// digits, exponent <c>E</c>/<c>D</c> marker, exponent pad width, fixed/scientific threshold) the
  /// float formatter replicates, independent of <see cref="Dialect"/>. Null = format like
  /// <see cref="Dialect"/>. Set by codegen from <c>$COMPAT</c>, which the back-emitter emits so a
  /// transpiled-to-pb35 program still prints floats the way its source dialect did.
  /// </summary>
  public Syntax.Dialect? CompatDialect { get; set; }

  /// <summary>The dialect that governs runtime quirk emulation (PRINT formatting, the BASCOM ^Z-on-close): the <c>$COMPAT</c> override when set, else the compile dialect.</summary>
  private Syntax.Dialect EffectiveDialect => this.CompatDialect ?? this.Dialect;

  /// <summary>pb36 P3 gate: virtual BSS only applies to directly written images (the $LINK path lays out its own image).</summary>
  public bool EnableBss { get; set; }

  /// <summary>Legacy compatibility gate. New runtime code should query <see cref="Target"/>.</summary>
  public bool Cpu386 { get; set; }

  /// <summary>R1 ($OPTION VIDEO): console PRINT writes glyphs straight into B800 text memory - the classic direct-video speedup. The fast path handles the common straight text run (printables only, no line wrap); control characters, wraps and non-console handles keep the exact DOS path, and the BIOS cursor is resynced so mixed output stays coherent.</summary>
  public bool EnableFastVideo { get; set; }

  /// <summary>C6: on DOS 5+, link UMBs and allocate high-then-low so DOS 48h blocks (HUGE arrays) land in upper memory, freeing conventional; the previous link/strategy are restored at exit. Off by default; the optimizer turns it on for pb36 standalone images.</summary>
  public bool EnableUmb { get; set; }

  /// <summary>
  /// Target-aware forward byte copy of CX bytes (DS:SI -> ES:DI, DF clear). Long runs first consume
  /// the widest legal vector width, a 386+ target then consumes DWORDs, and the final <=3 bytes use
  /// MOVSB. Borrowed vector state is preserved by <see cref="EmitVectorCopyPrefix"/>.
  /// </summary>
  private void EmitRepMovsbWidened(Assembler asm) {
    this.EmitVectorCopyPrefix(asm);
    if (!this.Target.Has32BitGeneralPurpose) {
      asm.Rep();
      asm.Movsb();
      return;
    }
    asm.Push(Reg.CX);
    asm.Shr(Reg.CX, 2);
    asm.Rep();
    asm.Movsd();
    asm.Pop(Reg.CX);
    asm.And(Reg.CX, (Imm)3);
    asm.Rep();
    asm.Movsb();
  }

  /// <summary>
  /// Target-aware zero fill of CX words at ES:DI. Vectors consume the bulk, 386+ uses STOSD for
  /// pairs, and the odd word remains STOSW. AX/EAX end zeroed just like the legacy implementation.
  /// </summary>
  private void EmitRepStoswZeroWidened(Assembler asm) {
    this.EmitVectorZeroPrefix(asm, unitBytes: 2);
    if (!this.Target.Has32BitGeneralPurpose) {
      asm.Xor(Reg.AX, Reg.AX);
      asm.Rep();
      asm.Stosw();
      return;
    }
    var even = asm.DefineLabel();
    asm.Xor(Reg.EAX, Reg.EAX);
    asm.Shr(Reg.CX, 1);
    asm.Rep();
    asm.Stosd();
    asm.Jnc(even);
    asm.Stosw();
    asm.MarkLabel(even);
  }

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

  /// <summary>CEIL: FRNDINT toward +infinity, the same shape as INT and FIX next to it.</summary>
  public Label Ceil { get; private set; } = null!;

  /// <summary>ROUND: ST0 = value, CX = decimal places -> ST0 rounded, halves away from zero.</summary>
  public Label Round { get; private set; } = null!;
  public Label LongMul { get; private set; } = null!;
  public Label LongDiv { get; private set; } = null!;
  public Label LongMod { get; private set; } = null!;
  public Label LongDivU { get; private set; } = null!;
  public Label LongModU { get; private set; } = null!;
  public Label Exit { get; private set; } = null!;

  private Label _numBuffer = null!;
  private Label _scratch = null!;
  private readonly List<(Label Label, int Bytes)> _bss = [];

  /// <summary>
  /// pb36 P3: emits a zero-initialized blob as virtual BSS - the label points
  /// past the image end (assigned by <see cref="PlaceBss"/>) and the entry
  /// stub zeroes the whole region before any runtime store, so the bytes never
  /// hit the disk image. Other dialects keep the in-image zero bytes.
  /// </summary>
  private Label ZeroBlob(Assembler asm, string name, int bytes) {
    if (this.EnableBss) {
      var label = asm.Lbl(name);
      label.IsConstant = true;
      this._bss.Add((label, bytes));
      return label;
    }
    var bound = asm.MarkLabel(name);
    asm.Db(new byte[bytes]);
    return bound;
  }

  /// <summary>Lays the recorded BSS blobs out behind the image and patches the entry stub's zero range; call once after all emission.</summary>
  public void PlaceBss(Assembler asm) {
    ArgumentNullException.ThrowIfNull(asm);
    if (!this.EnableBss)
      return;
    var start = (asm.Position + 1) & ~1;
    var cursor = start;
    foreach (var (label, bytes) in this._bss) {
      label.Position = cursor;
      cursor += (bytes + 1) & ~1;
    }
    var offLabel = asm.Lbl("rt_bss_off");
    offLabel.IsConstant = true;
    offLabel.Position = start;
    var wordsLabel = asm.Lbl("rt_bss_words");
    wordsLabel.IsConstant = true;
    wordsLabel.Position = (cursor - start) / 2;
    var endLabel = asm.Lbl("rt_bss_end");
    endLabel.IsConstant = true;
    endLabel.Position = cursor;
  }

  /// <summary>Emits the entry stub: segment setup, heap segment registers, FPU init, jump to user main.</summary>
  public void EmitEntry(Assembler asm, Label userMain) {
    asm.Push(Reg.DS);
    asm.Mov(Reg.AX, Reg.CS);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Mov(Reg.ES, Reg.AX);
    if (this.EnableBss) {
      asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_bss_off")));
      asm.Mov(Reg.CX, Imm.OffsetOf(asm.Lbl("rt_bss_words")));
      this.EmitRepStoswZeroWidened(asm);
    }
    if (this.EnableUmb) {
      var noUmb = asm.DefineLabel();
      asm.Mov(Reg.AH, 0x30);
      asm.Int(0x21);
      asm.Cmp(Reg.AL, (Imm)5);
      asm.Jb(noUmb);
      asm.Mov(Reg.AX, 0x5802);
      asm.Int(0x21);
      asm.Jc(noUmb);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Mov(Mem.Word(asm.Lbl("rt_umb_oldlink")), Reg.AX);
      asm.Mov(Reg.AX, 0x5800);
      asm.Int(0x21);
      asm.Jc(noUmb);
      asm.Mov(Mem.Word(asm.Lbl("rt_umb_oldstrat")), Reg.AX);
      asm.Mov(Reg.AX, 0x5803);
      asm.Mov(Reg.BX, 1);
      asm.Int(0x21);
      asm.Mov(Reg.AX, 0x5801);
      asm.Mov(Reg.BX, 0x0080);
      asm.Int(0x21);
      asm.Mov(Mem.Word(asm.Lbl("rt_umb_active")), 1);
      asm.MarkLabel(noUmb);
    }
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
    ("memory", this.EmitMemoryProcedures),
    ("strings", this.EmitStringProcedures),
    ("binary_strings", this.EmitBinaryStringProcedures),
    ("strings2", this.EmitString2Procedures),
    ("trig", this.EmitTrig),
    ("strcmpeq", this.EmitStrCmpEq),
    ("charat", this.EmitCharAt),
    ("lastchar", this.EmitLastChar),
    ("scanchar", this.EmitScanChar),
    ("arraynum", this.EmitArrayNum),
    ("files", this.EmitFileProcedures),
    ("bsave", this.EmitBsaveProcedures),
    ("arrays", this.EmitArrayProcedures),
    ("array_alloc_nz", this.EmitArrayAllocNoZero),
    ("array_realloc", this.EmitArrayRealloc),
    ("array_ptr", this.EmitArrayPointerHelpers),
    ("lowlevel", this.EmitLowLevelProcedures),
    ("misc", this.EmitMiscProcedures),
    ("misc2", this.EmitMiscProcedures2),
    ("graphics", this.EmitGraphicsProcedures),
    ("paint", this.EmitPaint),
    ("getput", this.EmitGetPutProcedures),
    ("extras", this.EmitExtraProcedures),
    ("using_dyn", this.EmitUsingDyn),
    ("capture", this.EmitCaptureProcedures),
    ("quad", this.EmitQuadProcedures),
    ("ems", this.EmitEmsProcedures),
    ("fields", this.EmitFieldProcedures),
    ("chain", this.EmitChainProcedures),
  ];

  public void EmitProcedures(Assembler asm, Func<string, bool>? filter = null, Action<string, int, int>? onSection = null) {
    this._numBuffer = asm.Lbl("rt_numbuf");
    this._scratch = asm.Lbl("rt_scratch");
    this._asmStrTab = asm.Lbl("rt_strtab");
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

  public void BindDeferred(Assembler asm) {
    ArgumentNullException.ThrowIfNull(asm);
    foreach (var property in typeof(DosRuntime).GetProperties().Where(p => p.PropertyType == typeof(Label)))
      property.SetValue(this, asm.Lbl(_labelNames.Value[property.Name]));
  }

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
    this._numBuffer = this.ZeroBlob(asm, "rt_numbuf", 36);
    this._scratch = this.ZeroBlob(asm, "rt_scratch", 16);
  }

  private void EmitExit(Assembler asm) {
    this.Exit = asm.MarkLabel("rt_exit");
    if (this.EnableUmb) {
      var noRestore = asm.DefineLabel();
      asm.Cmp(Mem.Word(asm.Lbl("rt_umb_active")), (Imm)0);
      asm.Je(noRestore);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_umb_oldstrat")));
      asm.Mov(Reg.AX, 0x5801);
      asm.Int(0x21);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_umb_oldlink")));
      asm.Mov(Reg.AX, 0x5803);
      asm.Int(0x21);
      asm.MarkLabel(noRestore);
    }
    asm.Mov(Reg.AH, 0x4C);
    asm.Int(0x21);
  }

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
}
