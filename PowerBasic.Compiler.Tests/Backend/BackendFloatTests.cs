using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

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
