using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;
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
        this.PreserveHandler(save: true);
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
          // TRY's handler triple is ON ERROR's own state
          case IrGlobalVariable { Name: "rt_onerr" }:
            return new AddressOperand(module._errorHandler);
          case IrGlobalVariable { Name: "rt_onerr_bp" }:
            return new AddressOperand(module._errorSoftStack);
          case IrGlobalVariable { Name: "rt_onerr_sp" }:
            return new AddressOperand(module._errorStack);
          case IrGlobalVariable global:
            return new AddressOperand(module._globals[global]);
          case IrAlloca alloca:
            return new AddressOperand(this._frame.AddressOf(alloca));
          case IrGep gep when this.FoldedAddress(gep) is { } folded:
            return new AddressOperand(folded);
          case IrBlockAddress address when this._blocks.TryGetValue(address.Block, out var label):
            return new AddressOperand(label);
          case IrFunction:
            throw Decline($"'{function.Name}' takes a procedure's address, which has no 6502 lowering yet");
          case IrConstantFloat constant:
            return new MemoryOperand(module.Constant(constant));
          default:
            if (this._frame.Holds(value))
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

      private bool Stored(IrInstruction instruction) => this._frame.Holds(instruction);

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
        if (binary.IsFloatOp) {
          this.Unpack(binary.Lhs, intoB: false);
          this.Unpack(binary.Rhs, intoB: true);
          this._asm.Call(this._runtime.Routine(binary.Op switch {
            IrBinaryOp.FAdd => M6502Routine.FloatAdd,
            IrBinaryOp.FSub => M6502Routine.FloatSubtract,
            IrBinaryOp.FMul => M6502Routine.FloatMultiply,
            _ => M6502Routine.FloatDivide,
          }));
          this.Pack(binary.Type, this.Destination(binary));
          return;
        }
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
              size switch { <= 2 => M6502Routine.Multiply16, 4 => M6502Routine.Multiply32, _ => M6502Routine.Multiply64 },
              Zp.Ret, destination);
            return;
          case IrBinaryOp.SDiv or IrBinaryOp.SRem or IrBinaryOp.UDiv or IrBinaryOp.URem:
            var signed = binary.Op is IrBinaryOp.SDiv or IrBinaryOp.SRem;
            var routine = (signed, size) switch {
              (true, <= 2) => M6502Routine.SignedDivide16,
              (true, 4) => M6502Routine.SignedDivide32,
              (true, _) => M6502Routine.SignedDivide64,
              (false, <= 2) => M6502Routine.UnsignedDivide16,
              (false, 4) => M6502Routine.UnsignedDivide32,
              _ => M6502Routine.UnsignedDivide64,
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
        var width = size <= 2 ? 2 : size <= 4 ? 4 : 8;
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
        if (compare.Lhs.Type.IsFloat) {
          // FloatCompare answers $FF, 0 or 1 in A for less, equal and greater
          this.Unpack(compare.Lhs, intoB: false);
          this.Unpack(compare.Rhs, intoB: true);
          this._asm.Call(this._runtime.Routine(M6502Routine.FloatCompare));
          var (answer, holdsWhenEqual) = compare.Pred switch {
            IrCmpPred.Foeq => (0, true),
            IrCmpPred.Fone => (0, false),
            IrCmpPred.Folt => (0xFF, true),
            IrCmpPred.Foge => (0xFF, false),
            IrCmpPred.Fogt => (1, true),
            IrCmpPred.Fole => (1, false),
            _ => throw Decline($"{compare.Pred} compares integers, not floats"),
          };
          this._asm.Immediate(Cmp, answer);
          this._asm.Branch(holdsWhenEqual ? Beq : Bne, target);
          return;
        }
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
          case IrCastOp.FPExt or IrCastOp.FPTrunc:
            this.Unpack(cast.Value, intoB: false);
            this.Pack(cast.Type, destination);
            return;
          case IrCastOp.SIToFP or IrCastOp.UIToFP:
            this.Copy(source, sourceSize, Zp.Arg, 8, signed: cast.Op == IrCastOp.SIToFP);
            this._asm.Call(this._runtime.Routine(cast.Op == IrCastOp.SIToFP ? M6502Routine.FloatFromSigned : M6502Routine.FloatFromUnsigned));
            this.Pack(cast.Type, destination);
            return;
          case IrCastOp.FPToSI or IrCastOp.FPToUI or IrCastOp.FPToSIRound or IrCastOp.FPToUIRound:
            this.Unpack(cast.Value, intoB: false);
            this._asm.Immediate(Ldx, size);
            this._asm.Call(this._runtime.Routine(cast.Op switch {
              IrCastOp.FPToSI => M6502Routine.FloatToSignedTruncate,
              IrCastOp.FPToSIRound => M6502Routine.FloatToSignedRound,
              IrCastOp.FPToUI => M6502Routine.FloatToUnsignedTruncate,
              _ => M6502Routine.FloatToUnsignedRound,
            }));
            this.Copy(new MemoryOperand(Zp.Ret), size, destination, size);
            return;
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

      private static M6502FloatFormat FormatOf(IrType type) => type.Bits switch {
        32 => M6502FloatFormat.Single,
        64 => M6502FloatFormat.Double,
        _ => M6502FloatFormat.Extended,
      };

      /// <summary>Points <c>Ptr</c> at a float and unpacks it into accumulator A or B.</summary>
      private void Unpack(IrValue value, bool intoB) {
        if (this.Of(value) is not MemoryOperand { Address: var address })
          throw Decline($"a float in '{function.Name}' is not in memory");
        this.Copy(new AddressOperand(address), 2, Zp.Ptr, 2);
        this._asm.Call(this._runtime.Routine(Mos6502Runtime.Unpack(FormatOf(value.Type), intoB)));
      }

      /// <summary>Rounds accumulator A into <paramref name="destination"/> in <paramref name="type"/>'s format.</summary>
      private void Pack(IrType type, M6502Address destination) {
        this.Copy(new AddressOperand(destination), 2, Zp.Ptr, 2);
        this._asm.Call(this._runtime.Routine(Mos6502Runtime.Pack(FormatOf(type))));
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
        if (pointer is IrFarPtr)
          throw Decline("a segment:offset address (DIM AT, DEF SEG) has no 6502 meaning");
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
            this._asm.IndirectY(Lda, Zp.Ptr);
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
            this._asm.IndirectY(Sta, Zp.Ptr);
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
        if (callee.Name == PortableRuntime.NativeCopy) {
          // (dst, src, n) onto CopyMemory's (Ptr2, Ptr, Temp)
          var args = call.Args.ToList();
          this.Copy(this.Of(args[0]), 2, Zp.Ptr2, 2);
          this.Copy(this.Of(args[1]), 2, Zp.Ptr, 2);
          this.Copy(this.Of(args[2]), 4, Zp.Temp, 2);
          this._asm.Call(this._runtime.Routine(M6502Routine.CopyMemory));
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

      /// <summary>
      /// A call to a declaration: the portable runtime defines everything a program calls, so what is
      /// left are its two system primitives - <c>sys_write</c> through the KERNAL, <c>sys_exit</c>
      /// back to BASIC.
      /// </summary>
      private void CallRuntime(IrCall call, IrFunction callee) {
        switch (callee.Name) {
          case "sys_write": {
            // the screen, descriptor 1, is the common case and needs no file routines at all
            var fd = call.Args.ElementAt(0);
            this.Copy(this.Of(call.Args.ElementAt(1)), 2, Zp.Arg, 2);
            this.Copy(this.Of(call.Args.ElementAt(2)), 4, Zp.Arg.Plus(2), 4);
            if (fd is IrConstantInt { Value: 1 }) {
              this._asm.Call(this._runtime.Routine(M6502Routine.SystemWrite));
              return;
            }
            this.Copy(this.Of(fd), 1, Zp.Arg.Plus(6), 1);
            this._asm.Call(this._runtime.Routine(M6502Routine.FileWrite));
            return;
          }
          case "sys_exit":
            this._asm.Jump(this._runtime.Routine(M6502Routine.Exit));
            return;
          case "sys_trap":
            this.Trap(call.Args.ElementAt(0));
            return;
          case "rt_onerr_arm" or "rt_onerr_resume_next" or "rt_onerr_disarm" or "rt_resume_mark"
              when !module._trapsErrors:
            return;
          case "rt_onerr_arm":
            this.Arm(this.Of(call.Args.ElementAt(0)));
            return;
          case "rt_onerr_resume_next":
            this.Arm(new AddressOperand(module._resumeNextStub));
            return;
          case "rt_onerr_disarm":
            this.Copy(new ConstantOperand(0), 2, module._errorHandler, 2);
            return;
          case "rt_resume_mark":
            this.Copy(this.Of(call.Args.ElementAt(0)), 2, module._statementStart, 2);
            this.Copy(this.Of(call.Args.ElementAt(1)), 2, module._statementNext, 2);
            return;
          case "rt_resume_same" or "rt_resume_next":
            this.ClearError();
            JumpThrough(this._asm, callee.Name == "rt_resume_same" ? module._faultStart : module._faultNext);
            return;
          case "rt_err_clear":
            this.ClearError();
            return;
          // EXIT FAR AT label: where to land, and the stacks to have back when it does
          case "rt_efar_arm":
            this.Copy(this.Of(call.Args.ElementAt(0)), 2, module._exitFarTarget, 2);
            this._asm.Emit(Tsx);
            this._asm.Memory(Stx, module._exitFarStack);
            this.Copy(new MemoryOperand(Zp.SoftStack), 2, module._exitFarSoftStack, 2);
            return;
          // a bare EXIT FAR: every frame between here and there is abandoned at once
          case "rt_efar_go":
            this._asm.Memory(Ldx, module._exitFarStack);
            this._asm.Emit(Txs);
            this.Copy(new MemoryOperand(module._exitFarSoftStack), 2, Zp.SoftStack, 2);
            JumpThrough(this._asm, module._exitFarTarget);
            return;
          case "sys_read": {
            var args = call.Args.ToList();
            this.Copy(this.Of(args[0]), SizeOf(args[0].Type), Zp.Arg, 4, signed: true);
            this.Copy(this.Of(args[1]), 2, Zp.Arg.Plus(4), 2);
            this.Copy(this.Of(args[2]), SizeOf(args[2].Type), Zp.Arg.Plus(6), 4, signed: true);
            this._asm.Call(this._runtime.Routine(module.UsesFiles ? M6502Routine.FileRead : M6502Routine.SystemRead));
            if (this.Stored(call))
              this.Copy(new MemoryOperand(Zp.Ret), 4, this.Destination(call), SizeOf(call.Type));
            return;
          }
          case "sys_open" or "sys_close" or "sys_unlink": {
            // a path's address, or a descriptor - a logical file number, which fits a byte
            var args = call.Args.ToList();
            var width = args[0].Type.IsPointer ? 2 : 1;
            this.Copy(this.Of(args[0]), width, Zp.Arg, width);
            if (callee.Name == "sys_open")
              this.Copy(this.Of(args[1]), 1, Zp.Arg.Plus(2), 1);
            this._asm.Call(this._runtime.Routine(callee.Name switch {
              "sys_open" => M6502Routine.FileOpen,
              "sys_close" => M6502Routine.FileClose,
              _ => M6502Routine.FileUnlink,
            }));
            if (this.Stored(call))
              this.Copy(new MemoryOperand(Zp.Ret), 4, this.Destination(call), SizeOf(call.Type));
            return;
          }
          case "sys_seek": {
            // a descriptor, the offset's low word - a cached file is 4 KB at most - and whence
            var args = call.Args.ToList();
            this.Copy(this.Of(args[0]), 1, Zp.Arg, 1);
            this.Copy(this.Of(args[1]), 2, Zp.Arg.Plus(2), 2);
            this.Copy(this.Of(args[2]), 1, Zp.Arg.Plus(4), 1);
            this._asm.Call(this._runtime.Routine(M6502Routine.FileSeek));
            if (this.Stored(call))
              this.Copy(new MemoryOperand(Zp.Ret), 4, this.Destination(call), SizeOf(call.Type));
            return;
          }
          default:
            throw Decline($"the 6502 runtime has no {callee.Name} yet");
        }
      }

      private void ClearError() {
        if (module.ErrorCode is { } err)
          this.Copy(new ConstantOperand(0), 2, err, 2);
      }

      /// <summary>Arms <paramref name="handler"/> on the stacks as they are now, and clears ERR, as DOS does.</summary>
      private void Arm(Operand handler) {
        this.Copy(handler, 2, module._errorHandler, 2);
        this._asm.Emit(Tsx);
        this._asm.Memory(Stx, module._errorStack);
        this.Copy(new MemoryOperand(Zp.SoftStack), 2, module._errorSoftStack, 2);
        this.ClearError();
      }

      /// <summary>
      /// <c>sys_trap(code)</c>, the portable runtime's first question in <c>rt_error</c>, and the DOS
      /// runtime's <c>rt_raise</c>: ERR gets the code; with no handler armed it falls through and the
      /// error is reported; with one it never returns - the statement that faulted is latched for
      /// RESUME, the armed stacks come back, and the handler runs (a fault inside it enters it again).
      /// </summary>
      private void Trap(IrValue code) {
        if (!module._trapsErrors)
          return;
        var none = this.Local("noHandler");
        if (module.ErrorCode is { } err)
          this.Copy(this.Of(code), 2, err, 2);
        this._asm.Memory(Lda, module._errorHandler);
        this._asm.Memory(Ora, module._errorHandler.Plus(1));
        this._asm.Branch(Beq, none);
        this.LatchFault();
        this._asm.Memory(Ldx, module._errorStack);
        this._asm.Emit(Txs);
        this.Copy(new MemoryOperand(module._errorSoftStack), 2, Zp.SoftStack, 2);
        JumpThrough(this._asm, module._errorHandler);
        this._asm.Bind(none);
      }

      /// <summary>
      /// A procedure that arms a handler promises its caller the caller's handler back: the triple is
      /// saved on entry and restored on every return, as the DOS back end does.
      /// </summary>
      private void PreserveHandler(bool save) {
        if (!this._frame.Holds(this._frame.SavedHandler))
          return;
        var slot = this._frame.AddressOf(this._frame.SavedHandler);
        var offset = 0;
        foreach (var cell in (M6502Label[])[module._errorHandler, module._errorSoftStack, module._errorStack]) {
          if (save)
            this.Copy(new MemoryOperand(cell), 2, slot.Plus(offset), 2);
          else
            this.Copy(new MemoryOperand(slot.Plus(offset)), 2, cell, 2);
          offset += 2;
        }
      }

      /// <summary>The statement that faulted becomes the one RESUME returns to.</summary>
      private void LatchFault() {
        this.Copy(new MemoryOperand(module._statementStart), 2, module._faultStart, 2);
        this.Copy(new MemoryOperand(module._statementNext), 2, module._faultNext, 2);
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
        this.PreserveHandler(save: false);
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
