using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Translates a bound program into a 16-bit real-mode DOS executable.
/// Evaluation model: stack machine - INTEGER/WORD in AX, LONG/DWORD in DX:AX,
/// floats on the x87 stack, machine stack for spills. Memory model: one segment
/// (CS=DS=SS), data area behind the code, stack at the segment top.
/// </summary>
public sealed class CodeGenerator(SemanticModel model) {

  private readonly Assembler _asm = new();
  private readonly DosRuntime _rt = new();
  private readonly Dictionary<VariableSymbol, Label> _variableSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, Label> _stringLiterals = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Label> _userLabels = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<(Label Slot, double Value)> _floatConstants = [];
  private Label _scratch8 = null!;
  private int _nextTemp;

  /// <summary>Generated diagnostics for constructs the generator does not support yet.</summary>
  public List<Diagnostic> Errors { get; } = [];

  public byte[] EmitExecutable() {
    var asm = this._asm;
    var userMain = asm.DefineLabel("user_main");
    this._scratch8 = asm.DefineLabel("cg_scratch8");

    this._rt.EmitEntry(asm, userMain);
    this._rt.EmitProcedures(asm);

    asm.MarkLabel(userMain);
    foreach (var statement in model.MainBody)
      this.EmitStatement(statement);

    // implicit END
    asm.Mov(Reg.AL, (Imm)0);
    asm.Jmp(this._rt.Exit);

    this.EmitDataArea();

    var image = asm.ToArray();
    var writer = new MzExeWriter(image) {
      EntrySegment = 0,
      EntryOffset = 0,
      StackSegment = 0,
      StackPointer = 0xFFFE,
      // grow the single segment to its full 64 KiB so data + stack always fit
      MinExtraParagraphs = (ushort)((0x10000 - image.Length % 0x10000 + 15) / 16),
    };
    writer.AddRelocations(asm.SegmentRelocations);
    return writer.ToArray();
  }

  private void EmitDataArea() {
    var asm = this._asm;
    asm.Align(2);
    this._rt.EmitConstants(asm);
    this._rt.EmitData(asm);

    asm.MarkLabel(this._scratch8);
    asm.Db(new byte[8]);

    foreach (var (slot, value) in this._floatConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Dq(value);
    }

    foreach (var (text, label) in this._stringLiterals) {
      asm.MarkLabel(label);
      asm.Db(text);
    }

    foreach (var symbol in model.ModuleVariables.Values) {
      asm.Align(2);
      this._asm.MarkLabel(this.SlotOf(symbol));
      asm.Db(new byte[Math.Max(symbol.Type.Size, 1)]);
    }
  }

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

  private void Unsupported(Statement s) => this.Errors.Add(new(s.Position, $"not yet generated: {s.GetType().Name}"));
  private void Unsupported(Expression e, string what) => this.Errors.Add(new(e.Position, $"not yet generated: {what}"));

  #region value categories

  private enum ValueKind { Int16, Int32, Float, StringLiteral }

