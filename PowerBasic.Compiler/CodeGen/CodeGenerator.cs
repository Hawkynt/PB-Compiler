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

  public byte[] EmitExecutable() {
    var asm = this._asm;
    var userMain = asm.DefineLabel("user_main");
    this._scratch = asm.DefineLabel("cg_scratch");

    this._rt.EmitEntry(asm, userMain);
    this._rt.EmitProcedures(asm);

    asm.MarkLabel(userMain);
    this.BeginFrame();
    foreach (var statement in model.MainBody)
      this.EmitStatement(statement);

    // implicit END
    asm.Mov(Reg.AL, (Imm)0);
    asm.Jmp(this._rt.Exit);
    this.EndFrame();

    foreach (var proc in model.Procedures.Values)
      if (!proc.IsExternal)
        this.EmitProcedure(proc);

    this.EmitDataArea();

    var image = asm.ToArray();
    var writer = new MzExeWriter(image) {
      EntrySegment = 0,
      EntryOffset = 0,
      StackSegment = 0,
      StackPointer = 0xFFFE,
      // grow the single segment to its full 64 KiB so data + stack always fit,
      // then reserve the far string and array heap segments behind it
      MinExtraParagraphs = (ushort)((0x10000 - image.Length % 0x10000 + 15) / 16 + DosRuntime.ExtraHeapParagraphs),
    };
    writer.AddRelocations(asm.SegmentRelocations);
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
      this._procLabels[proc] = label = this._asm.DefineLabel($"p_{proc.Name}");
    return label;
  }

  private void EmitDataArea() {
    var asm = this._asm;
    asm.Align(2);
    this._rt.EmitConstants(asm);
    this._rt.EmitData(asm);

    asm.Align(2);
    asm.MarkLabel(this._scratch);
    asm.Db(new byte[12]);

    foreach (var (slot, value) in this._floatConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Dq(value);
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
  }

  private void Unsupported(Statement s) => this.Errors.Add(new(s.Position, $"not yet generated: {s.GetType().Name}"));
  private void Unsupported(Expression e, string what) => this.Errors.Add(new(e.Position, $"not yet generated: {what}"));
  private void Unsupported(SourcePosition position, string what) => this.Errors.Add(new(position, $"not yet generated: {what}"));

  /// <summary>Replicates the binder's variable table key (name + suffix character).</summary>
  private static string KeyOf(string name, TypeSuffix suffix) => suffix switch {
    TypeSuffix.Integer => name + "%",
    TypeSuffix.Long => name + "&",
    TypeSuffix.Single => name + "!",
    TypeSuffix.Double => name + "#",
    TypeSuffix.Ext => name + "E",
    TypeSuffix.String => name + "$",
    _ => name,
  };

  private VariableSymbol? LookupVariable(string name, TypeSuffix suffix) {
    var key = KeyOf(name, suffix);
    if (this._currentProc != null && this._currentProc.Variables.TryGetValue(key, out var local))
      return local;
    return model.ModuleVariables.GetValueOrDefault(key);
  }

  #endregion

  #region value categories

  private enum ValueKind { Int16, Int32, Float, Str }

  private static ValueKind KindOf(PbType type) => type switch {
    ScalarType { IsFloat: true } => ValueKind.Float,
    ScalarType { ByteSize: <= 2 } => ValueKind.Int16,
    ScalarType => ValueKind.Int32,
    StringType or FixedStringType or FlexType => ValueKind.Str,
    _ => ValueKind.Int16,
  };

  #endregion

  #region statements

  private void EmitStatement(Statement statement) {
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

      case CommandStmt { Keyword: "KILL" } kill when kill.Arguments is [{ } name]:
        this.EmitExpression(name);
        asm.Call(this._rt.Kill);
        break;

      case MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
        break; // declarations & module bookkeeping - nothing to execute

      default:
        this.Unsupported(statement);
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
      case ValueKind.Float:
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
    if (f.Variable is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var counter)) {
      this.Unsupported(f);
      return;
    }
    var counterCell = this.TryDirectCell(counter);
    if (KindOf(counter.Type) != ValueKind.Int16 || counterCell is not { } slot) {
      this.Unsupported(f); // 16-bit directly addressable counters only
      return;
    }
    slot = slot.WithSize(OperandSize.Word);

    var step = 1L;
    if (f.Step is IntegerLiteralExpr stepLit)
      step = stepLit.Value;
    else if (f.Step is UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteralExpr negLit })
      step = -negLit.Value;
    else if (f.Step != null) {
      this.Unsupported(f); // non-constant STEP not yet generated
      return;
    }

    // counter = from
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counter.Type, f.From);
    asm.Mov(slot, Reg.AX);

    // limit -> per-invocation stack temp
    var limit = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), counter.Type, f.To);
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
    if (KindOf(subjectType) != ValueKind.Int16) {
      this.Unsupported(s); // integer subjects only
      return;
    }

    var subject = this.AllocTemp(2);
    this.EmitExpression(s.Subject);
    this.Coerce(subjectType, PbType.Integer, s.Subject);
    asm.Mov(subject, Reg.AX);

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

          this.EmitExpression(selector.Value);
          this.Coerce(model.TypeOf(selector.Value), PbType.Integer, selector.Value);

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
            switch (relation) {
              case CaseComparison.Equal: asm.Je(armBody); break;
              case CaseComparison.NotEqual: asm.Jne(armBody); break;
              case CaseComparison.Less: asm.Jl(armBody); break;
              case CaseComparison.LessEqual: asm.Jle(armBody); break;
              case CaseComparison.Greater: asm.Jg(armBody); break;
              case CaseComparison.GreaterEqual: asm.Jge(armBody); break;
            }
          } else {
            asm.Cmp(subject, Reg.AX);
            asm.Je(armBody);
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
    this._exitSelect.Pop();
    this.ReleaseTemp(2);
  }

  private void EmitIncrDecr(IncrDecrStmt id) {
    var asm = this._asm;
    var targetType = model.TypeOf(id.Target);
    var kind = KindOf(targetType);
    if (kind is not (ValueKind.Int16 or ValueKind.Int32) || targetType.Size == 1) {
      this.Unsupported(id);
      return;
    }

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
    var cell = place.Cell.WithSize(OperandSize.Word);

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
      asm.Add(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Adc(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    } else {
      asm.Sub(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Sbb(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    }
  }

  #endregion
}
