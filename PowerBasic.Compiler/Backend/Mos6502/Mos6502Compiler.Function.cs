using PowerBasic.Compiler.Ir;
using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

public static partial class Mos6502Compiler {

  private sealed partial class ModuleGenerator {

    /// <summary>One function: its blocks in IR order, each instruction lowered in place.</summary>
    private sealed class FunctionGenerator(ModuleGenerator module, IrFunction function) {

      private readonly Mos6502Assembler _asm = module._asm;
      private readonly Mos6502Runtime _runtime = module._runtime;
      private readonly Frame _frame = module._frames[function];
      private readonly Dictionary<IrBasicBlock, M6502Label> _blocks = [];
      private IrBasicBlock? _next;
      private int _local;

      public void Generate() {
        this._asm.Bind(module._entries[function]);
        foreach (var block in function.Blocks)
          this._blocks.Add(block, this._asm.NewLabel($"{function.Name}.{block.Label}"));
        for (var i = 0; i < function.Blocks.Count; ++i) {
          var block = function.Blocks[i];
          this._next = i + 1 < function.Blocks.Count ? function.Blocks[i + 1] : null;
          this._asm.Bind(this._blocks[block]);
          var instructions = block.Instructions;
          for (var j = 0; j < instructions.Count; ++j) {
            var instruction = instructions[j];
            // a compare that only steers the branch after it is folded into that branch
            if (instruction is IrCmp compare && j + 1 < instructions.Count
                && instructions[j + 1] is IrCondBr branch && branch.Condition == compare
                && compare.Users.Count == 1)
              continue;
            this.Lower(block, instruction);
          }
        }
      }

      private M6502Label Local(string what) => this._asm.NewLabel($"{function.Name}.{what}{this._local++}");

      // --- operands --------------------------------------------------------------------------

      private Operand Of(IrValue value) {
        switch (value) {
          case IrConstantInt constant:
            return new ConstantOperand(constant.Value);
          case IrNullPtr or IrUndef:
            return new ConstantOperand(0);
          case IrGlobalVariable global:
            return new AddressOperand(module._globals[global]);
          case IrAlloca alloca:
            return new AddressOperand(this._frame.AddressOf(alloca));
          case IrGep gep when this.FoldedAddress(gep) is { } folded:
            return new AddressOperand(folded);
          case IrFunction:
            throw Decline($"'{function.Name}' takes a procedure's address, which has no 6502 lowering yet");
          case IrConstantFloat:
            throw Decline("floating point has no 6502 lowering yet");
          default:
            if (this._frame.Offsets.ContainsKey(value))
              return new MemoryOperand(this._frame.AddressOf(value));
            throw Decline($"'{function.Name}' uses a {value.GetType().Name}, which has no 6502 lowering");
        }
      }

      /// <summary>A GEP whose base and offset are known at assembly time is an address, not a computation.</summary>
      private M6502Address? FoldedAddress(IrGep gep) {
        if (this.Of(gep.BasePtr) is not AddressOperand { Address: var address } || gep.ByteOffset is not IrConstantInt offset)
          return null;
        return address.Plus(checked((int)(offset.Value * Scale(gep))));
      }

      private static int Scale(IrGep gep) => gep.ElementType is { } element ? SizeOf(element) : 1;

      /// <summary><c>op</c> with byte <paramref name="k"/> of an operand <paramref name="size"/> bytes wide.</summary>
      private void WithByte(M6502Op op, Operand operand, int k, int size) {
        switch (operand) {
          case ConstantOperand constant:
            this._asm.Immediate(op, k < 8 ? (int)((constant.Value >> (8 * k)) & 0xFF) : 0);
            break;
          case MemoryOperand memory when k < size:
            this._asm.Memory(op, memory.Address.Plus(k));
            break;
          case MemoryOperand:
            this._asm.Immediate(op, 0);
            break;
          case AddressOperand address when k == 0:
            this._asm.ImmediateLow(op, address.Address);
            break;
          case AddressOperand address when k == 1:
            this._asm.ImmediateHigh(op, address.Address);
            break;
          case AddressOperand:
            this._asm.Immediate(op, 0);
            break;
        }
      }

      private M6502Address Destination(IrValue value) => this._frame.AddressOf(value);

