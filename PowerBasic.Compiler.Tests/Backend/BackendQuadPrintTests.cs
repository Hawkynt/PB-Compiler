using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PRINT of a signed QUAD. Genuine PB 3.5 keeps the integer exact on the x87 stack and sends it
/// through the 15-digit DOUBLE formatter, so the back end must stage every one of the integer's
/// 64 bits before FILDing it and calling that same formatter.
/// </summary>
[TestFixture]
public sealed class BackendQuadPrintTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static bool Contains(byte[] image, params byte[] pattern) {
    for (var i = 0; i <= image.Length - pattern.Length; ++i)
      if (image.AsSpan(i, pattern.Length).SequenceEqual(pattern))
        return true;
    return false;
  }

  private static MFunction Select(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var fn in module!.Functions)
      if (!fn.IsDeclaration)
        IntegerRecovery.Run(fn);
    IrPassManager.Standard().RunOnModule(module);

    var main = module.Functions.First(fn => fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var machine = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    return machine!;
  }

  private static IrFunction QwordBitwiseFunction(IrBinaryOp operation) {
    var fn = new IrFunction("F", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrAlloca(IrType.I64));
    var right = entry.Append(new IrAlloca(IrType.I64));
    var result = entry.Append(new IrAlloca(IrType.I64));
    var lhs = entry.Append(new IrLoad(IrType.I64, left));
    var rhs = entry.Append(new IrLoad(IrType.I64, right));
    var binary = entry.Append(new IrBinary(operation, lhs, rhs));
    entry.Append(new IrStore(binary, result));
    entry.Append(new IrRet());
    return fn;
  }

  private static IrFunction QwordShiftFunction(IrBinaryOp operation, long count) {
    var fn = new IrFunction("F", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I64));
    var result = entry.Append(new IrAlloca(IrType.I64));
    var value = entry.Append(new IrLoad(IrType.I64, source));
    var shifted = entry.Append(new IrBinary(operation, value, new IrConstantInt(IrType.I64, count)));
    entry.Append(new IrStore(shifted, result));
    entry.Append(new IrRet());
    return fn;
  }

  [TestCase(IrBinaryOp.And, MOpcode.And, 0x23)]
  [TestCase(IrBinaryOp.Or, MOpcode.Or, 0x0B)]
  [TestCase(IrBinaryOp.Xor, MOpcode.Xor, 0x33)]
  public void Select_Given386QuadBitwiseOperation_ThenUsesTwoDwordHalves(
      IrBinaryOp operation, MOpcode expected, byte encoding) {
    var target = new SelectionTarget(Cpu386: true, Optimize: true);
    var machine = InstructionSelector.TrySelect(QwordBitwiseFunction(operation), out var reason, target);

    Assert.That(machine, Is.Not.Null, reason);
    var native = machine!.AllInstructions.Where(i => i.Opcode == expected
      && i.Operands is [MOperand.Register { Reg: { Physical: Reg.EAX, Size: MRegSize.Dword } },
        MOperand.StackSlot { Size: MRegSize.Dword }]).ToList();
    Assert.That(native, Has.Count.EqualTo(2), "one operation per 32-bit half");

    var allocation = LinearScanAllocator.Allocate(machine, target);
    Assert.That(allocation, Is.Not.Null);
    var assembler = new Assembler();
    MachineEmitter.EmitFunction(assembler, machine, allocation!, [], 0);
    var bytes = assembler.ToArray();
    Assert.That(bytes.Zip(bytes.Skip(1), (a, b) => (a, b)),
      Has.Some.EqualTo(((byte)0x66, encoding)), "the selected dword operation must reach the encoder");
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  public void Select_GivenTargetWithoutOptimized386_ThenDeclinesQuadBitwise(bool cpu386, bool optimize) {
    var machine = InstructionSelector.TrySelect(QwordBitwiseFunction(IrBinaryOp.Or), out _,
      new SelectionTarget(Cpu386: cpu386, Optimize: optimize));

    Assert.That(machine, Is.Null, "the baseline path must stay with the direct emitter's QUAD runtime call");
  }

  [TestCase(IrCastOp.SExt, true)]
  [TestCase(IrCastOp.ZExt, false)]
  public void Select_GivenLongExtendedToQuad_ThenFillsTheUpperDwordExactly(IrCastOp operation, bool signed) {
    var argument = new IrArgument(IrType.I32, 0);
    var fn = new IrFunction("F", IrType.Void, [argument]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var destination = entry.Append(new IrAlloca(IrType.I64));
    var extended = entry.Append(new IrCast(operation, argument, IrType.I64));
    entry.Append(new IrStore(extended, destination));
    entry.Append(new IrRet());

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, reason);
    var upperStores = machine!.AllInstructions.Where(i => i is {
      Opcode: MOpcode.Mov,
      Operands: [MOperand.StackSlot { Disp: 4 or 6 }, _],
    }).ToList();
    Assert.That(upperStores, Has.Count.EqualTo(2));
    if (signed) {
      Assert.That(machine.AllInstructions, Has.Some.Matches<MInstr>(i => i.Opcode == MOpcode.Sar
        && i.Operands[1] is MOperand.Immediate { Value: 15 }), "the source sign fills both upper words");
      Assert.That(upperStores.Select(i => i.Operands[1]), Is.All.TypeOf<MOperand.Register>());
    } else {
      Assert.That(upperStores.Select(i => i.Operands[1]),
        Is.All.EqualTo(new MOperand.Immediate(0)), "zero extension clears both upper words");
    }
  }

  [TestCase(IrBinaryOp.Shl, MOpcode.Shld, Reg.EDX, Reg.EAX, 0xA4, 1)]
  [TestCase(IrBinaryOp.Shl, MOpcode.Shld, Reg.EDX, Reg.EAX, 0xA4, 15)]
  [TestCase(IrBinaryOp.Shl, MOpcode.Shld, Reg.EDX, Reg.EAX, 0xA4, 16)]
  [TestCase(IrBinaryOp.Shl, MOpcode.Shld, Reg.EDX, Reg.EAX, 0xA4, 31)]
  [TestCase(IrBinaryOp.LShr, MOpcode.Shrd, Reg.EAX, Reg.EDX, 0xAC, 1)]
  [TestCase(IrBinaryOp.LShr, MOpcode.Shrd, Reg.EAX, Reg.EDX, 0xAC, 15)]
  [TestCase(IrBinaryOp.LShr, MOpcode.Shrd, Reg.EAX, Reg.EDX, 0xAC, 16)]
  [TestCase(IrBinaryOp.LShr, MOpcode.Shrd, Reg.EAX, Reg.EDX, 0xAC, 31)]
  public void Select_Given386QuadShift_ThenUsesOneDwordDoubleShift(IrBinaryOp operation,
      MOpcode expected, Reg destination, Reg source, byte encoding, long count) {
    var target = new SelectionTarget(Cpu386: true, Optimize: true);
    var machine = InstructionSelector.TrySelect(QwordShiftFunction(operation, count), out var reason, target);

    Assert.That(machine, Is.Not.Null, reason);
    var shift = machine!.AllInstructions.Single(i => i.Opcode == expected);
    Assert.That(shift.Operands, Is.EqualTo(new MOperand[] {
      new MOperand.Register(MReg.Physical_(destination, MRegSize.Dword)),
      new MOperand.Register(MReg.Physical_(source, MRegSize.Dword)),
      new MOperand.Immediate(count),
    }));

    var allocation = LinearScanAllocator.Allocate(machine, target);
    Assert.That(allocation, Is.Not.Null);
    var assembler = new Assembler();
    MachineEmitter.EmitFunction(assembler, machine, allocation!, [], 0);
    var bytes = assembler.ToArray();
    Assert.That(Contains(bytes, 0x66, 0x0F, encoding), Is.True,
      "the dword double shift must reach the encoder");
  }

  [TestCase(false, false, 1)]
  [TestCase(false, true, 1)]
  [TestCase(true, false, 1)]
  [TestCase(true, true, 0)]
  [TestCase(true, true, 32)]
  public void Select_GivenUnsupportedTargetOrCount_ThenDeclinesQuadShift(bool cpu386, bool optimize, long count) {
    var machine = InstructionSelector.TrySelect(QwordShiftFunction(IrBinaryOp.Shl, count), out _,
      new SelectionTarget(Cpu386: cpu386, Optimize: optimize));

    Assert.That(machine, Is.Null, "only optimized 386 counts 1..31 may use the native double shift");
  }

  [Test]
  public void Select_GivenArithmeticQuadShift_ThenDeclinesNativeLogicalPath() {
    var target = new SelectionTarget(Cpu386: true, Optimize: true);
    var machine = InstructionSelector.TrySelect(QwordShiftFunction(IrBinaryOp.AShr, 5), out _, target);

    Assert.That(machine, Is.Null, "BASIC SHIFT RIGHT is logical; arithmetic i64 shifts need separate semantics");
  }

  [Test]
  public void Select_GivenAQuadLiteral_WhenPrinting_ThenStagesAllFourWordsForTheDoubleFormatter() {
    const long value = 0x0123456789ABCDEF;
    var machine = Select($"PRINT {value}&&");

    var instructions = machine.AllInstructions.ToList();
    var callAt = instructions.FindIndex(instruction =>
      instruction is { Opcode: MOpcode.Call, Operands: [MOperand.LabelRef { Name: "rt_print_f64" }] });
    Assert.That(callAt, Is.GreaterThanOrEqualTo(0), "QUAD uses PB's 15-digit DOUBLE formatter");
    var fild = instructions.Take(callAt).Last(instruction => instruction.Opcode == MOpcode.Fild);
    Assert.That(fild.Operands, Is.EqualTo(new[] { new MOperand.StackSlot(0, MRegSize.Qword) }));

    var staged = instructions.Take(callAt)
      .Where(instruction => instruction is { Opcode: MOpcode.Mov, Operands: [MOperand.StackSlot, MOperand.Immediate] })
      .Select(instruction => (
        Cell: (MOperand.StackSlot)instruction.Operands[0],
        Value: (MOperand.Immediate)instruction.Operands[1]))
      .Where(word => word.Cell.Index == ((MOperand.StackSlot)fild.Operands[0]).Index)
      .OrderBy(word => word.Cell.Disp)
      .ToList();
    Assert.That(staged.Select(word => word.Cell.Disp), Is.EqualTo(new[] { 0, 2, 4, 6 }));
    Assert.That(staged.Select(word => (ushort)word.Value.Value),
      Is.EqualTo(new ushort[] { 0xCDEF, 0x89AB, 0x4567, 0x0123 }));
  }

  /// <summary>
  /// A QUAD READ out of storage has no literal words to stage, and no register to hold it either -
  /// it would need four. It gets a frame cell of its own instead, filled by the only instruction
  /// pair on this target that moves eight bytes at once and does so exactly: <c>FILD qword</c> from
  /// the variable, <c>FISTP qword</c> into the cell. The printer then FILDs the cell, which is the
  /// literal case's last instruction unchanged.
  /// </summary>
  [Test]
  public void Select_GivenANonConstantQuad_WhenPrinting_ThenCopiesItThroughItsOwnQwordCell() {
    var module = new IrModule("t");
    var print = module.AddFunction(new IrFunction("rt_print_i64", IrType.Void, [new IrArgument(IrType.I64, 0)]));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var slot = entry.Append(new IrAlloca(IrType.I64));
    var value = entry.Append(new IrLoad(IrType.I64, slot));
    entry.Append(new IrCall(IrType.Void, print, [value]));
    entry.Append(new IrRet());

    var machine = InstructionSelector.TrySelect(main, out var reason);

    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    var x87 = machine!.AllInstructions
      .Where(i => i.Opcode is MOpcode.Fild or MOpcode.Fistp)
      .Select(i => (i.Opcode, Cell: (MOperand.StackSlot)i.Operands[0]))
      .ToList();
    Assert.That(x87.Select(i => i.Opcode),
      Is.EqualTo(new[] { MOpcode.Fild, MOpcode.Fistp, MOpcode.Fild }));
    Assert.That(x87.Select(i => i.Cell.Size), Is.All.EqualTo(MRegSize.Qword), "all eight bytes, every time");
    Assert.That(x87[0].Cell.Index, Is.Not.EqualTo(x87[1].Cell.Index), "the variable is copied, not aliased");
    Assert.That(x87[2].Cell.Index, Is.EqualTo(x87[1].Cell.Index), "the printer reads the copy");
  }

  /// <summary>
  /// The value that separates a 64-bit path from one that is quietly 32 bits wide: the low half of
  /// 4294967296 is zero, so anything that carried only DX:AX would print 0, and the low half of
  /// 8589934593 is 1. Both are asserted as exact TEXT, because "the two back ends agree" would still
  /// pass if both had truncated.
  /// </summary>
  [Test]
  public void Print_GivenAQuadVariableAbove32Bits_WhenRun_ThenTheWholeValuePrints() {
    const string source = """
      OPEN "R.TXT" FOR OUTPUT AS #1
      DIM q&&(1 TO 3)
      q&&(1) = 4294967296
      q&&(2) = 8589934593
      q&&(3) = -1234567890123
      n&& = 4294967296
      FOR i% = 1 TO 3
        PRINT #1, q&&(i%)
      NEXT i%
      PRINT #1, n&&
      CLOSE #1
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directRun = Cpu8086.Run(direct.EmitExecutable());
    var routedRun = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the test must not pass through fallback");
      Assert.That(routedRun.FileContent("R.TXT"),
        Is.EqualTo(" 4294967296 \r\n 8589934593 \r\n-1234567890123 \r\n 4294967296 \r\n"));
      Assert.That(directRun.FileContent("R.TXT"), Is.EqualTo(routedRun.FileContent("R.TXT")));
    });
  }

  [Test]
  public void Print_GivenQuadBoundaryClasses_WhenRun_ThenRoutedAndDirectFilesAgree() {
    const string source = """
      OPEN "R.TXT" FOR OUTPUT AS #1
      PRINT #1, 0&&
      PRINT #1, 1234567890123&&
      PRINT #1, 1234567890123456789&&
      PRINT #1, 9223372036854775807&&
      PRINT #1, -9223372036854775807&&
      PRINT #1, -9223372036854775807&& - 1&&
      CLOSE #1
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directRun = Cpu8086.Run(direct.EmitExecutable());
    var routedRun = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the test must not pass through fallback");
      Assert.That(routedRun.FileContent("R.TXT"), Is.EqualTo(directRun.FileContent("R.TXT")));
      Assert.That(routedRun.ExitCode, Is.EqualTo(directRun.ExitCode));
    });
  }

  [Test]
  public void Print_GivenAQuadBitwiseExpression_WhenOptimized_ThenRoutedAndDirectFilesAgree() {
    const string source = """
      OPEN "R.TXT" FOR OUTPUT AS #1
      PRINT #1, 73300775185&&
      x&& = 1099511627775
      y&& = 76861433640456465
      PRINT #1, x&& AND y&&
      CLOSE #1
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directRun = Cpu8086.Run(direct.EmitExecutable());
    var routedRun = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the test must not pass through fallback");
    Assert.That(directRun.FileContent("R.TXT"), Is.EqualTo(" 73300775185 \r\n 73300775185 \r\n"),
      "FILD/FISTP must retain all 64 integer bits while the direct path evaluates the expression");
    Assert.That(routedRun.FileContent("R.TXT"), Is.EqualTo(directRun.FileContent("R.TXT")));
  }

  [Test]
  public void Print_Given386QuadBitwiseOperations_WhenRun_ThenRoutedAndDirectResultsAgree() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      DECLARE SUB Bits(BYVAL a&, BYVAL b&)
      Bits -1, 2147483647
      Bits -2, 1
      END
      SUB Bits(BYVAL a&, BYVAL b&) NOINLINE
        x&& = a&
        y&& = b&
        PRINT x&& AND y&&
        PRINT x&& OR y&&
        PRINT x&& XOR y&&
      END SUB
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(routed.BackendRoutedNames, Is.SupersetOf(new[] { "main", "Bits" }),
      "the test must not pass through fallback");
    foreach (var opcode in new byte[] { 0x23, 0x0B, 0x33 })
      Assert.That(routedImage.Zip(routedImage.Skip(1), (a, b) => (a, b)),
        Has.Some.EqualTo(((byte)0x66, opcode)), $"missing dword opcode {opcode:X2}");

    var directRun = Cpu8086.Run(directImage);
    var routedRun = Cpu8086.Run(routedImage);
    Assert.Multiple(() => {
      Assert.That(directRun.Output,
        Is.EqualTo(" 2147483647 \r\n-1 \r\n-2147483648 \r\n 0 \r\n-1 \r\n-1 \r\n"));
      Assert.That(routedRun.Output, Is.EqualTo(directRun.Output));
    });
  }

  [Test]
  public void Print_Given386QuadShifts_WhenRun_ThenRoutedAndDirectResultsAgree() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      DECLARE SUB Shifted(BYVAL a&)
      Shifted -1
      Shifted 2147483647
      END
      SUB Shifted(BYVAL a&) NOINLINE
        x&& = a&
        SHIFT LEFT x&&, 5
        PRINT x&&
        x&& = a&
        SHIFT RIGHT x&&, 5
        PRINT x&&
      END SUB
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(routed.BackendRoutedNames, Is.SupersetOf(new[] { "main", "Shifted" }),
      "the test must not pass through fallback");
    Assert.That(Contains(routedImage, 0x66, 0x0F, 0xA4), Is.True, "missing dword SHLD");
    Assert.That(Contains(routedImage, 0x66, 0x0F, 0xAC), Is.True, "missing dword SHRD");

    var directRun = Cpu8086.Run(directImage);
    var routedRun = Cpu8086.Run(routedImage);
    Assert.Multiple(() => {
      Assert.That(directRun.Output,
        Is.EqualTo("-32 \r\n 5.76460752303424E+17 \r\n 68719476704 \r\n 67108863 \r\n"));
      Assert.That(routedRun.Output, Is.EqualTo(directRun.Output));
    });
  }
}