  private static ValueKind KindOf(PbType type) => type switch {
    ScalarType { IsFloat: true } => ValueKind.Float,
    ScalarType { ByteSize: <= 2 } => ValueKind.Int16,
    ScalarType => ValueKind.Int32,
    StringType or FixedStringType or FlexType => ValueKind.StringLiteral,
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

      case EndStmt e:
        if (e.ExitCode != null) {
          this.EmitExpression(e.ExitCode);
          this.Coerce(model.TypeOf(e.ExitCode), PbType.Integer, e.ExitCode);
        } else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Jmp(this._rt.Exit);
        break;

      case DimStmt or MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
        break; // declarations & module bookkeeping - nothing to execute in this slice

      default:
        this.Unsupported(statement);
        break;
    }
  }

  private void EmitAssign(AssignStmt a) {
    if (a.Target is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var symbol)) {
      this.Unsupported(a);
      return;
    }

    this.EmitExpression(a.Value);
    this.Coerce(model.TypeOf(a.Value), symbol.Type, a.Value);
    this.StoreToSlot(symbol);
  }

  private void StoreToSlot(VariableSymbol symbol) {
    var asm = this._asm;
    var slot = this.SlotOf(symbol);
    switch (KindOf(symbol.Type)) {
      case ValueKind.Int16:
        asm.Mov(Mem.Word(slot), Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(Mem.Word(slot), Reg.AX);
        asm.Mov(Mem.Word(slot, 2), Reg.DX);
        break;
      case ValueKind.Float when symbol.Type.Size == 4:
        asm.Fstp(Mem.Dword(slot));
        break;
      case ValueKind.Float:
        asm.Fstp(Mem.Qword(slot));
        break;
      default:
        this.Errors.Add(new(default, $"cannot store {symbol.Type} yet"));
        break;
    }
  }

  private void EmitPrint(PrintStmt p) {
    var asm = this._asm;
    if (p.FileNumber != null || p.IsLPrint || p.UsingFormat != null) {
      this.Unsupported(p);
      return;
    }

    foreach (var item in p.Items) {
      if (item.Value == null)
        continue;

      if (item.Value is StringLiteralExpr lit) {
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(lit.Value)));
        asm.Mov(Reg.CX, lit.Value.Length);
        asm.Call(this._rt.PrintStr);
      } else {
        this.EmitExpression(item.Value);
        switch (KindOf(model.TypeOf(item.Value))) {
          case ValueKind.Int16:
            asm.Call(this._rt.PrintInt16);
            break;
          case ValueKind.Int32:
            asm.Call(this._rt.PrintInt32);
            break;
          case ValueKind.Float when model.TypeOf(item.Value).Size == 4:
            asm.Call(this._rt.PrintSingle);
            break;
          case ValueKind.Float:
            asm.Call(this._rt.PrintDouble);
            break;
          default:
            this.Unsupported(item.Value, "PRINT of this type");
            break;
        }
      }
    }

    var lastSeparator = p.Items.Count == 0 ? PrintSeparator.Newline : p.Items[^1].Separator;
    if (lastSeparator == PrintSeparator.Newline)
      asm.Call(this._rt.PrintNewLine);
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
    if (KindOf(counter.Type) != ValueKind.Int16) {
      this.Unsupported(f); // bring-up: 16-bit counters only
      return;
    }

    var step = 1L;
    if (f.Step is IntegerLiteralExpr stepLit)
      step = stepLit.Value;
    else if (f.Step != null) {
      this.Unsupported(f); // non-constant STEP comes with the full backend
      return;
    }

    // counter = from
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counter.Type, f.From);
    this.StoreToSlot(counter);

    // limit -> anonymous temp slot
    var limit = this._asm.DefineLabel($"t_{this._nextTemp++}");
    this._floatConstants.Add((limit, 0)); // reuse the qword pool as scratch storage
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), counter.Type, f.To);
    asm.Mov(Mem.Word(limit), Reg.AX);

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.MarkLabel(top);
    asm.Mov(Reg.AX, Mem.Word(this.SlotOf(counter)));
    asm.Cmp(Reg.AX, Mem.Word(limit));
    if (step >= 0)
      asm.Jg(done);
    else
      asm.Jl(done);

    foreach (var s in f.Body)
      this.EmitStatement(s);

    asm.Mov(Reg.AX, Mem.Word(this.SlotOf(counter)));
    asm.Add(Reg.AX, (int)step);
    asm.Mov(Mem.Word(this.SlotOf(counter)), Reg.AX);
    asm.Jmp(top);
    asm.MarkLabel(done);
  }

  private void EmitDoLoop(DoLoopStmt d) {
    var asm = this._asm;
    var top = asm.DefineLabel();
    var done = asm.DefineLabel();

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
  }

  private void EmitSelect(SelectStmt s) {
    var asm = this._asm;
    var subjectType = model.TypeOf(s.Subject);
    if (KindOf(subjectType) != ValueKind.Int16) {
      this.Unsupported(s); // bring-up: integer subjects
      return;
    }

    var subject = asm.DefineLabel($"t_{this._nextTemp++}");
    this._floatConstants.Add((subject, 0));
    this.EmitExpression(s.Subject);
    this.Coerce(subjectType, PbType.Integer, s.Subject);
    asm.Mov(Mem.Word(subject), Reg.AX);

    var endLabel = asm.DefineLabel();
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
            asm.Cmp(Mem.Word(subject), Reg.AX);
            asm.Jl(noMatch);
            this.EmitExpression(selector.RangeUpper);
            this.Coerce(model.TypeOf(selector.RangeUpper), PbType.Integer, selector.RangeUpper);
            asm.Cmp(Mem.Word(subject), Reg.AX);
            asm.Jle(armBody);
            asm.MarkLabel(noMatch);
          } else if (selector.IsComparison is { } relation) {
            asm.Mov(Reg.BX, Reg.AX);
            asm.Mov(Reg.AX, Mem.Word(subject));
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
            asm.Cmp(Mem.Word(subject), Reg.AX);
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
  }

  private void EmitIncrDecr(IncrDecrStmt id) {
    var asm = this._asm;
    if (id.Target is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var symbol) || KindOf(symbol.Type) != ValueKind.Int16) {
      this.Unsupported(id);
      return;
    }

    var slot = this.SlotOf(symbol);
    if (id.Amount == null) {
      if (id.Increment)
        asm.Inc(Mem.Word(slot));
      else
        asm.Dec(Mem.Word(slot));
      return;
    }

    this.EmitExpression(id.Amount);
    this.Coerce(model.TypeOf(id.Amount), PbType.Integer, id.Amount);
    if (id.Increment)
      asm.Add(Mem.Word(slot), Reg.AX);
    else
      asm.Sub(Mem.Word(slot), Reg.AX);
  }

  #endregion

  #region expressions

  private void EmitExpression(Expression expression) {
    var asm = this._asm;
    switch (expression) {
      case IntegerLiteralExpr i:
        if (KindOf(model.TypeOf(i)) == ValueKind.Int16)
          asm.Mov(Reg.AX, (int)i.Value);
        else {
          asm.Mov(Reg.AX, (int)(i.Value & 0xFFFF));
          asm.Mov(Reg.DX, (int)((i.Value >> 16) & 0xFFFF));
        }
        break;

      case FloatLiteralExpr f:
        asm.Fld(Mem.Qword(this.FloatConstOf(f.Value)));
        break;

      case NamedConstantExpr c: {
        var value = model.Equates.TryGetValue(c.Name, out var v) ? v.AsInteger : 0;
        if (KindOf(model.TypeOf(c)) == ValueKind.Int16)
          asm.Mov(Reg.AX, (int)value);
        else {
          asm.Mov(Reg.AX, (int)(value & 0xFFFF));
          asm.Mov(Reg.DX, (int)((value >> 16) & 0xFFFF));
        }
        break;
      }

      case NameExpr n: {
        if (!model.VariableBindings.TryGetValue(n, out var symbol)) {
          this.Unsupported(n, $"unbound name {n.Name}");
          break;
        }
        var slot = this.SlotOf(symbol);
        switch (KindOf(symbol.Type)) {
          case ValueKind.Int16:
            asm.Mov(Reg.AX, Mem.Word(slot));
            break;
          case ValueKind.Int32:
            asm.Mov(Reg.AX, Mem.Word(slot));
            asm.Mov(Reg.DX, Mem.Word(slot, 2));
            break;
          case ValueKind.Float when symbol.Type.Size == 4:
            asm.Fld(Mem.Dword(slot));
            break;
          case ValueKind.Float:
            asm.Fld(Mem.Qword(slot));
            break;
          default:
            this.Unsupported(n, $"load of {symbol.Type}");
            break;
        }
        break;
      }

      case UnaryExpr u:
        this.EmitUnary(u);
        break;

      case BinaryExpr b:
        this.EmitBinary(b);
        break;

      default:
        this.Unsupported(expression, expression.GetType().Name);
        break;
    }
  }

  private void EmitUnary(UnaryExpr u) {
    var asm = this._asm;
    this.EmitExpression(u.Operand);
    var kind = KindOf(model.TypeOf(u.Operand));
    switch (u.Op, kind) {
      case (UnaryOp.Negate, ValueKind.Int16):
        asm.Neg(Reg.AX);
        break;
      case (UnaryOp.Negate, ValueKind.Int32):
        asm.Not(Reg.DX);
        asm.Neg(Reg.AX);
        asm.Sbb(Reg.DX, -1);
        break;
      case (UnaryOp.Negate, ValueKind.Float):
        asm.Fchs();
        break;
      case (UnaryOp.Not, ValueKind.Int16):
        asm.Not(Reg.AX);
        break;
      case (UnaryOp.Not, ValueKind.Int32):
        asm.Not(Reg.AX);
        asm.Not(Reg.DX);
        break;
      default:
        this.Unsupported(u, "unary op");
        break;
    }
  }

  private void EmitBinary(BinaryExpr b) {
    var asm = this._asm;
    var leftType = model.TypeOf(b.Left);
    var rightType = model.TypeOf(b.Right);
    var resultType = model.TypeOf(b);

    // arithmetic runs in the result type; comparisons in the widest operand type
    var opType = b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual
      ? WidestOf(leftType, rightType)
      : resultType;

    switch (KindOf(opType)) {
      case ValueKind.Int16:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        asm.Push(Reg.AX);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Pop(Reg.AX);
        this.EmitInt16Op(b);
        break;

      case ValueKind.Int32:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Reg.CX, Reg.DX);
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
        this.EmitInt32Op(b);
        break;

      case ValueKind.Float:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        this.EmitFloatOp(b);
        break;

      default:
        this.Unsupported(b, "binary op on this type");
        break;
    }
  }

  /// <summary>left AX, right BX -> result AX.</summary>
  private void EmitInt16Op(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add: asm.Add(Reg.AX, Reg.BX); break;
      case BinaryOp.Subtract: asm.Sub(Reg.AX, Reg.BX); break;
      case BinaryOp.Multiply: asm.Imul(Reg.BX); break;
      case BinaryOp.IntegerDivide:
        asm.Cwd();
        asm.Idiv(Reg.BX);
        break;
      case BinaryOp.Modulo:
        asm.Cwd();
        asm.Idiv(Reg.BX);
        asm.Mov(Reg.AX, Reg.DX);
        break;
      case BinaryOp.And: asm.And(Reg.AX, Reg.BX); break;
      case BinaryOp.Or: asm.Or(Reg.AX, Reg.BX); break;
      case BinaryOp.Xor: asm.Xor(Reg.AX, Reg.BX); break;
      case BinaryOp.Eqv:
        asm.Xor(Reg.AX, Reg.BX);
        asm.Not(Reg.AX);
        break;
      case BinaryOp.Imp:
        asm.Not(Reg.AX);
        asm.Or(Reg.AX, Reg.BX);
        break;
      case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jl); break;
      case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Jg); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jle); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jge); break;
      default:
        this.Unsupported(b, $"int16 {b.Op}");
        break;
    }
  }

  private void EmitInt16Compare(Func<Assembler, Action<Label>> jump) {
    var asm = this._asm;
    var done = asm.DefineLabel();
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Mov(Reg.AX, -1);    // MOV leaves flags intact
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>left DX:AX, right CX:BX -> result DX:AX.</summary>
  private void EmitInt32Op(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        asm.Adc(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Multiply:
        asm.Call(this._rt.LongMul);
        break;
      case BinaryOp.IntegerDivide:
        asm.Call(this._rt.LongDiv);
        break;
      case BinaryOp.Modulo:
        asm.Call(this._rt.LongMod);
        break;
      case BinaryOp.And:
        asm.And(Reg.AX, Reg.BX);
        asm.And(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Or:
        asm.Or(Reg.AX, Reg.BX);
        asm.Or(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Xor:
        asm.Xor(Reg.AX, Reg.BX);
        asm.Xor(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Equal or BinaryOp.NotEqual: {
        var done = asm.DefineLabel();
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        asm.Or(Reg.AX, Reg.DX);    // zero iff equal
        asm.Mov(Reg.DX, Reg.AX);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? -1 : 0);
        asm.Test(Reg.DX, Reg.DX);
        asm.Jz(done);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? 0 : -1);
        asm.MarkLabel(done);
        asm.Cwd();
        break;
      }
      case BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual: {
        // sign of (left - right); fine for in-range operands (full backend adds overflow-safe compare)
        var jump = b.Op;
        var done = asm.DefineLabel();
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        asm.Or(Reg.AX, Reg.DX);    // combine for zero detection
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Reg.AX, -1);
        switch (jump) {
          case BinaryOp.Less:
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(done);
            break;
          case BinaryOp.GreaterEqual:
            asm.Test(Reg.DX, Reg.DX);
            asm.Jns(done);
            break;
          case BinaryOp.Greater: {
            var no = asm.DefineLabel();
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(no);
            asm.Test(Reg.BX, Reg.BX);
            asm.Jnz(done);
            asm.MarkLabel(no);
            break;
          }
          case BinaryOp.LessEqual: {
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(done);
            asm.Test(Reg.BX, Reg.BX);
            asm.Jz(done);
            break;
          }
        }
        asm.Mov(Reg.AX, (Imm)0);
        asm.MarkLabel(done);
        asm.Cwd();
        break;
      }
      default:
        this.Unsupported(b, $"int32 {b.Op}");
        break;
    }
  }

  /// <summary>left ST(1), right ST(0) -> result ST(0).</summary>
  private void EmitFloatOp(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add: asm.Faddp(); break;
      case BinaryOp.Subtract: asm.Fsubp(); break;
      case BinaryOp.Multiply: asm.Fmulp(); break;
      case BinaryOp.Divide: asm.Fdivp(); break;
      case BinaryOp.Power: asm.Call(this._rt.Pow); break;
      case BinaryOp.Equal: this.EmitFloatCompare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitFloatCompare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitFloatCompare(asm => asm.Jb); break;
      case BinaryOp.Greater: this.EmitFloatCompare(asm => asm.Ja); break;
      case BinaryOp.LessEqual: this.EmitFloatCompare(asm => asm.Jbe); break;
      case BinaryOp.GreaterEqual: this.EmitFloatCompare(asm => asm.Jae); break;
      default:
        this.Unsupported(b, $"float {b.Op}");
        break;
    }
  }

  private void EmitFloatCompare(Func<Assembler, Action<Label>> jump) {
    var asm = this._asm;
    var done = asm.DefineLabel();
    asm.Fxch();              // FCOMPP compares ST0 with ST1: want left in ST0
    asm.Fcompp();
    asm.FstswAx();
    asm.Sahf();              // CF/ZF now mirror the (unsigned-style) FPU compare
    asm.Mov(Reg.AX, -1);
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>Converts the current value (registers/FPU per <paramref name="from"/>) into <paramref name="to"/>'s category.</summary>
  private void Coerce(PbType from, PbType to, Expression at) {
    var asm = this._asm;
    var src = KindOf(from);
    var dst = KindOf(to);
    if (src == dst)
      return;

    switch (src, dst) {
      case (ValueKind.Int16, ValueKind.Int32):
        asm.Cwd();
        break;

      case (ValueKind.Int32, ValueKind.Int16):
        break; // keep AX (range checking is the full backend's job)

      case (ValueKind.Int16, ValueKind.Float):
        asm.Mov(Mem.Word(this._scratch8), Reg.AX);
        asm.Fild(Mem.Word(this._scratch8));
        break;

      case (ValueKind.Int32, ValueKind.Float):
        asm.Mov(Mem.Word(this._scratch8), Reg.AX);
        asm.Mov(Mem.Word(this._scratch8, 2), Reg.DX);
        asm.Fild(Mem.Dword(this._scratch8));
        break;

      case (ValueKind.Float, ValueKind.Int16):
        asm.Fistp(Mem.Word(this._scratch8));
        asm.Mov(Reg.AX, Mem.Word(this._scratch8));
        break;

      case (ValueKind.Float, ValueKind.Int32):
        asm.Fistp(Mem.Dword(this._scratch8));
        asm.Mov(Reg.AX, Mem.Word(this._scratch8));
        asm.Mov(Reg.DX, Mem.Word(this._scratch8, 2));
        break;

      default:
        this.Unsupported(at, $"conversion {from} -> {to}");
        break;
    }
  }

  private static PbType WidestOf(PbType a, PbType b) {
    if (a is ScalarType { IsFloat: true } || b is ScalarType { IsFloat: true })
      return PbType.Double;
    if (a is ScalarType { ByteSize: > 2 } || b is ScalarType { ByteSize: > 2 })
      return PbType.Long;
    return PbType.Integer;
  }

  #endregion
}
