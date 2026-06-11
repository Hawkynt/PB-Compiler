using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Translates a bound program into a 16-bit real-mode DOS executable.
/// Evaluation model: stack machine - INTEGER/WORD/BYTE in AX, LONG/DWORD in
/// DX:AX, floats on the x87 stack, dynamic strings as owned temp handles in AX,
/// machine stack for spills. Memory model: one segment (CS=DS=SS) with the data
/// area behind the code; far string heap at CS+0x1000, far array heap at
/// CS+0x2000. Procedures use BP frames (params at [BP+4..], locals/temps below
/// BP, RET n callee-clean); main gets a BP frame for statement temporaries too.
/// </summary>
public sealed partial class CodeGenerator(SemanticModel model) {

  private readonly Assembler _asm = new();
  private readonly DosRuntime _rt = new();
  private readonly Dictionary<VariableSymbol, Label> _variableSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, Label> _stringLiterals = new(StringComparer.Ordinal);
  private readonly Dictionary<ProcedureSymbol, Label> _procLabels = new(ReferenceEqualityComparer.Instance);
  private readonly List<(Label Slot, double Value)> _floatConstants = [];
  private readonly Stack<Label> _exitFor = new();
  private readonly Stack<Label> _exitDo = new();
  private readonly Stack<Label> _exitSelect = new();
  private Dictionary<string, Label> _userLabels = new(StringComparer.OrdinalIgnoreCase);
  private Label _scratch = null!;

  // current frame (main or procedure)
  private ProcedureSymbol? _currentProc;
  private Label _epilogue = null!;
  private Label _frameBytesLabel = null!;
  private Label _frameWordsLabel = null!;
  private int _frameLocalBytes;
  private int _tempBytes;
  private int _tempMax;

  /// <summary>Generated diagnostics for constructs the generator does not support yet.</summary>
  public List<Diagnostic> Errors { get; } = [];

  public byte[] EmitExecutable() => this.EmitExecutable([], []);

  /// <summary>
  /// Emits the program as a DOS MZ executable; <paramref name="units"/> link
  /// unconditionally, <paramref name="libraries"/> contribute units on demand
  /// (<c>$LINK</c>). Link failures surface as compile diagnostics.
  /// </summary>
  public byte[] EmitExecutable(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries) {
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(libraries);
    this._allowExternalCalls = units.Count > 0 || libraries.Count > 0;

    var asm = this._asm;
    var userMain = asm.DefineLabel("user_main");
    this._scratch = asm.DefineLabel("cg_scratch");

    this._rt.EmitEntry(asm, userMain);
    this._rt.EmitProcedures(asm);

    asm.MarkLabel(userMain);
    this.BeginFrame();
    this._trackResume = ContainsErrorHandling(model.MainBody);
    foreach (var statement in model.MainBody)
      this.EmitStatement(statement);

    // implicit END
    asm.Mov(Reg.AL, (Imm)0);
    asm.Jmp(this._rt.Exit);
    this.EndFrame();
    this._trackResume = false;

    foreach (var proc in model.Procedures.Values)
      if (!proc.IsExternal)
        this.EmitProcedure(proc);

    this.EmitFarThunks();
    this.EmitDataArea();

    var image = this._allowExternalCalls ? this.LinkImage(units, libraries) : asm.ToArray();
    if (image.Length == 0)
      return []; // link errors already reported

    var writer = new MzExeWriter(image) {
      EntrySegment = 0,
      EntryOffset = 0,
      StackSegment = 0,
      StackPointer = 0xFFFE,
      // grow the single segment to its full 64 KiB so data + stack always fit,
      // then reserve the far string and array heap segments behind it
      MinExtraParagraphs = (ushort)((0x10000 - image.Length % 0x10000 + 15) / 16 + DosRuntime.ExtraHeapParagraphs),
    };
    writer.AddRelocations(this._allowExternalCalls ? this._linkedSegmentSites : asm.SegmentRelocations);
    return writer.ToArray();
  }

  #region frames & temporaries

  /// <summary>
  /// Opens a BP frame. The frame size is not known until the body has been
  /// emitted, so the SUB SP immediate is a label whose "position" is patched
  /// to the final byte count by <see cref="EndFrame"/>.
  /// </summary>
  private void BeginFrame() {
    var asm = this._asm;
    this._frameBytesLabel = asm.DefineLabel();
    this._frameWordsLabel = asm.DefineLabel();
    // their "positions" are byte counts, not image offsets - never relocate
    this._frameBytesLabel.IsConstant = true;
    this._frameWordsLabel.IsConstant = true;
    this._tempBytes = 0;
    this._tempMax = 0;

    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Mov(Reg.CX, Imm.OffsetOf(this._frameBytesLabel));
    asm.Sub(Reg.SP, Reg.CX);
    // zero the whole frame: numeric locals start at 0, strings at handle 0
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    asm.Mov(Reg.DI, Reg.SP);
    asm.Mov(Reg.CX, Imm.OffsetOf(this._frameWordsLabel));
    asm.Xor(Reg.AX, Reg.AX);
    asm.Rep();
    asm.Stosw();
  }