      private bool Stored(IrInstruction instruction) => this._frame.Offsets.ContainsKey(instruction);

      /// <summary>Copies <paramref name="bytes"/> bytes of a value, zero- or sign-extending it past its own width.</summary>
      private void Copy(Operand source, int sourceSize, M6502Address destination, int bytes, bool signed = false) {
        var direct = Math.Min(sourceSize, bytes);
        for (var k = 0; k < direct; ++k) {
          this.WithByte(Lda, source, k, sourceSize);
          this._asm.Memory(Sta, destination.Plus(k));
        }
        if (bytes > direct)
          this.Fill(destination.Plus(direct), bytes - direct, signed ? (source, sourceSize) : null);
      }

      /// <summary>Fills with zeros, or with the sign of <paramref name="signOf"/>.</summary>
      private void Fill(M6502Address destination, int bytes, (Operand Operand, int Size)? signOf) {
        if (signOf is { } sign) {
          this.WithByte(Lda, sign.Operand, sign.Size - 1, sign.Size);
          this._asm.EmitAccumulator(Asl);
          this._asm.Immediate(Lda, 0);
          this._asm.Immediate(Adc, 0xFF);
          this._asm.Immediate(Eor, 0xFF);
        } else {
          this._asm.Immediate(Lda, 0);
        }
        for (var k = 0; k < bytes; ++k)
          this._asm.Memory(Sta, destination.Plus(k));
      }

      // --- instructions ------------------------------------------------------------------------

      private void Lower(IrBasicBlock block, IrInstruction instruction) {
        switch (instruction) {
          case IrPhi or IrAlloca:
            return;
          case IrBinary binary when this.Stored(binary): this.LowerBinary(binary); return;
          case IrCmp compare when this.Stored(compare): this.LowerCompare(compare); return;
          case IrCast cast when this.Stored(cast): this.LowerCast(cast); return;
          case IrSelect select when this.Stored(select): this.LowerSelect(select); return;
          case IrGep gep when this.Stored(gep): this.LowerGep(gep); return;
          case IrBinary or IrCmp or IrCast or IrSelect or IrGep:
            return;
          case IrLoad load: this.LowerLoad(load); return;
          case IrStore store: this.LowerStore(store); return;
          case IrCall call: this.LowerCall(call); return;
          case IrRet ret: this.LowerReturn(ret); return;
          case IrBr branch:
            this.Edge(block, branch.Target);
            this.JumpUnlessNext(branch.Target);
            return;
          case IrCondBr branch: this.LowerConditionalBranch(block, branch); return;
          case IrSwitch @switch: this.LowerSwitch(block, @switch); return;
          case IrUnreachable:
            this.RaiseError(51);
            return;
          default:
            throw Decline($"'{function.Name}' uses {instruction.GetType().Name}, which has no 6502 lowering yet");
        }
      }

      private void LowerBinary(IrBinary binary) {
        if (binary.IsFloatOp)
          throw Decline("floating point has no 6502 lowering yet");
        var size = SizeOf(binary.Type);
        var lhs = this.Of(binary.Lhs);
        var rhs = this.Of(binary.Rhs);
        var destination = this.Destination(binary);
        switch (binary.Op) {
          case IrBinaryOp.Add or IrBinaryOp.Sub:
            this._asm.Emit(binary.Op == IrBinaryOp.Add ? Clc : Sec);
            for (var k = 0; k < size; ++k) {
              this.WithByte(Lda, lhs, k, size);
              this.WithByte(binary.Op == IrBinaryOp.Add ? Adc : Sbc, rhs, k, size);
              this._asm.Memory(Sta, destination.Plus(k));
            }
            return;
          case IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor:
            var op = binary.Op switch { IrBinaryOp.And => M6502Op.And, IrBinaryOp.Or => Ora, _ => Eor };
            for (var k = 0; k < size; ++k) {
              this.WithByte(Lda, lhs, k, size);
              this.WithByte(op, rhs, k, size);
              this._asm.Memory(Sta, destination.Plus(k));
            }
            return;
          case IrBinaryOp.Mul:
            this.Arithmetic(lhs, rhs, size, signed: false,
              size <= 2 ? M6502Routine.Multiply16 : M6502Routine.Multiply32, Zp.Ret, destination);
            return;
          case IrBinaryOp.SDiv or IrBinaryOp.SRem or IrBinaryOp.UDiv or IrBinaryOp.URem:
            var signed = binary.Op is IrBinaryOp.SDiv or IrBinaryOp.SRem;
            var routine = (signed, size <= 2) switch {
              (true, true) => M6502Routine.SignedDivide16,
              (true, false) => M6502Routine.SignedDivide32,
              (false, true) => M6502Routine.UnsignedDivide16,
              _ => M6502Routine.UnsignedDivide32,
            };
            var result = binary.Op is IrBinaryOp.SDiv or IrBinaryOp.UDiv ? Zp.Arg : Zp.Temp;
            this.Arithmetic(lhs, rhs, size, signed, routine, result, destination);
            return;
          case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
            this.Shift(binary.Op, lhs, rhs, SizeOf(binary.Rhs.Type), size, destination);
            return;
          default:
            throw Decline($"{binary.Op} has no 6502 lowering yet");
        }
      }

