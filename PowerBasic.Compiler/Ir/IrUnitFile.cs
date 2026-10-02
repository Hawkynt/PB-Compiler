using System.Text;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A compiled unit for the platforms whose code pbc generates from IR alone - x86-32, x64 and the
/// 6502: the unit's lowered IR, before any optimization, in a file. <c>$LINK</c> reads it back and
/// <see cref="IrModuleLinker"/> joins it to the program, so the whole program is optimized as one -
/// the unit is compiled once and linked anywhere, and nothing in it is specific to one of the three.
///
/// <para>
/// The format is binary and versioned: the magic <c>PBIRUNIT</c>, a version, the module header, the
/// globals, every function's signature and attributes, then each body. A body starts with the result
/// type of every instruction in it, so an operand that refers FORWARD - a phi's input, or a block
/// the lowering built after the blocks it dominates - can be read as a typed placeholder and patched
/// once the body is complete. Operands are tagged references: an instruction, an argument, a global,
/// a function, or a constant written in place. An F80 constant keeps its exact eighty bits.
/// </para>
/// <para>
/// A DOS unit (<c>PBU</c> with 8086 code) and this one share the <c>.PBU</c> extension, as the two
/// kinds of unit of one language; the magic tells them apart, and each linker refuses the other's.
/// </para>
/// </summary>
public static class IrUnitFile {

  private static ReadOnlySpan<byte> Magic => "PBIRUNIT"u8;
  private const int Version = 1;

  private enum Ref : byte { Instruction, Argument, Global, Function, Int, Float, Null, BlockAddress, FarEntry, Undef }

  private enum Op : byte {
    Binary, Cmp, Cast, Alloca, Load, Store, InlineAsm, Gep, FarPtr, Phi, Select, Call, Ret, Br, CondBr,
    Switch, IndirectBr, Unreachable,
  }

  private static ReadOnlySpan<byte> LibraryMagic => "PBIRLIB\0"u8;

  /// <summary>Whether <paramref name="bytes"/> start like an IR unit.</summary>
  public static bool IsIrUnit(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

  /// <summary>Whether <paramref name="bytes"/> start like a library of IR units.</summary>
  public static bool IsIrLibrary(ReadOnlySpan<byte> bytes) => bytes.StartsWith(LibraryMagic);

  /// <summary>
  /// A library: the magic <c>PBIRLIB</c>, a version, then each member unit's file as it is, under
  /// its name. <c>$LINK</c> of a library links every member; the program's dead-procedure pass
  /// removes what nothing calls.
  /// </summary>
  public static byte[] WriteLibrary(IReadOnlyList<(string Name, byte[] Unit)> members) {
    ArgumentNullException.ThrowIfNull(members);
    using var stream = new MemoryStream();
    using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) {
      w.Write(LibraryMagic);
      w.Write(Version);
      w.Write(members.Count);
      foreach (var (name, unit) in members) {
        if (!IsIrUnit(unit))
          throw new InvalidDataException($"library member '{name}' is not an IR unit");
        w.Write(name);
        w.Write(unit.Length);
        w.Write(unit);
      }
    }
    return stream.ToArray();
  }

  public static IReadOnlyList<(string Name, byte[] Unit)> ReadLibrary(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    if (!IsIrLibrary(bytes))
      throw new InvalidDataException("not a library of IR units (the magic is not PBIRLIB)");
    using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
    r.ReadBytes(LibraryMagic.Length);
    var version = r.ReadInt32();
    if (version != Version)
      throw new InvalidDataException($"IR library version {version}; this compiler reads version {Version}");
    var members = new List<(string, byte[])>();
    var count = r.ReadInt32();
    for (var i = 0; i < count; ++i) {
      var name = r.ReadString();
      members.Add((name, r.ReadBytes(r.ReadInt32())));
    }
    return members;
  }

  public static byte[] Write(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
      new UnitWriter(writer, module).Write();
    return stream.ToArray();
  }

  public static IrModule Read(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    if (!IsIrUnit(bytes))
      throw new InvalidDataException("not an IR unit (the magic is not PBIRUNIT)");
    using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
    reader.ReadBytes(Magic.Length);
    var version = reader.ReadInt32();
    if (version != Version)
      throw new InvalidDataException($"IR unit version {version}; this compiler reads version {Version}");
    return new UnitReader(reader).Read();
  }