  private void EndFrame() {
    var bytes = (this._frameLocalBytes + this._tempMax + 1) & ~1;
    this._frameBytesLabel.Position = bytes;
    this._frameWordsLabel.Position = bytes / 2;
    this._frameLocalBytes = 0;
  }

  /// <summary>Reserves a BP-relative scratch block; release in reverse order.</summary>
  private Mem AllocTemp(int bytes, OperandSize size = OperandSize.Word) {
    bytes = (bytes + 1) & ~1;
    this._tempBytes += bytes;
    this._tempMax = Math.Max(this._tempMax, this._tempBytes);
    return Mem.At(Reg.BP, -(this._frameLocalBytes + this._tempBytes)).WithSize(size);
  }

  private void ReleaseTemp(int bytes) => this._tempBytes -= (bytes + 1) & ~1;

  #endregion

  #region slots, literals & labels

  private Label SlotOf(VariableSymbol symbol) {
    if (!this._variableSlots.TryGetValue(symbol, out var label))
      this._variableSlots[symbol] = label = this._asm.DefineLabel($"v_{symbol.Name}_{this._variableSlots.Count}");
    return label;
  }

  private Label LiteralOf(string text) {
    if (!this._stringLiterals.TryGetValue(text, out var label))
      this._stringLiterals[text] = label = this._asm.DefineLabel($"s_{this._stringLiterals.Count}");
    return label;
  }

  private Label FloatConstOf(double value) {
    var slot = this._asm.DefineLabel($"f_{this._floatConstants.Count}");
    this._floatConstants.Add((slot, value));
    return slot;
  }

  private Label UserLabel(string name) {
    if (!this._userLabels.TryGetValue(name, out var label))
      this._userLabels[name] = label = this._asm.DefineLabel($"l_{name}");
    return label;
  }

  private Label ProcLabelOf(ProcedureSymbol proc) {
    if (!this._procLabels.TryGetValue(proc, out var label))
      // DECLAREd-but-undefined procedures resolve at link time by name
      this._procLabels[proc] = label = proc.IsExternal && this._allowExternalCalls
        ? this._asm.External(proc.Name)
        : this._asm.DefineLabel($"p_{proc.Name}");
    return label;
  }

  private void EmitDataArea() {
    var asm = this._asm;
    asm.Align(2);
    if (!this._isUnit) { // units import the runtime (and the main module's DATA pool) instead
      this._rt.EmitConstants(asm);
      this._rt.EmitData(asm);
      this.EmitDataPool();
    }

    asm.Align(2);
    asm.MarkLabel(this._scratch);
    asm.Db(new byte[12]);

    foreach (var (slot, value) in this._floatConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Dq(value);
    }

    foreach (var (slot, value) in this._quadConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Db([.. BitConverter.GetBytes(value)]);
    }

    foreach (var (text, label) in this._stringLiterals) {
      asm.MarkLabel(label);
      asm.Db(text);
    }

    foreach (var (symbol, label) in this._variableSlots) {
      asm.Align(2);
      asm.MarkLabel(label);
      asm.Db(new byte[Math.Max(symbol.Type.Size, 1)]);
    }

    foreach (var (symbol, label) in this._shadowDescriptors) {
      asm.Align(2);
      asm.MarkLabel(label);
      asm.Db(new byte[8 + ((ArrayType)symbol.Type).Rank * 4]);
    }
  }

  private void Unsupported(Statement s) => this.Errors.Add(new(s.Position, $"not yet generated: {(s is CommandStmt c ? $"command {c.Keyword}" : s.GetType().Name)}"));
  private void Unsupported(Expression e, string what) => this.Errors.Add(new(e.Position, $"not yet generated: {what}"));
  private void Unsupported(SourcePosition position, string what) => this.Errors.Add(new(position, $"not yet generated: {what}"));

  /// <summary>Replicates the binder's variable table key (name + canonical suffix text).</summary>
  private static string KeyOf(string name, TypeSuffix suffix) => name + suffix.KeyText();

  private VariableSymbol? LookupVariable(string name, TypeSuffix suffix) {
    var key = KeyOf(name, suffix);
    if (this._currentProc != null && this._currentProc.Variables.TryGetValue(key, out var local))
      return local;
    return model.ModuleVariables.GetValueOrDefault(key);
  }

  #endregion

  #region value categories