      /// <summary>A runtime multiply or divide: operands widened into Arg and ArgB, the result copied back.</summary>
      private void Arithmetic(Operand lhs, Operand rhs, int size, bool signed, M6502Routine routine,
          M6502Address result, M6502Address destination) {
        if (size > 4)
          throw Decline("64-bit multiplication and division have no 6502 lowering yet");
        var width = size <= 2 ? 2 : 4;
        this.Copy(lhs, size, Zp.Arg, width, signed);
        this.Copy(rhs, size, Zp.ArgB, width, signed);
        this._asm.Call(this._runtime.Routine(routine));
        this.Copy(new MemoryOperand(result), size, destination, size);
      }

      private void Shift(IrBinaryOp op, Operand value, Operand count, int countSize, int size, M6502Address destination) {
        if (count is ConstantOperand { Value: var constant }) {
          if (constant >= size * 8) {
            this.Fill(destination, size, op == IrBinaryOp.AShr ? (value, size) : null);
            return;
          }
          var bytes = (int)constant / 8;
          if (op == IrBinaryOp.Shl) {
            for (var k = size - 1; k >= bytes; --k) {
              this.WithByte(Lda, value, k - bytes, size);
              this._asm.Memory(Sta, destination.Plus(k));
            }
            if (bytes > 0)
              this.Fill(destination, bytes, null);
          } else {
            for (var k = 0; k < size - bytes; ++k) {
              this.WithByte(Lda, value, k + bytes, size);
              this._asm.Memory(Sta, destination.Plus(k));
            }
            if (bytes > 0)
              this.Fill(destination.Plus(size - bytes), bytes, op == IrBinaryOp.AShr ? (value, size) : null);
          }
          for (var bit = 0; bit < constant % 8; ++bit)
            this.ShiftOnce(op, destination, size);
          return;
        }
        if (count is not (MemoryOperand or ConstantOperand))
          throw Decline("a shift by an address has no meaning");
        this.Copy(value, size, destination, size);
        var loop = this.Local("shift");
        var done = this.Local("shifted");
        this.WithByte(Lda, count, 0, countSize);
        this._asm.Emit(Tax);
        this._asm.Branch(Beq, done);
        this._asm.Bind(loop);
        this.ShiftOnce(op, destination, size);
        this._asm.Emit(Dex);
        this._asm.Branch(Bne, loop);
        this._asm.Bind(done);
      }

      private void ShiftOnce(IrBinaryOp op, M6502Address cell, int size) {
        switch (op) {
          case IrBinaryOp.Shl:
            this._asm.Memory(Asl, cell);
            for (var k = 1; k < size; ++k)
              this._asm.Memory(Rol, cell.Plus(k));
            return;
          case IrBinaryOp.LShr:
            this._asm.Memory(Lsr, cell.Plus(size - 1));
            break;
          default:
            this._asm.Memory(Lda, cell.Plus(size - 1));
            this._asm.EmitAccumulator(Asl);
            this._asm.Memory(Ror, cell.Plus(size - 1));
            break;
        }
        for (var k = size - 2; k >= 0; --k)
          this._asm.Memory(Ror, cell.Plus(k));
      }

