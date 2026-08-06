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
/// Floating point on the x86-16 back end. x87 computes on a <b>stack</b>, not in a register file, so
/// it does not fit the linear-scan allocator at all - and the answer is not to make it fit. Every
/// float SSA value lives in a frame cell, and each operation is bracketed <c>FLD ... FSTP</c>, which
/// leaves the x87 stack empty at every instruction boundary. That is also what the direct emitter
/// does with ST0, so the two paths agree on where a float is between operations.
///
/// The operand order matters and is easy to get backwards: pushing the left operand first leaves it
/// in ST(1), and the popping arithmetic computes ST(1) op ST(0) - so <c>FSUBP</c> after
/// <c>FLD a; FLD b</c> is a - b, not b - a.
/// </summary>
[TestFixture]
public sealed class BackendFloatTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static MFunction Select(string source, string function) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    var fn = module.Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, $"{function} declined: {reason}");
    return m!;
  }

  [Test]
  public void Select_GivenFloatSubtraction_ThenLoadsLeftFirstSoTheOperandOrderHolds() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.F32));
    var value = entry.Append(new IrLoad(IrType.F32, slot));
    var difference = entry.Append(new IrBinary(IrBinaryOp.FSub, value, new IrConstantFloat(IrType.F32, 1.5)));
    entry.Append(new IrStore(difference, slot));
    entry.Append(new IrRet(null));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    var subtract = opcodes.IndexOf(MOpcode.Fsubp);
    Assert.That(subtract, Is.GreaterThan(1), "both operands are pushed before the popping subtract");
    Assert.That(opcodes[subtract - 2], Is.EqualTo(MOpcode.Fld), "left operand first - it ends up in ST(1)");
    Assert.That(opcodes[subtract - 1], Is.EqualTo(MOpcode.Fld));
    Assert.That(opcodes[subtract + 1], Is.EqualTo(MOpcode.Fstp), "the result goes straight back to its cell");
  }

  [TestCase(IrCmpPred.Foeq, Condition.Equal)]
  [TestCase(IrCmpPred.Fone, Condition.NotEqual)]
  [TestCase(IrCmpPred.Folt, Condition.Below)]
  [TestCase(IrCmpPred.Fole, Condition.BelowOrEqual)]
  [TestCase(IrCmpPred.Fogt, Condition.Above)]
  [TestCase(IrCmpPred.Foge, Condition.AboveOrEqual)]
  public void Select_GivenFloatComparisonUsedAsAValue_ThenMaterializesBasicTruthFromX87Flags(
      IrCmpPred predicate, Condition condition) {
    var fn = new IrFunction("F", IrType.I1, []);
    var entry = fn.CreateBlock("entry");
    var comparison = entry.Append(new IrCmp(predicate,
      new IrConstantFloat(IrType.F32, 1.25), new IrConstantFloat(IrType.F32, 2.5)));
    entry.Append(new IrRet(comparison));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var selected = m!;
    MachineScheduler.Schedule(selected);
    var instructions = selected.AllInstructions.ToList();
    var opcodes = instructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes.IndexOf(MOpcode.Fld), Is.LessThan(opcodes.LastIndexOf(MOpcode.Fld)));
    Assert.That(opcodes.LastIndexOf(MOpcode.Fld), Is.LessThan(opcodes.IndexOf(MOpcode.Fxch)));
    Assert.That(opcodes.IndexOf(MOpcode.Fxch), Is.LessThan(opcodes.IndexOf(MOpcode.Fcompp)));
    Assert.That(opcodes.IndexOf(MOpcode.Fcompp), Is.LessThan(opcodes.IndexOf(MOpcode.FstswAx)));
    Assert.That(opcodes.IndexOf(MOpcode.FstswAx), Is.LessThan(opcodes.IndexOf(MOpcode.Sahf)));
    var status = instructions.Single(i => i.Opcode == MOpcode.FstswAx);
    Assert.That(status.Clobbers, Does.Contain(Reg.AX), "a live virtual must not be allocated over FSTSW AX");
    var branch = instructions.Single(i => i.Opcode == MOpcode.Jcc);
    Assert.That(branch.Condition, Is.EqualTo(condition));
    Assert.That(LinearScanAllocator.Allocate(selected), Is.Not.Null,
      "the selected and scheduled diamond must allocate");
  }

  [Test]
  public void Execute_GivenUnsuffixedSingleLiteralsInADoubleForLoop_ThenTheRoutedPathPreservesTheirBits() {
    const string source = """
      FUNCTION Walk%
        total# = 0
        FOR counter# = 0.1 TO 1 STEP 0.3
          total# = total# + counter#
        NEXT counter#
        PRINT total#
        Walk% = 0
      END FUNCTION

      PRINT Walk%
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Walk"), "the comparison must not silently fall back");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenAFloatLiteral_ThenReadsItFromTheConstantPoolAsAQword() {
    var fn = new IrFunction("F", IrType.F32, []);
    var entry = fn.CreateBlock("entry");
    var sum = entry.Append(new IrBinary(IrBinaryOp.FAdd,
      new IrConstantFloat(IrType.F32, 1.5), new IrConstantFloat(IrType.F32, 2.25)));
    entry.Append(new IrRet(sum));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var pooled = m!.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.DataCell>().ToList();
    Assert.That(pooled, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(pooled, Is.All.Matches<MOperand.DataCell>(c => c.Size == MRegSize.Qword),
      "the codegen's float pool stores every constant as a qword double, whatever its source precision");
    Assert.That(pooled[0].Name, Does.StartWith(".fc."));
  }

  [Test]
  public void Select_GivenAnIntegerWidenedToAFloat_ThenParksItInACellForFild() {
    // x87 reads its integers from memory, so a register value has to be stored first
    var m = Select("""
      FUNCTION Half!(BYVAL n%)
        Half! = n% / 2
      END FUNCTION

      PRINT Half!(7)
      PRINT Half!(9)
      """, "Half");                              // two constants, so n% stays a parameter (see BackendSpillTests)

    var opcodes = m.AllInstructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes, Does.Contain(MOpcode.Fild));
    var load = opcodes.IndexOf(MOpcode.Fild);
    Assert.That(opcodes.Take(load), Does.Contain(MOpcode.Mov), "the integer is written to its cell first");
    var cell = m.AllInstructions.First(i => i.Opcode == MOpcode.Fild).Operands[0];
    Assert.That(cell, Is.InstanceOf<MOperand.StackSlot>());
  }

  [Test]
  public void Select_GivenAFloatReturn_ThenLeavesItOnTheStackWithoutPopping() {
    // "Results: AX / DX:AX / ST0 / string handle in AX" - the caller pops it
    var m = Select("""
      FUNCTION Half!(BYVAL n%)
        Half! = n% / 2
      END FUNCTION

      PRINT Half!(7)
      """, "Half");

    var instructions = m.AllInstructions.ToList();
    var ret = instructions.FindIndex(i => i.Opcode == MOpcode.Ret);
    Assert.That(instructions[ret - 1].Opcode, Is.EqualTo(MOpcode.Fld),
      "the result is loaded, not stored-and-popped, right before the return");
  }

  [Test]
  public void Select_GivenAFloatParameter_ThenReadsItsDeclaredWidthFromTheIncomingStackCell() {
    var fn = new IrFunction("Scale", IrType.F32, [new IrArgument(IrType.F32, 0)]);
    var entry = fn.CreateBlock("entry");
    var result = entry.Append(new IrBinary(IrBinaryOp.FMul, fn.Parameters[0],
      new IrConstantFloat(IrType.F32, 2)));
    entry.Append(new IrRet(result));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var incoming = m!.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.ParamCell>().ToList();
    Assert.That(incoming, Does.Contain(new MOperand.ParamCell(0, 0, MRegSize.Dword)),
      "SINGLE is loaded from the four bytes the caller pushed, then widened by FLD");
    Assert.That(m.ArgumentLoads, Is.Empty, "x87 reads the parameter cell directly; no scalar vreg is minted");
  }

  [Test]
  public void Select_GivenAFloatCall_ThenRoundsArgumentsToTheirDeclaredWidthAndParksTheSt0Result() {
    var callee = new IrFunction("Blend", IrType.F64,
      [new IrArgument(IrType.F64, 0), new IrArgument(IrType.F32, 1)]);
    var calleeEntry = callee.CreateBlock("entry");
    calleeEntry.Append(new IrRet(callee.Parameters[0]));
    var caller = new IrFunction("Caller", IrType.F64, []);
    var entry = caller.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.F64, callee,
      [new IrConstantFloat(IrType.F64, 1.25), new IrConstantFloat(IrType.F32, 0.5)]));
    entry.Append(new IrRet(call));

    var m = InstructionSelector.TrySelect(caller, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var selected = m!;
    var instructions = selected.AllInstructions.ToList();
    var callAt = instructions.FindIndex(i => i.Opcode == MOpcode.Call);
    Assert.That(callAt, Is.GreaterThan(0));
    var stores = instructions.Take(callAt).Where(i => i.Opcode == MOpcode.Fstp)
      .Select(i => (MOperand.StackSlot)i.Operands[0]).ToList();
    Assert.That(stores.Select(s => s.Size), Is.EqualTo(new[] { MRegSize.Qword, MRegSize.Dword }),
      "arguments round from the x87 temporary to the callee's declared IEEE widths");
    var pushes = instructions.Take(callAt).Where(i => i.Opcode == MOpcode.Push)
      .Select(i => (MOperand.StackSlot)i.Operands[0]).ToList();
    Assert.That(pushes.Select(p => p.Size), Is.All.EqualTo(MRegSize.Word));
    Assert.That(pushes.Select(p => p.Disp), Is.EqualTo(new[] { 6, 4, 2, 0, 2, 0 }),
      "each argument is pushed high word first, matching the BASIC/PASCAL stack layout");
    Assert.That(instructions[callAt + 1].Opcode, Is.EqualTo(MOpcode.Fstp),
      "the caller pops the returned ST0 value into its own x87 cell");
    MachineScheduler.Schedule(selected);
    Assert.That(LinearScanAllocator.Allocate(selected), Is.Not.Null);
  }

  [Test]
  public void Execute_GivenFloatParametersAndResults_ThenTheRoutedStackAbiMatchesTheDirectEmitter() {
    const string source = """
      DECLARE FUNCTION Weighted#(BYVAL a#, BYVAL b!)
      DECLARE FUNCTION Echo!(BYVAL value!)

      PRINT Weighted#(1.25#, 0.5!); Echo!(16777217#)
      END

      FUNCTION Weighted#(BYVAL a#, BYVAL b!) NOINLINE
        Weighted# = a# + b! * 10
      END FUNCTION

      FUNCTION Echo!(BYVAL value!) NOINLINE
        Echo! = value!
      END FUNCTION
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Weighted"));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Echo"));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenPrintOfASingle_ThenPushesItAndCallsTheSinglePrecisionEntry() {
    // rt_print_f32 and rt_print_f64 share a body but set different significant-digit counts, which is
    // exactly what the rendering tests compare - so the SOURCE type has to pick the entry
    var m = Select("""
      FUNCTION Show%
        DIM s AS SINGLE
        s = 1.5
        PRINT s
        Show% = 0
      END FUNCTION

      PRINT Show%
      """, "Show");

    var instructions = m.AllInstructions.ToList();
    var call = instructions.FindIndex(i =>
      i.Opcode == MOpcode.Call && ((MOperand.LabelRef)i.Operands[0]).Name == "rt_print_f32");
    Assert.That(call, Is.GreaterThan(0), "the SINGLE entry, not the DOUBLE one");
    Assert.That(instructions[call - 1].Opcode, Is.EqualTo(MOpcode.Fld), "the value is on ST(0) at the call");
  }

  [Test]
  public void Emit_GivenAFloatComputingFunction_ThenTheImageAssemblesAndTheBackEndTookIt() {
    const string source = """
      FUNCTION Scaled%(BYVAL n%)
        DIM s AS SINGLE
        s = n% * 1.5
        Scaled% = 0
        PRINT s
      END FUNCTION

      PRINT Scaled%(4)
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Scaled"), "the back end did not take the float function");
  }
}
