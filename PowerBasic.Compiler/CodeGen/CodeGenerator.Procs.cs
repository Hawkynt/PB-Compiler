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
  /// so the last parameter sits at [BP+4]), stack locals below BP. STATIC
  /// variables and arrays use data segment slots instead.
  /// </summary>
  private int LayoutFrame(ProcedureSymbol proc) {
    var offset = 4;
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

    asm.MarkLabel(this.ProcLabelOf(proc));
    this.BeginFrame();

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
    if (paramBytes > 0)
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
    this.EmitCall(proc, c.Arguments, wantResult: false, c.Position);
  }

  /// <summary>
  /// Emits a SUB/FUNCTION invocation: arguments pushed left to right (BYREF =
  /// near pointer, BYVAL = value; BYVAL strings transfer temp ownership to the
  /// callee), RET n cleans up. Results: AX / DX:AX / ST0 / string handle in AX.
  /// </summary>
  private void EmitCall(ProcedureSymbol proc, IReadOnlyList<Expression> args, bool wantResult, SourcePosition position) {
    var asm = this._asm;
    if (proc.IsExternal && !this._allowExternalCalls) {
      this.Unsupported(position, $"external procedure {proc.Name} (no $LINK provides it)");
      return;
    }
    if (args.Count != proc.Parameters.Count) {
      this.Unsupported(position, $"argument count for {proc.Name}");
      return;
    }

    var tempBytesUsed = 0;
    var stringTemps = new List<Mem>();

    for (var i = 0; i < args.Count; ++i) {
      var parameter = proc.Parameters[i];
      var arg = args[i];
      var argType = model.TypeOf(arg);

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
    var asm = this._asm;
    if (!model.VariableBindings.TryGetValue(arg, out var symbol) || symbol.Type is not ArrayType arrayType) {
      this.Unsupported(arg, $"array argument to {proc.Name}");
      return;
    }

    if (symbol.Storage == VariableStorage.Parameter) {
      asm.Push(Mem.Word(Reg.BP, symbol.Offset));   // forward the descriptor pointer
      return;
    }

    if (arrayType.IsDynamic) {
      asm.Push(Imm.OffsetOf(this.SlotOf(symbol)));
      return;
    }

    var descriptor = this.ShadowDescriptorOf(symbol, arrayType);
    asm.Mov(Mem.Word(descriptor), Reg.DS);
    asm.Mov(Mem.Word(descriptor, 2), Imm.OffsetOf(this.SlotOf(symbol)));
    asm.Mov(Mem.Word(descriptor, 4), Math.Max(arrayType.Element.Size, 1));
    asm.Mov(Mem.Word(descriptor, 6), arrayType.Rank);
    for (var d = 0; d < arrayType.Rank; ++d) {
      var (lower, upper) = arrayType.StaticBounds![d];
      asm.Mov(Mem.Word(descriptor, 8 + d * 4), lower);
      asm.Mov(Mem.Word(descriptor, 8 + d * 4 + 2), upper - lower + 1);
    }
    asm.Push(Imm.OffsetOf(descriptor));
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