  /// <summary>
  /// Evaluation-register category. <see cref="ValueKind.Int64"/> (QUAD) values
  /// travel on the x87 stack like floats - the 64-bit mantissa holds the full
  /// integer range exactly - but print/store as integers.
  /// </summary>
  private enum ValueKind { Int16, Int32, Int64, Float, Str }

  private static ValueKind KindOf(PbType type) => type switch {
    ScalarType { IsFloat: true } => ValueKind.Float,
    ScalarType { ByteSize: <= 2 } => ValueKind.Int16,
    ScalarType { ByteSize: 8 } => ValueKind.Int64,
    ScalarType => ValueKind.Int32,
    PointerType => ValueKind.Int32,
    StringType or FixedStringType or FlexType or AsciizType => ValueKind.Str,
    _ => ValueKind.Int16,
  };

  #endregion

  #region statements

  private bool _trackResume;

  /// <summary>
  /// Emits one statement; inside scopes containing ON ERROR/RESUME every
  /// statement additionally records its own start and successor offsets so
  /// RESUME / RESUME NEXT can re-enter after an error unwound the stack.
  /// </summary>
  private void EmitStatement(Statement statement) {
    if (!this._trackResume || statement is LabelStmt or DataStmt or MetaStmt or EquateStmt or DefTypeStmt) {
      this.EmitStatementCore(statement);
      return;
    }
    var asm = this._asm;
    var start = asm.DefineLabel();
    var after = asm.DefineLabel();
    asm.MarkLabel(start);
    asm.Mov(Mem.Word(asm.Lbl("rt_resume")), Imm.OffsetOf(start));
    asm.Mov(Mem.Word(asm.Lbl("rt_resumenext")), Imm.OffsetOf(after));
    this.EmitStatementCore(statement);
    asm.MarkLabel(after);
  }

  private void EmitStatementCore(Statement statement) {
    var asm = this._asm;
    switch (statement) {
      case AssignStmt a:
        this.EmitAssign(a);
        break;

      case PrintStmt p:
        this.EmitPrint(p);
        break;

      case IfStmt i:
        this.EmitIf(i);
        break;

      case ForStmt f:
        this.EmitFor(f);
        break;

      case DoLoopStmt d:
        this.EmitDoLoop(d);
        break;

      case SelectStmt s:
        this.EmitSelect(s);
        break;

      case LabelStmt l:
        asm.MarkLabel(this.UserLabel(l.Name));
        break;

      case GotoStmt g:
        asm.Jmp(this.UserLabel(g.Target));
        break;

      case GosubStmt g:
        asm.Call(this.UserLabel(g.Target));
        break;

      case GotoPtrStmt gp:
        this.EmitGotoGosubPtr(gp.Pointer, isGosub: false);
        break;

      case GosubPtrStmt gsp:
        this.EmitGotoGosubPtr(gsp.Pointer, isGosub: true);
        break;

      case OnGotoStmt og:
        this.EmitOnGoto(og);
        break;

      case ReturnStmt { Target: null }:
        asm.Ret();
        break;

      case IncrDecrStmt id:
        this.EmitIncrDecr(id);
        break;

      case CallStmt c:
        this.EmitCallStatement(c);
        break;

      case ExitStmt e:
        this.EmitExit(e);
        break;

      case EndStmt e:
        if (e.ExitCode != null) {
          this.EmitExpression(e.ExitCode);
          this.Coerce(model.TypeOf(e.ExitCode), PbType.Integer, e.ExitCode);
        } else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Jmp(this._rt.Exit);
        break;

      case DimStmt dim:
        this.EmitDim(dim);
        break;

      case RedimStmt redim:
        this.EmitRedim(redim);
        break;

      case EraseStmt erase:
        this.EmitErase(erase);
        break;

      case MidAssignStmt mid:
        this.EmitMidAssign(mid);
        break;

      case AscAssignStmt ascAssign:
        this.EmitAscAssign(ascAssign);
        break;

      case StdOutStmt stdOut:
        this.EmitStdOut(stdOut);
        break;

      case StdInStmt stdIn:
        this.EmitStdIn(stdIn);
        break;

      case LsetRsetStmt ls:
        this.EmitLsetRset(ls);
        break;

      case OpenStmt open:
        this.EmitOpen(open);
        break;

      case CloseStmt close:
        this.EmitClose(close);
        break;

      case InputStmt input:
        this.EmitInput(input);
        break;

      case GetPutFileStmt gp:
        this.EmitGetPutFile(gp);
        break;

      case SeekStmt seek:
        this.EmitSeekStatement(seek);
        break;

      case SwapStmt sw:
        this.EmitSwap(sw);
        break;

      case DefSegStmt seg:
        this.EmitDefSeg(seg);
        break;

      case CallPtrStmt cp:
        this.EmitCallPtr(cp);
        break;

      case OnErrorStmt oe:
        this.EmitOnError(oe);
        break;

      case ResumeStmt rs:
        this.EmitResume(rs);
        break;

      case ErrorStmt err:
        this.EmitError(err);
        break;

      case ReadStmt read:
        this.EmitRead(read);
        break;

      case RestoreStmt restore:
        this.EmitRestore(restore);
        break;

      case OnEventStmt or EventControlStmt:
        break; // event statements are recorded-but-inert (no event dispatch; SVGA hooks ints itself)

      case CommandStmt cmd:
        this.EmitCommand(cmd);
        break;

      case InlineAsmStmt ia:
        this.EmitInlineAsm(ia);
        break;

      case MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
        break; // declarations & module bookkeeping - nothing to execute

      default:
        this.Unsupported(statement);
        break;
    }
  }

