using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>Bytes one argument occupies on the stack (BYREF = near pointer; BYVAL = value, word-aligned).</summary>
  private static int ParamSlotSize(VariableSymbol p) => p.ByVal ? Math.Max(2, (p.Type.Size + 1) & ~1) : 2;

  /// <summary>
  /// Assigns BP-relative offsets: parameters at [BP+4..] (pushed left to right,
  /// so the last parameter sits at [BP+4]; CDECL pushes right to left, so the
  /// FIRST parameter sits at [BP+4]), stack locals below BP. STATIC variables
  /// and arrays use data segment slots instead.
  /// </summary>
  private int LayoutFrame(ProcedureSymbol proc) {
    var offset = 4;
    if (proc.IsCdecl)
      for (var i = 0; i < proc.Parameters.Count; ++i) {
        proc.Parameters[i].Offset = offset;
        offset += ParamSlotSize(proc.Parameters[i]);
      }
    else
      for (var i = proc.Parameters.Count - 1; i >= 0; --i) {
        proc.Parameters[i].Offset = offset;
        offset += ParamSlotSize(proc.Parameters[i]);
      }
    var paramBytes = offset - 4;

    this._frameLocalBytes = 0;
    foreach (var symbol in this.StackLocalsOf(proc)) {
      this._frameLocalBytes += Math.Max(2, (symbol.Type.Size + 1) & ~1);
      symbol.Offset = -this._frameLocalBytes;
    }
    return paramBytes;
  }

  private IEnumerable<VariableSymbol> StackLocalsOf(ProcedureSymbol proc) {
    var seen = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var symbol in proc.Variables.Values)
      if (symbol.Storage == VariableStorage.Local && !symbol.IsArray && seen.Add(symbol))
        yield return symbol;
  }

  private void EmitProcedure(ProcedureSymbol proc) {
    var asm = this._asm;
    this._currentProc = proc;
    var outerLabels = this._userLabels;
    this._userLabels = new(StringComparer.OrdinalIgnoreCase);
    var paramBytes = this.LayoutFrame(proc);
    this._epilogue = asm.DefineLabel($"p_{proc.Name}_end");
    this._trackResume = ContainsErrorHandling(proc.Body!);

    // pb36 C2 ($CPU 80486): 16-byte-align procedure entries to the 486 cache
    // line - reached only by CALL, so the NOP pad never executes
    if (this.OptimizePb36 && this.Cpu486)
      asm.AlignCode(16);
    asm.MarkLabel(this.ProcLabelOf(proc));
    if (this.CheckStack) { // $ERROR STACK ON: SP headroom probe -> Error 201 (oracle-verified)
      var roomy = asm.DefineLabel();
      asm.Cmp(Reg.SP, Mem.Word(asm.Lbl("rt_stackmin")));
      asm.Ja(roomy);
      asm.Mov(Reg.AX, 201);
      asm.Call(this._rt.Raise);
      asm.MarkLabel(roomy);
    }

    // pb36 O19: when every (non-string) local is definitely assigned before
    // use and no error handler can re-enter with stale state, the whole-frame
    // zero fill collapses to zeroing just the dynamic-string handle slots
    // (those must stay 0 for the first StrAssign and the epilogue StrFree)
    var stackLocals = this.StackLocalsOf(proc).ToList();
    var elideZeroing = this.OptimizePb36 && !this._trackResume
      && CanElideFrameZeroing(model, proc.Body!, stackLocals);

    // pb36 O14: self-calls in tail position become frame-reusing jumps when
    // nothing must outlive the call - no error handler, no GOSUB returns, no
    // string/FLEX locals pending release, and every parameter is a small
    // BYVAL scalar whose slot can be rewritten in place
    this._tailSelfCalls = null;
    this._tailEntry = null;
    if (this.OptimizePb36 && !this._trackResume
        && !proc.IsCdecl
        && proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { IsFloat: false, ByteSize: <= 4 })
        && stackLocals.All(l => l.Type is ScalarType)
        && !ContainsGosub(proc.Body!)) {
      var tails = CollectTailSelfCalls(proc.Body!, proc, model);
      if (tails.Count > 0) {
        this._tailSelfCalls = tails;
        this._tailEntry = asm.DefineLabel($"p_{proc.Name}_tail");
      }
    }

    this.PrepareCse(proc.Body!);
    this.BeginFrame(elideZeroing, this._tailEntry);
    if (elideZeroing)
      foreach (var local in stackLocals)
        if (local.Type is StringType or FlexType)
          asm.Mov(Mem.Word(Reg.BP, local.Offset), (Imm)0);

    // procedures that arm ON ERROR save and restore the caller's handler state
    Mem? savedHandler = null;
    if (this._trackResume) {
      savedHandler = this.AllocTemp(6);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr")));
      asm.Mov(savedHandler.Value.WithSize(OperandSize.Word), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr_bp")));
      asm.Mov(Adjust(savedHandler.Value, 2, OperandSize.Word), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr_sp")));
      asm.Mov(Adjust(savedHandler.Value, 4, OperandSize.Word), Reg.AX);
    }

    foreach (var statement in proc.Body!)
      this.EmitStatement(statement);

    asm.MarkLabel(this._epilogue);
    if (savedHandler is { } saved) {
      asm.Mov(Reg.AX, saved.WithSize(OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr")), Reg.AX);
      asm.Mov(Reg.AX, Adjust(saved, 2, OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr_bp")), Reg.AX);
      asm.Mov(Reg.AX, Adjust(saved, 4, OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr_sp")), Reg.AX);
    }

    // release string ownership: stack locals and BYVAL string parameters
    var resultVar = proc.IsFunction && proc.Variables.TryGetValue(proc.Name, out var rv) ? rv : null;
    foreach (var symbol in this.StackLocalsOf(proc))
      if (symbol.Type is StringType or FlexType && !ReferenceEquals(symbol, resultVar)) {
        asm.Mov(Reg.AX, Mem.Word(Reg.BP, symbol.Offset));
        asm.Call(this._rt.StrFree);
      }
    foreach (var parameter in proc.Parameters)
      if (parameter is { ByVal: true, Type: StringType or FlexType }) {
        asm.Mov(Reg.AX, Mem.Word(Reg.BP, parameter.Offset));
        asm.Call(this._rt.StrFree);
      }

    if (resultVar != null)
      this.EmitLoadPlace(new(Mem.At(Reg.BP, resultVar.Offset), false), resultVar.Type, null!);
    else if (proc.IsFunction)
      this.Errors.Add(new(proc.Position, $"FUNCTION {proc.Name} has no result variable"));

    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
    if (paramBytes > 0 && !proc.IsCdecl)   // CDECL: the caller cleans up
      asm.Ret((ushort)paramBytes);
    else
      asm.Ret();

    this.EndFrame();
    this._userLabels = outerLabels;
    this._currentProc = null;
    this._trackResume = false;
  }

  private void EmitCallStatement(CallStmt c) {
    if (!model.CallBindings.TryGetValue(c, out var proc)) {
      this.Unsupported(c);
      return;
    }
    if (this._tailSelfCalls?.Contains(c) == true && ReferenceEquals(proc, this._currentProc)
        && c.Arguments.Count == proc.Parameters.Count) {
      this.EmitTailSelfCall(proc, c.Arguments);
      return;
    }
    this.EmitCall(proc, c.Arguments, wantResult: false, c.Position);
  }

  /// <summary>
  /// pb36 O14: evaluates the new arguments left to right onto the stack (old
  /// parameter values stay readable during evaluation), pops them into the
  /// BYVAL parameter slots and jumps back to the frame entry - recursion in
  /// constant stack space.
  /// </summary>
  private void EmitTailSelfCall(ProcedureSymbol proc, IReadOnlyList<Expression> args) {
    var asm = this._asm;
    for (var i = 0; i < args.Count; ++i) {
      var parameter = proc.Parameters[i];
      this.EmitExpression(args[i]);
      this.Coerce(model.TypeOf(args[i]), parameter.Type, args[i]);
      if (parameter.Type.Size > 2)
        asm.Push(Reg.DX);
      asm.Push(Reg.AX);
    }
    for (var i = args.Count - 1; i >= 0; --i) {
      var parameter = proc.Parameters[i];
      asm.Pop(Reg.AX);
      asm.Mov(Mem.Word(Reg.BP, parameter.Offset), Reg.AX);
      if (parameter.Type.Size > 2) {
        asm.Pop(Reg.DX);
        asm.Mov(Mem.Word(Reg.BP, parameter.Offset + 2), Reg.DX);
      }
    }
    asm.Jmp(this._tailEntry!);
  }

  /// <summary>Statements whose CallStmt to <paramref name="proc"/> sits in tail position (last in the body or last in arms of trailing IF/SELECT chains).</summary>
  private static HashSet<Statement> CollectTailSelfCalls(IReadOnlyList<Statement> body, ProcedureSymbol proc, SemanticModel model) {
    var tails = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
    Visit(body);
    return tails;

    void Visit(IReadOnlyList<Statement> block) {
      if (block.Count == 0)
        return;
      var last = block[^1];
      switch (last) {
        case CallStmt c when model.CallBindings.TryGetValue(c, out var target) && ReferenceEquals(target, proc):
          tails.Add(c);
          break;
        case IfStmt i:
          Visit(i.Then);
          foreach (var (_, armBody) in i.ElseIfs)
            Visit(armBody);
          if (i.Else != null)
            Visit(i.Else);
          break;
        case SelectStmt s:
          foreach (var arm in s.Arms)
            Visit(arm.Body);
          break;
      }
    }
  }

  private static bool ContainsGosub(IEnumerable<Statement> statements) {
    foreach (var statement in statements) {
      if (statement is GosubStmt or GosubPtrStmt or OnGotoStmt { IsGosub: true })
        return true;
      if (ChildStatementBlocks(statement).Any(ContainsGosub))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Emits a SUB/FUNCTION invocation: arguments pushed left to right (BYREF =
  /// near pointer, BYVAL = value; BYVAL strings transfer temp ownership to the
  /// callee), RET n cleans up. Results: AX / DX:AX / ST0 / string handle in AX.
  /// </summary>
  /// <summary>
  /// pb36 O6 subset: a FUNCTION whose body is exactly one result assignment
  /// over BYVAL scalar parameters and constants inlines as the expression
  /// itself - arguments evaluate once into frame temps (caller effects and
  /// order preserved), the body expression emits with parameter reads mapped
  /// onto those temps, and the frame/call/return overhead disappears.
  /// </summary>
  private bool TryEmitInlinedFunction(ProcedureSymbol proc, IReadOnlyList<Expression> args, bool wantResult) {
    if (!this.OptimizePb36 || !wantResult || proc.IsCdecl || proc.IsStatic || proc.Body is not [AssignStmt single])
      return false;
    if (args.Count != proc.Parameters.Count)
      return false;
    if (proc.ReturnType is not ScalarType)
      return false; // FIX/BCD are BcdType, strings/UDTs excluded with them
    if (single.Target is not NameExpr resultName
        || !model.VariableBindings.TryGetValue(resultName, out var resultSymbol)
        || !proc.Variables.TryGetValue(proc.Name, out var expectedResult)
        || !ReferenceEquals(resultSymbol, expectedResult))
      return false;
    foreach (var parameter in proc.Parameters)
      if (!parameter.ByVal || parameter.Type is not ScalarType)
        return false;
    if (!InlinableExpression(single.Value, proc))
      return false;

    var asm = this._asm;
    var outer = this._inlineParamSlots;
    var slots = new Dictionary<VariableSymbol, (Mem Cell, PbType Type)>(ReferenceEqualityComparer.Instance);
    var reserved = 0;
    for (var i = 0; i < args.Count; ++i) {
      var parameter = proc.Parameters[i];
      this.EmitExpression(args[i]);
      this.Coerce(model.TypeOf(args[i]), parameter.Type, args[i]);
      var bytes = Math.Max(2, (parameter.Type.Size + 1) & ~1);
      var cell = this.AllocTemp(bytes);
      reserved += bytes;
      switch (KindOf(parameter.Type)) {
        case ValueKind.Int16:
          asm.Mov(cell, Reg.AX);
          break;
        case ValueKind.Int32:
          asm.Mov(cell, Reg.AX);
          asm.Mov(Adjust(cell, 2, OperandSize.Word), Reg.DX);
          break;
        default: // float parameters park x87-exact at their declared width
          asm.Fstp(Adjust(cell, 0, parameter.Type.Size == 4 ? OperandSize.Dword : OperandSize.Qword));
          break;
      }
      slots[parameter] = (cell, parameter.Type);
    }

    this._inlineParamSlots = slots;
    this.EmitExpression(single.Value);
    this.Coerce(model.TypeOf(single.Value), proc.ReturnType, single.Value);
    this._inlineParamSlots = outer;
    this.ReleaseTemp(reserved);
    return true;
  }

  /// <summary>True when the expression reads only the procedure's own parameters, literals and equates through scalar operators.</summary>
  private bool InlinableExpression(Expression e, ProcedureSymbol proc) => e switch {
    IntegerLiteralExpr or FloatLiteralExpr or NamedConstantExpr => true,
    NameExpr n => model.VariableBindings.TryGetValue(n, out var s) && proc.Parameters.Contains(s),
    UnaryExpr u => this.InlinableExpression(u.Operand, proc),
    BinaryExpr b => this.InlinableExpression(b.Left, proc) && this.InlinableExpression(b.Right, proc),
    _ => false,
  };

  private void EmitCall(ProcedureSymbol proc, IReadOnlyList<Expression> args, bool wantResult, SourcePosition position) {
    var asm = this._asm;
    if (this.TryEmitInlinedFunction(proc, args, wantResult))
      return;
    if (proc.IsExternal && !this._allowExternalCalls) {
      this.Unsupported(position, $"external procedure {proc.Name} (no $LINK provides it)");
      return;
    }
    var cdeclVariadic = proc.IsCdecl && args.Count >= proc.RequiredParameters && args.Count <= proc.Parameters.Count;
    if (args.Count != proc.Parameters.Count && !cdeclVariadic) {
      this.Unsupported(position, $"argument count for {proc.Name}");
      return;
    }

    var tempBytesUsed = 0;
    var stringTemps = new List<Mem>();
    var pushedBytes = 0;

    // CDECL pushes right to left (the caller cleans up); the default convention left to right
    foreach (var i in proc.IsCdecl ? Enumerable.Range(0, args.Count).Reverse() : Enumerable.Range(0, args.Count)) {
      var parameter = proc.Parameters[i];
      var arg = args[i];
      var argType = model.TypeOf(arg);
      pushedBytes += ParamSlotSize(parameter);

      // BYVAL override (PB 3.2): the value itself is passed - against a BYREF
      // parameter the low word acts as the near address of the target
      if (arg is ByValArgExpr byValOverride) {
        var innerType = model.TypeOf(byValOverride.Value);
        if (parameter.ByVal)
          this.EmitByValArgument(byValOverride.Value, innerType, parameter.Type);
        else {
          this.EmitExpression(byValOverride.Value);
          asm.Push(Reg.AX); // offset word of the pointer/value
        }
        continue;
      }

      if (parameter.Type is ArrayType || argType is ArrayType) {
        this.EmitArrayArgument(arg, proc);
        continue;
      }

      if (parameter.Type is AnyType) {
        // BYREF ANY: address of whatever storage the argument names - no checks
        if (this.EmitPlace(arg) is { } anyPlace) {
          asm.Lea(Reg.BX, anyPlace.Cell);
          asm.Push(Reg.BX);
        } else
          this.Unsupported(arg, $"ANY argument to {proc.Name}");
        continue;
      }

      if (parameter.ByVal) {
        this.EmitByValArgument(arg, argType, parameter.Type);
        continue;
      }

      // BYREF: pass the address when the argument is a matching near lvalue,
      // otherwise copy into a hidden stack temp (copy-in only)
      if (Equals(argType, parameter.Type) && this.IsNearLValue(arg) && this.EmitPlace(arg) is { } place) {
        asm.Lea(Reg.BX, place.Cell);
        asm.Push(Reg.BX);
        continue;
      }

      var slotBytes = Math.Max(2, (parameter.Type.Size + 1) & ~1);
      var temp = this.AllocTemp(slotBytes);
      tempBytesUsed += slotBytes;
      this.EmitExpression(arg);
      this.Coerce(argType, parameter.Type, arg);
      this.EmitStoreTempArgument(temp, parameter.Type, arg, stringTemps);
      asm.Lea(Reg.BX, temp);
      asm.Push(Reg.BX);
    }

    asm.Call(this.ProcLabelOf(proc));
    if (proc.IsCdecl && pushedBytes > 0)
      asm.Add(Reg.SP, pushedBytes);

    var resultKind = proc is { IsFunction: true, ReturnType: { } rt } ? KindOf(rt) : (ValueKind?)null;
    if (stringTemps.Count > 0) {
      // protect the result registers while releasing byref string temps
      if (resultKind is ValueKind.Int16 or ValueKind.Str)
        asm.Push(Reg.AX);
      else if (resultKind == ValueKind.Int32) {
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
      }
      foreach (var cell in stringTemps) {
        asm.Mov(Reg.AX, cell.WithSize(OperandSize.Word));
        asm.Call(this._rt.StrFree);
      }
      if (resultKind is ValueKind.Int16 or ValueKind.Str)
        asm.Pop(Reg.AX);
      else if (resultKind == ValueKind.Int32) {
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
      }
    }
    this.ReleaseTemp(tempBytesUsed);

    if (wantResult || resultKind == null)
      return;

    // discarded FUNCTION result
    switch (resultKind) {
      case ValueKind.Str:
        asm.Call(this._rt.StrFree);
        break;
      case ValueKind.Float:
        asm.Fstp(St.St0);
        break;
    }
  }

  /// <summary>
  /// Array argument: push the address of a dynamic-array descriptor. Static
  /// arrays get a shadow descriptor in the data area, (re)filled at the call
  /// site so the callee can index uniformly.
  /// </summary>
  private void EmitArrayArgument(Expression arg, ProcedureSymbol proc) {
    if (!model.VariableBindings.TryGetValue(arg, out var symbol) || symbol.Type is not ArrayType arrayType) {
      this.Unsupported(arg, $"array argument to {proc.Name}");
      return;
    }
    this.EmitArrayDescriptorPush(arg, symbol, arrayType);
  }

  private readonly Dictionary<VariableSymbol, Label> _shadowDescriptors = new(ReferenceEqualityComparer.Instance);

  private Label ShadowDescriptorOf(VariableSymbol symbol, ArrayType arrayType) {
    if (!this._shadowDescriptors.TryGetValue(symbol, out var label))
      this._shadowDescriptors[symbol] = label = this._asm.DefineLabel($"ad_{symbol.Name}_{this._shadowDescriptors.Count}");
    _ = arrayType;
    return label;
  }

  private void EmitByValArgument(Expression arg, PbType argType, PbType parameterType) {
    var asm = this._asm;
    this.EmitExpression(arg);
    this.Coerce(argType, parameterType, arg);
    switch (KindOf(parameterType)) {
      case ValueKind.Int16 or ValueKind.Str:
        asm.Push(Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        break;
      case ValueKind.Float: {
        var size = parameterType.Size;
        if (parameterType is BcdType { IsFixedPoint: true }) {        // FIX: scaled int64 cell
          asm.Call(asm.Lbl("rt_fixup"));
          asm.Fistp(Mem.Qword(this._scratch));
        } else
          switch (size) {
            case 4: asm.Fstp(Mem.Dword(this._scratch)); break;
            case 8: asm.Fstp(Mem.Qword(this._scratch)); break;
            default: asm.Fstp(Mem.Tbyte(this._scratch)); break;
          }
        for (var offset = ((size + 1) & ~1) - 2; offset >= 0; offset -= 2)
          asm.Push(Mem.Word(this._scratch, offset));
        break;
      }
    }
  }

  private void EmitStoreTempArgument(Mem temp, PbType type, Expression at, List<Mem> stringTemps) {
    var asm = this._asm;
    switch (KindOf(type)) {
      case ValueKind.Int16:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(temp, 2, OperandSize.Word), Reg.DX);
        break;
      case ValueKind.Float:
        if (type is BcdType { IsFixedPoint: true }) {                  // FIX: scaled int64 cell
          asm.Call(asm.Lbl("rt_fixup"));
          asm.Fistp(temp.WithSize(OperandSize.Qword));
          break;
        }
        switch (type.Size) {
          case 4: asm.Fstp(temp.WithSize(OperandSize.Dword)); break;
          case 8: asm.Fstp(temp.WithSize(OperandSize.Qword)); break;
          default: asm.Fstp(temp.WithSize(OperandSize.Tbyte)); break;
        }
        break;
      case ValueKind.Str when type is StringType or FlexType:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        stringTemps.Add(temp);
        break;
      default:
        this.Unsupported(at, $"byref temp of {type}");
        break;
    }
  }
}