  private static void WriteType(BinaryWriter w, IrType type) {
    w.Write((byte)type.Kind);
    w.Write(type.Bits);
    w.Write(type.Signed);
    w.Write((byte)type.Format);
    w.Write(type.AddressSpace);
  }

  private static IrType ReadType(BinaryReader r) {
    var kind = (IrTypeKind)r.ReadByte();
    var bits = r.ReadInt32();
    var signed = r.ReadBoolean();
    var format = (IrFloatFormat)r.ReadByte();
    var space = r.ReadInt32();
    return kind switch {
      IrTypeKind.Void => IrType.Void,
      IrTypeKind.Int => IrType.Integer(bits, signed),
      IrTypeKind.Float => IrType.Floating(bits, format),
      _ => space == 0 ? IrType.Ptr : new IrType(IrTypeKind.Ptr, 0, AddressSpace: space),
    };
  }

  private static void WriteOptionalString(BinaryWriter w, string? text) {
    w.Write(text is not null);
    if (text is not null)
      w.Write(text);
  }

  private static string? ReadOptionalString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

  private sealed class UnitWriter(BinaryWriter w, IrModule module) {

    private readonly Dictionary<IrGlobalVariable, int> _globals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrFunction, int> _functions = new(ReferenceEqualityComparer.Instance);
    private Dictionary<IrInstruction, int> _instructions = new(ReferenceEqualityComparer.Instance);
    private Dictionary<IrBasicBlock, int> _blocks = new(ReferenceEqualityComparer.Instance);

    public void Write() {
      w.Write(Magic);
      w.Write(Version);
      w.Write(module.Name);
      w.Write((int)module.Dialect);
      w.Write((int)module.EffectiveDialect);
      w.Write(module.AsciiOnly);

      w.Write(module.Globals.Count);
      foreach (var global in module.Globals) {
        this._globals.Add(global, this._globals.Count);
        w.Write(global.Name);
        WriteType(w, global.ValueType);
        w.Write(global.IsZeroInitialized);
        w.Write(global.Count);
        w.Write(global.Bytes is not null);
        if (global.Bytes is { } bytes) {
          w.Write(bytes.Length);
          w.Write(bytes);
        }
        w.Write(global.FloatingValues is not null);
        if (global.FloatingValues is { } values) {
          w.Write(values.Length);
          foreach (var value in values)
            w.Write(value);
        }
      }

      w.Write(module.Functions.Count);
      foreach (var function in module.Functions) {
        this._functions.Add(function, this._functions.Count);
        w.Write(function.Name);
        WriteType(w, function.ReturnType);
        w.Write(function.Parameters.Count);
        foreach (var parameter in function.Parameters) {
          WriteType(w, parameter.Type);
          WriteOptionalString(w, parameter.Name);
        }
        w.Write(function.HasConvention);
        w.Write((byte)function.Convention);
        w.Write(function.IsVarArgs);
        w.Write(function.NoInline);
        w.Write(function.ReturnsClosure);
        w.Write(function.HasErrorHandler);
        w.Write(function.HasInlineAsm);
        WriteOptionalString(w, function.ClonedFrom);
      }

      foreach (var function in module.Functions)
        this.WriteBody(function);
    }

    private void WriteBody(IrFunction function) {
      this._blocks = new(ReferenceEqualityComparer.Instance);
      this._instructions = new(ReferenceEqualityComparer.Instance);
      w.Write(function.Blocks.Count);
      foreach (var block in function.Blocks) {
        this._blocks.Add(block, this._blocks.Count);
        w.Write(block.Label);
      }
      var all = function.AllInstructions.ToList();
      w.Write(all.Count);
      foreach (var instruction in all) {
        this._instructions.Add(instruction, this._instructions.Count);
        WriteType(w, instruction.Type);
      }
      foreach (var block in function.Blocks) {
        w.Write(block.Instructions.Count);
        foreach (var instruction in block.Instructions)
          this.WriteInstruction(instruction);
      }
    }