      private void LowerCompare(IrCmp compare) {
        var holds = this.Local("true");
        var done = this.Local("compared");
        this.BranchIf(compare, holds);
        this._asm.Immediate(Lda, 0);
        this._asm.Jump(done);
        this._asm.Bind(holds);
        this._asm.Immediate(Lda, 1);
        this._asm.Bind(done);
        this._asm.Memory(Sta, this.Destination(compare));
      }

      /// <summary>Jumps to <paramref name="target"/> when the comparison holds; falls through when it does not.</summary>
      private void BranchIf(IrCmp compare, M6502Label target) {
        if (compare.Lhs.Type.IsFloat)
          throw Decline("floating point has no 6502 lowering yet");
        var size = SizeOf(compare.Lhs.Type);
        var (lhs, rhs) = (this.Of(compare.Lhs), this.Of(compare.Rhs));
        switch (compare.Pred) {
          case IrCmpPred.Eq: {
            var differs = this.Local("differs");
            for (var k = 0; k < size; ++k) {
              this.WithByte(Lda, lhs, k, size);
              this.WithByte(Cmp, rhs, k, size);
              if (k < size - 1)
                this._asm.Branch(Bne, differs);
              else
                this._asm.Branch(Beq, target);
            }
            this._asm.Bind(differs);
            return;
          }
          case IrCmpPred.Ne:
            for (var k = 0; k < size; ++k) {
              this.WithByte(Lda, lhs, k, size);
              this.WithByte(Cmp, rhs, k, size);
              this._asm.Branch(Bne, target);
            }
            return;
          case IrCmpPred.Ult: this.Subtract(lhs, rhs, size); this._asm.Branch(Bcc, target); return;
          case IrCmpPred.Uge: this.Subtract(lhs, rhs, size); this._asm.Branch(Bcs, target); return;
          case IrCmpPred.Ugt: this.Subtract(rhs, lhs, size); this._asm.Branch(Bcc, target); return;
          case IrCmpPred.Ule: this.Subtract(rhs, lhs, size); this._asm.Branch(Bcs, target); return;
          case IrCmpPred.Slt: this.SignedLess(lhs, rhs, size); this._asm.Branch(Bmi, target); return;
          case IrCmpPred.Sge: this.SignedLess(lhs, rhs, size); this._asm.Branch(Bpl, target); return;
          case IrCmpPred.Sgt: this.SignedLess(rhs, lhs, size); this._asm.Branch(Bmi, target); return;
          case IrCmpPred.Sle: this.SignedLess(rhs, lhs, size); this._asm.Branch(Bpl, target); return;
          default:
            throw Decline("floating point has no 6502 lowering yet");
        }
      }

      /// <summary>
      /// <c>a - b</c> for its flags: carry clear exactly when <c>a &lt; b</c> unsigned. The low byte
      /// is a <c>CMP</c>, which needs no <c>SEC</c> - but a <c>CMP</c> leaves V alone, so a signed
      /// comparison asks for <c>SBC</c> throughout.
      /// </summary>
      private void Subtract(Operand a, Operand b, int size, bool forOverflow = false) {
        if (forOverflow)
          this._asm.Emit(Sec);
        for (var k = 0; k < size; ++k) {
          this.WithByte(Lda, a, k, size);
          this.WithByte(k == 0 && !forOverflow ? Cmp : Sbc, b, k, size);
        }
      }

      /// <summary><c>a - b</c>, then N set exactly when <c>a &lt; b</c> signed: N xor V of the top byte.</summary>
      private void SignedLess(Operand a, Operand b, int size) {
        this.Subtract(a, b, size, forOverflow: true);
        var noOverflow = this.Local("noOverflow");
        this._asm.Branch(Bvc, noOverflow);
        this._asm.Immediate(Eor, 0x80);
        this._asm.Bind(noOverflow);
      }

      private void LowerCast(IrCast cast) {
        var source = this.Of(cast.Value);
        var sourceSize = SizeOf(cast.Value.Type);
        var size = SizeOf(cast.Type);
        var destination = this.Destination(cast);
        switch (cast.Op) {
          case IrCastOp.Trunc or IrCastOp.ZExt or IrCastOp.PtrToInt or IrCastOp.IntToPtr or IrCastOp.BitCast:
            this.Copy(source, sourceSize, destination, size);
            return;
          case IrCastOp.SExt when cast.Value.Type.IsBool:
            // true is all ones
            this._asm.Immediate(Lda, 0);
            this._asm.Emit(Sec);
            this.WithByte(Sbc, source, 0, sourceSize);
            for (var k = 0; k < size; ++k)
              this._asm.Memory(Sta, destination.Plus(k));
            return;
          case IrCastOp.SExt:
            this.Copy(source, sourceSize, destination, size, signed: true);
            return;
          default:
            throw Decline($"the {cast.Op} conversion has no 6502 lowering yet");
        }
      }