  /// <summary>Generic keyword statements (BEEP, POKE, OUT, GET$, REG, SHIFT, ...).</summary>
  private void EmitCommand(CommandStmt cmd) {
    var asm = this._asm;
    switch (cmd.Keyword) {
      case "KILL" when cmd.Arguments is [{ } name]:
        this.EmitExpression(name);
        asm.Call(this._rt.Kill);
        break;

      case "POKE":
        this.EmitPoke(cmd);
        break;

      case "OUT":
        this.EmitOut(cmd);
        break;

      case "WAIT":
        this.EmitWait(cmd);
        break;

      case "REG":
        this.EmitRegStatement(cmd);
        break;

      case "INTERRUPT":
        this.EmitInterrupt(cmd);
        break;

      case "SHIFT LEFT" or "SHIFT RIGHT" or "ROTATE LEFT" or "ROTATE RIGHT":
        this.EmitShiftRotate(cmd);
        break;

      case "GET$" or "PUT$":
        this.EmitGetPutString(cmd);
        break;

      case "CLS":
        asm.Call(this._rt.Cls);
        break;

      case "ERRCLEAR":
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        break;

      case "SETEOF" when cmd.Arguments is [{ } setEofFile]:
        // truncate at the current position: DOS write of 0 bytes
        this.EmitInt16Argument(UnwrapFileNumber(setEofFile));
        asm.Call(this._rt.FHandle);
        asm.Xor(Reg.CX, Reg.CX);
        asm.Mov(Reg.AH, 0x40);
        asm.Int(0x21);
        break;

      case "LOCATE": {
        if (cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } row)
          this.EmitInt16Argument(row);
        else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Push(Reg.AX);
        if (cmd.Arguments.Count >= 2 && cmd.Arguments[1] is { } column)
          this.EmitInt16Argument(column);
        else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Locate);
        break;
      }