    private void WriteInstruction(IrInstruction instruction) {
      switch (instruction) {
        case IrBinary binary:
          w.Write((byte)Op.Binary);
          w.Write((byte)binary.Op);
          break;
        case IrCmp compare:
          w.Write((byte)Op.Cmp);
          w.Write((byte)compare.Pred);
          w.Write(compare.IsSourceCondition);
          break;
        case IrCast cast:
          w.Write((byte)Op.Cast);
          w.Write((byte)cast.Op);
          break;
        case IrAlloca alloca:
          w.Write((byte)Op.Alloca);
          WriteType(w, alloca.Allocated);
          w.Write(alloca.Count);
          w.Write(alloca.IsSourceVariable);
          w.Write((byte)alloca.EnvRole);
          break;
        case IrLoad:
          w.Write((byte)Op.Load);
          break;
        case IrStore:
          w.Write((byte)Op.Store);
          break;
        case IrInlineAsm asm:
          w.Write((byte)Op.InlineAsm);
          w.Write(asm.Text);
          w.Write(asm.Routable);
          w.Write(asm.Names.Count);
          foreach (var name in asm.Names)
            w.Write(name);
          break;
        case IrGep gep:
          w.Write((byte)Op.Gep);
          w.Write(gep.ElementType is not null);
          if (gep.ElementType is { } element)
            WriteType(w, element);
          break;
        case IrFarPtr:
          w.Write((byte)Op.FarPtr);
          break;
        case IrPhi phi:
          w.Write((byte)Op.Phi);
          w.Write(phi.IncomingBlocks.Count);
          foreach (var block in phi.IncomingBlocks)
            w.Write(this._blocks[block]);
          break;
        case IrSelect:
          w.Write((byte)Op.Select);
          break;
        case IrCall call:
          w.Write((byte)Op.Call);
          w.Write((byte)call.Convention);
          break;
        case IrRet:
          w.Write((byte)Op.Ret);
          break;
        case IrBr branch:
          w.Write((byte)Op.Br);
          w.Write(this._blocks[branch.Target]);
          break;
        case IrCondBr branch:
          w.Write((byte)Op.CondBr);
          w.Write(this._blocks[branch.IfTrue]);
          w.Write(this._blocks[branch.IfFalse]);
          break;
        case IrSwitch @switch:
          w.Write((byte)Op.Switch);
          w.Write(this._blocks[@switch.DefaultTarget]);
          w.Write(@switch.Cases.Count);
          foreach (var (value, target) in @switch.Cases) {
            w.Write(value);
            w.Write(this._blocks[target]);
          }
          break;
        case IrIndirectBr branch:
          w.Write((byte)Op.IndirectBr);
          w.Write(branch.Targets.Count);
          foreach (var target in branch.Targets)
            w.Write(this._blocks[target]);
          break;
        case IrUnreachable:
          w.Write((byte)Op.Unreachable);
          break;
        default:
          throw new NotSupportedException($"an IR unit has no encoding for {instruction.GetType().Name}");
      }
      WriteOptionalString(w, instruction.Name);
      w.Write((int)instruction.FastMathFlags);
      w.Write(instruction.Operands.Count);
      foreach (var operand in instruction.Operands)
        this.WriteRef(operand);
    }