      private void LowerSelect(IrSelect select) {
        var size = SizeOf(select.Type);
        var destination = this.Destination(select);
        var otherwise = this.Local("otherwise");
        var done = this.Local("selected");
        this.WithByte(Lda, this.Of(select.Condition), 0, 1);
        this._asm.Branch(Beq, otherwise);
        this.Copy(this.Of(select.IfTrue), size, destination, size);
        this._asm.Jump(done);
        this._asm.Bind(otherwise);
        this.Copy(this.Of(select.IfFalse), size, destination, size);
        this._asm.Bind(done);
      }

      private void LowerGep(IrGep gep) {
        var destination = this.Destination(gep);
        var offsetSize = SizeOf(gep.ByteOffset.Type);
        this.Copy(this.Of(gep.ByteOffset), offsetSize, destination, 2, signed: true);
        var scale = Scale(gep);
        if (scale != 1 && (scale & (scale - 1)) == 0) {
          for (var bit = scale; bit > 1; bit >>= 1)
            this.ShiftOnce(IrBinaryOp.Shl, destination, 2);
        } else if (scale != 1) {
          this.Copy(new MemoryOperand(destination), 2, Zp.Arg, 2);
          this.Copy(new ConstantOperand(scale), 2, Zp.ArgB, 2);
          this._asm.Call(this._runtime.Routine(M6502Routine.Multiply16));
          this.Copy(new MemoryOperand(Zp.Ret), 2, destination, 2);
        }
        var basePointer = this.Of(gep.BasePtr);
        this._asm.Emit(Clc);
        for (var k = 0; k < 2; ++k) {
          this._asm.Memory(Lda, destination.Plus(k));
          this.WithByte(Adc, basePointer, k, 2);
          this._asm.Memory(Sta, destination.Plus(k));
        }
      }

      /// <summary>Where a load or store goes: a fixed address, or through <see cref="Zp.Ptr"/>.</summary>
      private M6502Address? Target(IrValue pointer) {
        if (pointer.Type.IsFarPointer || pointer is IrFarPtr)
          throw Decline("far pointers have no 6502 meaning");
        switch (this.Of(pointer)) {
          case AddressOperand address:
            return address.Address;
          case ConstantOperand constant:
            return M6502Address.Absolute((int)(constant.Value & 0xFFFF));
          case var computed:
            this.Copy(computed, 2, Zp.Ptr, 2);
            return null;
        }
      }

      private void LowerLoad(IrLoad load) {
        if (!this.Stored(load))
          return;
        var size = SizeOf(load.Type);
        var target = this.Target(load.Pointer);
        var destination = this.Destination(load);
        for (var k = 0; k < size; ++k) {
          if (target is { } fixedAddress) {
            this._asm.Memory(Lda, fixedAddress.Plus(k));
          } else {
            this._asm.Immediate(Ldy, k);
            this._asm.IndirectY(Lda, 0x02);
          }
          this._asm.Memory(Sta, destination.Plus(k));
        }
      }

      private void LowerStore(IrStore store) {
        var size = SizeOf(store.Value.Type);
        var value = this.Of(store.Value);
        var target = this.Target(store.Pointer);
        for (var k = 0; k < size; ++k) {
          this.WithByte(Lda, value, k, size);
          if (target is { } fixedAddress) {
            this._asm.Memory(Sta, fixedAddress.Plus(k));
          } else {
            this._asm.Immediate(Ldy, k);
            this._asm.IndirectY(Sta, 0x02);
          }
        }
      }

