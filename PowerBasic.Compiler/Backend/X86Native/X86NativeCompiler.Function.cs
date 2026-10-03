using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.X86Native;

public static partial class X86NativeCompiler {

  private sealed partial class ModuleGenerator {

    /// <summary>One function: prologue, its blocks in IR order, each instruction lowered in place.</summary>
    private sealed class FunctionGenerator(ModuleGenerator module, IrFunction function) {

      private readonly X86Assembler _asm = module._asm;
      private readonly Frame _frame = module._frames[function];
      private readonly Dictionary<IrBasicBlock, X86Label> _blocks = [];
      private IrBasicBlock? _next;
      private int _local;

      /// <summary>Divisions whose answer an earlier divide already stored (<see cref="IrDivRem"/>).</summary>
      private readonly HashSet<IrInstruction> _paired = new(ReferenceEqualityComparer.Instance);

      private X86Width Word => module.Word;
      private int WordBytes => module.WordBytes;
      private bool Is64 => module._asm.Machine == X86Machine.Amd64;

      public void Generate() {
        this._asm.Bind(module._entries[function]);
        this._asm.Push(X86Reg.Bp);
        this._asm.Mov(this.Word, X86Reg.Bp, X86Reg.Sp);
        if (this._frame.Size > 0)
          this._asm.AluImmediate(X86Alu.Sub, this.Word, X86Reg.Sp, this._frame.Size);
        this.PreserveHandler(save: true);
        foreach (var block in function.Blocks)
          this._blocks.Add(block, this._asm.NewLabel($"{function.Name}.{block.Label}"));
        for (var i = 0; i < function.Blocks.Count; ++i) {
          var block = function.Blocks[i];
          this._next = i + 1 < function.Blocks.Count ? function.Blocks[i + 1] : null;
          this._asm.Bind(this._blocks[block]);
          var instructions = block.Instructions;
          for (var j = 0; j < instructions.Count; ++j) {
            if (instructions[j] is IrCmp compare && this.FusesWithBranch(block, compare))
              continue;
            this.Lower(block, instructions[j]);
          }
        }
      }

      /// <summary>A compare whose only use is the branch right after it is folded into that branch.</summary>
      private bool FusesWithBranch(IrBasicBlock block, IrCmp compare)
        => compare.Users.Count == 1 && block.Instructions.Count >= 2 && block.Instructions[^2] == compare
           && block.Instructions[^1] is IrCondBr branch && branch.Condition == compare
           && !this.IsWide(compare.Lhs.Type);

      private X86Label Local(string what) => this._asm.NewLabel($"{function.Name}.{what}{this._local++}");

      /// <summary>An integer wider than a register: a 64-bit value on i386, worked in two halves.</summary>
      private bool IsWide(IrType type) => !type.IsFloat && module.SizeOf(type) > this.WordBytes;

      private static X86Width WidthOf(int bytes) => bytes switch {
        1 => X86Width.Byte,
        2 => X86Width.Word,
        4 => X86Width.Dword,
        8 => X86Width.Qword,
        _ => throw Decline($"a {bytes}-byte value has no register width"),
      };

      // --- operands ----------------------------------------------------------------------------

      private X86Mem Place(IrValue value) => X86Mem.At(X86Reg.Bp, this._frame.Places[value]);

      private X86Mem Scratch => this.Place(this._frame.Scratch);

      private bool Stored(IrInstruction instruction) => this._frame.Places.ContainsKey(instruction);

      private Operand Of(IrValue value) {
        switch (value) {
          case IrConstantInt constant:
            // an i1 is 0 or 1 in a register whatever sign its constant was written with: `true` as -1
            // would make `xor %flag, true` 0xFE, which a byte test still reads as true
            return new ConstantOperand(constant.Type.IsBool ? constant.Value & 1 : constant.Value);
          case IrNullPtr or IrUndef:
            return new ConstantOperand(0);
          case IrConstantFloat constant:
            return new MemoryOperand(X86Mem.At(module.Constant(constant)));
          // TRY's handler triple is ON ERROR's own state
          case IrGlobalVariable { Name: "rt_onerr" }:
            return new AddressOperand(X86Mem.At(module._errorHandler));
          case IrGlobalVariable { Name: "rt_onerr_bp" }:
            return new AddressOperand(X86Mem.At(module._errorFrame));
          case IrGlobalVariable { Name: "rt_onerr_sp" }:
            return new AddressOperand(X86Mem.At(module._errorStack));
          case IrGlobalVariable global:
            return new AddressOperand(X86Mem.At(module._globals[global]));
          case IrBlockAddress address when this._blocks.TryGetValue(address.Block, out var label):
            return new AddressOperand(X86Mem.At(label));
          case IrAlloca alloca:
            return new AddressOperand(this.Place(alloca));
          case IrGep gep when this.FoldedAddress(gep) is { } folded:
            return new AddressOperand(folded);
          case IrFunction:
            throw Decline($"'{function.Name}' takes a procedure's address, which has no native lowering yet");
          default:
            if (this._frame.Places.ContainsKey(value))
              return new MemoryOperand(this.Place(value));
            throw Decline($"'{function.Name}' uses a {value.GetType().Name}, which has no native lowering");
        }
      }

      private X86Mem? FoldedAddress(IrGep gep) {
        if (this.Of(gep.BasePtr) is not AddressOperand { Place: var place } || gep.ByteOffset is not IrConstantInt offset)
          return null;
        return place.Plus(checked((int)(offset.Value * this.Scale(gep))));
      }

      private int Scale(IrGep gep) => gep.ElementType is { } element ? module.SizeOf(element) : 1;

      /// <summary>Loads an operand of <paramref name="width"/> into <paramref name="register"/>.</summary>
      private void Load(X86Reg register, Operand operand, X86Width width) {
        switch (operand) {
          case ConstantOperand constant:
            var value = width switch {
              X86Width.Byte => (long)(sbyte)constant.Value,
              X86Width.Word => (short)constant.Value,
              X86Width.Dword => (int)constant.Value,
              _ => constant.Value,
            };
            this._asm.MovImmediate(width, register, value);
            break;
          case MemoryOperand memory:
            this._asm.Mov(width, register, memory.Place);
            break;
          case AddressOperand address:
            this._asm.Lea(this.Word, register, address.Place);
            break;
        }
      }

      /// <summary>
      /// Loads an integer of <paramref name="size"/> bytes into <paramref name="register"/> widened
      /// to <paramref name="width"/>, zero- or sign-extending.
      /// </summary>
      private void LoadExtended(X86Reg register, Operand operand, int size, bool signed, X86Width width) {
        if (operand is ConstantOperand constant) {
          var bits = size * 8;
          var value = bits >= 64 ? constant.Value
            : signed ? (constant.Value << (64 - bits)) >> (64 - bits)
            : constant.Value & ((1L << bits) - 1);
          this._asm.MovImmediate(width, register, width == X86Width.Qword ? value : (int)value);
          return;
        }
        if (operand is AddressOperand || size >= width.Bytes()) {
          this.Load(register, operand, width);
          return;
        }
        var place = ((MemoryOperand)operand).Place;
        if (size == 4 && !signed)
          this._asm.Mov(X86Width.Dword, register, place);
        else
          this._asm.Extend(signed, width, register, WidthOf(size), place);
      }

      /// <summary>Stores the low <paramref name="width"/> of <paramref name="register"/>.</summary>
      private void Store(X86Mem destination, X86Reg register, X86Width width) => this._asm.Mov(width, destination, register);

      /// <summary>Copies a value of <paramref name="size"/> bytes to memory, a register's width at a time.</summary>
      private void Copy(Operand source, int size, X86Mem destination) {
        switch (source) {
          case ConstantOperand constant: {
            var at = 0;
            while (at < size) {
              var chunk = size - at >= 4 ? 4 : size - at >= 2 ? 2 : 1;
              var bits = constant.Value >> (8 * Math.Min(at, 7));
              if (at >= 8)
                bits = constant.Value < 0 ? -1 : 0;
              this._asm.MovImmediate(WidthOf(chunk), destination.Plus(at),
                chunk == 4 ? (int)bits : chunk == 2 ? (short)bits : (sbyte)bits);
              at += chunk;
            }
            return;
          }
          case AddressOperand address:
            this._asm.Lea(this.Word, X86Reg.Ax, address.Place);
            this.Store(destination, X86Reg.Ax, WidthOf(Math.Min(size, this.WordBytes)));
            for (var at = this.WordBytes; at < size; at += 4)
              this._asm.MovImmediate(X86Width.Dword, destination.Plus(at), 0);
            return;
          case MemoryOperand memory:
            this.CopyMemory(memory.Place, destination, size);
            return;
        }
      }