    private void WriteRef(IrValue value) {
      switch (value) {
        case IrInstruction instruction:
          w.Write((byte)Ref.Instruction);
          w.Write(this._instructions[instruction]);
          break;
        case IrArgument argument:
          w.Write((byte)Ref.Argument);
          w.Write(argument.Index);
          break;
        case IrGlobalVariable global:
          w.Write((byte)Ref.Global);
          w.Write(this._globals[global]);
          break;
        case IrFunction function:
          w.Write((byte)Ref.Function);
          w.Write(this._functions[function]);
          break;
        case IrConstantInt constant:
          w.Write((byte)Ref.Int);
          WriteType(w, constant.Type);
          w.Write(constant.Value);
          break;
        case IrConstantFloat constant:
          w.Write((byte)Ref.Float);
          WriteType(w, constant.Type);
          if (constant.Type is { Bits: 80, Format: IrFloatFormat.Ieee }) {
            w.Write(constant.Float80.SignExponent);
            w.Write(constant.Float80.Significand);
          } else {
            constant.TryGetDoubleExact(out var number);
            w.Write(number);
          }
          break;
        case IrNullPtr nullPointer:
          w.Write((byte)Ref.Null);
          WriteType(w, nullPointer.Type);
          break;
        case IrBlockAddress address:
          w.Write((byte)Ref.BlockAddress);
          w.Write(this._blocks[address.Block]);
          break;
        case IrFarEntry entry:
          w.Write((byte)Ref.FarEntry);
          w.Write(this._functions[entry.Target]);
          break;
        case IrUndef undef:
          w.Write((byte)Ref.Undef);
          WriteType(w, undef.Type);
          break;
        default:
          throw new NotSupportedException($"an IR unit has no encoding for an operand of kind {value.GetType().Name}");
      }
    }
  }

  private sealed class UnitReader(BinaryReader r) {

    private readonly List<IrGlobalVariable> _globals = [];
    private readonly List<IrFunction> _functions = [];

    public IrModule Read() {
      var name = r.ReadString();
      var dialect = (Dialect)r.ReadInt32();
      var effective = (Dialect)r.ReadInt32();
      var module = new IrModule(name, dialect, effective) { AsciiOnly = r.ReadBoolean() };

      var globals = r.ReadInt32();
      for (var i = 0; i < globals; ++i) {
        var globalName = r.ReadString();
        var type = ReadType(r);
        var zero = r.ReadBoolean();
        var count = r.ReadInt32();
        var bytes = r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null;
        double[]? values = null;
        if (r.ReadBoolean()) {
          values = new double[r.ReadInt32()];
          for (var j = 0; j < values.Length; ++j)
            values[j] = r.ReadDouble();
        }
        this._globals.Add(module.AddGlobal(new IrGlobalVariable(globalName, type) {
          IsZeroInitialized = zero, Count = count, Bytes = bytes, FloatingValues = values,
        }));
      }

      var functions = r.ReadInt32();
      for (var i = 0; i < functions; ++i) {
        var functionName = r.ReadString();
        var returnType = ReadType(r);
        var parameters = new List<IrArgument>();
        var count = r.ReadInt32();
        for (var j = 0; j < count; ++j)
          parameters.Add(new IrArgument(ReadType(r), j, ReadOptionalString(r)));
        var hasConvention = r.ReadBoolean();
        var convention = (IrCallConvention)r.ReadByte();
        var varArgs = r.ReadBoolean();
        var noInline = r.ReadBoolean();
        var returnsClosure = r.ReadBoolean();
        var errorHandler = r.ReadBoolean();
        var inlineAsm = r.ReadBoolean();
        var clonedFrom = ReadOptionalString(r);
        var function = hasConvention
          ? new IrFunction(functionName, returnType, parameters) {
            Convention = convention, IsVarArgs = varArgs, NoInline = noInline, ReturnsClosure = returnsClosure,
            ClonedFrom = clonedFrom,
          }
          : new IrFunction(functionName, returnType, parameters) {
            IsVarArgs = varArgs, NoInline = noInline, ReturnsClosure = returnsClosure, ClonedFrom = clonedFrom,
          };
        function.HasErrorHandler = errorHandler;
        function.HasInlineAsm = inlineAsm;
        this._functions.Add(module.AddFunction(function));
      }

      foreach (var function in this._functions)
        this.ReadBody(function);

      var errors = IrVerifier.Verify(module);
      if (errors.Count != 0)
        throw new InvalidDataException($"IR unit '{name}' does not verify: {string.Join("; ", errors)}");
      return module;
    }

    private IrFunction _function = null!;
    private List<IrBasicBlock> _blocks = [];
    private IrInstruction?[] _built = [];
    private IrType[] _types = [];
    private readonly List<(IrInstruction User, int Operand, int Target)> _forward = [];
    private readonly Dictionary<int, IrUndef> _placeholders = [];