      private void LowerCall(IrCall call) {
        if (call.Callee is not IrFunction callee)
          throw Decline($"'{function.Name}' calls through a pointer, which has no 6502 lowering yet");
        if (call.Convention == IrCallConvention.BasicClosure)
          throw Decline("delegates have no 6502 lowering yet");
        if (callee.IsDeclaration) {
          this.CallRuntime(call, callee);
          return;
        }

        var reenters = module.Reenters(function, callee);
        if (reenters)
          this.MoveFrame(M6502Routine.SaveFrame);
        var arguments = call.Args.ToList();
        var calleeFrame = module._frames[callee];
        if (reenters) {
          // the arguments may read the very parameter cells they are about to overwrite: stage them
          var offset = 0;
          foreach (var argument in arguments) {
            var size = SizeOf(argument.Type);
            this.Copy(this.Of(argument), size, new M6502Address(module._staging, offset), size);
            offset += size;
          }
          offset = 0;
          for (var i = 0; i < arguments.Count; ++i) {
            var size = SizeOf(arguments[i].Type);
            this.Copy(new MemoryOperand(new M6502Address(module._staging, offset)), size,
              calleeFrame.AddressOf(callee.Parameters[i]), size);
            offset += size;
          }
        } else {
          for (var i = 0; i < arguments.Count; ++i) {
            var size = SizeOf(arguments[i].Type);
            this.Copy(this.Of(arguments[i]), size, calleeFrame.AddressOf(callee.Parameters[i]), size);
          }
        }
        this._asm.Call(module._entries[callee]);
        if (reenters)
          this.MoveFrame(M6502Routine.RestoreFrame);
        if (this.Stored(call))
          this.Copy(new MemoryOperand(Zp.Ret), SizeOf(call.Type), this.Destination(call), SizeOf(call.Type));
      }

      private void MoveFrame(M6502Routine routine) {
        this.Copy(new AddressOperand(this._frame.Start), 2, Zp.Arg, 2);
        this.Copy(new ConstantOperand(this._frame.Size), 2, Zp.Arg.Plus(2), 2);
        this._asm.Call(this._runtime.Routine(routine));
      }

      private void CallRuntime(IrCall call, IrFunction callee) {
        if (callee.Name == "rt_unreachable") {
          this.RaiseError(51);
          return;
        }
        var routine = callee.Name switch {
          "rt_print_i8" => M6502Routine.PrintI8,
          "rt_print_i16" => M6502Routine.PrintI16,
          "rt_print_i32" => M6502Routine.PrintI32,
          "rt_print_u8" => M6502Routine.PrintU8,
          "rt_print_u16" => M6502Routine.PrintU16,
          "rt_print_u32" => M6502Routine.PrintU32,
          "rt_print_nl" => M6502Routine.PrintNewLine,
          "rt_print_comma" or "rt_print_zone" => M6502Routine.PrintZone,
          "rt_print_tab" => M6502Routine.PrintTab,
          "rt_print_spc" => M6502Routine.PrintSpaces,
          "rt_print_str" => M6502Routine.PrintString,
          "rt_error" => M6502Routine.Error,
          "rt_end" => M6502Routine.End,
          "rt_inp" => M6502Routine.ReturnZero,
          "rt_outp" => M6502Routine.Nothing,
          _ => throw Decline($"the 6502 runtime has no {callee.Name} yet"),
        };
        var offset = 0;
        foreach (var argument in call.Args) {
          var size = SizeOf(argument.Type);
          if (offset + size > Zp.ArgumentBytes)
            throw Decline($"{callee.Name} takes more argument bytes than the 6502 runtime passes");
          this.Copy(this.Of(argument), size, Zp.Arg.Plus(offset), size);
          offset += size;
        }
        this._asm.Call(this._runtime.Routine(routine));
        if (this.Stored(call))
          this.Copy(new MemoryOperand(Zp.Ret), SizeOf(call.Type), this.Destination(call), SizeOf(call.Type));
      }

      private void RaiseError(int code) {
        this.Copy(new ConstantOperand(code), 4, Zp.Arg, 4);
        this._asm.Jump(this._runtime.Routine(M6502Routine.Error));
      }

      private void LowerReturn(IrRet ret) {
        if (ret.Value is { } value) {
          var size = SizeOf(value.Type);
          this.Copy(this.Of(value), size, Zp.Ret, size);
        }
        this._asm.Emit(Rts);
      }

      // --- control flow ------------------------------------------------------------------------