      private void CopyMemory(X86Mem source, X86Mem destination, int size) {
        var at = 0;
        while (at < size) {
          var remaining = size - at;
          var chunk = remaining >= this.WordBytes ? this.WordBytes : remaining >= 4 ? 4 : remaining >= 2 ? 2 : 1;
          this._asm.Mov(WidthOf(chunk), X86Reg.Ax, source.Plus(at));
          this._asm.Mov(WidthOf(chunk), destination.Plus(at), X86Reg.Ax);
          at += chunk;
        }
      }

      // --- instructions ------------------------------------------------------------------------

      private void Lower(IrBasicBlock block, IrInstruction instruction) {
        switch (instruction) {
          case IrPhi or IrAlloca:
            return;
          case IrBinary binary when this._paired.Contains(binary): return;
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
          case IrUnreachable unreachable when IrRaise.FollowsRaise(unreachable): return;
          case IrUnreachable: this.RaiseError(51); return;
          default:
            throw Decline($"'{function.Name}' uses {instruction.GetType().Name}, which has no native lowering yet");
        }
      }

      private void LowerBinary(IrBinary binary) {
        var destination = this.Place(binary);
        if (binary.IsFloatOp) {
          var format = FormatOf(binary.Type);
          this.LoadFloat(binary.Lhs);
          this.LoadFloat(binary.Rhs);
          this._asm.FArithmetic(binary.Op switch {
            IrBinaryOp.FAdd => X87Op.AddPop,
            IrBinaryOp.FSub => X87Op.SubPop,
            IrBinaryOp.FMul => X87Op.MulPop,
            _ => X87Op.DivPop,
          });
          this._asm.Fstp(format, destination);
          return;
        }
        var size = module.SizeOf(binary.Type);
        var lhs = this.Of(binary.Lhs);
        var rhs = this.Of(binary.Rhs);
        if (this.IsWide(binary.Type)) {
          this.LowerWideBinary(binary.Op, lhs, rhs, destination);
          return;
        }
        var width = WidthOf(size);
        switch (binary.Op) {
          case IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor:
            this.Load(X86Reg.Ax, lhs, width);
            this.AluWith(binary.Op switch {
              IrBinaryOp.Add => X86Alu.Add, IrBinaryOp.Sub => X86Alu.Sub, IrBinaryOp.And => X86Alu.And,
              IrBinaryOp.Or => X86Alu.Or, _ => X86Alu.Xor,
            }, width, X86Reg.Ax, rhs);
            this.Store(destination, X86Reg.Ax, width);
            return;
          case IrBinaryOp.Mul: {
            var working = width == X86Width.Byte ? X86Width.Dword : width;
            this.LoadExtended(X86Reg.Ax, lhs, size, signed: false, working);
            this.LoadExtended(X86Reg.Cx, rhs, size, signed: false, working);
            this._asm.Imul(working, X86Reg.Ax, X86Reg.Cx);
            this.Store(destination, X86Reg.Ax, width);
            return;
          }
          case IrBinaryOp.SDiv or IrBinaryOp.SRem or IrBinaryOp.UDiv or IrBinaryOp.URem: {
            // one divide answers both n \ d and n MOD d: a partner later in the block is stored now
            X86Mem? partner = null;
            if (IrDivRem.PartnerOf(binary) is { } other && this.Stored(other)) {
              partner = this.Place(other);
              this._paired.Add(other);
            }
            this.Divide(binary.Op, lhs, rhs, size, destination, partner);
            return;
          }
          case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
            this.Shift(binary.Op, lhs, rhs, module.SizeOf(binary.Rhs.Type), width, destination);
            return;
          default:
            throw Decline($"{binary.Op} has no native lowering yet");
        }
      }

      /// <summary><c>op register, operand</c> for any operand.</summary>
      private void AluWith(X86Alu op, X86Width width, X86Reg register, Operand operand) {
        switch (operand) {
          case ConstantOperand constant when width != X86Width.Qword || constant.Value is >= int.MinValue and <= int.MaxValue:
            this._asm.AluImmediate(op, width, register, width switch {
              X86Width.Byte => (long)(byte)constant.Value,
              X86Width.Word => (ushort)constant.Value,
              X86Width.Dword => (int)constant.Value,
              _ => constant.Value,
            });
            return;
          case MemoryOperand memory:
            this._asm.Alu(op, width, register, memory.Place);
            return;
          default:
            this.Load(X86Reg.Dx, operand, width);
            this._asm.Alu(op, width, register, X86Reg.Dx);
            return;
        }
      }

      /// <summary>Half <paramref name="half"/> of a two-register value, as an operand of its own.</summary>
      private static Operand Half(Operand operand, int half) => operand switch {
        ConstantOperand constant => new ConstantOperand((int)(constant.Value >> (32 * half))),
        MemoryOperand memory => new MemoryOperand(memory.Place.Plus(4 * half)),
        _ => half == 0 ? operand : new ConstantOperand(0),
      };

      private void LowerWideBinary(IrBinaryOp op, Operand lhs, Operand rhs, X86Mem destination) {
        switch (op) {
          case IrBinaryOp.Mul:
            this.WideMultiply(lhs, rhs, destination);
            return;
          case IrBinaryOp.SDiv or IrBinaryOp.SRem or IrBinaryOp.UDiv or IrBinaryOp.URem:
            this.WideDivide(op, lhs, rhs, destination);
            return;
          case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
            this.WideShift(op, lhs, rhs, destination);
            return;
        }
        var (low, high) = op switch {
          IrBinaryOp.Add => (X86Alu.Add, X86Alu.Adc),
          IrBinaryOp.Sub => (X86Alu.Sub, X86Alu.Sbb),
          IrBinaryOp.And => (X86Alu.And, X86Alu.And),
          IrBinaryOp.Or => (X86Alu.Or, X86Alu.Or),
          IrBinaryOp.Xor => (X86Alu.Xor, X86Alu.Xor),
          _ => throw Decline($"64-bit {op} has no i386 lowering yet"),
        };
        // the carry must survive between the halves: load with MOV, which leaves flags alone
        this.Load(X86Reg.Ax, Half(lhs, 0), X86Width.Dword);
        this.Load(X86Reg.Cx, Half(rhs, 0), X86Width.Dword);
        this.Load(X86Reg.Dx, Half(lhs, 1), X86Width.Dword);
        this.Load(X86Reg.Bx, Half(rhs, 1), X86Width.Dword);
        this._asm.Alu(low, X86Width.Dword, X86Reg.Ax, X86Reg.Cx);
        this._asm.Alu(high, X86Width.Dword, X86Reg.Dx, X86Reg.Bx);
        this.Store(destination, X86Reg.Ax, X86Width.Dword);
        this.Store(destination.Plus(4), X86Reg.Dx, X86Width.Dword);
      }

      /// <summary>
      /// The low 64 bits of a 64-bit product from 32-bit multiplies:
      /// <c>lo(a)*lo(b)</c> in full, plus the two cross products into the high half.
      /// </summary>
      private void WideMultiply(Operand lhs, Operand rhs, X86Mem destination) {
        this.Load(X86Reg.Ax, Half(lhs, 0), X86Width.Dword);
        this.Load(X86Reg.Bx, Half(rhs, 1), X86Width.Dword);
        this._asm.Imul(X86Width.Dword, X86Reg.Bx, X86Reg.Ax);
        this.Load(X86Reg.Cx, Half(lhs, 1), X86Width.Dword);
        this.Load(X86Reg.Dx, Half(rhs, 0), X86Width.Dword);
        this._asm.Imul(X86Width.Dword, X86Reg.Cx, X86Reg.Dx);
        this._asm.Alu(X86Alu.Add, X86Width.Dword, X86Reg.Bx, X86Reg.Cx);
        this._asm.Unary(X86Unary.Mul, X86Width.Dword, X86Reg.Dx);
        this._asm.Alu(X86Alu.Add, X86Width.Dword, X86Reg.Dx, X86Reg.Bx);
        this.Store(destination, X86Reg.Ax, X86Width.Dword);
        this.Store(destination.Plus(4), X86Reg.Dx, X86Width.Dword);
      }