    private void ReadBody(IrFunction function) {
      this._function = function;
      this._blocks = [];
      var blocks = r.ReadInt32();
      for (var i = 0; i < blocks; ++i)
        this._blocks.Add(function.CreateBlock(r.ReadString()));
      var instructions = r.ReadInt32();
      this._types = new IrType[instructions];
      for (var i = 0; i < instructions; ++i)
        this._types[i] = ReadType(r);
      this._built = new IrInstruction?[instructions];
      this._forward.Clear();
      this._placeholders.Clear();

      var id = 0;
      var phis = new List<(IrPhi Phi, int[] Blocks, IrValue[] Values)>();
      foreach (var block in this._blocks) {
        var count = r.ReadInt32();
        for (var i = 0; i < count; ++i, ++id) {
          var instruction = this.ReadInstruction(id, phis);
          if (instruction is IrPhi phi)
            block.AppendPhi(phi);
          else
            block.Append(instruction);
          this._built[id] = instruction;
        }
      }
      foreach (var (user, operand, target) in this._forward)
        user.SetOperand(operand, this._built[target]!);
      foreach (var (phi, incomingBlocks, values) in phis)
        for (var i = 0; i < values.Length; ++i)
          phi.AddIncoming(values[i] is IrUndef placeholder && this.PlaceholderTarget(placeholder) is { } target
            ? this._built[target]! : values[i], this._blocks[incomingBlocks[i]]);
    }

    private int? PlaceholderTarget(IrUndef placeholder) {
      foreach (var (target, value) in this._placeholders)
        if (ReferenceEquals(value, placeholder))
          return target;
      return null;
    }

    private IrInstruction ReadInstruction(int id, List<(IrPhi, int[], IrValue[])> phis) {
      var op = (Op)r.ReadByte();
      var type = this._types[id];
      IrInstruction instruction;
      Func<IrValue[], IrInstruction> build;
      int[]? phiBlocks = null;
      switch (op) {
        case Op.Binary: {
          var binary = (IrBinaryOp)r.ReadByte();
          build = operands => new IrBinary(binary, operands[0], operands[1]);
          break;
        }
        case Op.Cmp: {
          var predicate = (IrCmpPred)r.ReadByte();
          var source = r.ReadBoolean();
          build = operands => new IrCmp(predicate, operands[0], operands[1]) { IsSourceCondition = source };
          break;
        }
        case Op.Cast: {
          var cast = (IrCastOp)r.ReadByte();
          build = operands => new IrCast(cast, operands[0], type);
          break;
        }
        case Op.Alloca: {
          var allocated = ReadType(r);
          var count = r.ReadInt32();
          var variable = r.ReadBoolean();
          var role = (ClosureEnvRole)r.ReadByte();
          build = _ => new IrAlloca(allocated) { Count = count, IsSourceVariable = variable, EnvRole = role };
          break;
        }
        case Op.Load:
          build = operands => new IrLoad(type, operands[0]);
          break;
        case Op.Store:
          build = operands => new IrStore(operands[0], operands[1]);
          break;
        case Op.InlineAsm: {
          var text = r.ReadString();
          var routable = r.ReadBoolean();
          var names = new string[r.ReadInt32()];
          for (var i = 0; i < names.Length; ++i)
            names[i] = r.ReadString();
          build = operands => {
            var asm = new IrInlineAsm(text) { Routable = routable };
            for (var i = 0; i < names.Length; ++i)
              asm.Bind(names[i], operands[i]);
            return asm;
          };
          break;
        }
        case Op.Gep: {
          var element = r.ReadBoolean() ? ReadType(r) : null;
          build = operands => element is null ? new IrGep(operands[0], operands[1]) : new IrGep(operands[0], operands[1], element);
          break;
        }
        case Op.FarPtr:
          build = operands => new IrFarPtr(operands[0], operands[1]);
          break;
        case Op.Phi: {
          phiBlocks = new int[r.ReadInt32()];
          for (var i = 0; i < phiBlocks.Length; ++i)
            phiBlocks[i] = r.ReadInt32();
          build = _ => new IrPhi(type);
          break;
        }
        case Op.Select:
          build = operands => new IrSelect(operands[0], operands[1], operands[2]);
          break;
        case Op.Call: {
          var convention = (IrCallConvention)r.ReadByte();
          build = operands => new IrCall(type, operands[0], operands[1..], convention);
          break;
        }
        case Op.Ret:
          build = operands => new IrRet(operands.Length > 0 ? operands[0] : null);
          break;
        case Op.Br: {
          var target = this._blocks[r.ReadInt32()];
          build = _ => new IrBr(target);
          break;
        }
        case Op.CondBr: {
          var ifTrue = this._blocks[r.ReadInt32()];
          var ifFalse = this._blocks[r.ReadInt32()];
          build = operands => new IrCondBr(operands[0], ifTrue, ifFalse);
          break;
        }
        case Op.Switch: {
          var fallback = this._blocks[r.ReadInt32()];
          var cases = new (long, IrBasicBlock)[r.ReadInt32()];
          for (var i = 0; i < cases.Length; ++i)
            cases[i] = (r.ReadInt64(), this._blocks[r.ReadInt32()]);
          build = operands => {
            var @switch = new IrSwitch(operands[0], fallback);
            foreach (var (value, target) in cases)
              @switch.AddCase(value, target);
            return @switch;
          };
          break;
        }
        case Op.IndirectBr: {
          var targets = new IrBasicBlock[r.ReadInt32()];
          for (var i = 0; i < targets.Length; ++i)
            targets[i] = this._blocks[r.ReadInt32()];
          build = operands => new IrIndirectBr(operands[0], targets);
          break;
        }
        case Op.Unreachable:
          build = _ => new IrUnreachable();
          break;
        default:
          throw new InvalidDataException($"IR unit: unknown instruction tag {(byte)op}");
      }

      var name = ReadOptionalString(r);
      var fastMath = (IrFastMathFlags)r.ReadInt32();
      var operandCount = r.ReadInt32();
      var operands = new IrValue[operandCount];
      var forward = new List<(int Operand, int Target)>();
      for (var i = 0; i < operandCount; ++i) {
        operands[i] = this.ReadRef(out var target);
        if (target is { } pending)
          forward.Add((i, pending));
      }

      instruction = build(operands);
      instruction.Name = name;
      instruction.FastMathFlags = fastMath;
      if (instruction is IrPhi phi)
        phis.Add((phi, phiBlocks!, operands));
      else
        foreach (var (operand, target) in forward)
          this._forward.Add((instruction, operand, target));
      return instruction;
    }