      case "SCREEN" when cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } mode:
        // PB SCREEN numbers map onto BIOS modes for the ones the suites use
        this.EmitInt16Argument(mode);
        asm.Call(this._rt.ScreenMode);
        break;

      case "RANDOMIZE": {
        if (cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } seed) {
          this.EmitExpression(seed);
          this.Coerce(model.TypeOf(seed), PbType.Long, seed);
        } else {
          asm.Xor(Reg.AH, Reg.AH);
          asm.Int(0x1A);
          asm.Mov(Reg.AX, Reg.DX);
          asm.Mov(Reg.DX, Reg.CX);
        }
        asm.Mov(Mem.Word(asm.Lbl("rt_rndseed")), Reg.AX);
        asm.Mov(Mem.Word(asm.Lbl("rt_rndseed"), 2), Reg.DX);
        break;
      }

      case "BEEP":
        asm.Mov(Reg.AX, 880);
        asm.Mov(Reg.DX, 4);
        asm.Call(this._rt.Sound);
        break;

      case "SOUND" when cmd.Arguments is [{ } frequency, { } duration]: {
        this.EmitInt16Argument(frequency);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(duration);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Sound);
        break;
      }

      case "DELAY" when cmd.Arguments is [{ } seconds]:
        this.EmitExpression(seconds);
        this.Coerce(model.TypeOf(seconds), PbType.Double, seconds);
        asm.Call(this._rt.Delay);
        break;

      case "PLAY": // parse-and-ignore stub: evaluate and drop the tune string
        foreach (var argument in cmd.Arguments)
          if (argument != null) {
            this.EmitExpression(argument);
            if (KindOf(model.TypeOf(argument)) == ValueKind.Str)
              asm.Call(this._rt.StrFree);
          }
        break;

      case "COLOR" or "WIDTH" or "KEY" or "VIEW" or "WINDOW" or "PALETTE" or "PALETTE USING" or "OPTION BASE":
        break; // accepted, harmless no-ops on this runtime

      default:
        this.Unsupported(cmd);
        break;
    }
  }

  private void EmitExit(ExitStmt e) {
    var asm = this._asm;
    switch (e.Kind) {
      case ExitKind.For when this._exitFor.Count > 0:
        asm.Jmp(this._exitFor.Peek());
        break;
      case ExitKind.Do or ExitKind.Loop when this._exitDo.Count > 0:
        asm.Jmp(this._exitDo.Peek());
        break;
      case ExitKind.Select when this._exitSelect.Count > 0:
        asm.Jmp(this._exitSelect.Peek());
        break;
      case ExitKind.Sub or ExitKind.Function or ExitKind.Def when this._currentProc != null:
        asm.Jmp(this._epilogue);
        break;
      default:
        this.Unsupported(e);
        break;
    }
  }

  private void EmitCondition(Expression condition) {
    // leaves truth in AX (0 / nonzero) and sets ZF accordingly
    var asm = this._asm;
    this.EmitExpression(condition);
    switch (KindOf(model.TypeOf(condition))) {
      case ValueKind.Int16:
        asm.Test(Reg.AX, Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Or(Reg.AX, Reg.DX);
        break;
      case ValueKind.Int64 or ValueKind.Float:
        asm.Ftst();
        asm.FstswAx();
        asm.Fstp(St.St0);
        asm.And(Reg.AX, 0x4000);     // C3 set = zero
        asm.Xor(Reg.AX, 0x4000);     // AX nonzero exactly when value nonzero
        break;
      default:
        this.Unsupported(condition, "condition of this type");
        break;
    }
  }

  private void EmitIf(IfStmt i) {
    var asm = this._asm;
    var elseLabel = asm.DefineLabel();
    var endLabel = asm.DefineLabel();

    this.EmitCondition(i.Condition);
    asm.Jz(elseLabel);
    foreach (var s in i.Then)
      this.EmitStatement(s);
    asm.Jmp(endLabel);

    asm.MarkLabel(elseLabel);
    foreach (var (condition, body) in i.ElseIfs) {
      var next = asm.DefineLabel();
      this.EmitCondition(condition);
      asm.Jz(next);
      foreach (var s in body)
        this.EmitStatement(s);
      asm.Jmp(endLabel);
      asm.MarkLabel(next);
    }
    if (i.Else != null)
      foreach (var s in i.Else)
        this.EmitStatement(s);

    asm.MarkLabel(endLabel);
  }

  private void EmitFor(ForStmt f) {
    var asm = this._asm;
    if (f.Variable is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var counter)
        || this.TryDirectCell(counter) is not { } slot) {
      this.Unsupported(f);
      return;
    }
    var kind = KindOf(counter.Type);
    if (kind == ValueKind.Str) {
      this.Unsupported(f);
      return;
    }

    // constant steps fix the loop direction at compile time
    long? constantStep = f.Step switch {
      null => 1L,
      IntegerLiteralExpr lit => lit.Value,
      UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteralExpr neg } => -neg.Value,
      _ => null,
    };

    if (kind == ValueKind.Int16 && constantStep is { } fastStep) {
      this.EmitForInt16Fast(f, slot.WithSize(OperandSize.Word), fastStep);
      return;
    }

    var counterPlace = new Place(slot, false);
    var slotBytes = kind switch { ValueKind.Int16 => 2, ValueKind.Int32 => 4, _ => 8 };
    var limit = this.AllocTemp(slotBytes);
    var step = this.AllocTemp(slotBytes);
    var limitType = kind switch { ValueKind.Int16 => PbType.Integer, ValueKind.Int32 => PbType.Long, _ => PbType.Double };

    // counter = from; limit and step into per-invocation stack temps
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counter.Type, f.From);
    this.EmitStorePlace(counterPlace, counter.Type, f.From);

    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), limitType, f.To);
    this.EmitStorePlace(new(limit, false), limitType, f.To);

    if (f.Step is { } stepExpr) {
      this.EmitExpression(stepExpr);
      this.Coerce(model.TypeOf(stepExpr), limitType, stepExpr);
    } else {
      asm.Mov(Reg.AX, 1);
      this.Coerce(PbType.Integer, limitType, f.From);
    }
    this.EmitStorePlace(new(step, false), limitType, f.From);

    var top = asm.DefineLabel();
    var negative = asm.DefineLabel();
    var body = asm.DefineLabel();
    var done = asm.DefineLabel();
    this._exitFor.Push(done);
    asm.MarkLabel(top);

    switch (kind) {
      case ValueKind.Int16:
        if (constantStep is { } cs16) {
          asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          if (cs16 >= 0)
            asm.Jg(done);
          else
            asm.Jl(done);
        } else {
          asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
          asm.Cmp(step.WithSize(OperandSize.Word), (Imm)0);
          asm.Jl(negative);
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Jg(done);
          asm.Jmp(body);
          asm.MarkLabel(negative);
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Jl(done);
        }
        break;

      case ValueKind.Int32: {
        var stepSign = constantStep is { } cs32 ? Math.Sign(cs32) : 0;
        if (stepSign == 0) {
          asm.Cmp(Adjust(step, 2, OperandSize.Word), (Imm)0);
          asm.Jl(negative);
        }
        if (stepSign >= 0) {
          // ascending: done when limit - counter < 0
          asm.Mov(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Mov(Reg.DX, Adjust(limit, 2, OperandSize.Word));
          asm.Sub(Reg.AX, Adjust(slot, 0, OperandSize.Word));
          asm.Sbb(Reg.DX, Adjust(slot, 2, OperandSize.Word));
          asm.Test(Reg.DX, Reg.DX);
          asm.Js(done);
          asm.Jmp(body);
        }
        if (stepSign == 0)
          asm.MarkLabel(negative);
        if (stepSign <= 0) {
          // descending: done when counter - limit < 0
          asm.Mov(Reg.AX, Adjust(slot, 0, OperandSize.Word));
          asm.Mov(Reg.DX, Adjust(slot, 2, OperandSize.Word));
          asm.Sub(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Sbb(Reg.DX, Adjust(limit, 2, OperandSize.Word));
          asm.Test(Reg.DX, Reg.DX);
          asm.Js(done);
        }
        break;
      }

      default: {
        var stepSign = constantStep is { } csf ? Math.Sign(csf) : 0;
        if (stepSign == 0) {
          asm.Fld(step.WithSize(OperandSize.Qword));
          asm.Ftst();
          asm.FstswAx();
          asm.Fstp(St.St0);
          asm.Sahf();
          asm.Jb(negative);
        }
        if (stepSign >= 0) {
          this.EmitLoadPlace(counterPlace, counter.Type, f.From);
          asm.Fcomp(limit.WithSize(OperandSize.Qword));
          asm.FstswAx();
          asm.Sahf();
          asm.Ja(done);
          asm.Jmp(body);
        }
        if (stepSign == 0)
          asm.MarkLabel(negative);
        if (stepSign <= 0) {
          this.EmitLoadPlace(counterPlace, counter.Type, f.From);
          asm.Fcomp(limit.WithSize(OperandSize.Qword));
          asm.FstswAx();
          asm.Sahf();
          asm.Jb(done);
        }
        break;
      }
    }

    asm.MarkLabel(body);
    foreach (var s in f.Body)
      this.EmitStatement(s);

    switch (kind) {
      case ValueKind.Int16:
        asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
        asm.Add(Reg.AX, step.WithSize(OperandSize.Word));
        asm.Mov(slot.WithSize(OperandSize.Word), Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(Reg.AX, Adjust(slot, 0, OperandSize.Word));
        asm.Mov(Reg.DX, Adjust(slot, 2, OperandSize.Word));
        asm.Add(Reg.AX, step.WithSize(OperandSize.Word));
        asm.Adc(Reg.DX, Adjust(step, 2, OperandSize.Word));
        asm.Mov(Adjust(slot, 0, OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(slot, 2, OperandSize.Word), Reg.DX);
        break;
      default:
        this.EmitLoadPlace(counterPlace, counter.Type, f.From);
        asm.Fadd(step.WithSize(OperandSize.Qword));
        this.EmitStorePlace(counterPlace, counter.Type, f.From);
        break;
    }
    asm.Jmp(top);
    asm.MarkLabel(done);
    this._exitFor.Pop();
    this.ReleaseTemp(slotBytes);
    this.ReleaseTemp(slotBytes);
  }

  /// <summary>The common case: 16-bit counter with a constant step.</summary>
  private void EmitForInt16Fast(ForStmt f, Mem slot, long step) {
    var asm = this._asm;
    var counterType = model.VariableBindings[(NameExpr)f.Variable].Type;

    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counterType, f.From);
    asm.Mov(slot, Reg.AX);

    var limit = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), counterType, f.To);
    asm.Mov(limit, Reg.AX);

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    this._exitFor.Push(done);
    asm.MarkLabel(top);
    asm.Mov(Reg.AX, slot);
    asm.Cmp(Reg.AX, limit);
    if (step >= 0)
      asm.Jg(done);
    else
      asm.Jl(done);

    foreach (var s in f.Body)
      this.EmitStatement(s);

    asm.Mov(Reg.AX, slot);
    asm.Add(Reg.AX, (int)step);
    asm.Mov(slot, Reg.AX);
    asm.Jmp(top);
    asm.MarkLabel(done);
    this._exitFor.Pop();
    this.ReleaseTemp(2);
  }

  private void EmitDoLoop(DoLoopStmt d) {
    var asm = this._asm;
    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    this._exitDo.Push(done);

    asm.MarkLabel(top);
    if (d.PreCondition != null) {
      this.EmitCondition(d.PreCondition);
      if (d.PreTest == LoopTestKind.While)
        asm.Jz(done);
      else
        asm.Jnz(done);
    }

    foreach (var s in d.Body)
      this.EmitStatement(s);

    if (d.PostCondition != null) {
      this.EmitCondition(d.PostCondition);
      if (d.PostTest == LoopTestKind.While)
        asm.Jnz(top);
      else
        asm.Jz(top);
    } else
      asm.Jmp(top);

    asm.MarkLabel(done);
    this._exitDo.Pop();
  }

  private void EmitSelect(SelectStmt s) {
    var asm = this._asm;
    var subjectType = model.TypeOf(s.Subject);
    var kind = KindOf(subjectType);
    if (kind is ValueKind.Float or ValueKind.Int64) {
      this.Unsupported(s); // float/QUAD subjects are not used by the corpus
      return;
    }

    var subjectBytes = kind switch { ValueKind.Int32 => 4, _ => 2 };
    var subject = this.AllocTemp(subjectBytes);
    this.EmitExpression(s.Subject);
    switch (kind) {
      case ValueKind.Int16:
        this.Coerce(subjectType, PbType.Integer, s.Subject);
        asm.Mov(subject, Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(subject, Reg.AX);
        asm.Mov(Adjust(subject, 2, OperandSize.Word), Reg.DX);
        break;
      default: // owned string handle for the SELECT's duration
        asm.Mov(subject, Reg.AX);
        break;
    }

    var endLabel = asm.DefineLabel();
    this._exitSelect.Push(endLabel);
    foreach (var arm in s.Arms) {
      var armBody = asm.DefineLabel();
      var nextArm = asm.DefineLabel();

      if (arm.Selectors.Count == 0)
        asm.Jmp(armBody); // CASE ELSE
      else {
        foreach (var selector in arm.Selectors) {
          if (selector.Value == null) {
            this.Unsupported(s);
            continue;
          }
          switch (kind) {
            case ValueKind.Int16:
              this.EmitSelectorInt16(s, subject, selector, armBody);
              break;
            case ValueKind.Int32:
              this.EmitSelectorInt32(s, subject, selector, armBody);
              break;
            default:
              this.EmitSelectorString(s, subject, selector, armBody);
              break;
          }
        }
        asm.Jmp(nextArm);
      }

      asm.MarkLabel(armBody);
      foreach (var statement in arm.Body)
        this.EmitStatement(statement);
      asm.Jmp(endLabel);
      asm.MarkLabel(nextArm);
    }
    asm.MarkLabel(endLabel);
    if (kind == ValueKind.Str) {
      asm.Mov(Reg.AX, subject);
      asm.Call(this._rt.StrFree);
    }
    this._exitSelect.Pop();
    this.ReleaseTemp(subjectBytes);
  }

  private void EmitSelectorInt16(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    this.EmitExpression(selector.Value!);
    this.Coerce(model.TypeOf(selector.Value!), PbType.Integer, selector.Value!);

    if (selector.RangeUpper != null) {
      // lower <= subject <= upper
      var noMatch = asm.DefineLabel();
      asm.Cmp(subject, Reg.AX);
      asm.Jl(noMatch);
      this.EmitExpression(selector.RangeUpper);
      this.Coerce(model.TypeOf(selector.RangeUpper), PbType.Integer, selector.RangeUpper);
      asm.Cmp(subject, Reg.AX);
      asm.Jle(armBody);
      asm.MarkLabel(noMatch);
    } else if (selector.IsComparison is { } relation) {
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AX, subject);
      asm.Cmp(Reg.AX, Reg.BX);
      this.EmitRelationJump(relation, armBody);
    } else {
      asm.Cmp(subject, Reg.AX);
      asm.Je(armBody);
    }
  }

  private void EmitRelationJump(CaseComparison relation, Label armBody) {
    var asm = this._asm;
    switch (relation) {
      case CaseComparison.Equal: asm.Je(armBody); break;
      case CaseComparison.NotEqual: asm.Jne(armBody); break;
      case CaseComparison.Less: asm.Jl(armBody); break;
      case CaseComparison.LessEqual: asm.Jle(armBody); break;
      case CaseComparison.Greater: asm.Jg(armBody); break;
      case CaseComparison.GreaterEqual: asm.Jge(armBody); break;
    }
  }

  /// <summary>Loads subject - (DX:AX) into DX:AX (sign in DX, zero iff AX|DX == 0).</summary>
  private void EmitSubjectMinusValue32(Mem subject) {
    var asm = this._asm;
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    asm.Mov(Reg.DX, Adjust(subject, 2, OperandSize.Word));
    asm.Sub(Reg.AX, Reg.BX);
    asm.Sbb(Reg.DX, Reg.CX);
  }

  private void EmitSelectorInt32(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    this.EmitExpression(selector.Value!);
    this.Coerce(model.TypeOf(selector.Value!), PbType.Long, selector.Value!);

    if (selector.RangeUpper != null) {
      var noMatch = asm.DefineLabel();
      this.EmitSubjectMinusValue32(subject);     // subject - lower
      asm.Test(Reg.DX, Reg.DX);
      asm.Js(noMatch);
      this.EmitExpression(selector.RangeUpper);
      this.Coerce(model.TypeOf(selector.RangeUpper), PbType.Long, selector.RangeUpper);
      this.EmitSubjectMinusValue32(subject);     // subject - upper: match when <= 0
      asm.Test(Reg.DX, Reg.DX);
      asm.Js(armBody);
      asm.Or(Reg.AX, Reg.DX);
      asm.Jz(armBody);
      asm.MarkLabel(noMatch);
      return;
    }

    this.EmitSubjectMinusValue32(subject);
    var relation = selector.IsComparison ?? CaseComparison.Equal;
    var skip = asm.DefineLabel();
    switch (relation) {
      case CaseComparison.Equal:
        asm.Or(Reg.AX, Reg.DX);
        asm.Jz(armBody);
        break;
      case CaseComparison.NotEqual:
        asm.Or(Reg.AX, Reg.DX);
        asm.Jnz(armBody);
        break;
      case CaseComparison.Less:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(armBody);
        break;
      case CaseComparison.GreaterEqual:
        asm.Test(Reg.DX, Reg.DX);
        asm.Jns(armBody);
        break;
      case CaseComparison.LessEqual:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(armBody);
        asm.Or(Reg.AX, Reg.DX);
        asm.Jz(armBody);
        break;
      case CaseComparison.Greater:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(skip);
        asm.Or(Reg.AX, Reg.DX);
        asm.Jnz(armBody);
        break;
    }
    asm.MarkLabel(skip);
  }

  private void EmitSelectorString(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    if (selector.RangeUpper != null) {
      this.Unsupported(s); // string ranges are not used by the corpus
      return;
    }
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    asm.Call(this._rt.StrDup);                  // compare consumes - keep the subject alive
    asm.Push(Reg.AX);
    this.EmitExpression(selector.Value!);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Call(this._rt.StrCmp);                  // AX = -1/0/1
    asm.Test(Reg.AX, Reg.AX);
    switch (selector.IsComparison ?? CaseComparison.Equal) {
      case CaseComparison.Equal: asm.Jz(armBody); break;
      case CaseComparison.NotEqual: asm.Jnz(armBody); break;
      case CaseComparison.Less: asm.Js(armBody); break;
      case CaseComparison.GreaterEqual: asm.Jns(armBody); break;
      case CaseComparison.Greater: {
        var skip = asm.DefineLabel();
        asm.Js(skip);
        asm.Jnz(armBody);
        asm.MarkLabel(skip);
        break;
      }
      case CaseComparison.LessEqual: {
        asm.Js(armBody);
        asm.Jz(armBody);
        break;
      }
    }
  }

  private void EmitIncrDecr(IncrDecrStmt id) {
    var asm = this._asm;
    var targetType = model.TypeOf(id.Target);
    var kind = KindOf(targetType);
    if (kind is not (ValueKind.Int16 or ValueKind.Int32)) {
      this.Unsupported(id);
      return;
    }
    var isByte = targetType.Size == 1;

    if (id.Amount != null) {
      this.EmitExpression(id.Amount);
      this.Coerce(model.TypeOf(id.Amount), targetType, id.Amount);
      if (kind == ValueKind.Int32)
        asm.Push(Reg.DX);
      asm.Push(Reg.AX);
    }

    if (this.EmitPlace(id.Target) is not { } place) {
      this.Unsupported(id);
      return;
    }
    var cell = place.Cell.WithSize(isByte ? OperandSize.Byte : OperandSize.Word);

    if (id.Amount == null) {
      if (kind == ValueKind.Int16) {
        if (id.Increment)
          asm.Inc(cell);
        else
          asm.Dec(cell);
      } else if (id.Increment) {
        asm.Add(cell, (Imm)1);
        asm.Adc(Adjust(cell, 2, OperandSize.Word), (Imm)0);
      } else {
        asm.Sub(cell, (Imm)1);
        asm.Sbb(Adjust(cell, 2, OperandSize.Word), (Imm)0);
      }
      return;
    }

    asm.Pop(Reg.AX);
    if (kind == ValueKind.Int32)
      asm.Pop(Reg.DX);
    if (id.Increment) {
      if (isByte)
        asm.Add(cell, Reg.AL);
      else
        asm.Add(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Adc(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    } else {
      if (isByte)
        asm.Sub(cell, Reg.AL);
      else
        asm.Sub(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Sbb(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    }
  }

  #endregion
}
