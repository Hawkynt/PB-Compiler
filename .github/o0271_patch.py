from pathlib import Path

path = Path("PowerBasic.Compiler/Backend/InstructionSelector.cs")
raw = path.read_bytes()
text = raw.decode("utf-8")
uses_crlf = b"\r\n" in raw
if uses_crlf:
  text = text.replace("\r\n", "\n")

old = '''      case IrGlobalVariable g when IsAddressableGlobal(g):
        operand = new MOperand.DataOffset(g.Name, 0);
        return true;
'''
new = '''      // A function value is its near code offset. Keep it symbolic until emission so the whole-program
      // resolver can bind a local body, external declaration, or runtime entry to the same address direct CALL uses.
      case IrFunction function:
        operand = new MOperand.LabelRef(function.Name);
        return true;
      case IrGlobalVariable g when IsAddressableGlobal(g):
        operand = new MOperand.DataOffset(g.Name, 0);
        return true;
'''
assert text.count(old) == 1, "IrFunction operand insertion point changed"
text = text.replace(old, new, 1)

old = '''        if (!this.TryOperand(cast.Value, out var pointer))
          return false;
        if (pointer is not MOperand.Register held)
          return this.Decline("ptrtoint: the address is not in a register");
        this._vregs[cast] = held.Reg with { Size = MRegSize.Word };
        return true;
'''
new = '''        if (!this.TryOperand(cast.Value, out var pointer))
          return false;
        // A function has no defining instruction that could already have put its address in a vreg.
        // Materialize the symbolic code offset here, just as the global-data arm above materializes OFFSET data.
        if (pointer is MOperand.LabelRef functionAddress) {
          var offsetReg = this.FreshVreg(to);
          var offsetDest = new MOperand.Register(offsetReg);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [offsetDest, functionAddress],
            MovEffect(offsetDest, functionAddress)));
          this._vregs[cast] = offsetReg;
          return true;
        }
        if (pointer is not MOperand.Register held)
          return this.Decline("ptrtoint: the address is not in a register");
        this._vregs[cast] = held.Reg with { Size = MRegSize.Word };
        return true;
'''
assert text.count(old) == 1, "PtrToInt insertion point changed"
text = text.replace(old, new, 1)

start = text.index("  private bool SelectCall(IrCall call, MBlock block) {")
end = text.index("  private bool PushStackCallArgument", start)
replacement = '''  private bool SelectCall(IrCall call, MBlock block) {
    var callee = call.Callee as IrFunction;
    // A declaration is one of two very different things. A RUNTIME routine has a hand-written body
    // with a register convention, and reaching it needs an entry in the ABI table - anything not
    // listed declines, which is the signal the coverage census reads. An EXTERNAL user procedure has
    // no body HERE but a source-declared ABI, supplied by another object file and resolved by the
    // linker; its IrCall convention chooses the ordinary stack-call path below. An indirect callee
    // has no declaration to classify and therefore goes straight to that ordinary ABI path.
    if (callee is { IsDeclaration: true }) {
      if (NonLocalJumpIntrinsics.Contains(callee.Name))
        return this.SelectNonLocalJumpIntrinsic(call, callee);
      if (MathSequence(callee.Name, this._target.Cpu386OrLater) is { } sequence)
        return this.SelectMathIntrinsic(call, callee, sequence);
      if (callee.Name == "rt_str_concat_n")
        return this.SelectMultiConcat(call);
      if (RuntimeAbi.For(callee.Name) is { } routine)
        return this.SelectRuntimeCall(call, callee, routine);
      if (IsRuntimeName(callee.Name))
        return this.Decline($"call: {callee.Name} (runtime declaration - not in the runtime ABI table)");
    }

    var calleeName = callee?.Name ?? "indirect callee";
    if (!call.Type.IsVoid && !call.Type.IsIeeeFloat && !IsWide(call.Type)
        && RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: {calleeName} returns {call.Type} (unsupported result shape)");

    var abi = X86CallAbi.For(call.Convention);
    if (abi.Distance != X86CallDistance.Near)
      return this.Decline($"call: {calleeName} uses a far return address");
    if (abi.ArgumentRegisters.Count > 0)
      return this.Decline($"call: {calleeName} uses {call.Convention} register arguments");

    var arguments = abi.StackArgumentOrder == X86StackArgumentOrder.RightToLeft
      ? call.Args.Reverse()
      : call.Args;
    var stackBytes = 0;
    foreach (var arg in arguments) {
      if (!this.PushStackCallArgument(arg, calleeName, out var argumentBytes))
        return false;
      stackBytes += argumentBytes;
    }

    if (callee is not null) {
      this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(callee.Name)],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: true, WritesMemory: true),
        condition: null, clobbers: _callClobbers));
    } else {
      if (!this.TryOperand(call.Callee, out var target))
        return false;

      // The call destroys every allocatable register. Leaving the target in a virtual register would
      // therefore make that vreg live into an instruction on which it has nowhere legal to reside.
      // Stage it into a fixed physical register immediately before CALL: the source dies at the MOV,
      // while the CALL's explicit read keeps SI intact for exactly the one instruction that needs it.
      var stagedTarget = new MOperand.Register(MReg.Physical_(Reg.SI, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [stagedTarget, target],
        MovEffect(stagedTarget, target), condition: null, clobbers: [Reg.SI]));
      this._current.Instructions.Add(new MInstr(MOpcode.Call, [stagedTarget],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: true, WritesMemory: true),
        condition: null, clobbers: _callClobbers));
    }

    if (abi.StackCleanup == X86StackCleanup.Caller && stackBytes > 0) {
      var sp = new MOperand.Register(MReg.Physical_(Reg.SP, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Add,
        [sp, new MOperand.Immediate(stackBytes)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false),
        condition: null, clobbers: [Reg.SP]));
    }

    if (call.Type.IsVoid)
      return true;

    if (call.Type.IsIeeeFloat) {
      // The BASIC function ABI returns every IEEE real on ST(0); park it immediately so the x87
      // stack is empty again at the instruction boundary.
      this.EmitX87(MOpcode.Fstp, this.FloatCell(call), reads: false);
      return true;
    }

    if (IsWide(call.Type)) {
      // a 32-bit result comes back in DX:AX, the convention the direct codegen documents
      var (lo, hi) = this.FreshPair(call);
      var axResult = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
      var dxResult = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, axResult], MovEffect(lo, axResult)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, dxResult], MovEffect(hi, dxResult)));
      return true;
    }

    // the result is in AX; copy it into the call's own virtual register so the allocator may place
    // the value anywhere (the copy is free when it lands in AX again)
    var dest = this.FreshVreg(call.Type);
    this._vregs[call] = dest;
    var destOp = new MOperand.Register(dest);
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, RegSize(call.Type)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ax], MovEffect(destOp, ax)));
    return true;
  }

'''
text = text[:start] + replacement + text[end:]
if uses_crlf:
  text = text.replace("\n", "\r\n")
path.write_bytes(text.encode("utf-8"))