    /// <summary>An operand; a reference to an instruction not built yet is a typed placeholder, with its id in <paramref name="forward"/>.</summary>
    private IrValue ReadRef(out int? forward) {
      forward = null;
      var tag = (Ref)r.ReadByte();
      switch (tag) {
        case Ref.Instruction: {
          var id = r.ReadInt32();
          if (this._built[id] is { } built)
            return built;
          forward = id;
          if (!this._placeholders.TryGetValue(id, out var placeholder))
            this._placeholders[id] = placeholder = new IrUndef(this._types[id]);
          return placeholder;
        }
        case Ref.Argument:
          return this._function.Parameters[r.ReadInt32()];
        case Ref.Global:
          return this._globals[r.ReadInt32()];
        case Ref.Function:
          return this._functions[r.ReadInt32()];
        case Ref.Int: {
          var type = ReadType(r);
          return new IrConstantInt(type, r.ReadInt64());
        }
        case Ref.Float: {
          var type = ReadType(r);
          if (type is { Bits: 80, Format: IrFloatFormat.Ieee }) {
            var signExponent = r.ReadUInt16();
            return IrConstantFloat.FromFloat80Bits(signExponent, r.ReadUInt64());
          }
          return new IrConstantFloat(type, r.ReadDouble());
        }
        case Ref.Null:
          return new IrNullPtr(ReadType(r));
        case Ref.BlockAddress:
          return new IrBlockAddress(this._blocks[r.ReadInt32()]);
        case Ref.FarEntry:
          return new IrFarEntry(this._functions[r.ReadInt32()]);
        case Ref.Undef:
          return new IrUndef(ReadType(r));
        default:
          throw new InvalidDataException($"IR unit: unknown operand tag {(byte)tag}");
      }
    }
  }
}
