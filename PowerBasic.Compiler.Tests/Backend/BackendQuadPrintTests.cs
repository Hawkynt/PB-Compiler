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
}