      /// <summary>
      /// The phi copies of the edge <paramref name="from"/> → <paramref name="to"/>. They are a
      /// parallel assignment, so when one phi reads another of the same block every source is
      /// staged before any phi is written.
      /// </summary>
      private void Edge(IrBasicBlock from, IrBasicBlock to) {
        var phis = to.Phis.ToList();
        if (phis.Count == 0)
          return;
        var moves = phis.Select(phi => (Phi: phi, Source: phi.IncomingFrom(from)
          ?? throw Decline($"a phi in '{function.Name}' has no value for one of its edges"))).ToList();
        if (moves.Any(move => move.Source is IrPhi phi && phi.Parent == to && phi != move.Phi)) {
          var offset = 0;
          foreach (var (phi, source) in moves) {
            var size = SizeOf(phi.Type);
            this.Copy(this.Of(source), size, this.Scratch(offset, size), size);
            offset += size;
          }
          offset = 0;
          foreach (var (phi, _) in moves) {
            var size = SizeOf(phi.Type);
            this.Copy(new MemoryOperand(this.Scratch(offset, size)), size, this.Destination(phi), size);
            offset += size;
          }
          return;
        }
        foreach (var (phi, source) in moves) {
          if (source == phi)
            continue;
          var size = SizeOf(phi.Type);
          this.Copy(this.Of(source), size, this.Destination(phi), size);
        }
      }

      /// <summary>Staging for a parallel copy, in the shared staging area, which grows to fit.</summary>
      private M6502Address Scratch(int offset, int size) {
        module._stagingBytes = Math.Max(module._stagingBytes, offset + size);
        return new(module._staging, offset);
      }

      private void JumpUnlessNext(IrBasicBlock target) {
        if (target != this._next)
          this._asm.Jump(this._blocks[target]);
      }

      /// <summary>The label to branch to for the edge to <paramref name="to"/>: the block, or a stub doing its phi copies.</summary>
      private M6502Label EdgeLabel(IrBasicBlock to, List<(M6502Label Stub, IrBasicBlock Target)> stubs) {
        if (!to.Phis.Any())
          return this._blocks[to];
        var stub = this.Local("edge");
        stubs.Add((stub, to));
        return stub;
      }

      private void EmitStubs(IrBasicBlock from, List<(M6502Label Stub, IrBasicBlock Target)> stubs) {
        foreach (var (stub, target) in stubs) {
          this._asm.Bind(stub);
          this.Edge(from, target);
          this._asm.Jump(this._blocks[target]);
        }
      }

      private void LowerConditionalBranch(IrBasicBlock block, IrCondBr branch) {
        var stubs = new List<(M6502Label, IrBasicBlock)>();
        var ifTrue = this.EdgeLabel(branch.IfTrue, stubs);
        if (branch.Condition is IrCmp compare && compare.Parent == block && compare.Users.Count == 1
            && block.Instructions[^2] == compare) {
          this.BranchIf(compare, ifTrue);
        } else {
          this.WithByte(Lda, this.Of(branch.Condition), 0, 1);
          this._asm.Branch(Bne, ifTrue);
        }
        this.Edge(block, branch.IfFalse);
        if (stubs.Count == 0)
          this.JumpUnlessNext(branch.IfFalse);
        else
          this._asm.Jump(this._blocks[branch.IfFalse]);
        this.EmitStubs(block, stubs);
      }

      private void LowerSwitch(IrBasicBlock block, IrSwitch @switch) {
        var stubs = new List<(M6502Label, IrBasicBlock)>();
        var size = SizeOf(@switch.Condition.Type);
        var condition = this.Of(@switch.Condition);
        foreach (var (value, target) in @switch.Cases) {
          var label = this.EdgeLabel(target, stubs);
          var differs = this.Local("case");
          for (var k = 0; k < size; ++k) {
            this.WithByte(Lda, condition, k, size);
            this._asm.Immediate(Cmp, (int)((value >> (8 * k)) & 0xFF));
            if (k < size - 1)
              this._asm.Branch(Bne, differs);
            else
              this._asm.Branch(Beq, label);
          }
          this._asm.Bind(differs);
        }
        this.Edge(block, @switch.DefaultTarget);
        this._asm.Jump(this._blocks[@switch.DefaultTarget]);
        this.EmitStubs(block, stubs);
      }
    }
  }
}