      /// <summary>
      /// A 64-bit division on i386 by shift and subtract: the dividend in EDX:EAX becomes the
      /// quotient as the remainder builds in EDI:ESI, against the divisor in the frame's scratch. A
      /// signed division divides the magnitudes and puts the signs back: the quotient's is the
      /// operands' product's, the remainder's the dividend's.
      /// </summary>
      private void WideDivide(IrBinaryOp op, Operand lhs, Operand rhs, X86Mem destination) {
        var signed = op is IrBinaryOp.SDiv or IrBinaryOp.SRem;
        var divisor = this.Scratch;
        var signs = this.Scratch.Plus(8);
        this.Copy(rhs, 8, divisor);
        var divisible = this.Local("divisible");
        this._asm.Mov(X86Width.Dword, X86Reg.Ax, divisor);
        this._asm.Alu(X86Alu.Or, X86Width.Dword, X86Reg.Ax, divisor.Plus(4));
        this._asm.Jump(X86Cond.NotEqual, divisible);
        this.RaiseError(11);
        this._asm.Bind(divisible);
        this.Load(X86Reg.Ax, Half(lhs, 0), X86Width.Dword);
        this.Load(X86Reg.Dx, Half(lhs, 1), X86Width.Dword);
        if (signed) {
          // signs: byte 0 the quotient's (dividend xor divisor), byte 1 the remainder's (dividend)
          this._asm.Mov(X86Width.Dword, X86Reg.Cx, X86Reg.Dx);
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Cx, divisor.Plus(4));
          this._asm.Shift(X86Shift.Shr, X86Width.Dword, X86Reg.Cx, 31);
          this._asm.Mov(X86Width.Byte, signs, X86Reg.Cx);
          this._asm.Mov(X86Width.Dword, X86Reg.Cx, X86Reg.Dx);
          this._asm.Shift(X86Shift.Shr, X86Width.Dword, X86Reg.Cx, 31);
          this._asm.Mov(X86Width.Byte, signs.Plus(1), X86Reg.Cx);
          this.NegateIfNegative(X86Reg.Ax, X86Reg.Dx);
          this._asm.Mov(X86Width.Dword, X86Reg.Si, divisor);
          this._asm.Mov(X86Width.Dword, X86Reg.Di, divisor.Plus(4));
          this.NegateIfNegative(X86Reg.Si, X86Reg.Di);
          this._asm.Mov(X86Width.Dword, divisor, X86Reg.Si);
          this._asm.Mov(X86Width.Dword, divisor.Plus(4), X86Reg.Di);
        }
        var loop = this.Local("divide");
        var subtract = this.Local("subtract");
        var next = this.Local("nextBit");
        this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Si, X86Reg.Si);
        this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Di, X86Reg.Di);
        this._asm.MovImmediate(X86Width.Dword, X86Reg.Cx, 64);
        this._asm.Bind(loop);
        this._asm.Shift(X86Shift.Shl, X86Width.Dword, X86Reg.Ax, 1);
        this._asm.Shift(X86Shift.Rcl, X86Width.Dword, X86Reg.Dx, 1);
        this._asm.Shift(X86Shift.Rcl, X86Width.Dword, X86Reg.Si, 1);
        this._asm.Shift(X86Shift.Rcl, X86Width.Dword, X86Reg.Di, 1);
        // a bit carried out of the remainder makes it certainly the larger
        this._asm.Jump(X86Cond.Below, subtract);
        this._asm.Alu(X86Alu.Cmp, X86Width.Dword, X86Reg.Di, divisor.Plus(4));
        this._asm.Jump(X86Cond.Below, next);
        this._asm.Jump(X86Cond.Above, subtract);
        this._asm.Alu(X86Alu.Cmp, X86Width.Dword, X86Reg.Si, divisor);
        this._asm.Jump(X86Cond.Below, next);
        this._asm.Bind(subtract);
        this._asm.Alu(X86Alu.Sub, X86Width.Dword, X86Reg.Si, divisor);
        this._asm.Alu(X86Alu.Sbb, X86Width.Dword, X86Reg.Di, divisor.Plus(4));
        this._asm.AluImmediate(X86Alu.Or, X86Width.Dword, X86Reg.Ax, 1);
        this._asm.Bind(next);
        this._asm.AluImmediate(X86Alu.Sub, X86Width.Dword, X86Reg.Cx, 1);
        this._asm.Jump(X86Cond.NotEqual, loop);
        var quotient = op is IrBinaryOp.SDiv or IrBinaryOp.UDiv;
        if (!quotient) {
          this._asm.Mov(X86Width.Dword, X86Reg.Ax, X86Reg.Si);
          this._asm.Mov(X86Width.Dword, X86Reg.Dx, X86Reg.Di);
        }
        if (signed) {
          var positive = this.Local("positive");
          this._asm.Mov(X86Width.Byte, X86Reg.Cx, signs.Plus(quotient ? 0 : 1));
          this._asm.Test(X86Width.Byte, X86Reg.Cx, X86Reg.Cx);
          this._asm.Jump(X86Cond.Equal, positive);
          this.Negate(X86Reg.Ax, X86Reg.Dx);
          this._asm.Bind(positive);
        }
        this.Store(destination, X86Reg.Ax, X86Width.Dword);
        this.Store(destination.Plus(4), X86Reg.Dx, X86Width.Dword);
      }

      /// <summary>Two's-complement negation of <c>high:low</c>.</summary>
      private void Negate(X86Reg low, X86Reg high) {
        this._asm.Unary(X86Unary.Neg, X86Width.Dword, low);
        this._asm.AluImmediate(X86Alu.Adc, X86Width.Dword, high, 0);
        this._asm.Unary(X86Unary.Neg, X86Width.Dword, high);
      }

      private void NegateIfNegative(X86Reg low, X86Reg high) {
        var positive = this.Local("nonNegative");
        this._asm.Test(X86Width.Dword, high, high);
        this._asm.Jump(X86Cond.NoSign, positive);
        this.Negate(low, high);
        this._asm.Bind(positive);
      }

      /// <summary>A 64-bit shift on i386, one bit at a time through the carry; past 63 everything is shifted out.</summary>
      private void WideShift(IrBinaryOp op, Operand value, Operand count, X86Mem destination) {
        this.Load(X86Reg.Ax, Half(value, 0), X86Width.Dword);
        this.Load(X86Reg.Dx, Half(value, 1), X86Width.Dword);
        this.Load(X86Reg.Cx, Half(count, 0), X86Width.Dword);
        var inRange = this.Local("inRange");
        var loop = this.Local("shift");
        var done = this.Local("shifted");
        this._asm.AluImmediate(X86Alu.Cmp, X86Width.Dword, X86Reg.Cx, 64);
        this._asm.Jump(X86Cond.Below, inRange);
        this._asm.MovImmediate(X86Width.Dword, X86Reg.Cx, 64);
        this._asm.Bind(inRange);
        this._asm.Test(X86Width.Dword, X86Reg.Cx, X86Reg.Cx);
        this._asm.Jump(X86Cond.Equal, done);
        this._asm.Bind(loop);
        if (op == IrBinaryOp.Shl) {
          this._asm.Shift(X86Shift.Shl, X86Width.Dword, X86Reg.Ax, 1);
          this._asm.Shift(X86Shift.Rcl, X86Width.Dword, X86Reg.Dx, 1);
        } else {
          this._asm.Shift(op == IrBinaryOp.AShr ? X86Shift.Sar : X86Shift.Shr, X86Width.Dword, X86Reg.Dx, 1);
          this._asm.Shift(X86Shift.Rcr, X86Width.Dword, X86Reg.Ax, 1);
        }
        this._asm.AluImmediate(X86Alu.Sub, X86Width.Dword, X86Reg.Cx, 1);
        this._asm.Jump(X86Cond.NotEqual, loop);
        this._asm.Bind(done);
        this.Store(destination, X86Reg.Ax, X86Width.Dword);
        this.Store(destination.Plus(4), X86Reg.Dx, X86Width.Dword);
      }

      private void Divide(IrBinaryOp op, Operand lhs, Operand rhs, int size, X86Mem destination, X86Mem? partner) {
        var signed = op is IrBinaryOp.SDiv or IrBinaryOp.SRem;
        var working = size == 8 ? X86Width.Qword : X86Width.Dword;
        this.LoadExtended(X86Reg.Ax, lhs, size, signed, working);
        this.LoadExtended(X86Reg.Cx, rhs, size, signed, working);
        // BASIC's error 11 where the CPU would fault
        var divisible = this.Local("divisible");
        this._asm.Test(working, X86Reg.Cx, X86Reg.Cx);
        this._asm.Jump(X86Cond.NotEqual, divisible);
        this.RaiseError(11);
        this._asm.Bind(divisible);
        var done = this.Local("divided");
        if (signed) {
          // x / -1 is -x, remainder 0: IDIV faults on the one quotient that does not fit
          var general = this.Local("general");
          this._asm.AluImmediate(X86Alu.Cmp, working, X86Reg.Cx, -1);
          this._asm.Jump(X86Cond.NotEqual, general);
          this._asm.Unary(X86Unary.Neg, working, X86Reg.Ax);
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Dx, X86Reg.Dx);
          this._asm.Jump(done);
          this._asm.Bind(general);
          this._asm.SignExtendIntoDx(working);
          this._asm.Unary(X86Unary.Idiv, working, X86Reg.Cx);
        } else {
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Dx, X86Reg.Dx);
          this._asm.Unary(X86Unary.Div, working, X86Reg.Cx);
        }
        this._asm.Bind(done);
        var quotient = op is IrBinaryOp.SDiv or IrBinaryOp.UDiv;
        this.Store(destination, quotient ? X86Reg.Ax : X86Reg.Dx, WidthOf(size));
        if (partner is { } other)
          this.Store(other, quotient ? X86Reg.Dx : X86Reg.Ax, WidthOf(size));
      }

      private void Shift(IrBinaryOp op, Operand value, Operand count, int countSize, X86Width width, X86Mem destination) {
        var shift = op switch { IrBinaryOp.Shl => X86Shift.Shl, IrBinaryOp.LShr => X86Shift.Shr, _ => X86Shift.Sar };
        var bits = width.Bytes() * 8;
        this.Load(X86Reg.Ax, value, width);
        if (count is ConstantOperand { Value: var constant }) {
          if (constant >= bits) {
            if (op == IrBinaryOp.AShr)
              this._asm.Shift(X86Shift.Sar, width, X86Reg.Ax, bits - 1);
            else
              this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Ax, X86Reg.Ax);
          } else if (constant > 0) {
            this._asm.Shift(shift, width, X86Reg.Ax, (int)constant);
          }
          this.Store(destination, X86Reg.Ax, width);
          return;
        }
        // the CPU masks the count; BASIC does not: a count past the width shifts everything out
        var inRange = this.Local("inRange");
        var done = this.Local("shifted");
        this.LoadExtended(X86Reg.Cx, count, countSize, signed: false, X86Width.Dword);
        this._asm.AluImmediate(X86Alu.Cmp, X86Width.Dword, X86Reg.Cx, bits);
        this._asm.Jump(X86Cond.Below, inRange);
        if (op == IrBinaryOp.AShr)
          this._asm.Shift(X86Shift.Sar, width, X86Reg.Ax, bits - 1);
        else
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Ax, X86Reg.Ax);
        this._asm.Jump(done);
        this._asm.Bind(inRange);
        this._asm.Shift(shift, width, X86Reg.Ax);
        this._asm.Bind(done);
        this.Store(destination, X86Reg.Ax, width);
      }

      // --- comparisons -------------------------------------------------------------------------

      private void LowerCompare(IrCmp compare) {
        if (!compare.Lhs.Type.IsFloat && this.IsWide(compare.Lhs.Type)) {
          this.WideCompare(compare);
          this.Store(this.Place(compare), X86Reg.Ax, X86Width.Byte);
          return;
        }
        var (condition, orderedOnly) = this.Compare(compare);
        this._asm.Set(condition, X86Reg.Ax);
        if (orderedOnly) {
          this._asm.Set(X86Cond.NoParity, X86Reg.Cx);
          this._asm.Alu(X86Alu.And, X86Width.Byte, X86Reg.Ax, X86Reg.Cx);
        }
        this.Store(this.Place(compare), X86Reg.Ax, X86Width.Byte);
      }

      /// <summary>
      /// Emits the comparison and answers the condition under which it holds, and whether an
      /// unordered (NaN) outcome must additionally be ruled out through the parity flag.
      /// </summary>
      private (X86Cond Condition, bool OrderedOnly) Compare(IrCmp compare) {
        if (compare.Lhs.Type.IsFloat) {
          // relational predicates are ordered so that "above" answers them, which is false for NaN
          var (first, second, condition, orderedOnly) = compare.Pred switch {
            IrCmpPred.Folt => (compare.Lhs, compare.Rhs, X86Cond.Above, false),
            IrCmpPred.Fole => (compare.Lhs, compare.Rhs, X86Cond.AboveOrEqual, false),
            IrCmpPred.Fogt => (compare.Rhs, compare.Lhs, X86Cond.Above, false),
            IrCmpPred.Foge => (compare.Rhs, compare.Lhs, X86Cond.AboveOrEqual, false),
            IrCmpPred.Foeq => (compare.Rhs, compare.Lhs, X86Cond.Equal, true),
            IrCmpPred.Fone => (compare.Rhs, compare.Lhs, X86Cond.NotEqual, true),
            _ => throw Decline($"{compare.Pred} compares integers, not floats"),
          };
          // fucomip compares ST(0) with ST(1): load `first` below `second`
          this.LoadFloat(first);
          this.LoadFloat(second);
          this._asm.FucomipSt1();
          this._asm.FPop();
          return (condition, orderedOnly);
        }
        var width = WidthOf(module.SizeOf(compare.Lhs.Type));
        this.Load(X86Reg.Ax, this.Of(compare.Lhs), width);
        this.AluWith(X86Alu.Cmp, width, X86Reg.Ax, this.Of(compare.Rhs));
        return (compare.Pred switch {
          IrCmpPred.Eq => X86Cond.Equal,
          IrCmpPred.Ne => X86Cond.NotEqual,
          IrCmpPred.Slt => X86Cond.Less,
          IrCmpPred.Sle => X86Cond.LessOrEqual,
          IrCmpPred.Sgt => X86Cond.Greater,
          IrCmpPred.Sge => X86Cond.GreaterOrEqual,
          IrCmpPred.Ult => X86Cond.Below,
          IrCmpPred.Ule => X86Cond.BelowOrEqual,
          IrCmpPred.Ugt => X86Cond.Above,
          IrCmpPred.Uge => X86Cond.AboveOrEqual,
          _ => throw Decline($"{compare.Pred} compares floats, not integers"),
        }, false);
      }

      /// <summary>A 64-bit comparison on i386, into AL: the high halves decide unless they are equal.</summary>
      private void WideCompare(IrCmp compare) {
        var (lhs, rhs) = (this.Of(compare.Lhs), this.Of(compare.Rhs));
        if (compare.Pred is IrCmpPred.Eq or IrCmpPred.Ne) {
          this.Load(X86Reg.Ax, Half(lhs, 0), X86Width.Dword);
          this.AluWith(X86Alu.Xor, X86Width.Dword, X86Reg.Ax, Half(rhs, 0));
          this.Load(X86Reg.Cx, Half(lhs, 1), X86Width.Dword);
          this.Load(X86Reg.Dx, Half(rhs, 1), X86Width.Dword);
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Cx, X86Reg.Dx);
          this._asm.Alu(X86Alu.Or, X86Width.Dword, X86Reg.Ax, X86Reg.Cx);
          this._asm.Set(compare.Pred == IrCmpPred.Eq ? X86Cond.Equal : X86Cond.NotEqual, X86Reg.Ax);
          return;
        }
        var (highCondition, lowCondition) = compare.Pred switch {
          IrCmpPred.Slt => (X86Cond.Less, X86Cond.Below),
          IrCmpPred.Sle => (X86Cond.Less, X86Cond.BelowOrEqual),
          IrCmpPred.Sgt => (X86Cond.Greater, X86Cond.Above),
          IrCmpPred.Sge => (X86Cond.Greater, X86Cond.AboveOrEqual),
          IrCmpPred.Ult => (X86Cond.Below, X86Cond.Below),
          IrCmpPred.Ule => (X86Cond.Below, X86Cond.BelowOrEqual),
          IrCmpPred.Ugt => (X86Cond.Above, X86Cond.Above),
          _ => (X86Cond.Above, X86Cond.AboveOrEqual),
        };
        var highDecides = this.Local("highDecides");
        var done = this.Local("compared");
        this.Load(X86Reg.Ax, Half(lhs, 1), X86Width.Dword);
        this.AluWith(X86Alu.Cmp, X86Width.Dword, X86Reg.Ax, Half(rhs, 1));
        this._asm.Jump(X86Cond.NotEqual, highDecides);
        this.Load(X86Reg.Ax, Half(lhs, 0), X86Width.Dword);
        this.AluWith(X86Alu.Cmp, X86Width.Dword, X86Reg.Ax, Half(rhs, 0));
        this._asm.Set(lowCondition, X86Reg.Ax);
        this._asm.Jump(done);
        this._asm.Bind(highDecides);
        this._asm.Set(highCondition, X86Reg.Ax);
        this._asm.Bind(done);
      }

      // --- conversions -------------------------------------------------------------------------

      private void LowerCast(IrCast cast) {
        var destination = this.Place(cast);
        var sourceSize = module.SizeOf(cast.Value.Type);
        var size = module.SizeOf(cast.Type);
        var source = this.Of(cast.Value);
        switch (cast.Op) {
          case IrCastOp.FPExt or IrCastOp.FPTrunc:
            this.LoadFloat(cast.Value);
            this._asm.Fstp(FormatOf(cast.Type), destination);
            return;
          case IrCastOp.SIToFP or IrCastOp.UIToFP:
            this.IntegerToFloat(source, sourceSize, cast.Op == IrCastOp.SIToFP);
            this._asm.Fstp(FormatOf(cast.Type), destination);
            return;
          case IrCastOp.FPToSI or IrCastOp.FPToUI or IrCastOp.FPToSIRound or IrCastOp.FPToUIRound:
            this.FloatToInteger(cast.Value, truncate: cast.Op is IrCastOp.FPToSI or IrCastOp.FPToUI);
            this.CopyMemory(this.Scratch, destination, size);
            return;
          case IrCastOp.SExt when cast.Value.Type.IsBool:
            this.LoadExtended(X86Reg.Ax, source, 1, signed: false, X86Width.Dword);
            this._asm.Unary(X86Unary.Neg, X86Width.Dword, X86Reg.Ax);
            this.StoreExtended(destination, size, signedHigh: true);
            return;
          case IrCastOp.SExt or IrCastOp.ZExt or IrCastOp.Trunc or IrCastOp.PtrToInt or IrCastOp.IntToPtr or IrCastOp.BitCast:
            var signed = cast.Op == IrCastOp.SExt;
            if (size <= sourceSize) {
              this.Copy(source, size, destination);
            } else if (size <= this.WordBytes) {
              var width = WidthOf(size) == X86Width.Qword ? X86Width.Qword : X86Width.Dword;
              this.LoadExtended(X86Reg.Ax, source, sourceSize, signed, width);
              this.Store(destination, X86Reg.Ax, WidthOf(size));
            } else {
              this.LoadExtended(X86Reg.Ax, source, sourceSize, signed, X86Width.Dword);
              this.StoreExtended(destination, size, signed);
            }
            return;
          default:
            throw Decline($"the {cast.Op} conversion has no native lowering yet");
        }
      }

      /// <summary>EAX into a value of <paramref name="size"/> bytes; above four, the high half its sign or zero.</summary>
      private void StoreExtended(X86Mem destination, int size, bool signedHigh) {
        if (size <= 4) {
          this.Store(destination, X86Reg.Ax, WidthOf(size));
          return;
        }
        if (this.Is64) {
          this._asm.Extend(true, X86Width.Qword, X86Reg.Ax, X86Width.Dword, X86Reg.Ax);
          if (!signedHigh)
            this._asm.Mov(X86Width.Dword, X86Reg.Ax, X86Reg.Ax);
          this.Store(destination, X86Reg.Ax, X86Width.Qword);
          return;
        }
        this.Store(destination, X86Reg.Ax, X86Width.Dword);
        if (signedHigh) {
          this._asm.Mov(X86Width.Dword, X86Reg.Dx, X86Reg.Ax);
          this._asm.Shift(X86Shift.Sar, X86Width.Dword, X86Reg.Dx, 31);
        } else {
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Dx, X86Reg.Dx);
        }
        this.Store(destination.Plus(4), X86Reg.Dx, X86Width.Dword);
      }

      /// <summary>Pushes a float value onto the x87 stack.</summary>
      private void LoadFloat(IrValue value) {
        if (this.Of(value) is not MemoryOperand memory)
          throw Decline($"a float in '{function.Name}' ({value.GetType().Name}) is not in memory");
        this._asm.Fld(FormatOf(value.Type), memory.Place);
      }

      /// <summary>Pushes an integer onto the x87 stack, going through the frame's scratch where FILD cannot read it directly.</summary>
      private void IntegerToFloat(Operand source, int size, bool signed) {
        if (source is MemoryOperand memory && signed && size >= 2) {
          this._asm.Fild(WidthOf(size), memory.Place);
          return;
        }
        if (size <= 2 || (size == 4 && signed)) {
          this.LoadExtended(X86Reg.Ax, source, size, signed, X86Width.Dword);
          this.Store(this.Scratch, X86Reg.Ax, X86Width.Dword);
          this._asm.Fild(X86Width.Dword, this.Scratch);
          return;
        }
        if (size == 4) {
          // unsigned 32 bits: zero-extended to a quadword FILD reads as signed
          this.LoadExtended(X86Reg.Ax, source, 4, signed: false, X86Width.Dword);
          this.Store(this.Scratch, X86Reg.Ax, X86Width.Dword);
          this._asm.MovImmediate(X86Width.Dword, this.Scratch.Plus(4), 0);
          this._asm.Fild(X86Width.Qword, this.Scratch);
          return;
        }
        this.Copy(source, 8, this.Scratch);
        this._asm.Fild(X86Width.Qword, this.Scratch);
        if (!signed) {
          // a set top bit read as negative: add 2^64 back
          var small = this.Local("belowTwo63");
          this._asm.Mov(X86Width.Byte, X86Reg.Ax, this.Scratch.Plus(7));
          this._asm.Test(X86Width.Byte, X86Reg.Ax, X86Reg.Ax);
          this._asm.Jump(X86Cond.NoSign, small);
          this._asm.FaddDouble(X86Mem.At(module.Constant(new IrConstantFloat(IrType.F64, 18446744073709551616.0))));
          this._asm.Bind(small);
        }
      }

      /// <summary>
      /// Pops the float into a 64-bit integer in the frame's scratch: rounded to nearest-even, the
      /// control word's default, or truncated with the rounding mode switched to chop for the store.
      /// </summary>
      private void FloatToInteger(IrValue value, bool truncate) {
        this.LoadFloat(value);
        if (!truncate) {
          this._asm.Fistp(X86Width.Qword, this.Scratch);
          return;
        }
        var saved = X86Mem.At(module._controlWord);
        this._asm.Fnstcw(saved);
        this._asm.Mov(X86Width.Word, X86Reg.Ax, saved);
        this._asm.AluImmediate(X86Alu.Or, X86Width.Word, X86Reg.Ax, 0x0C00);
        this._asm.Mov(X86Width.Word, saved.Plus(2), X86Reg.Ax);
        this._asm.Fldcw(saved.Plus(2));
        this._asm.Fistp(X86Width.Qword, this.Scratch);
        this._asm.Fldcw(saved);
      }

      private void LowerSelect(IrSelect select) {
        var size = module.SizeOf(select.Type);
        var destination = this.Place(select);
        var otherwise = this.Local("otherwise");
        var done = this.Local("selected");
        this.Load(X86Reg.Ax, this.Of(select.Condition), X86Width.Byte);
        this._asm.Test(X86Width.Byte, X86Reg.Ax, X86Reg.Ax);
        this._asm.Jump(X86Cond.Equal, otherwise);
        this.Copy(this.Of(select.IfTrue), size, destination);
        this._asm.Jump(done);
        this._asm.Bind(otherwise);
        this.Copy(this.Of(select.IfFalse), size, destination);
        this._asm.Bind(done);
      }

      private void LowerGep(IrGep gep) {
        var word = this.Word;
        this.Load(X86Reg.Ax, this.Of(gep.BasePtr), word);
        var offsetSize = module.SizeOf(gep.ByteOffset.Type);
        this.LoadExtended(X86Reg.Cx, this.Of(gep.ByteOffset), Math.Min(offsetSize, this.WordBytes), signed: true, word);
        var scale = this.Scale(gep);
        if (scale != 1 && (scale & (scale - 1)) == 0) {
          this._asm.Shift(X86Shift.Shl, word, X86Reg.Cx, System.Numerics.BitOperations.Log2((uint)scale));
        } else if (scale != 1) {
          this._asm.MovImmediate(word, X86Reg.Dx, scale);
          this._asm.Imul(word, X86Reg.Cx, X86Reg.Dx);
        }
        this._asm.Alu(X86Alu.Add, word, X86Reg.Ax, X86Reg.Cx);
        this.Store(this.Place(gep), X86Reg.Ax, word);
      }

      /// <summary>Where a load or store goes: a fixed place, or through the pointer loaded into ECX/RCX.</summary>
      private X86Mem Target(IrValue pointer) {
        if (pointer is IrFarPtr)
          throw Decline("a segment:offset address (DIM AT, DEF SEG) has no meaning on a flat target");
        switch (this.Of(pointer)) {
          case AddressOperand address:
            return address.Place;
          case var computed:
            this.Load(X86Reg.Cx, computed, this.Word);
            return X86Mem.At(X86Reg.Cx);
        }
      }

      private void LowerLoad(IrLoad load) {
        if (!this.Stored(load))
          return;
        var source = this.Target(load.Pointer);
        this.CopyMemory(source, this.Place(load), module.SizeOf(load.Type));
      }

      private void LowerStore(IrStore store) {
        var size = module.SizeOf(store.Value.Type);
        var value = this.Of(store.Value);
        var target = this.Target(store.Pointer);
        this.Copy(value, size, target);
      }

      // --- calls -------------------------------------------------------------------------------

      private void LowerCall(IrCall call) {
        if (call.Callee is not IrFunction callee)
          throw Decline($"'{function.Name}' calls through a pointer, which has no native lowering yet");
        if (call.Convention == IrCallConvention.BasicClosure)
          throw Decline("delegates have no native lowering yet");
        var arguments = call.Args.ToList();
        if (callee.IsDeclaration) {
          if (SystemCalls.TryGetValue(callee.Name, out var numbers)) {
            this.SystemCall(call, numbers, arguments);
            return;
          }
          if (this.TryErrorIntrinsic(callee.Name, arguments))
            return;
          if (TryMathIntrinsic(callee.Name) is { } math && this.Stored(call)) {
            this.MathFunction(math, arguments);
            this._asm.Fstp(FormatOf(call.Type), this.Place(call));
            return;
          }
          if (callee.Name is "rt_peek" or "rt_peeki" or "rt_peekl" or "rt_poke" or "rt_poke_str")
            throw Decline("PEEK and POKE name 16-bit DOS offsets, and a 32- or 64-bit Linux process has nothing at them");
          throw Decline($"the native runtime has no {callee.Name} yet");
        }
        this.CallFunction(callee, arguments.Select(this.Of).ToList(), arguments.Select(argument => argument.Type).ToList());
        if (this.Stored(call))
          this.CopyMemory(X86Mem.At(module._returnArea), this.Place(call), module.SizeOf(call.Type));
      }

      /// <summary>Writes the arguments into a stack area the callee finds above its frame pointer, calls, and drops the area.</summary>
      private void CallFunction(IrFunction callee, IReadOnlyList<Operand> arguments, IReadOnlyList<IrType> types) {
        var slots = types.Select(module.ArgumentSlot).ToList();
        var area = (slots.Sum() + 15) / 16 * 16;
        if (area > 0)
          this._asm.AluImmediate(X86Alu.Sub, this.Word, X86Reg.Sp, area);
        var offset = 0;
        for (var i = 0; i < arguments.Count; ++i) {
          this.Copy(arguments[i], module.SizeOf(types[i]), X86Mem.At(X86Reg.Sp, offset));
          offset += slots[i];
        }
        this._asm.Call(module._entries[callee]);
        if (area > 0)
          this._asm.AluImmediate(X86Alu.Add, this.Word, X86Reg.Sp, area);
      }

      /// <summary><c>llvm.sqrt.f64</c> and its kin: the function name, or null for anything else.</summary>
      private static string? TryMathIntrinsic(string name) {
        var parts = name.Split('.');
        return parts is ["llvm", var fn, ['f', ..]] && fn is "sqrt" or "sin" or "cos" or "tan" or "atan" or "log" or "log2"
          or "log10" or "exp" or "exp2" or "exp10" or "pow" ? fn : null;
      }

      /// <summary>
      /// A math function on the x87, leaving its answer in ST(0). The domain errors are BASIC's error
      /// 5, illegal function call: a square root or logarithm out of range, a fractional power of a
      /// negative number.
      /// </summary>
      private void MathFunction(string fn, IReadOnlyList<IrValue> arguments) {
        var x = arguments[0];
        switch (fn) {
          case "sqrt":
            this.RequireSign(x, allowZero: true);
            this.LoadFloat(x);
            this._asm.X87(X87Code.Fsqrt);
            return;
          case "sin": this.LoadFloat(x); this._asm.X87(X87Code.Fsin); return;
          case "cos": this.LoadFloat(x); this._asm.X87(X87Code.Fcos); return;
          case "tan":
            this.LoadFloat(x);
            this._asm.X87(X87Code.Fptan);
            this._asm.FPop();                         // FPTAN pushes a 1.0 above the tangent
            return;
          case "atan":
            this.LoadFloat(x);
            this._asm.X87(X87Code.Fld1);
            this._asm.X87(X87Code.Fpatan);            // atan(ST(1) / ST(0)) = atan(x / 1)
            return;
          case "log" or "log2" or "log10":
            this.RequireSign(x, allowZero: false);
            this._asm.X87(fn switch { "log" => X87Code.Fldln2, "log10" => X87Code.Fldlg2, _ => X87Code.Fld1 });
            this.LoadFloat(x);
            this._asm.X87(X87Code.Fyl2x);             // ST(1) * log2(ST(0))
            return;
          case "exp" or "exp2" or "exp10":
            this.LoadFloat(x);
            if (fn != "exp2") {
              this._asm.X87(fn == "exp" ? X87Code.Fldl2e : X87Code.Fldl2t);
              this._asm.FArithmetic(X87Op.MulPop);
            }
            this.TwoToThePower();
            return;
          default:
            this.Power(x, arguments[1]);
            return;
        }
      }

      /// <summary>Raises error 5 unless the float is positive (or zero, when <paramref name="allowZero"/>).</summary>
      private void RequireSign(IrValue value, bool allowZero) {
        var fine = this.Local("inDomain");
        this.LoadFloat(value);
        this._asm.X87(X87Code.Fldz);
        this._asm.FucomipSt1();                       // compares 0 with x
        this._asm.FPop();
        this._asm.Jump(allowZero ? X86Cond.BelowOrEqual : X86Cond.Below, fine);
        this.RaiseError(5);
        this._asm.Bind(fine);
      }

      /// <summary>
      /// <c>2^ST(0)</c>: FSCALE by the integer part and F2XM1 on the fraction, which FRNDINT's
      /// nearest-rounding keeps within F2XM1's [-1, 1].
      /// </summary>
      private void TwoToThePower() {
        this._asm.X87(X87Code.Duplicate);
        this._asm.X87(X87Code.Frndint);
        this._asm.X87(X87Code.SubtractFromSt1);       // ST(1) = fraction, ST(0) = integer
        this._asm.X87(X87Code.Fxch);
        this._asm.X87(X87Code.F2xm1);
        this._asm.X87(X87Code.Fld1);
        this._asm.FArithmetic(X87Op.AddPop);          // 2^fraction
        this._asm.X87(X87Code.Fscale);                // times 2^integer
        this._asm.X87(X87Code.DropSt1);
      }

      /// <summary>
      /// <c>x ^ y</c> as <c>2^(y * log2 |x|)</c>. A zero base gives 1 for a zero exponent and 0
      /// otherwise; a negative base needs a whole exponent, and an odd one makes the answer negative.
      /// </summary>
      private void Power(IrValue x, IrValue y) {
        var positive = this.Local("positiveBase");
        var negative = this.Local("negativeBase");
        var done = this.Local("powered");
        var zeroExponent = this.Local("zeroExponent");
        this.LoadFloat(x);
        this._asm.X87(X87Code.Fldz);
        this._asm.FucomipSt1();                       // compares 0 with x
        this._asm.FPop();
        this._asm.Jump(X86Cond.Below, positive);
        this._asm.Jump(X86Cond.Above, negative);
        // 0 ^ y
        this.LoadFloat(y);
        this._asm.X87(X87Code.Fldz);
        this._asm.FucomipSt1();
        this._asm.FPop();
        this._asm.Jump(X86Cond.Equal, zeroExponent);
        this._asm.X87(X87Code.Fldz);
        this._asm.Jump(done);
        this._asm.Bind(zeroExponent);
        this._asm.X87(X87Code.Fld1);
        this._asm.Jump(done);
        // a negative base: the exponent must be whole, and its parity decides the sign
        this._asm.Bind(negative);
        var whole = this.Local("wholeExponent");
        this.LoadFloat(y);
        this._asm.X87(X87Code.Duplicate);
        this._asm.X87(X87Code.Frndint);
        this._asm.FucomipSt1();
        this._asm.Jump(X86Cond.Equal, whole);
        this._asm.FPop();
        this.RaiseError(5);
        this._asm.Bind(whole);
        this._asm.Fistp(X86Width.Qword, this.Scratch);
        this.LoadFloat(y);
        this.LoadFloat(x);
        this._asm.X87(X87Code.Fabs);
        this._asm.X87(X87Code.Fyl2x);
        this.TwoToThePower();
        var even = this.Local("evenExponent");
        this._asm.Mov(X86Width.Byte, X86Reg.Ax, this.Scratch);
        this._asm.AluImmediate(X86Alu.And, X86Width.Byte, X86Reg.Ax, 1);
        this._asm.Jump(X86Cond.Equal, even);
        this._asm.X87(X87Code.Fchs);
        this._asm.Bind(even);
        this._asm.Jump(done);
        this._asm.Bind(positive);
        this.LoadFloat(y);
        this.LoadFloat(x);
        this._asm.X87(X87Code.Fyl2x);                 // y * log2(x)
        this.TwoToThePower();
        this._asm.Bind(done);
      }

      private X86Mem Cell(X86Label label) => X86Mem.At(label);

      /// <summary>
      /// A procedure that arms a handler promises its caller the caller's handler back: the triple is
      /// saved on entry and restored on every return, as the DOS back end does.
      /// </summary>
      private void PreserveHandler(bool save) {
        if (!this._frame.Places.TryGetValue(this._frame.SavedHandler, out var slot))
          return;
        var cells = (X86Label[])[module._errorHandler, module._errorFrame, module._errorStack];
        for (var i = 0; i < cells.Length; ++i) {
          var saved = X86Mem.At(X86Reg.Bp, slot + i * this.WordBytes);
          this._asm.Mov(this.Word, X86Reg.Ax, save ? this.Cell(cells[i]) : saved);
          this._asm.Mov(this.Word, save ? saved : this.Cell(cells[i]), X86Reg.Ax);
        }
      }

      /// <summary>
      /// ON ERROR's and EXIT FAR's intrinsics, expanded in place because arming captures the CURRENT
      /// frame and stack - a call would capture its own - and keeping the DOS runtime's rules.
      /// <c>sys_trap(code)</c> is the portable runtime's first question in <c>rt_error</c>, and
      /// <c>rt_raise</c>'s: ERR gets the code, and with a handler armed it never returns - the
      /// statement pair is latched for RESUME, the armed frame and stack come back, and the handler
      /// runs (a fault inside it enters it again); with none it returns and the runtime reports the error.
      /// </summary>
      private bool TryErrorIntrinsic(string name, IReadOnlyList<IrValue> arguments) {
        var word = this.Word;
        void ClearError() {
          if (module.ErrorCode is { } err)
            this._asm.MovImmediate(X86Width.Word, X86Mem.At(err), 0);
        }
        void Arm(Operand handler) {
          this.Load(X86Reg.Ax, handler, word);
          this._asm.Mov(word, this.Cell(module._errorHandler), X86Reg.Ax);
          this._asm.Mov(word, this.Cell(module._errorFrame), X86Reg.Bp);
          this._asm.Mov(word, this.Cell(module._errorStack), X86Reg.Sp);
          ClearError();
        }
        switch (name) {
          case "rt_onerr_arm":
            Arm(this.Of(arguments[0]));
            return true;
          case "rt_onerr_resume_next":
            Arm(new AddressOperand(X86Mem.At(module._resumeNextStub)));
            return true;
          case "rt_onerr_disarm":
            this._asm.MovImmediate(word, this.Cell(module._errorHandler), 0);
            return true;
          case "rt_resume_mark":
            this.Load(X86Reg.Ax, this.Of(arguments[0]), word);
            this._asm.Mov(word, this.Cell(module._statementStart), X86Reg.Ax);
            this.Load(X86Reg.Ax, this.Of(arguments[1]), word);
            this._asm.Mov(word, this.Cell(module._statementNext), X86Reg.Ax);
            return true;
          case "rt_resume_same" or "rt_resume_next":
            ClearError();
            this._asm.JumpIndirect(this.Cell(name == "rt_resume_same" ? module._faultStart : module._faultNext));
            return true;
          case "rt_err_clear":
            ClearError();
            return true;
          case "sys_trap": {
            var none = this.Local("noHandler");
            if (module.ErrorCode is { } err) {
              this.Load(X86Reg.Ax, this.Of(arguments[0]), X86Width.Dword);
              this._asm.Mov(X86Width.Word, X86Mem.At(err), X86Reg.Ax);
            }
            this._asm.Mov(word, X86Reg.Ax, this.Cell(module._errorHandler));
            this._asm.Alu(X86Alu.Or, word, X86Reg.Ax, X86Reg.Ax);
            this._asm.Jump(X86Cond.Equal, none);
            this.LatchFault();
            this._asm.Mov(word, X86Reg.Bp, this.Cell(module._errorFrame));
            this._asm.Mov(word, X86Reg.Sp, this.Cell(module._errorStack));
            this._asm.JumpIndirect(this.Cell(module._errorHandler));
            this._asm.Bind(none);
            return true;
          }
          // EXIT FAR AT label: where to land, and the frame and stack to have back when it does
          case "rt_efar_arm":
            this.Load(X86Reg.Ax, this.Of(arguments[0]), word);
            this._asm.Mov(word, this.Cell(module._exitFarTarget), X86Reg.Ax);
            this._asm.Mov(word, this.Cell(module._exitFarFrame), X86Reg.Bp);
            this._asm.Mov(word, this.Cell(module._exitFarStack), X86Reg.Sp);
            return true;
          // a bare EXIT FAR: every frame between here and there is abandoned at once
          case "rt_efar_go":
            this._asm.Mov(word, X86Reg.Bp, this.Cell(module._exitFarFrame));
            this._asm.Mov(word, X86Reg.Sp, this.Cell(module._exitFarStack));
            this._asm.JumpIndirect(this.Cell(module._exitFarTarget));
            return true;
          default:
            return false;
        }
      }

      /// <summary>The statement that faulted becomes the one RESUME returns to.</summary>
      private void LatchFault() {
        this._asm.Mov(this.Word, X86Reg.Ax, this.Cell(module._statementStart));
        this._asm.Mov(this.Word, this.Cell(module._faultStart), X86Reg.Ax);
        this._asm.Mov(this.Word, X86Reg.Ax, this.Cell(module._statementNext));
        this._asm.Mov(this.Word, this.Cell(module._faultNext), X86Reg.Ax);
      }

      /// <summary>The portable runtime's system primitives, by Linux system call number: x64, then i386.</summary>
      private static readonly Dictionary<string, (int Amd64, int I386)> SystemCalls = new() {
        ["sys_read"] = (0, 3),
        ["sys_write"] = (1, 4),
        ["sys_open"] = (2, 5),
        ["sys_close"] = (3, 6),
        ["sys_seek"] = (8, 19),
        ["sys_unlink"] = (87, 10),
        ["sys_exit"] = (231, 1),     // exit_group on x64, so no thread is left behind
      };

      /// <summary>
      /// A system primitive as a Linux system call: the arguments into the ABI's registers - RDI, RSI,
      /// RDX on x64, EBX, ECX, EDX on i386 - and the answer, a result or a negative errno, from the
      /// accumulator. <c>sys_open</c>'s portable mode becomes the flags that mean it, and a file it
      /// creates gets 0644.
      /// </summary>
      private void SystemCall(IrCall call, (int Amd64, int I386) numbers, IReadOnlyList<IrValue> arguments) {
        X86Reg[] registers = this.Is64 ? [X86Reg.Di, X86Reg.Si, X86Reg.Dx] : [X86Reg.Bx, X86Reg.Cx, X86Reg.Dx];
        for (var i = 0; i < arguments.Count; ++i) {
          var argument = arguments[i];
          if (argument.Type.IsPointer)
            this.Load(registers[i], this.Of(argument), this.Word);
          else
            this.LoadExtended(registers[i], this.Of(argument), module.SizeOf(argument.Type), signed: true,
              this.Is64 ? X86Width.Qword : X86Width.Dword);
        }
        if (call.Callee is IrFunction { Name: "sys_open" }) {
          // mode 0 read, 1 create and truncate, 2 append (creating), 3 read and write (creating)
          const int readOnly = 0, writeOnly = 1, readWrite = 2, create = 0x40, truncate = 0x200, append = 0x400;
          var flags = registers[1];
          var done = this.Local("flags");
          foreach (var (mode, value) in (ReadOnlySpan<(int, int)>)[
              (1, writeOnly | create | truncate), (2, writeOnly | create | append), (3, readWrite | create)]) {
            var next = this.Local("mode");
            this._asm.AluImmediate(X86Alu.Cmp, X86Width.Dword, flags, mode);
            this._asm.Jump(X86Cond.NotEqual, next);
            this._asm.MovImmediate(X86Width.Dword, flags, value);
            this._asm.Jump(done);
            this._asm.Bind(next);
          }
          this._asm.MovImmediate(X86Width.Dword, flags, readOnly);
          this._asm.Bind(done);
          this._asm.MovImmediate(X86Width.Dword, registers[2], 0x1A4);
        }
        this._asm.MovImmediate(X86Width.Dword, X86Reg.Ax, this.Is64 ? numbers.Amd64 : numbers.I386);
        this._asm.SystemCall();
        if (this.Stored(call))
          this.Store(this.Place(call), X86Reg.Ax, WidthOf(module.SizeOf(call.Type)));
      }

      /// <summary>BASIC's run-time error <paramref name="code"/>, through the portable runtime's <c>rt_error</c>.</summary>
      private void RaiseError(int code) {
        var error = module.ErrorFunction
          ?? throw Decline("the program raises a run-time error but the runtime has no rt_error");
        this.CallFunction(error, [new ConstantOperand(code)], [IrType.I32]);
      }

      private void LowerReturn(IrRet ret) {
        if (ret.Value is { } value)
          this.Copy(this.Of(value), module.SizeOf(value.Type), X86Mem.At(module._returnArea));
        this.PreserveHandler(save: false);
        this._asm.Mov(this.Word, X86Reg.Sp, X86Reg.Bp);
        this._asm.Pop(X86Reg.Bp);
        this._asm.Ret();
      }

      // --- control flow ------------------------------------------------------------------------

      /// <summary>The phi copies of an edge: a parallel assignment, staged when one phi reads another of the block.</summary>
      private void Edge(IrBasicBlock from, IrBasicBlock to) {
        var moves = to.Phis.Select(phi => (Phi: phi, Source: phi.IncomingFrom(from)
          ?? throw Decline($"a phi in '{function.Name}' has no value for one of its edges"))).ToList();
        if (moves.Count == 0)
          return;
        if (moves.Any(move => move.Source is IrPhi phi && phi.Parent == to && phi != move.Phi)) {
          var offset = 0;
          foreach (var (phi, source) in moves) {
            var size = module.SizeOf(phi.Type);
            this.Copy(this.Of(source), size, X86Mem.At(module._staging, offset));
            offset += (size + 7) / 8 * 8;
          }
          module._stagingBytes = Math.Max(module._stagingBytes, offset);
          offset = 0;
          foreach (var (phi, _) in moves) {
            var size = module.SizeOf(phi.Type);
            this.CopyMemory(X86Mem.At(module._staging, offset), this.Place(phi), size);
            offset += (size + 7) / 8 * 8;
          }
          return;
        }
        foreach (var (phi, source) in moves)
          if (source != phi)
            this.Copy(this.Of(source), module.SizeOf(phi.Type), this.Place(phi));
      }

      private void JumpUnlessNext(IrBasicBlock target) {
        if (target != this._next)
          this._asm.Jump(this._blocks[target]);
      }

      private X86Label EdgeLabel(IrBasicBlock to, List<(X86Label Stub, IrBasicBlock Target)> stubs) {
        if (!to.Phis.Any())
          return this._blocks[to];
        var stub = this.Local("edge");
        stubs.Add((stub, to));
        return stub;
      }

      private void EmitStubs(IrBasicBlock from, List<(X86Label Stub, IrBasicBlock Target)> stubs) {
        foreach (var (stub, target) in stubs) {
          this._asm.Bind(stub);
          this.Edge(from, target);
          this._asm.Jump(this._blocks[target]);
        }
      }

      private void LowerConditionalBranch(IrBasicBlock block, IrCondBr branch) {
        var stubs = new List<(X86Label, IrBasicBlock)>();
        var ifTrue = this.EdgeLabel(branch.IfTrue, stubs);
        if (branch.Condition is IrCmp compare && this.FusesWithBranch(block, compare)) {
          var (condition, orderedOnly) = this.Compare(compare);
          if (orderedOnly) {
            var unordered = this.Local("unordered");
            this._asm.Jump(X86Cond.Parity, unordered);
            this._asm.Jump(condition, ifTrue);
            this._asm.Bind(unordered);
          } else {
            this._asm.Jump(condition, ifTrue);
          }
        } else {
          this.Load(X86Reg.Ax, this.Of(branch.Condition), X86Width.Byte);
          this._asm.Test(X86Width.Byte, X86Reg.Ax, X86Reg.Ax);
          this._asm.Jump(X86Cond.NotEqual, ifTrue);
        }
        this.Edge(block, branch.IfFalse);
        if (stubs.Count == 0)
          this.JumpUnlessNext(branch.IfFalse);
        else
          this._asm.Jump(this._blocks[branch.IfFalse]);
        this.EmitStubs(block, stubs);
      }

      private void LowerSwitch(IrBasicBlock block, IrSwitch @switch) {
        var stubs = new List<(X86Label, IrBasicBlock)>();
        var size = module.SizeOf(@switch.Condition.Type);
        var condition = this.Of(@switch.Condition);
        if (size > this.WordBytes)
          throw Decline("a 64-bit SELECT has no i386 lowering yet");
        var width = WidthOf(size);
        foreach (var (value, target) in @switch.Cases) {
          this.Load(X86Reg.Ax, condition, width);
          this.AluWith(X86Alu.Cmp, width, X86Reg.Ax, new ConstantOperand(value));
          this._asm.Jump(X86Cond.Equal, this.EdgeLabel(target, stubs));
        }
        this.Edge(block, @switch.DefaultTarget);
        this._asm.Jump(this._blocks[@switch.DefaultTarget]);
        this.EmitStubs(block, stubs);
      }
    }
  }
}
